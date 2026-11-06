namespace IsItGreenYet.Core;

/// <summary>
/// 所有可調的數值集中在這裡（spec 附錄 B；第二階段新增的見第二階段 spec 附錄 A），程式其他地方不寫 magic number。
/// 「需求」「官方」標示來源；「設計選擇」是自訂值，可以改。
/// </summary>
public static class Tuning
{
    // ── API ───────────────────────────────────────────────────────────
    public const string ApiBaseUrl = "https://api.github.com";
    public const string ApiHost = "api.github.com";
    public const string WebHost = "github.com";

    /// <summary>固定送出的 X-GitHub-Api-Version（spec §4.1）。</summary>
    public const string ApiVersion = "2026-03-10";

    /// <summary>Windows 認證管理員中 PAT 的目標名稱。</summary>
    public const string CredentialTarget = "IsItGreenYet:github.com";

    /// <summary>設計選擇：要大於 GitHub 伺服器端的 10 秒逾時。</summary>
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    /// <summary>設計選擇：job log 可能有數 MB。</summary>
    public static readonly TimeSpan LogDownloadTimeout = TimeSpan.FromSeconds(120);

    public const int MaxRedirects = 3;

    /// <summary>官方預設值；PR 分支補抓也用這個值。</summary>
    public const int RunsPageSize = 30;

    /// <summary>官方上限。</summary>
    public const int ListPageSize = 100;

    /// <summary>設計選擇：排除 fork 同名分支與 PR 事件的 run 後仍有餘裕。</summary>
    public const int SeedPageSize = 5;

    /// <summary>需求範例 <c>Commit dcf27ad</c>。</summary>
    public const int ShortShaLength = 7;

    // ── 輪詢間隔（spec §6.2）────────────────────────────────────────────
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ActiveRepoInterval = TimeSpan.FromSeconds(15);
    public const double QuietDivisor = 30;
    public static readonly TimeSpan QuietMin = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan QuietMax = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan LockedMinInterval = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan RepoListRefresh = TimeSpan.FromSeconds(3600);
    public static readonly TimeSpan WorkflowsRefresh = TimeSpan.FromSeconds(3600);
    public static readonly TimeSpan PullListTtl = TimeSpan.FromSeconds(1800);
    public static readonly TimeSpan PullListTtlActive = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan SeedRecheck = TimeSpan.FromSeconds(3600);
    public const int StaleRepoDays = 90;

    // ── 節流（spec §6.3）───────────────────────────────────────────────
    public const int MaxConcurrentPolling = 1;
    public const int MaxConcurrentUser = 1;
    public const int MaxConcurrentRequests = MaxConcurrentPolling + MaxConcurrentUser;
    public const int MaxRequestsPerMinute = 60;
    public static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PriorityAgingStep = TimeSpan.FromSeconds(60);

    // ── 額度與退避（spec §6.4）─────────────────────────────────────────
    public const double LowQuotaRatio = 0.25;
    public static readonly TimeSpan QuotaSampleWindow = TimeSpan.FromSeconds(60);
    public const double StretchMin = 2;
    public const double StretchMax = 8;
    public static readonly TimeSpan RateLimitResetMargin = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MinRateLimitWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 設計選擇：全域暫停的上限，1 小時的額度視窗加 10 分鐘餘裕。reset 或 retry-after 異常大時才用得到：
    /// 到期後多送一個請求，還在限流就再暫停。規格 §6.4 第 1 列沒有上限（docs/spec-deviations.md）。
    /// </summary>
    public static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromMinutes(70);

    public static readonly TimeSpan SecondaryBackoffInitial = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SecondaryBackoffMax = TimeSpan.FromSeconds(960);
    public const int SecondaryBackoffPersistentLevel = 5;
    public static readonly TimeSpan RepoErrorBackoffMax = TimeSpan.FromSeconds(600);

