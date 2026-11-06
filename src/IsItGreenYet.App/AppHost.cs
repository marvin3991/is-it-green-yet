using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using IsItGreenYet.App.Services;
using IsItGreenYet.Core;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Logging;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Services;

namespace IsItGreenYet.App;

/// <summary>
/// 各視窗共用的物件（同 SAM 的 AppServices），並負責組裝 Engine 的相依物件（spec §3.3）。
/// 透過 <see cref="App.Services"/> 取得。
/// </summary>
internal sealed class AppHost : IAsyncDisposable
{
    /// <summary>User-Agent 的產品名稱（spec §4.1：<c>IsItGreenYet/&lt;版本&gt;</c>）。</summary>
    private const string UserAgentProduct = "IsItGreenYet";

    /// <summary>SDK 產生的 InformationalVersion 會在版本號後面接 <c>+&lt;commit&gt;</c>，User-Agent 與畫面只放版本號。</summary>
    private const char BuildMetadataSeparator = '+';

    private const int NotDisposed = 0;
    private const int Disposed = 1;

    private readonly SocketsHttpHandler _handler;
    private readonly Dispatcher _dispatcher;

    // 設定頁驗證 PAT 時也要用（VerifyTokenAsync）：和 Engine 共用同一個 handler、api.log 與 User-Agent。
    private readonly TimeProvider _time;
    private readonly ApiLog _apiLog;
    private readonly string _userAgent;

    private int _disposed = NotDisposed;

    /// <param name="startupWarning">
    /// DB 或設定檔損毀復原時給主畫面顯示的橫幅（依語言產生，換語言時用同一個方法重新產生、保留 NoticeId）；沒有則為 null。
    /// </param>
    /// <param name="dispatcher">UI 執行緒的 Dispatcher；背景錯誤的橫幅事件在這裡觸發。</param>
    public AppHost(
        AppPaths paths,
        SettingsService settingsStore,
        AppSettings settings,
        ErrorLog errorLog,
        SecretRedactor redactor,
        TimeProvider time,
        DateTimeOffset startedAtUtc,
        Func<AppLanguage, BannerInfo>? startupWarning,
        Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(errorLog);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(dispatcher);

        Paths = paths;
        SettingsStore = settingsStore;
        Settings = settings;
        ErrorLog = errorLog;
        StartupWarning = startupWarning;
        _dispatcher = dispatcher;
        _time = time;
        _apiLog = new ApiLog(paths.LogsDirectory, redactor, time);
        _userAgent = BuildUserAgent();

        // 轉址由 GitHubClient 自己處理：.NET 自動轉址時一律清掉 Authorization（spec §4.1）。
        _handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        try
        {
            var dependencies = new EngineDependencies(
                Time: time,
                Handler: _handler,
                Paths: paths,
                Cache: new CacheRepository(paths.CacheDbPath, time),
                Settings: settings,
                // 取不到 token 的原因跟著設定的語言（每次取 token 時才讀）。
                TokenProvider: new SettingsTokenProvider(() => Settings, new ProcessRunner(), new WindowsCredentialStore()),
                ApiLog: _apiLog,
                ErrorLog: errorLog,
                Redactor: redactor,
                UserAgent: _userAgent,
                StartedAtUtc: startedAtUtc,
                AppVersion: BuildAppVersion());

            Engine = new MonitorEngine(dependencies);
            Engine.UpdateCheckRecorded += OnUpdateCheckRecorded;
        }
        catch
        {
            _handler.Dispose();
            throw;
        }
    }

    /// <summary>背景錯誤要顯示橫幅時觸發，一律在 UI 執行緒上（spec §15）。</summary>
    public event Action<BannerInfo>? BackgroundErrorRaised;

    /// <summary>
    /// 畫面上顯示的版本（設定頁最下方），User-Agent 也用它：不含 <c>+</c> 後面的 build metadata；
    /// 沒有 InformationalVersion 時用組件版本，都沒有時是空字串。
    /// </summary>
    public static string DisplayVersion { get; } = BuildDisplayVersion();

    public MonitorEngine Engine { get; }

    /// <summary>目前的設定。設定頁儲存時可以整個換掉，Engine 取 token 時讀的也是這裡的值。</summary>
    public AppSettings Settings { get; set; }

    public SettingsService SettingsStore { get; }

    public AppPaths Paths { get; }

    /// <summary>
    /// 啟動時 DB 或設定檔損毀復原的提示（<see cref="BannerKind.DatabaseRecovered"/>／<see cref="BannerKind.SettingsRecovered"/>），
    /// 依語言產生；同一次執行每次產生的 <see cref="BannerInfo.NoticeId"/> 相同。
    /// </summary>
    public Func<AppLanguage, BannerInfo>? StartupWarning { get; }

    /// <summary>App 與 Engine 共用的 error.log（同一個遮罩、同一個去重狀態）。</summary>
    public ErrorLog ErrorLog { get; }

    /// <exception cref="IOException">寫入失敗；呼叫端決定怎麼提示（同 SAM）。</exception>
    /// <exception cref="UnauthorizedAccessException">沒有寫入權限。</exception>
    public void SaveSettings() => SettingsStore.Save(Settings);

