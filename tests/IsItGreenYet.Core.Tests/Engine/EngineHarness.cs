using System.Text;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Logging;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Tests.Auth;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>
/// MonitorEngine 整合測試的共用環境：暫存資料夾裡的 cache.db、假 GitHub、假 token、可手動推進的時間。
/// 預設的世界：帳號 alice（1001）、組織 repo octo-org/sample-app（2001，兩天前 push），一個 active 的 CI workflow。
/// </summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    public const string UserAgent = "IsItGreenYet/0.0.0-test";

    /// <summary>傳給 Engine 的 App 版本（寫進狀態表用）。</summary>
    public const string AppVersion = "0.0.0-test";

    /// <summary>第二把假 token（重新取得 token 時換到的值）：由 <see cref="Fixture.FakeToken"/> 改幾個字元而來，格式相同。</summary>
    public static readonly string OtherFakeToken = Fixture.FakeToken.Replace('0', '1');

    public const string RunsPath = "/repos/octo-org/sample-app/actions/runs?per_page=30";
    public const string WorkflowsPath = "/repos/octo-org/sample-app/actions/workflows?per_page=100";
    public const string PullsPath = "/repos/octo-org/sample-app/pulls?state=open&per_page=100";
    public const string ListAPath = "/user/repos?affiliation=owner,organization_member&per_page=100";
    public const string ProbeAPath = "/user/repos?affiliation=owner,organization_member&sort=pushed&per_page=100";
    public const string SeedPath = "/repos/octo-org/sample-app/actions/workflows/161335/runs?branch=main&per_page=5";

    /// <summary>檢查新版本的端點；刻意寫字面值，不用 Tuning：owner 與 repo 是需求。</summary>
    public const string ReleasePath = "/repos/marvin3991/is-it-green-yet/releases/latest";

    /// <summary>防止 Drain 因為實作錯誤無限迴圈。</summary>
    private const int MaxDrainSteps = 500;

    /// <summary>讀 log 遇到 IOException（例如防毒軟體暫時獨占開檔）時最多試幾次。</summary>
    private const int ReadLogAttempts = 5;

    /// <summary>讀 log 重試之間的等待。</summary>
    private static readonly TimeSpan ReadLogRetryDelay = TimeSpan.FromMilliseconds(20);

    public static readonly DateTimeOffset Start = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    private readonly List<MonitorSnapshot> _snapshots = [];
    private readonly List<ToastContent> _toasts = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    private EngineHarness()
    {
        Clock = new EngineClock(Start);
        Workspace = new TempWorkspace();
        DatabaseInitializer.Ensure(Workspace.Paths.CacheDbPath, Clock);
        Cache = new CacheRepository(Workspace.Paths.CacheDbPath, Clock);
        GitHub = new FakeGitHub(Clock);
        Redactor = new SecretRedactor();
        ApiLog = new ApiLog(Workspace.Paths.LogsDirectory, Redactor, Clock);
        ErrorLog = new ErrorLog(Workspace.Paths.RootDirectory, Redactor, Clock);
        Main = GitHub.AddRepo(FakeData.Repo(FakeData.RepoId, FakeData.RepoName, Start.AddDays(-2)));
    }

    public EngineClock Clock { get; }

    public TempWorkspace Workspace { get; }

    public CacheRepository Cache { get; }

    public FakeGitHub GitHub { get; }

    public FakeTokenProvider Tokens { get; } = new();

    /// <summary>檢查新版本預設關閉，既有測試的請求序列才不會多一個 GET；要測的測試自己打開。</summary>
    public AppSettings Settings { get; } = new() { CheckForUpdates = false };

    /// <summary>建立 Engine 時傳入的 App 版本；檢查新版本的測試用它換目前版本。</summary>
    public string CurrentAppVersion { get; set; } = AppVersion;

    public SecretRedactor Redactor { get; }

    public ApiLog ApiLog { get; }

    public ErrorLog ErrorLog { get; }

    /// <summary>預設的 repo（octo-org/sample-app）。</summary>
    public RepoWorld Main { get; }

    public MonitorEngine Engine { get; private set; } = null!;

    public IReadOnlyList<MonitorSnapshot> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return _snapshots.ToList();
            }
        }
    }

    public MonitorSnapshot Last
    {
        get
        {
            lock (_gate)
            {
                return _snapshots[^1];
            }
        }
    }

    public IReadOnlyList<ToastContent> Toasts
    {
        get
        {
            lock (_gate)
            {
                return _toasts.ToList();
            }
        }
    }

    public string ApiLogText => ReadAll(Path.Combine(Workspace.Paths.LogsDirectory, "api.log"));

    public string ErrorLogText => ReadAll(Workspace.Paths.ErrorLogPath);

    /// <param name="arrange">建立 Engine 之前呼叫：設定假 GitHub 的內容、預先寫入 DB、排好 token。</param>
    /// <param name="defaultToken">true 時先排一個 <see cref="Fixture.FakeToken"/> 給 Engine 初始化用。</param>
    public static EngineHarness Create(Action<EngineHarness>? arrange = null, bool defaultToken = true)
    {
        var harness = new EngineHarness();
        if (defaultToken)
        {
            harness.Tokens.Returns(Fixture.FakeToken);
        }

        arrange?.Invoke(harness);
        harness.Engine = harness.NewEngine(Start);
        return harness;
    }

    /// <summary>模擬程式重新啟動：同一個 cache.db，新的 Engine，啟動時間是現在，先排好一個 token。</summary>
    public async Task RestartAsync()
    {
        await Engine.DisposeAsync();
        Tokens.Returns(Fixture.FakeToken);
        Engine = NewEngine(Clock.GetUtcNow());
    }

    private MonitorEngine NewEngine(DateTimeOffset startedAt)
    {
        var engine = new MonitorEngine(new EngineDependencies(
            Clock,
            GitHub.Handler,
            Workspace.Paths,
            Cache,
            Settings,
            Tokens,
            ApiLog,
            ErrorLog,
            Redactor,
            UserAgent,
            startedAt,
            CurrentAppVersion));
        engine.SnapshotPublished += OnSnapshot;
        engine.ToastReady += OnToast;

        // 同 App：引擎交回的紀錄寫進設定，RestartAsync 之後的引擎讀得到。
        engine.UpdateCheckRecorded += record => record.ApplyTo(Settings);
        return engine;
    }

    public Task<int> StepAsync() => Engine.RunOnceAsync(CancellationToken.None);

    /// <summary>一直推進到沒有到期的請求為止，回傳這段期間送出的請求總數。</summary>
    public async Task<int> DrainAsync()
    {
        var total = 0;
        for (var step = 0; step < MaxDrainSteps; step++)
        {
            var sent = await StepAsync();
            if (sent == 0)
            {
                return total;
            }

            total += sent;
        }

        throw new InvalidOperationException("Engine 在測試上限內沒有停下來，可能有無限重排。");
    }

    /// <summary>推進時間後把到期的請求都送完。</summary>
    public async Task<int> AdvanceAsync(TimeSpan delta)
    {
        Clock.Advance(delta);
        return await DrainAsync();
    }

    /// <summary>每次推進 <paramref name="step"/>（最後一步只推進剩下的），共推進 <paramref name="total"/>，每一步都把到期的請求送完。</summary>
    public async Task<int> AdvanceInStepsAsync(TimeSpan total, TimeSpan step)
    {
        var sent = 0;
        for (var elapsed = TimeSpan.Zero; elapsed < total;)
        {
            var delta = total - elapsed < step ? total - elapsed : step;
            sent += await AdvanceAsync(delta);
            elapsed += delta;
        }

        return sent;
    }

    public RepoStatusRow RepoRow(long repoId) => Last.Repos.Single(r => r.Repo.Id == repoId);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Engine is not null)
        {
            await Engine.DisposeAsync();
        }

        Workspace.Dispose();
    }

    /// <summary>
    /// StartAsync 之後背景迴圈可能正用 File.AppendAllText 寫同一個檔（只允許別人讀）。
    /// 這裡要允許對方同時寫入與改名（輪替）：File.ReadAllText 只允許共用讀，誰先開檔另一方就共用違規，
    /// 測試會丟 IOException，或 log 那一行被 ErrorLog 吞掉、測試等到逾時。
    /// </summary>
    private static string ReadAll(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // 還沒寫過任何一行（或輪替時剛好改名走）。
                return string.Empty;
            }
            catch (IOException) when (attempt < ReadLogAttempts)
            {
                Thread.Sleep(ReadLogRetryDelay);
            }
        }
    }

    private void OnSnapshot(MonitorSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshots.Add(snapshot);
        }
    }

    private void OnToast(ToastContent toast)
    {
        lock (_gate)
        {
            _toasts.Add(toast);
        }
    }
}