    /// <summary>
    /// 設計選擇：cache.db 兩次改名重建相隔不到這個時間才算連續（<see cref="Engine.MonitorEngine"/> 的改名重建退避）。
    /// 是退避上限（<see cref="RepoErrorBackoffMax"/>，k = 1）的兩倍：退避到上限之後的重試仍算連續，不會把間隔歸零。
    /// 必須排在 <see cref="RepoErrorBackoffMax"/> 之後：靜態欄位依序初始化。
    /// </summary>
    public static readonly TimeSpan RebuildStreakWindow = RepoErrorBackoffMax * 2;

    /// <summary>
    /// 設計選擇：cache.db 損毀時最多留幾份編號備份（<see cref="Data.DatabaseInitializer"/>）；滿了改存固定檔名的 latest 並覆寫，所以總量最多這個值加 1，共 6 份（舊版留下的編號備份也算名額、不會自動清除，所以總量可能更多）。
    /// 每份的大小是當時的 cache.db 加附屬檔。可能有靜音或強制納入設定的資料庫才佔名額；持續損毀時這個程序自己重建、沒有設定的檔直接丟棄，不佔名額。
    /// </summary>
    public const int MaxCorruptBackups = 5;

    public static readonly TimeSpan PermissionRetryInterval = TimeSpan.FromSeconds(3600);
    public static readonly TimeSpan NotFoundRetryInterval = TimeSpan.FromSeconds(3600);
    public const int ForbiddenBurstRepos = 3;
    public static readonly TimeSpan ForbiddenBurstWindow = TimeSpan.FromSeconds(60);
    public const int ServerErrorBadgeThreshold = 3;
    public const int OfflineThreshold = 3;
    public static readonly TimeSpan OfflineProbeInitial = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan OfflineProbeMax = TimeSpan.FromSeconds(300);
    public const int RemovalConfirmRounds = 2;
    public static readonly TimeSpan StaleDataThreshold = TimeSpan.FromSeconds(3600);

    // ── 認證（spec §13）─────────────────────────────────────────────────
    public static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AuthRecheckInterval = TimeSpan.FromSeconds(300);

    // ── 耗時與預估（spec §8）────────────────────────────────────────────
    public const int EtaSampleSize = 10;
    public const int EtaMinSamples = 3;
    public const int DurationSamplesKept = 20;

    // ── 通知（spec §9）─────────────────────────────────────────────────
    public static readonly TimeSpan NotificationMergeWindow = TimeSpan.FromSeconds(2);
    public const int FailureToastSeconds = 12;
    public const int SuccessToastSeconds = 5;
    public const int FailureToastMinSeconds = 3;
    public const int FailureToastMaxSeconds = 120;
    public const int SuccessToastMinSeconds = 2;
    public const int SuccessToastMaxSeconds = 60;
    public const int SummaryToastMaxLines = 5;

    // ── 畫面（spec §10）─────────────────────────────────────────────────
    public static readonly TimeSpan ReorderFreezeMax = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan JustNowThreshold = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RelativeTimeWindow = TimeSpan.FromSeconds(3600);
    public static readonly TimeSpan RelativeTextRefresh = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ElapsedRefresh = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan EngineTick = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SleepGapThreshold = TimeSpan.FromSeconds(120);
    public const int LogTailLines = 200;
    public const int LogTailLinesMin = 20;
    public const int LogTailLinesMax = 5000;
    public const int ClipboardRetries = 3;
    public static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan ErrorBannerDedupWindow = TimeSpan.FromSeconds(600);

    // ── 日誌與資料（spec §14）───────────────────────────────────────────
    public const long ApiLogMaxBytes = 2L * 1024 * 1024;
    public const int ApiLogKeepFiles = 3;
    public const long ErrorLogMaxBytes = 512L * 1024;
    public const int RunRetentionDays = 90;
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(1);
    public const int TrackedFailureMaxAgeDays = 90;

