using System.Collections.Concurrent;
using System.Collections.Frozen;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Polling;
using IsItGreenYet.Core.Updates;
using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Core.Engine;

/// <summary>
/// 監看引擎：單一背景迴圈，序列化送出請求、寫入 DB、發布快照（spec §3.1）。
/// App 只透過這裡的公開成員互動。
/// </summary>
/// <remarks>
/// 執行緒模型：
/// - 輪詢的每一步（<see cref="RunOnceAsync"/> 與背景迴圈）由 <c>_stepGate</c> 序列化，一次只有一步在跑；
///   標示「步驟專用」的欄位只在步驟裡讀寫，不必另外加鎖。
/// - <see cref="Tick"/>、<see cref="Post"/> 可從任何執行緒呼叫，只用 Interlocked／ConcurrentQueue 交給步驟處理。
/// - 使用者操作（複製失敗 log、檢查連線、寫入）走另一條工作（spec §3.1），和輪詢共用同一個 RequestThrottle：
///   TimeUntilAllowed 與 RecordSent 一律在 <c>_throttleGate</c> 裡成對呼叫。使用者操作只讀 <c>_repoLookup</c>、
///   <c>_actionLookup</c>、<c>_runStateLookup</c>、<c>_settings</c> 這些不可變的參考；要改步驟狀態時排進 <c>_userEffects</c>，由下一步套用。
///   寫入權限與可操作性的狀態（MonitorEngine.Actions.cs）兩條工作都會讀寫，一律是 volatile 的不可變參考或有鎖保護。
/// - 事件在背景執行緒觸發，由 App 自己轉回 UI 執行緒；事件處理丟出的例外只寫 error.log，不影響迴圈。
/// - 畫面文字（快照、橫幅、提示、使用者操作的結果）一律用設定裡的語言（<see cref="Language"/>），不看執行緒的 CurrentUICulture；
///   <see cref="EngineCommand.SettingsChanged"/> 換了語言時，記住的文字（全域暫停與一次性提示的橫幅、repo 的問題標示）用新的語言重建。
/// - <see cref="DisposeAsync"/> 先取消，再等進行中的步驟（含 <see cref="StartAsync"/> 的初始化）與使用者操作結束，
///   最後才釋放 HTTP 用戶端；之後才結束的操作一律以取消結束。
/// </remarks>
public sealed partial class MonitorEngine : IAsyncDisposable
{
    /// <summary>還沒有收到任何 tick。</summary>
    private const long NoTick = long.MinValue;

    /// <summary>沒有在等 cache.db 改名重建的重試。</summary>
    private const long NoRecoveryRetry = long.MinValue;

    /// <summary>
    /// 連續重建幾次（舊檔改名保留或丟棄都算）之後，又讀到損毀就改用退避（<see cref="RecoverDatabaseIfNeeded"/>）；兩次重建相隔不到 <see cref="Tuning.RebuildStreakWindow"/> 才算連續，
    /// 不管損毀是在寫回、重讀、寫狀態表還是步驟主體讀到的。是 2：新的資料庫只損毀一次時，第一次仍立即再重建一次（同一步結尾，或下一個步驟開頭），不必等退避；
    /// 再讀到損毀才當成儲存層持續損毀，不再每一步或每個請求都改名重建。
    /// </summary>
    private const int RebuildsBeforeBackoff = 2;

    /// <summary>丟棄損毀的資料庫之前 error.log 的標題。不可以包含「重建快取資料庫」或「讀取快取資料庫」：那兩個是重試失敗的紀錄，測試在數它們。</summary>
    private const string DatabaseDiscardedContext = "未保留損毀的快取資料庫";

    private readonly EngineDependencies _deps;
    private readonly TimeProvider _time;
    private readonly CacheRepository _cache;
    private readonly GitHubClient _client;
    private readonly GitHubApi _api;

    /// <summary>四個寫入端點（第二階段 spec §3.1）；只有 <see cref="SendWriteAsync"/> 會用。</summary>
    private readonly IActionsWriter _writer;

    private readonly AuthManager _auth;
    private readonly PollScheduler _scheduler;
    private readonly RequestThrottle _throttle;
    private readonly SecondaryBackoff _secondary;
    private readonly ForbiddenBurstDetector _forbiddenBurst;
    private readonly OfflineDetector _offline = new();
    private readonly ServerErrorDetector _serverErrors = new();
    private readonly NotificationPlanner _planner;
    private readonly NotificationQueue _toasts;

    /// <summary>這一個請求用哪個 token 送出；401 流程要知道「被拒的是哪一把」（AuthManager 的 internal 多載）。</summary>
    private readonly AsyncLocal<string?> _requestToken = new();

    private readonly ConcurrentQueue<EngineCommand> _commands = new();
    private readonly ConcurrentQueue<Action> _userEffects = new();
    private readonly SemaphoreSlim _stepGate = new(1, 1);
    private readonly SemaphoreSlim _userGate = new(Tuning.MaxConcurrentUser, Tuning.MaxConcurrentUser);
    private readonly Lock _throttleGate = new();

    /// <summary>橫幅欄位與全域暫停的註記：輪詢與使用者操作都可能寫入（限流、未預期例外）。</summary>
    private readonly Lock _bannerGate = new();

    private readonly CancellationTokenSource _lifetime = new();

    private TaskCompletionSource _wake = NewSignal();
    private long _lastTick = NoTick;
    private int _sleepGapDetected;
    private int _userWaiting;
    private int _started;
    private int _disposed;