    /// <summary>
    /// 背景（Engine 以外的背景工作、未觀察的工作例外、系統事件轉送）發生的未預期例外：寫 error.log；
    /// 同一種例外在 ErrorBannerDedupWindow 內只用橫幅提示一次，不跳對話框（spec §15）。可從任何執行緒呼叫。
    /// 橫幅是新的一則（新的 NoticeId）；文字用目前的語言，換語言時主畫面依種類重新產生。
    /// </summary>
    public void ReportBackgroundError(string context, Exception exception)
    {
        if (!ErrorLog.Write(context, exception))
        {
            return;
        }

        var banner = new BannerInfo(BannerKind.UnexpectedError, UiText.Of(Settings.Language).CommonUnexpectedError, Until: null)
        {
            NoticeId = BannerInfo.NextNoticeId(),
        };

        // Dispatcher 已經關閉（程式正在結束）時不會丟例外，操作直接被放棄。
        _dispatcher.InvokeAsync(() => BackgroundErrorRaised?.Invoke(banner));
    }

    /// <summary>
    /// 設定頁「儲存前先用 E1 驗證」PAT（spec §11.2、§13.3）：用候選的 token 單獨送一次 GET /user，
    /// 不經過 Engine，也不改變 Engine 正在用的 token（Engine 的公開 API 只能驗證目前的 token）。
    /// 共用 Engine 的 handler（不自動轉址）、api.log 與 User-Agent；不帶 If-None-Match，結果也不寫進快取。
    /// </summary>
    /// <exception cref="ObjectDisposedException">程式正在結束。</exception>
    internal async Task<ApiResult<AccountInfo>> VerifyTokenAsync(string token, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == Disposed, this);

        using var client = new GitHubClient(_handler, NoHttpCache.Instance, () => token, _apiLog, _time, _userAgent);
        return await new GitHubApi(client).GetUserAsync(ct).ConfigureAwait(false);
    }

    /// <summary>先收掉 Engine，再釋放它用的 HTTP handler（GitHubClient 不擁有 handler）。重複呼叫沒有作用。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, Disposed) == Disposed)
        {
            return;
        }

        Engine.UpdateCheckRecorded -= OnUpdateCheckRecorded;
        try
        {
            await Engine.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _handler.Dispose();
        }
    }

    /// <summary>
    /// Engine 交回上次檢查時間與已通知版本，回 UI 執行緒才改設定物件與存檔：所有 Save 都在 UI 執行緒，SettingsService 沒有鎖。
    /// 這是背景工作，任何失敗都只寫 error.log，不跳訊息條、橫幅或對話框；記憶體的值留著，下一次成功的存檔會一起寫出。
    /// Dispatcher 已經關閉（程式正在結束）時不會丟例外，操作直接被放棄，下次啟動最多多查一次。
    /// </summary>
    private void OnUpdateCheckRecorded(UpdateCheckRecord record) =>
        _dispatcher.InvokeAsync(() =>
        {
            try
            {
                record.ApplyTo(Settings);
                SaveSettings();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("儲存檢查新版本紀錄失敗", ex);
            }
        });

    private static string BuildUserAgent() =>
        string.IsNullOrEmpty(DisplayVersion) ? UserAgentProduct : $"{UserAgentProduct}/{DisplayVersion}";

    private static string BuildDisplayVersion()
    {
        var version = InformationalVersion()?.Split(BuildMetadataSeparator, 2)[0];
        if (string.IsNullOrWhiteSpace(version))
        {
            version = typeof(AppHost).Assembly.GetName().Version?.ToString();
        }

        return string.IsNullOrWhiteSpace(version) ? string.Empty : version;
    }

    /// <summary>
    /// 寫進狀態表給命令列讀的版本（第二階段 spec §2、§10.3）：完整的 InformationalVersion，含 <c>+</c> 後面的 commit；
    /// 沒有這個屬性時用組件版本，都沒有時是空字串。
    /// </summary>
    private static string BuildAppVersion()
    {
        var informational = InformationalVersion();
        return string.IsNullOrWhiteSpace(informational)
            ? typeof(AppHost).Assembly.GetName().Version?.ToString() ?? string.Empty
            : informational;
    }

    private static string? InformationalVersion() =>
        typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>驗證 PAT 用的空快取：一律沒有 ETag，請求不帶 If-None-Match。</summary>
    private sealed class NoHttpCache : IHttpCacheStore
    {
        public static NoHttpCache Instance { get; } = new();

        public HttpCacheEntry? Get(string url) => null;
    }

    /// <summary>
    /// 每次取 token 時依「目前」設定的認證方式，選 GhCliTokenProvider 或 PatTokenProvider（spec §13.1、§13.3）。
    /// 設定頁改了認證方式或「忽略環境變數」（Q32）之後，Engine 下一次取 token 就照新的設定，不必重新啟動。
    /// 取不到 token 的原因用設定裡的語言（Engine 跑在背景執行緒，明確傳入，不讀 UI 執行緒的設定）。
    /// </summary>
    private sealed class SettingsTokenProvider : ITokenProvider
    {
        private readonly Func<AppSettings> _currentSettings;
        private readonly IProcessRunner _runner;
        private readonly PatTokenProvider _pat;

        public SettingsTokenProvider(Func<AppSettings> currentSettings, IProcessRunner runner, ICredentialStore credentialStore)
        {
            _currentSettings = currentSettings;
            _runner = runner;
            _pat = new PatTokenProvider(credentialStore, Language);
        }

        public Task<TokenResult> GetTokenAsync(CancellationToken ct)
        {
            var settings = _currentSettings();
            if (settings.AuthMode == AuthMode.Pat)
            {
                return _pat.GetTokenAsync(ct);
            }

            var gh = new GhCliTokenProvider(_runner, settings.IgnoreGhTokenEnv, Environment.GetEnvironmentVariable, Language);
            return gh.GetTokenAsync(ct);
        }

        private AppLanguage Language() => _currentSettings().Language;
    }
}