    // ── 視窗（spec §10.1、§11.1）────────────────────────────────────────
    public const double WindowDefaultWidth = 1000;
    public const double WindowDefaultHeight = 640;
    public const double WindowMinWidth = 600;
    public const double WindowMinHeight = 400;
    public const double DialogDefaultWidth = 720;
    public const double DialogDefaultHeight = 560;
    public const double DialogMinWidth = 480;
    public const double DialogMinHeight = 360;
    public const double ResizeBorder = 6;

    /// <summary>
    /// 寬度斷點（spec §10.10）：比的是列內容寬度（清單可用寬度再扣掉每一列的內距與外框），低於這個值就隱藏。
    /// 設計選擇：2026-10-26 換成 Consolas 後用測試殼量兩種語言，取上一輪審查那一列偏長的假資料
    /// （組織 repo、CI #128、Pull request #17、by octocat、第 2 次嘗試、feature/login-重新整理，另有一列進行中、有 ETA）
    /// 在每個狀態不截斷需要的寬度，再多留約一個字（7 DIP）、往上取到 10 的倍數。
    /// 進行中那一列的已執行與剩餘時間取 2 位數的分鐘（Elapsed 12m 34s · ~45m left，時間欄比 1 位數寬 14 DIP）：
    /// 英文較寬，全部 965、隱藏觸發者 874、再隱藏分支 chip 698。原本依 1 位數（951、860、684）取 960／870／690，
    /// 審查回報 2 位數時在斷點之間會截斷。量測數字見 docs/design-notes.md「寬度斷點的量測」。
    /// </summary>
    public const double HideActorBelow = 980;

    /// <inheritdoc cref="HideActorBelow"/>
    public const double HideBranchBelow = 890;

    /// <inheritdoc cref="HideActorBelow"/>
    public const double HideTimeDetailBelow = 710;
    public const int TrayIconSize = 32;

    // ── 寫入與操作後追蹤（第二階段 spec §3.2、§5.3、§6、§7）──────────────

    /// <summary>官方：修改型請求之間至少等 1 秒。只看寫入請求，不影響輪詢（第二階段 spec §3.2）。</summary>
    public static readonly TimeSpan MutativeRequestSpacing = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 設計選擇：等使用者操作那一條空出來、加上等節流器放行超過這麼久，就放棄並回「未送出：排隊太久」（第二階段 spec §5.3）。
    /// 按下去之後一直顯示「正在送出…」，等更久不如放棄讓你重按。
    /// </summary>
    public static readonly TimeSpan MutativeMaxQueueWait = TimeSpan.FromSeconds(15);

    /// <summary>官方：首次執行後 30 天內才能重跑；從 created_at 起算（第二階段 spec §7.3）。</summary>
    public const int RerunWindowDays = 30;

    /// <summary>官方：一個 run 最多重跑 50 次；停用條件是 run_attempt ≥ MaxReruns + 1（第二階段 spec §7.3）。</summary>
    public const int MaxReruns = 50;

    /// <summary>
    /// 官方的取消逾時：取消受理超過這麼久仍未完成，才出現強制取消；取消的追蹤間隔也從這時起放慢（第二階段 spec §6.2、§6.4）。
    /// </summary>
    public static readonly TimeSpan CancelGraceBeforeForce = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 設計選擇：重跑送出後超過這麼久，而且已收到至少 <see cref="RerunConfirmMinResponses"/> 次 E6 回應，
    /// 還沒看到新的 attempt 就放棄確認（第二階段 spec §6.2）。重跑受理後應該很快出現新的 attempt，實測 R1 後再調。
    /// </summary>
    public static readonly TimeSpan RerunConfirmTimeout = TimeSpan.FromMinutes(5);

    /// <summary>設計選擇：只靠時間會在鎖定、離線、全域暫停時誤判放棄（第二階段 spec §6.2）。</summary>
    public const int RerunConfirmMinResponses = 3;