    /// <summary>
    /// 某次讀寫 cache.db 回報損毀（任何一條工作都可能遇到），交給下一次 <see cref="RecoverDatabaseIfNeeded"/> 改名重建（spec §15）。
    /// 步驟的開頭與結尾各呼叫一次：步驟開頭或中途發現的在同一步結尾重建，步驟結尾的重建裡或之後才發現的在下一個步驟開頭；
    /// 其他工作回報的由迴圈接下來最先呼叫的那一次重建（正在跑的那一步結尾，或下一個步驟開頭）。
    /// 初始化讀 DB 時發現的當場重建（<see cref="TryLoadDatabase"/>）。改名重建還在退避中時（改名失敗，或連續改名重建達到 <see cref="RebuildsBeforeBackoff"/> 次之後又讀到損毀），
    /// 等到重試的時間才重建。
    /// </summary>
    private int _databaseCorrupt;

    /// <summary>
    /// 改名重建失敗（例如另一個程式開著 cache.db），或連續改名重建達到 <see cref="RebuildsBeforeBackoff"/> 次之後又讀到損毀而退避之後，下一次重試的單調時間戳；
    /// 沒有在等重試時是 <see cref="NoRecoveryRetry"/>。改名重建成功時清掉（又讀到損毀而退避時再排上）。
    /// 任何一條工作都會讀（<see cref="ReportUnexpected"/>），一律用 Volatile 存取。
    /// </summary>
    private long _recoverRetryAt = NoRecoveryRetry;

    /// <summary>
    /// 最後一份已發布快照的 <see cref="MonitorSnapshot.Sequence"/>（還沒發布過是 0）。只在步驟裡用 Interlocked 加 1；
    /// 其他工作要讀時用 Volatile。
    /// </summary>
    private long _snapshotSequence;

    private Task? _loopTask;
    private CancellationTokenSource? _loopSource;

    // ── 給使用者操作讀的不可變參考 ───────────────────────────────────
    private volatile IReadOnlyDictionary<long, RepoInfo> _repoLookup = new Dictionary<long, RepoInfo>();

    /// <summary>可操作性查表（第二階段 spec §5.3）：發布快照時換成那一份快照的 <see cref="MonitorSnapshot.Actions"/>。</summary>
    private volatile IReadOnlyDictionary<long, RunActions> _actionLookup = new Dictionary<long, RunActions>();

    /// <summary>
    /// 只給寫入再檢查用的 run 狀態查表（<see cref="BuildSnapshot"/> 的第二份結果）：不算送出中、沒有共通條件，
    /// 四個動作的 run 狀態類原因不會被蓋掉。和 <see cref="_actionLookup"/> 在同一處換：先換這一份，再換 <see cref="_actionLookup"/>。
    /// CarryOver 沿用、MemorySnapshot 清空，同快照的 Actions。
    /// </summary>
    private volatile IReadOnlyDictionary<long, RunActions> _runStateLookup = new Dictionary<long, RunActions>();

    private volatile EngineSettings _settings;
    private volatile bool _versionError;

    // ── 橫幅（_bannerGate）────────────────────────────────────────────
    private BannerInfo? _pauseBanner;

    /// <summary>產生 <see cref="_pauseBanner"/> 文字的方法（語言、恢復時間）：換語言時用它重建。</summary>
    private Func<AppLanguage, DateTimeOffset, string>? _pauseText;

    private Notice? _deprecatedNotice;
    private Notice? _databaseNotice;
    private Notice? _unexpectedNotice;

    /// <summary>已經發布過幾次快照；一次性提示記下它發生在第幾次之前，用來判斷哪一則比較新。</summary>
    private long _publishWindow;

    // ── 步驟專用 ──────────────────────────────────────────────────────
    private bool _initialized;
    private RefreshMode _refreshMode;
    private bool _locked;
    private bool _suspended;

    /// <summary>只在步驟裡寫；寫入權限的判斷（<see cref="GlobalBlock"/>，使用者操作也會呼叫）會讀，所以是 volatile。</summary>
    private volatile AccountInfo? _account;

    private PollTarget? _current;

    /// <summary>初始化讀 DB 失敗時，下一次重試的時間與這次的退避間隔。</summary>
    private long? _initRetryAt;
    private TimeSpan _initRetryDelay;

    /// <summary>改名重建失敗，或連續改名重建達到門檻之後又讀到損毀時這次的退避間隔（下一次重試的時間在 <c>_recoverRetryAt</c>）。</summary>
    private TimeSpan _recoverRetryDelay;

    /// <summary>
    /// 連續重建了幾次（舊檔改名保留或丟棄都算；相隔不到 <see cref="Tuning.RebuildStreakWindow"/> 才算連續）；達到 <see cref="RebuildsBeforeBackoff"/> 之後又讀到損毀就改用退避。
    /// </summary>
    private int _rebuildStreak;

    /// <summary>上一次改名重建的單調時間戳（連續中改名失敗的重試也算）；還沒有重建過是 null。</summary>
    private long? _lastRebuildAt;

    /// <summary>上一次發布的快照：改名重建失敗或重建後仍損毀、還在等重試時沿用它的內容（不讀 DB）。</summary>
    private MonitorSnapshot? _lastSnapshot;

    /// <summary>
    /// 目前路徑上的 cache.db 可能有靜音或強制納入設定。初值 true：上一次執行留下的檔、App 的 Ensure 重建的檔，內容都當未知。
    /// 舊檔離開這個路徑時清掉（之後這個路徑上的檔都是這個程序建的），設定靜音或強制納入時再設回去（<see cref="ApplyCommandAsync"/>）。
    /// </summary>
    private bool _databaseMayHoldSettings = true;