    // ── 狀態表與命令列（第二階段 spec §10）───────────────────────────────

    /// <summary>設計選擇：內容沒變時也每這麼久整列重寫一次狀態表，負擔很小（第二階段 spec §10.2）。</summary>
    public static readonly TimeSpan StatusHeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 設計選擇：<see cref="StatusHeartbeatInterval"/> 的 3 倍，容許偶爾一次寫入失敗；
    /// 心跳比這更舊，命令列就當成 App 沒有在執行或停住了（第二階段 spec §10.6）。
    /// </summary>
    public static readonly TimeSpan StatusHeartbeatStale = TimeSpan.FromSeconds(180);

    /// <summary>
    /// 設計選擇：命令列要求的刷新最多等這麼久就強制收尾、標成部分完成（第二階段 spec §10.5）。
    /// 冷啟動約 64 個以上的請求（spec §6.7），受每分鐘 60 個的上限，約 1～1.5 分鐘送完。
    /// </summary>
    public static readonly TimeSpan RefreshCompletionMax = TimeSpan.FromSeconds(90);

    /// <summary>設計選擇：命令列讀 cache.db 遇到鎖定時的重試上限；Microsoft.Data.Sqlite 預設的 30 秒對命令列太長（第二階段 spec §10.4）。</summary>
    public static readonly TimeSpan CliDbBusyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>設計選擇：<c>--refresh</c> 每隔這麼久讀一次 refresh_done_seq（第二階段 spec §10.5）。</summary>
    public static readonly TimeSpan CliRefreshPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 設計選擇：App 正在刷新時要等兩次刷新，2 × <see cref="RefreshCompletionMax"/> = 180 秒，
    /// 再加 30 秒讓 App 強制收尾的結果來得及寫入（第二階段 spec §10.5）。
    /// </summary>
    public static readonly TimeSpan CliRefreshTimeout = TimeSpan.FromSeconds(210);

    /// <summary>
    /// 單一實例的 Mutex（spec §3.3），App 與命令列共用；Local\ 只在目前的登入工作階段內有效。
    /// 命令列只用 TryOpenExisting 檢查 App 是否在執行，開到後立刻 Dispose，絕不建立（第二階段 spec §10.5）。
    /// </summary>
    public const string SingleInstanceMutexName = @"Local\IsItGreenYet.SingleInstance";

    /// <summary>命令列 <c>--refresh</c> 通知 App 刷新的具名事件（AutoReset），由 App 建立（第二階段 spec §10.5）。</summary>
    public const string RefreshSignalName = @"Local\IsItGreenYet.Refresh";

    // ── 檢查新版本（需求）────────────────────────────────────────────────

    /// <summary>需求：專案自己的 repo，檢查新版本和設定頁的連結都用它。</summary>
    public const string ProjectRepoOwner = "marvin3991";

    /// <inheritdoc cref="ProjectRepoOwner"/>
    public const string ProjectRepoName = "is-it-green-yet";

    /// <inheritdoc cref="ProjectRepoOwner"/>
    public const string ProjectRepoFullName = ProjectRepoOwner + "/" + ProjectRepoName;

    /// <summary>需求：距離上次完成的檢查滿這麼多天才再查。設定頁的文字也用這個數字。</summary>
    public const int UpdateCheckIntervalDays = 3;

    /// <inheritdoc cref="UpdateCheckIntervalDays"/>
    public static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromDays(UpdateCheckIntervalDays);

    /// <summary>需求：執行中每隔這麼久在本機判斷一次是否到期，不連網。</summary>
    public static readonly TimeSpan UpdateCheckLocalInterval = TimeSpan.FromHours(1);

    /// <summary>需求：離線、限流、網路錯誤之後，最快隔這麼久才重試。</summary>
    public static readonly TimeSpan UpdateCheckRetryFloor = TimeSpan.FromHours(1);
}