    /// <summary>這個程序最後留下的那份備份的檔名；丟棄時橫幅仍指向它（檔案還在）。還沒留過是 null。</summary>
    private string? _lastKeptBackupName;

    /// <summary>重建資料庫成功的次數（改名保留或丟棄都算）。步驟裡加 1，步驟外的測試掛鉤會讀，所以用 Interlocked／Volatile。</summary>
    private long _databaseRebuildCount;

    public MonitorEngine(EngineDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);

        _deps = deps;
        _time = deps.Time;
        _cache = deps.Cache;
        _settings = EngineSettings.From(deps.Settings);
        _refreshMode = _settings.RefreshMode;
        SemVersion.TryParseCurrent(deps.AppVersion, out _currentVersion);
        _updateLastCheckUtc = deps.Settings.LastUpdateCheckUtc;
        _updateLastNotified = deps.Settings.LastNotifiedVersion;
        _epochUtc = deps.StartedAtUtc;
        _startedAtTimestamp = deps.Time.GetTimestamp();
        _client = new GitHubClient(deps.Handler, deps.Cache, () => _requestToken.Value, deps.ApiLog, deps.Time, deps.UserAgent);
        _api = new GitHubApi(_client);
        _writer = _api;
        _auth = new AuthManager(deps.TokenProvider, deps.Redactor);
        _scheduler = new PollScheduler(deps.Time);
        _throttle = new RequestThrottle(deps.Time);
        _secondary = new SecondaryBackoff(deps.Time);
        _forbiddenBurst = new ForbiddenBurstDetector(deps.Time);
        _planner = new NotificationPlanner(new SessionNotifiedStore(deps.Cache));
        _toasts = new NotificationQueue(deps.Time);
    }

    public event Action<MonitorSnapshot>? SnapshotPublished;

    /// <summary>畫面文字的語言（設定裡的；任何一條工作都可以讀，<c>_settings</c> 是不可變的參考）。</summary>
    private AppLanguage Language => _settings.Language;

    public event Action<ToastContent>? ToastReady;

    /// <summary>檢查新版本的上次檢查時間或已通知版本改變了：Engine 不寫設定檔，由 App 轉回 UI 執行緒寫入並存檔。</summary>
    public event Action<UpdateCheckRecord>? UpdateCheckRecorded;

    /// <summary>App 每秒呼叫：記錄單調時間戳、喚醒迴圈。不會重入。</summary>
    /// <remarks>
    /// 相鄰兩次 tick 的單調時鐘間隔超過 SleepGapThreshold 就記下「剛喚醒」，由下一步處理（spec §6.5 喚醒的備援）。
    /// 迴圈正在等請求時 tick 照樣記錄，所以睡眠期間卡住的請求不影響判斷。
    /// </remarks>
    public void Tick()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var now = _time.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastTick, now);
        if (previous != NoTick && _time.GetElapsedTime(previous, now) > Tuning.SleepGapThreshold)
        {
            Interlocked.Exchange(ref _sleepGapDetected, 1);
        }

        Wake();
    }

    public void Post(EngineCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _commands.Enqueue(command);
        Wake();
    }

    /// <summary>讀 DB → 發布快取快照 → 認證 → 開始迴圈。</summary>
    /// <remarks>
    /// 初始化（讀 DB、組第一份快照、取 token）與迴圈都在背景工作上跑：App 從 UI 執行緒呼叫也不會卡住畫面，
    /// 第一份快照同樣在背景執行緒觸發（spec §3.1）。迴圈不等它結束；<paramref name="ct"/> 取消時迴圈也會停下。
    /// 讀 DB 失敗也不往外丟（spec §15）：照樣發布帶橫幅的快照並開始迴圈，由迴圈稍後重試。
    /// 初始化期間（取 token 最多 GhTimeout）呼叫 <see cref="DisposeAsync"/> 時以取消結束，不啟動迴圈。
    /// </remarks>
    public async Task StartAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("監看引擎已經啟動。");
        }

        // 一開始就連到 _lifetime：初始化期間釋放引擎也會取消它。
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = source.Token;
        try
        {
            await Task.Run(() => InitializeAndStartLoopAsync(source, token), token).ConfigureAwait(false);
        }
        catch
        {
            // 迴圈沒有啟動：呼叫端取消初始化之後還可以再呼叫一次。
            if (!ReferenceEquals(_loopSource, source))
            {
                source.Dispose();
                Volatile.Write(ref _started, 0);
            }

            throw;
        }
    }

    /// <summary><see cref="StartAsync"/> 在背景工作上做的部分：在步驟的閘門裡初始化，再啟動迴圈。</summary>
    private async Task InitializeAndStartLoopAsync(CancellationTokenSource source, CancellationToken token)
    {
        await _stepGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfStopping(token);
            if (!await EnsureInitializedAsync(token).ConfigureAwait(false))
            {
                PublishSnapshot();
            }

            ThrowIfStopping(token);
            _loopSource = source;
            _loopTask = Task.Run(() => LoopAsync(token), CancellationToken.None);
        }
        finally
        {
            _stepGate.Release();
        }
    }

    /// <summary>測試用：這個引擎重建了幾次資料庫。丟棄不留檔，所以備份份數不等於重建次數。</summary>
    internal long DatabaseRebuildCount => Volatile.Read(ref _databaseRebuildCount);

    /// <summary>測試用：處理所有排隊的指令，再最多送出一個到期請求；回傳送出的請求數。</summary>
    /// <remarks>
    /// 一個目標收到 401、換到新 token 重送一次時算 2 個請求；節流器不放行而把重送延後時，這一步算 1 個。
    /// 背景迴圈每一步也是同一套步驟（spec §3.1）。
    /// </remarks>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var (sent, _) = await StepAsync(linked.Token).ConfigureAwait(false);
        return sent;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        Wake();

        // 等進行中的步驟（含 StartAsync 的初始化）結束；token 已經取消，之後不會再開始新的一步。
        await _stepGate.WaitAsync().ConfigureAwait(false);
        _stepGate.Release();
        if (_loopTask is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 迴圈因取消而結束，這是預期的。
            }
        }

        // 等進行中的使用者操作結束（排隊中的已經因為取消而離開），之後才釋放 HTTP 用戶端。這些名額不再歸還。
        for (var slot = 0; slot < Tuning.MaxConcurrentUser; slot++)
        {
            await _userGate.WaitAsync().ConfigureAwait(false);
        }

        PersistPendingChoices();

        // 命令列靠 stopped_at 分辨「App 已正常結束」和「App 當掉或停住」（第二階段 spec §10.2、§10.6）。
        WriteStoppedStatus();
        _loopSource?.Dispose();
        _lifetime.Dispose();
        _client.Dispose();
    }

    /// <summary>
    /// 結束時還在佇列裡、使用者已經做的選擇（靜音、強制納入、完成時通知我）直接寫進 DB，不因為迴圈來不及處理而遺失；
    /// 其他指令只影響這次執行，丟掉沒關係。迴圈與使用者操作都已經停下，這裡只有一條執行緒。
    /// 「完成時通知我」照平常的規則（<see cref="CanWatch"/>）判斷，已經完成的 run 不寫，免得留下永遠清不掉的標記。
    /// </summary>
    private void PersistPendingChoices()
    {
        while (_commands.TryDequeue(out var command))
        {
            try
            {
                switch (command)
                {
                    case EngineCommand.SetOverride(var repoId, var value) when value is not null:
                        _cache.SetOverride(repoId, value);
                        break;

                    case EngineCommand.SetWatched(var runId, var watched) when !watched || CanWatch(runId):
                        _cache.SetWatched(runId, watched);
                        break;
                }
            }
            catch (Exception ex)
            {
                _deps.ErrorLog.Write("結束前寫入指令 " + command.GetType().Name, ex);
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Wake() => Volatile.Read(ref _wake).TrySetResult();

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>已經開始釋放或呼叫端取消時，以取消結束。</summary>
    private void ThrowIfStopping(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new OperationCanceledException(ct);
        }
    }

    // ── 迴圈 ──────────────────────────────────────────────────────────

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 先換上新的喚醒訊號再做這一步：步驟進行中收到的 tick 或指令會讓下面的等待立刻結束。
            var signal = NewSignal();
            Volatile.Write(ref _wake, signal);

            int sent;
            TimeSpan? nextWake;
            try
            {
                (sent, nextWake) = await StepAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // 停止中（DisposeAsync 或 StartAsync 的 token 取消）：不當成錯誤。
                return;
            }
            catch (Exception ex)
            {
                // StepAsync 自己會攔下每一步的例外；這裡只是最後一道防線，迴圈不能停（spec §15）。
                ReportUnexpected("監看迴圈", ex);
                (sent, nextWake) = (0, Tuning.EngineTick);
            }

            if (sent > 0)
            {
                continue;
            }

            try
            {
                await WaitForWakeAsync(signal, nextWake, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ReportUnexpected("監看迴圈", ex);
            }
        }
    }

    /// <summary>等喚醒訊號或 TimeProvider 的延遲，取最早者；<paramref name="delay"/> 為 null 代表只等喚醒。</summary>
    private async Task WaitForWakeAsync(TaskCompletionSource signal, TimeSpan? delay, CancellationToken ct)
    {
        if (delay is { } wait && wait <= TimeSpan.Zero)
        {
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = delay is { } due
            ? Task.Delay(due, _time, stop.Token)
            : Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        await Task.WhenAny(signal.Task, timer).ConfigureAwait(false);

        // 先醒來的是訊號時，取消延遲以釋放計時器。
        await stop.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>迴圈的一步（spec §3.1）：指令 → 睡眠偵測 → 送一個到期請求並處理 → 清理 → 提示 → 命令列刷新的完成判斷 → 快照與狀態表。</summary>
    /// <returns>送出的請求數，與迴圈下一次最晚要在多久後醒來（null 代表只等喚醒訊號）。</returns>
    private async Task<(int Sent, TimeSpan? NextWake)> StepAsync(CancellationToken ct)
    {
        await _stepGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            var sent = 0;
            try
            {
                var rebuilt = RecoverDatabaseIfNeeded();

                // 步驟開頭改名重建後，寫回或重讀時新的資料庫又讀到損毀：這一步不送請求，交給這一步結尾再改名重建一次；
                // 連續重建已達 RebuildsBeforeBackoff 次時，結尾那一次改排退避，等到重試的時間才再改名重建（spec §15）。
                if (!(rebuilt && Volatile.Read(ref _databaseCorrupt) == 1) && await EnsureInitializedAsync(ct).ConfigureAwait(false))
                {
                    ApplyUserEffects();
                    await ProcessCommandsAsync(ct).ConfigureAwait(false);
                    DetectSleepGap();
                    await MaintainAuthAsync(ct).ConfigureAwait(false);
                    CleanupIfDue();
                    EvaluateUpdateCheck();
                    sent = await SendNextAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 單一步失敗不能讓迴圈停掉（spec §15）。
                ReportUnexpected("監看迴圈", ex);
            }

            // 這一步讀寫 DB 時發現損毀：馬上改名重建，這一步的快照就帶著復原的提示（spec §15）。
            // 改名重建還在退避中時等到重試的時間；連續重建已達 RebuildsBeforeBackoff 次時改排退避，不管損毀是在這一步的哪裡讀到的。
            try
            {
                RecoverDatabaseIfNeeded();
            }
            catch (Exception ex)
            {
                ReportUnexpected("重建快取資料庫", ex);
            }

            NotePauseEnded();
            FlushToasts();
            CheckCliRefresh();
            PublishSnapshot();
            return (sent, NextWakeDelay());
        }
        finally
        {
            _stepGate.Release();
        }
    }

    /// <summary>
    /// 第一次執行時：讀 DB、發布快取快照、取得 token。取得 token 之後的 E1 驗證由第一個步驟送出（經過節流器）。
    /// 讀 DB 失敗時不往外丟（spec §15）：損毀就改名重建後再讀，其他錯誤依退避稍後再試。
    /// </summary>
    /// <returns>初始化已經完成時回 true。</returns>
    private async Task<bool> EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return true;
        }

        if (_initRetryAt is { } retryAt && _time.GetElapsedTime(retryAt) < TimeSpan.Zero)
        {
            return false;
        }

        if (!TryLoadDatabase())
        {
            // 和單一目標的錯誤一樣退避（spec §6.4），不會每一秒都重試一次、寫一筆 error.log。
            _initRetryDelay = IntervalPolicy.ErrorBackoff(Tuning.EngineTick, _initRetryDelay, IntervalPolicy.StretchFactor(null));
            _initRetryAt = FutureTimestamp(_initRetryDelay);
            return false;
        }

        _initRetryAt = null;
        _initRetryDelay = TimeSpan.Zero;
        PublishSnapshot();

        try
        {
            await _auth.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 取 token 失敗時 AuthManager 已經是未登入；橫幅與角標照常顯示。
            ReportUnexpected("取得 token", ex);
        }

        _needsVerify = _auth.Token is not null;
        _lastAuthRecheckAt = _time.GetTimestamp();
        _initialized = true;
        return true;
    }

    /// <summary>讀 DB；讀到損毀就改名重建後再讀一次（spec §15）。</summary>
    private bool TryLoadDatabase()
    {
        try
        {
            LoadFromDatabase();
            return true;
        }
        catch (Exception ex)
        {
            ReportUnexpected("讀取快取資料庫", ex);
        }

        if (!RecoverDatabaseIfNeeded())
        {
            return false;
        }

        try
        {
            LoadFromDatabase();
            return true;
        }
        catch (Exception ex)
        {
            ReportUnexpected("讀取快取資料庫", ex);
            return false;
        }
    }

    /// <summary>
    /// 有工作回報 cache.db 損毀時：舊檔改名保留或丟棄、重建空白資料庫，換上 §10.2 的提示（spec §14.3、§15）。
    /// DatabaseInitializer.Ensure 啟動時不做完整檢查，所以內容的損毀要到讀寫時才會發現（handoff Task 8）。
    /// </summary>
    /// <remarks>
    /// 留檔規則：第一次損毀一定改名保留。持續損毀的重試，這份檔是這個程序自己重建的、之後沒設過靜音或強制納入（<see cref="_databaseMayHoldSettings"/>）、
    /// 磁碟上至少已有 1 份編號備份、當下也讀不到 repo_overrides 的資料列時，先寫 error.log 再直接刪掉（連同附屬檔）；其他情況一律改名保留，
    /// 編號備份滿額時存到 latest 並覆寫（<see cref="DatabaseInitializer.SetAside"/>）。丟棄也算一次重建，下面的退避與連續次數都一樣。
    /// 暫停時不走別的路：重試不再留檔，照常重試就好，儲存層恢復後最晚 10 分鐘自動恢復，不必解除暫停或重開 App。
    /// 這個檔案其他地方說的「改名重建」都包含丟棄這條路。
    /// 改名重建失敗時（例如另一個程式開著 cache.db）損毀的旗標留著，和初始化一樣退避後再試（從 EngineTick 起倍增，上限 RepoErrorBackoffMax × k）。
    /// 新的資料庫又讀到損毀（例如儲存層壞掉）也算進退避，不管是在寫回、重讀、寫狀態表還是步驟主體讀到的：連續改名重建未達 <see cref="RebuildsBeforeBackoff"/> 次時立即再重建，
    /// 達到之後這一次不重建、旗標留著，同樣退避，間隔接著倍增；到了重試的時間照常重建一次。
    /// 兩次重建相隔不到 <see cref="Tuning.RebuildStreakWindow"/> 才算連續（比退避上限長，到上限之後的重試仍算連續；連續中改名失敗的重試也算，視窗從失敗那次起算）；超過時次數與間隔才重新起算，
    /// 所以儲存層恢復之後，還在視窗內又讀到損毀會直接退避。不用「新的資料庫正常」當歸零的條件：撐過一個步驟可能只是那一步沒有碰到壞掉的地方。
    /// 退避期間不重試、不喚醒迴圈，快照沿用上一份的內容（<see cref="PublishSnapshot"/>），不會每一步都重試、都寫 error.log。
    /// </remarks>
    /// <returns>這一次真的重建了資料庫時回 true。</returns>
    private bool RecoverDatabaseIfNeeded()
    {
        if (Volatile.Read(ref _databaseCorrupt) == 0 || RecoveryWaiting())
        {
            return false;
        }

        if (Volatile.Read(ref _recoverRetryAt) == NoRecoveryRetry && _rebuildStreak >= RebuildsBeforeBackoff && RebuiltWithinStreakWindow())
        {
            // 連續重建之後又讀到損毀（例如儲存層壞掉）：和改名重建失敗一樣退避，不會每一步或每個請求都重建一次。
            _recoverRetryDelay = IntervalPolicy.ErrorBackoff(Tuning.EngineTick, _recoverRetryDelay, IntervalPolicy.StretchFactor(null));
            Volatile.Write(ref _recoverRetryAt, FutureTimestamp(_recoverRetryDelay));
            return false;
        }

        Interlocked.Exchange(ref _databaseCorrupt, 0);
        var databasePath = _deps.Paths.CacheDbPath;
        try
        {
            if (!_databaseMayHoldSettings && DatabaseInitializer.IsDiscardable(databasePath))
            {
                LogDatabaseDiscard(databasePath);
                DatabaseInitializer.Discard(databasePath);
            }
            else if (DatabaseInitializer.SetAside(databasePath, _time) is { } backupPath)
            {
                // 在 Rebuild 之前記好：Rebuild 失敗時這份已經留下了，之後的重試成功時橫幅要指向它。
                _lastKeptBackupName = Path.GetFileName(backupPath);
            }

            // 舊檔已離開這個路徑，之後這個路徑上的檔都是這個程序建的。在 Rebuild 之前設：
            // Rebuild 失敗時下一次重試不會把半成品再佔一個名額；SetAside 或 Discard 失敗時走不到這裡，旗標不變（SetAside 最後才搬主檔，失敗時舊檔還在）。
            _databaseMayHoldSettings = false;
            DatabaseInitializer.Rebuild(databasePath);
        }
        catch (Exception ex)
        {
            // 連續中改名失敗（例如另一個程式開著 cache.db）：視窗從這次失敗起算，下一次重建成功時仍算連續，次數與間隔不會因為隔了失敗的重試而歸零。
            if (RebuiltWithinStreakWindow())
            {
                _lastRebuildAt = _time.GetTimestamp();
            }
            else if (Volatile.Read(ref _recoverRetryAt) == NoRecoveryRetry)
            {
                // 不是在重試、上一次重建也已超過視窗：這是新的一段，不沿用上一段留下的間隔。
                _recoverRetryDelay = TimeSpan.Zero;
            }

            // 先排好下一次重試再回報：回報時就知道正在退避，不會立刻喚醒迴圈。
            _recoverRetryDelay = IntervalPolicy.ErrorBackoff(Tuning.EngineTick, _recoverRetryDelay, IntervalPolicy.StretchFactor(null));
            Volatile.Write(ref _recoverRetryAt, FutureTimestamp(_recoverRetryDelay));
            Interlocked.Exchange(ref _databaseCorrupt, 1);
            ReportUnexpected("重建快取資料庫", ex);
            return false;
        }

        Volatile.Write(ref _recoverRetryAt, NoRecoveryRetry);

        // 在 Reset 之前記好：Reset 裡丟出例外時，這一次重建也算進連續的次數。
        if (RebuiltWithinStreakWindow())
        {
            _rebuildStreak++;
        }
        else
        {
            _rebuildStreak = 1;
            _recoverRetryDelay = TimeSpan.Zero;
        }

        _lastRebuildAt = _time.GetTimestamp();
        Interlocked.Increment(ref _databaseRebuildCount);

        // 丟棄時仍然指向這個程序最後留下的那份（檔案還在，文字是真的）；一份都沒留過只有主檔已經不在的情況。每次重建都換一則新的。
        var keptBackupName = _lastKeptBackupName;
        SetNotice(ref _databaseNotice, BannerKind.DatabaseRecovered, language => UiText.DatabaseRebuilt(keptBackupName, language));
        if (_initialized)
        {
            ResetAfterDatabaseRecovery();
        }

        return true;
    }

    /// <summary>刪掉資料庫之前留下紀錄，事後查得到這份檔不是不見，是程式刪的。同一種情況重試幾次就寫幾筆（ErrorLog 只有橫幅去重）。</summary>
    private void LogDatabaseDiscard(string databasePath)
    {
        var file = new FileInfo(databasePath);
        var sizeBytes = file.Exists ? file.Length : 0;
        _deps.ErrorLog.WriteMessage(DatabaseDiscardedContext, EngineText.DatabaseDiscardedLog(sizeBytes, DatabaseInitializer.CountBackups(databasePath)));
    }

    /// <summary>改名重建失敗或重建後仍損毀之後還在退避中：cache.db 仍是損毀的，還沒到下一次重試的時間。</summary>
    private bool RecoveryWaiting()
    {
        var retryAt = Volatile.Read(ref _recoverRetryAt);
        return retryAt != NoRecoveryRetry && _time.GetElapsedTime(retryAt) < TimeSpan.Zero;
    }

    /// <summary>上一次改名重建（連續中改名失敗的重試也算）還在 <see cref="Tuning.RebuildStreakWindow"/> 內：這次的損毀算連續。</summary>
    private bool RebuiltWithinStreakWindow() =>
        _lastRebuildAt is { } last && _time.GetElapsedTime(last) < Tuning.RebuildStreakWindow;

    /// <summary>
    /// 套用使用者操作交給步驟的改變。每一個各自 try/catch：其中一個失敗時其他照樣套用，
    /// 寫入的「發布時釋放」不會因為排在後面而一直留著（第二階段 spec §5.3）。
    /// </summary>
    private void ApplyUserEffects()
    {
        while (_userEffects.TryDequeue(out var effect))
        {
            try
            {
                effect();
            }
            catch (Exception ex)
            {
                ReportUnexpected("套用使用者操作的結果", ex);
            }
        }
    }

    /// <summary>tick 間隔顯示剛從睡眠醒來：當成 Resume（spec §6.5）。</summary>
    private void DetectSleepGap()
    {
        if (Interlocked.Exchange(ref _sleepGapDetected, 0) == 0)
        {
            return;
        }

        _suspended = false;
        _updateEvalRequested = true;
        FullRefresh();
    }

    /// <summary>每 CleanupInterval 清理一次舊 run（spec §14.3）；啟動後第一步就先清一次。</summary>
    private void CleanupIfDue()
    {
        var now = _time.GetTimestamp();
        if (_lastCleanupAt is { } last && _time.GetElapsedTime(last, now) < Tuning.CleanupInterval)
        {
            return;
        }

        _lastCleanupAt = now;
        _cache.Cleanup(_time.GetUtcNow(), ProtectedRunIds());
        foreach (var repoId in _data.Keys.ToList())
        {
            Invalidate(repoId);
        }

        // 已經不在排程裡的目標（完成的 E6、做完的補抓）不再需要「上次送出／上次間隔」，長時間執行也不累積。
        var scheduled = _scheduler.Scheduled.ToHashSet();
        foreach (var target in _lastSent.Keys.Where(target => !scheduled.Contains(target)).ToList())
        {
            _lastSent.Remove(target);
        }

        foreach (var target in _lastDelay.Keys.Where(target => !scheduled.Contains(target)).ToList())
        {
            _lastDelay.Remove(target);
        }

        _errorBackoff.RemoveWhere(target => !scheduled.Contains(target));
        _unanswered.RemoveWhere(target => !scheduled.Contains(target));
    }

    /// <summary>
    /// 迴圈最晚多久後要醒來：下一個可送的請求、節流器放行、提示合併視窗、未登入的重新檢查、清理、檢查新版本的每小時判斷、初始化重試、資料庫重建重試、
    /// 狀態表的心跳（或寫入失敗後的重試）、命令列刷新的上限（第二階段 spec §10.2、§10.5）。
    /// </summary>
    private TimeSpan? NextWakeDelay()
    {
        var candidates = new List<TimeSpan>();
        if (SendWait() is { } send)
        {
            candidates.Add(send);
        }

        if (_toastPendingSince is { } pendingSince && !_locked)
        {
            var remaining = Tuning.NotificationMergeWindow - _time.GetElapsedTime(pendingSince);
            candidates.Add(remaining > TimeSpan.Zero ? remaining : Tuning.EngineTick);
        }

        if (_auth.Status == AuthStatus.Unauthorized && !_needsVerify && _settings.AuthMode == AuthMode.GhCli)
        {
            candidates.Add(Remaining(_lastAuthRecheckAt, Tuning.AuthRecheckInterval));
        }

        if (_lastCleanupAt is { } cleanup)
        {
            candidates.Add(Remaining(cleanup, Tuning.CleanupInterval));
        }

        if (UpdateCheckEnabled && _updateLastEvalAt is { } updateEval)
        {
            candidates.Add(Remaining(updateEval, Tuning.UpdateCheckLocalInterval));
        }

        if (_throttle.PauseRemaining is { } pause)
        {
            candidates.Add(pause);
        }

        if (!_initialized && _initRetryAt is { } retryAt)
        {
            candidates.Add(Remaining(retryAt, TimeSpan.Zero));
        }

        if (Volatile.Read(ref _recoverRetryAt) is var recoverAt && recoverAt != NoRecoveryRetry)
        {
            candidates.Add(Remaining(recoverAt, TimeSpan.Zero));
        }

        if (StatusWakeDelay() is { } heartbeat)
        {
            candidates.Add(heartbeat);
        }

        if (CliRefreshWakeDelay() is { } refreshMax)
        {
            candidates.Add(refreshMax);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // 最久等到下一次清理：Task.Delay 不接受超過約 49 天的延遲，間隔算到溢位（TimeSpan.MaxValue）時也不會讓迴圈出錯。
        var earliest = candidates.Min();
        return earliest < Tuning.CleanupInterval ? earliest : Tuning.CleanupInterval;
    }

    /// <summary>下一個請求最快多久後可以送；目前不會送任何請求時回 null。</summary>
    private TimeSpan? SendWait()
    {
        if (_versionError || _suspended || !_initialized)
        {
            return null;
        }

        // 使用者操作正在等節流器：輪詢讓它先送（SendNextAsync 不會送），等它送出時的喚醒（WaitForUserSlotAsync），不空轉。
        if (Volatile.Read(ref _userWaiting) > 0)
        {
            return null;
        }

        TimeSpan? due;
        if (_offline.IsOffline)
        {
            due = _scheduler.TimeUntilDue(OfflineProbeTarget);
        }
        else if (_needsVerify)
        {
            due = _verifyAt is { } at ? Remaining(at, TimeSpan.Zero) : TimeSpan.Zero;
        }
        else if (_auth.Status != AuthStatus.Ok)
        {
            return null;
        }
        else
        {
            due = _startupRateLimitPending || _recheckPending || UpdateCheckReady ? TimeSpan.Zero : _scheduler.TimeUntilNextDue();
        }

        if (due is not { } dueWait)
        {
            return null;
        }

        var allowed = _throttle.TimeUntilAllowed();
        return allowed is { } throttleWait && throttleWait > dueWait ? throttleWait : dueWait;
    }

    /// <summary>從 <paramref name="since"/> 起算經過 <paramref name="interval"/> 還要多久；已經過了回 0。</summary>
    private TimeSpan Remaining(long since, TimeSpan interval)
    {
        var remaining = interval - _time.GetElapsedTime(since);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    // ── 事件 ──────────────────────────────────────────────────────────

    /// <remarks>
    /// 「發布時釋放」的送出中（第二階段 spec §5.3 第 2 點）只在 <see cref="BuildSnapshot"/> 組出並發布的那一次移除：
    /// 那份快照已經套用過寫入的結果（當成不在送出中組出，見 <see cref="ActionContextFor"/>），而且先換上它的查表才移除，
    /// 使用者操作不會看到「舊的查表、又不在送出中」。沿用上一份內容的快照（CarryOver、MemorySnapshot）與建立失敗時不移除，
    /// 留到下一次成功的 BuildSnapshot。
    /// </remarks>
    private void PublishSnapshot()
    {
        MonitorSnapshot snapshot;
        IReadOnlyDictionary<long, RunActions> runStates;
        var built = false;
        if (Volatile.Read(ref _recoverRetryAt) != NoRecoveryRetry)
        {
            // 改名重建失敗或重建後仍損毀、還在等重試：不讀 DB（讀了只會再遇到損毀、再寫 error.log），
            // 沿用上一份快照的內容、換上目前的橫幅與連線狀態，畫面才看得到提示（spec §15）。
            // 還沒有任何一份快照（例如啟動時第一份快照就讀到損毀）時，只用記憶體組一份。
            // run 狀態查表跟著快照的 Actions：沿用時沿用，只用記憶體組時清空。
            if (_lastSnapshot is { } last)
            {
                snapshot = CarryOver(last);
                runStates = _runStateLookup;
            }
            else
            {
                snapshot = MemorySnapshot();
                runStates = FrozenDictionary<long, RunActions>.Empty;
            }
        }
        else
        {
            try
            {
                (snapshot, runStates) = BuildSnapshot();
                built = true;
            }
            catch (Exception ex)
            {
                ReportUnexpected("建立快照", ex);
                return;
            }
        }

        // 只有真的發布出去的快照才加 1（建立失敗的不算），CarryOver 沿用上一份的值也在這裡換掉。
        snapshot = snapshot with { Sequence = Interlocked.Increment(ref _snapshotSequence) };
        _runStateLookup = runStates;
        _actionLookup = snapshot.Actions;
        if (built)
        {
            ReleasePublishedWrites();
        }

        _lastSnapshot = snapshot;
        lock (_bannerGate)
        {
            _publishWindow++;
        }

        OnSnapshot(snapshot);

        // 給命令列讀的狀態表（第二階段 spec §10.2）；沿用上一份內容的那段（等重建重試）不寫，WriteStatus 自己判斷。
        UpdateStatusTable(snapshot);
    }

    private void FlushToasts()
    {
        if (!UpdateCheckEnabled)
        {
            CancelUpdateCheck();
        }

        var settings = _settings;
        ToastContent? content;
        try
        {
            content = _toasts.TryFlush(
                TimeSpan.FromSeconds(settings.FailureToastSeconds),
                TimeSpan.FromSeconds(settings.SuccessToastSeconds),
                settings.Language);
        }
        catch (Exception ex)
        {
            ReportUnexpected("組合提示", ex);
            return;
        }

        if (content is not null)
        {
            _toastPendingSince = null;
            OnToast(content);
            OnUpdateNoticeFlushed();
        }
        else if (_toastPendingSince is { } since && !_locked && _time.GetElapsedTime(since) >= Tuning.NotificationMergeWindow)
        {
            // 合併視窗已過卻沒有內容：只是重複加入已顯示的通知，不必再醒來。
            _toastPendingSince = null;
        }
    }

    /// <summary>逐一呼叫每個處理常式：其中一個丟例外，其他照樣收到（spec §15）。</summary>
    private void OnSnapshot(MonitorSnapshot snapshot) => Raise(SnapshotPublished, snapshot, "SnapshotPublished 事件處理");

    private void OnToast(ToastContent content) => Raise(ToastReady, content, "ToastReady 事件處理");

    private void Raise<T>(Action<T>? handlers, T value, string context)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(value);
            }
            catch (Exception ex)
            {
                ReportUnexpected(context, ex);
            }
        }
    }

    /// <summary>寫 error.log；ErrorLog.Write 回 true（這種例外 10 分鐘內第一次）時才換上新的一則提示（spec §10.2）。</summary>
    /// <remarks>
    /// cache.db 損毀不提示未預期錯誤：記下來交給下一次 <see cref="RecoverDatabaseIfNeeded"/> 改名重建（什麼時候重建見 <see cref="_databaseCorrupt"/>），
    /// 改顯示「快取資料庫已復原」（spec §15）。
    /// 改名重建正在退避中時不喚醒迴圈：到了重試的時間迴圈自己會醒來（<see cref="NextWakeDelay"/>），不會空轉。
    /// </remarks>
    private void ReportUnexpected(string context, Exception exception)
    {
        var firstInWindow = _deps.ErrorLog.Write(context, exception);
        if (IsDatabaseCorruption(exception))
        {
            Interlocked.Exchange(ref _databaseCorrupt, 1);
            if (!RecoveryWaiting())
            {
                Wake();
            }

            return;
        }

        if (firstInWindow)
        {
            SetNotice(ref _unexpectedNotice, BannerKind.UnexpectedError, EngineText.UnexpectedError);
        }
    }

    /// <summary>例外本身或它包著的例外是 SQLite 的損毀（SQLITE_CORRUPT／SQLITE_NOTADB，同 DatabaseInitializer 的判斷）。</summary>
    private static bool IsDatabaseCorruption(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite && DatabaseInitializer.IsCorruption(sqlite))
            {
                return true;
            }
        }

        return false;
    }
}
