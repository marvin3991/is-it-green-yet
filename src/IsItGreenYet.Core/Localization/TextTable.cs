namespace IsItGreenYet.Core.Localization;

/// <summary>
/// 一種語言的全部畫面文字（Core 與 App 共用）。每一項都是 <c>required</c>：兩種語言（<see cref="TextTables"/>）少寫任何一項就編譯不過。
/// </summary>
/// <remarks>
/// <para>含 <c>{0}</c>、<c>{1}</c> 的是格式字串，用 <see cref="UiText.Format"/> 填入；兩種語言的參數個數與順序相同（單元測試檢查）。</para>
/// <para>名稱以 <c>One</c>／<c>Other</c> 結尾的一組是單數與複數（<see cref="UiText.Plural"/>），中文兩項相同。</para>
/// <para>XAML 用 <c>{l:T 名稱}</c> 繫結（App 的 Localization/TExtension.cs），換語言時即時更新；名稱寫錯時單元測試會失敗。</para>
/// <para>繁體中文是 spec 與既有程式的原文，不改寫；只有 api.log／error.log 的內容（除錯用）不在這裡，維持中文。</para>
/// </remarks>
public sealed class TextTable
{
    // ── 共用 ──────────────────────────────────────────────────────────
    public required string CommonClose { get; init; }

    public required string CommonCancel { get; init; }

    public required string CommonSave { get; init; }

    public required string CommonOk { get; init; }

    public required string CommonSearch { get; init; }

    public required string CommonSearchPlaceholder { get; init; }

    public required string CommonSettings { get; init; }

    public required string CommonRepoManager { get; init; }

    public required string CommonOpenFolder { get; init; }

    public required string CommonOpenSettings { get; init; }

    public required string CommonOpenRepoManager { get; init; }

    public required string CommonRecheck { get; init; }

    public required string CommonRetryNow { get; init; }

    public required string CommonClearFilters { get; init; }

    public required string CommonLoading { get; init; }

    public required string CommonNeverUpdated { get; init; }

    /// <summary>{0}：時間。</summary>
    public required string CommonLastUpdated { get; init; }

    /// <summary>{0}：HTTP 狀態碼。</summary>
    public required string CommonStatusCode { get; init; }

    /// <summary>沒有狀態碼（連線層錯誤）時，放在括號裡的說明。</summary>
    public required string CommonConnectionFailed { get; init; }

    /// <summary>{0}：Windows 的錯誤說明；{1}：錯誤碼。</summary>
    public required string CommonErrorCode { get; init; }

    /// <summary>報讀名稱各段之間的分隔。</summary>
    public required string CommonSpeechSeparator { get; init; }

    /// <summary>並列項目之間的分隔（例如「2 失敗、1 成功」）。</summary>
    public required string CommonListSeparator { get; init; }

    public required string CommonUnexpectedError { get; init; }

    public required string CommonFolderFailed { get; init; }

    public required string CommonUnauthorizedFromEnvironment { get; init; }

    // ── run 的顯示狀態（spec §7.1）─────────────────────────────────────
    public required string RunQueued { get; init; }

    public required string RunInProgress { get; init; }

    public required string RunWaiting { get; init; }

    public required string RunSucceeded { get; init; }

    public required string RunFailed { get; init; }

    public required string RunTimedOut { get; init; }

    public required string RunStartupFailure { get; init; }

    public required string RunCancelled { get; init; }

    public required string RunSkipped { get; init; }

    public required string RunNeutral { get; init; }

    public required string RunStale { get; init; }

    public required string RunUnknown { get; init; }

    /// <summary>{0}：API 回傳的原字串。</summary>
    public required string RunUnknownValue { get; init; }

    // ── 開始時間、耗時與觸發說明（spec §8、§10.6、§10.8）─────────────────
    public required string TimeJustNow { get; init; }

    /// <summary>{0}：分鐘數。</summary>
    public required string TimeMinutesAgo { get; init; }

    /// <summary>{0}：<c>HH:mm</c>。</summary>
    public required string TimeToday { get; init; }

    /// <summary>今年的開始時間（DateTimeOffset 的自訂格式，InvariantCulture）。</summary>
    public required string TimeThisYearFormat { get; init; }

    /// <summary>更早的開始時間（DateTimeOffset 的自訂格式，InvariantCulture）。</summary>
    public required string TimeOlderFormat { get; init; }

    public required string DurationLessThanMinute { get; init; }

    /// <summary>{0}：無條件進位的分鐘數。</summary>
    public required string DurationApprox { get; init; }

    /// <summary>{0}：已執行時間（例如 <c>4m 12s</c>）。</summary>
    public required string DurationElapsed { get; init; }

    /// <summary>{0}：無條件進位的分鐘數。</summary>
    public required string DurationRemaining { get; init; }

    public required string DurationRemainingUnderMinute { get; init; }

    /// <summary>{0}：<see cref="DurationApprox"/> 或 <see cref="DurationLessThanMinute"/>。</summary>
    public required string DurationOverrun { get; init; }

    /// <summary>{0}：排隊時間。</summary>
    public required string DurationQueued { get; init; }

    public required string TriggerSchedule { get; init; }

    public required string TriggerManual { get; init; }

    // ── 系統匣的 tooltip 與選單（spec §7.4、§12）────────────────────────
    /// <summary>{0}：進行中；{1}：失敗；{2}：更新時間（<see cref="TrayUpdatedAt"/> 或 <see cref="CommonNeverUpdated"/>）。</summary>
    public required string TrayCounts { get; init; }

    /// <summary>{0}：<c>HH:mm</c>。</summary>
    public required string TrayUpdatedAt { get; init; }

    public required string TrayIssueUnauthorized { get; init; }

    public required string TrayIssueOffline { get; init; }

    public required string TrayIssueServerErrors { get; init; }

    public required string TrayIssueVersionError { get; init; }

    /// <summary>{0}：預定重試的時間。</summary>
    public required string TrayIssueRateLimited { get; init; }

    /// <summary>{0}：repo 數（單數）。tooltip 與摘要列共用。</summary>
    public required string StaleReposOne { get; init; }

    /// <summary>{0}：repo 數（複數）。</summary>
    public required string StaleReposOther { get; init; }

    public required string TrayOpen { get; init; }

    public required string TrayRefresh { get; init; }

    public required string TrayPause { get; init; }

    public required string TrayResume { get; init; }

    public required string TrayExit { get; init; }

    // ── 提示視窗（spec §9.4）──────────────────────────────────────────
    /// <summary>{0}：顯示狀態；{1}：repo；{2}：workflow 與 run 編號。</summary>
    public required string ToastHeading { get; init; }

    /// <summary>{0}：筆數（彙總一定至少 2 筆）；{1}：各結論的數量。</summary>
    public required string ToastSummary { get; init; }

    /// <summary>{0}：各結論的數量。</summary>
    public required string ToastLocked { get; init; }

    /// <summary>{0}：沒列出的筆數。</summary>
    public required string ToastMore { get; init; }

    public required string ToastCountFailed { get; init; }

    public required string ToastCountSucceeded { get; init; }

    public required string ToastCountOthers { get; init; }

    public required string ToastAppIcon { get; init; }

    /// <summary>只有新版本時的標題。{0}：版本號，不含 v。</summary>
    public required string ToastUpdateAvailable { get; init; }

    /// <summary>只有新版本時的內文。</summary>
    public required string ToastUpdateOpen { get; init; }

    /// <summary>新版本和 run 合併時的那一行。{0}：版本號，不含 v。提示的行不換行、超寬就截斷（Size.Toast.MaxWidth），所以要短。</summary>
    public required string ToastUpdateLine { get; init; }

    // ── repo 的略過原因與問題標示（spec §5.2、§5.4、§6.4）────────────────
    public required string SkipMuted { get; init; }

    public required string SkipArchived { get; init; }

    public required string SkipDisabled { get; init; }

    public required string SkipFork { get; init; }

    public required string SkipCollaborator { get; init; }

    /// <summary>{0}：天數。</summary>
    public required string SkipStale { get; init; }

    public required string SkipNoWorkflows { get; init; }

    public required string SkipAllWorkflowsDisabled { get; init; }

    public required string ProblemActionsNotFound { get; init; }

    public required string ProblemPullRequestsUnavailable { get; init; }

    public required string ProblemInsufficientPermission { get; init; }

    /// <summary>{0}：缺的權限（X-Accepted-GitHub-Permissions 或 X-Accepted-OAuth-Scopes 的原文）。</summary>
    public required string ProblemInsufficientPermissionNeeds { get; init; }

    public required string ProblemAccessDenied { get; init; }

    /// <summary>{0}：GitHub 回應的 message 原文。</summary>
    public required string ProblemAccessDeniedMessage { get; init; }

    /// <summary>同一個 repo 有多個問題時的分隔。</summary>
    public required string ProblemSeparator { get; init; }

    // ── 橫幅（spec §10.2）────────────────────────────────────────────
    public required string BannerUnauthorized { get; init; }

    public required string BannerVersionError { get; init; }

    public required string BannerDeprecated { get; init; }

    /// <summary>{0}：預計恢復的時間。</summary>
    public required string BannerPrimaryLimit { get; init; }

    /// <summary>{0}：恢復的時間。</summary>
    public required string BannerSecondaryRetryAfter { get; init; }

    /// <summary>{0}：退避等級（第一次限流是 1，安靜一段時間會降回來；中文寫成「第幾次」）；{1}：預定重試的時間。</summary>
    public required string BannerSecondary { get; init; }

    /// <summary>{0}：退避等級（同 <see cref="BannerSecondary"/>）；{1}：預定重試的時間。</summary>
    public required string BannerSecondaryPersistent { get; init; }

    /// <summary>{0}：預定重試的時間。</summary>
    public required string BannerForbiddenBurst { get; init; }

    /// <summary>{0}：改名保留的檔名。</summary>
    public required string BannerDatabaseRecovered { get; init; }

    /// <summary>快取資料庫重建了，但主檔已經不在、沒有東西可以改名保留。</summary>
    public required string BannerDatabaseRebuiltNoBackup { get; init; }

    public required string BannerSettingsRecovered { get; init; }

    /// <summary>設定檔讀不到、改名也失敗而且檔案還在（<see cref="Services.SettingsService.LoadFailedFileKept"/>）。</summary>
    public required string BannerSettingsLoadFailed { get; init; }

    /// <summary>{0}：<see cref="CommonLastUpdated"/> 或 <see cref="CommonNeverUpdated"/>。</summary>
    public required string BannerOffline { get; init; }

    /// <summary>{0}：<see cref="CommonLastUpdated"/> 或 <see cref="CommonNeverUpdated"/>。</summary>
    public required string BannerServerErrors { get; init; }

    // ── 使用者操作：複製失敗 log、檢查連線（spec §10.9、§11.2）────────────
    public required string ActionNoFailedJobs { get; init; }

    public required string ActionNotLoggedIn { get; init; }

    public required string ActionRepoUnknown { get; init; }

    public required string ActionScopesWithoutRepo { get; init; }

    /// <summary>{0}：job 名稱；{1}：結論的顯示狀態。</summary>
    public required string ActionJobHeader { get; init; }

    /// <summary>{0}：狀態碼或連線失敗。</summary>
    public required string ActionLogUnavailable { get; init; }

    /// <summary>{0}：狀態碼或連線失敗。</summary>
    public required string ActionJobsUnavailable { get; init; }

    /// <summary>{0}：複製了幾個 job（單數）。</summary>
    public required string ActionCopiedOne { get; init; }

    /// <summary>{0}：複製了幾個 job（複數）。</summary>
    public required string ActionCopiedOther { get; init; }

    /// <summary>接在 <see cref="ActionCopiedOne"/>／<see cref="ActionCopiedOther"/> 後面；{0}：拿不到的 job 數。</summary>
    public required string ActionCopiedMissing { get; init; }

    /// <summary>{0}：暫停到的時間。</summary>
    public required string ActionPaused { get; init; }

    /// <summary>{0}：看得到的 repo 數（單數）；{1}：其中私有的數量。</summary>
    public required string ActionVisibleReposOne { get; init; }

    /// <summary>{0}：看得到的 repo 數（複數）；{1}：其中私有的數量。</summary>
    public required string ActionVisibleReposOther { get; init; }

    /// <summary>{0}：狀態碼或連線失敗。</summary>
    public required string ActionConnectionFailed { get; init; }

    /// <summary>{0}：狀態碼或連線失敗。</summary>
    public required string ActionReposUnavailable { get; init; }

    // ── 認證的問題（spec §13.1、§13.3）──────────────────────────────────
    public required string AuthGhNotInstalled { get; init; }

    public required string AuthGhNotLoggedIn { get; init; }

    public required string AuthGhTimedOut { get; init; }

    public required string AuthPatNotConfigured { get; init; }

    /// <summary>{0}：錯誤說明。</summary>
    public required string AuthCredentialReadFailed { get; init; }

    // ── 啟動與錯誤對話框（spec §15）────────────────────────────────────
    /// <summary>{0}：資料夾；{1}：原因。</summary>
    public required string StartupFolderFailed { get; init; }

    /// <summary>{0}：error.log 的路徑；{1}：例外訊息。</summary>
    public required string StartupFailed { get; init; }

    /// <summary>{0}：cache.db 的路徑；{1}：例外訊息。</summary>
    public required string StartupDatabaseFailed { get; init; }

    /// <summary>{0}：error.log 的路徑；{1}：例外訊息。</summary>
    public required string ErrorUnexpectedWithLog { get; init; }

    public required string StartupAutoStartNoPath { get; init; }

    public required string StartupAutoStartKeyFailed { get; init; }

    /// <summary>{0}：例外訊息。</summary>
    public required string StartupAutoStartFailed { get; init; }

    public required string DialogErrorMessage { get; init; }

    // ── 主畫面（spec §10）────────────────────────────────────────────
    public required string MainPin { get; init; }

    public required string MainUnpin { get; init; }

    public required string MainCloseToTray { get; init; }

    public required string MainExit { get; init; }

    public required string MainMoreActions { get; init; }

    public required string MainWatchedTooltip { get; init; }

    public required string MainRunList { get; init; }

    public required string MainAttentionOnly { get; init; }

    /// <summary>{0}：自動更新的模式。</summary>
    public required string MainRefreshButton { get; init; }

    public required string RefreshAuto { get; init; }

    public required string RefreshSeconds30 { get; init; }

    public required string RefreshMinute1 { get; init; }

    public required string RefreshMinutes5 { get; init; }

    public required string RefreshPaused { get; init; }

    /// <summary>{0}：run 數（單數）。</summary>
    public required string SummaryRunsOne { get; init; }

    /// <summary>{0}：run 數（複數）。</summary>
    public required string SummaryRunsOther { get; init; }

    public required string SummaryInProgress { get; init; }

    public required string SummaryFailed { get; init; }

    /// <summary>{0}：剩餘；{1}：上限。</summary>
    public required string SummaryApiRemaining { get; init; }

    public required string FilterRepo { get; init; }

    public required string FilterWorkflow { get; init; }

    public required string FilterEvent { get; init; }

    public required string FilterStatus { get; init; }

    public required string FilterBranch { get; init; }

    public required string FilterActor { get; init; }

    /// <summary>{0}：欄位名稱；{1}：勾選的數量。</summary>
    public required string FilterWithCount { get; init; }

    public required string EmptyNotLoggedIn { get; init; }

    public required string EmptyUnauthorizedHint { get; init; }

    public required string EmptyOffline { get; init; }

    /// <summary>{0}：完成的 repo 數；{1}：監看中的 repo 數。</summary>
    public required string EmptyLoadingProgress { get; init; }

    public required string EmptyNoData { get; init; }

    public required string EmptyNoDataHint { get; init; }

    public required string EmptyFilteredOut { get; init; }

    public required string MenuOpenRun { get; init; }

    public required string MenuOpenPullRequest { get; init; }

    public required string MenuOpenCommit { get; init; }

    public required string MenuCopyRunUrl { get; init; }

    public required string MenuCopyFailedLog { get; init; }

    public required string MenuWatch { get; init; }

    /// <summary>已標記時「這筆完成時通知我」的報讀名稱。</summary>
    public required string MenuWatchChecked { get; init; }

    public required string MenuMuteRepo { get; init; }

    /// <summary>寫入功能開啟時 run 選單的項目（第二階段 spec §9.1）；下面三項同。</summary>
    public required string MenuRerunFailedJobs { get; init; }

    public required string MenuRerunAll { get; init; }

    public required string MenuCancelRun { get; init; }

    public required string MenuForceCancel { get; init; }

    /// <summary>{0}：第幾次嘗試。</summary>
    public required string RowAttempt { get; init; }

    /// <summary>{0}：重跑者的 login。</summary>
    public required string RowRerunBy { get; init; }

    public required string RowStartedFromCreated { get; init; }

    /// <summary>已標記「完成時通知我」時，列的報讀名稱加上這一段。</summary>
    public required string RowWatched { get; init; }

    /// <summary>{0}：PR 編號清單（例如 <c>#17、#18</c>）。</summary>
    public required string RowPullRequests { get; init; }

    /// <summary>「取消中」（第二階段 spec §9.3）：狀態的文字、tooltip、報讀名稱與時間欄共用。</summary>
    public required string RowCancelling { get; init; }

    public required string MessageClipboardBusy { get; init; }

    public required string MessageLinkBlocked { get; init; }

    public required string MessageBrowserFailed { get; init; }

    public required string MessageSettingsSaveFailed { get; init; }

    public required string MessageRunUrlCopied { get; init; }

    public required string MessageFetchingLogs { get; init; }

    /// <summary>{0}：repo 的 full_name。</summary>
    public required string MessageRepoMuted { get; init; }

    // ── 寫入的確認對話框（第二階段 spec §9.2）────────────────────────────
    public required string ConfirmRerunFailedJobsTitle { get; init; }

    /// <summary>{0}：一個 run 最多重跑幾次（<see cref="Tuning.MaxReruns"/>）。</summary>
    public required string ConfirmRerunFailedJobsBody { get; init; }

    public required string ConfirmRerunFailedJobsButton { get; init; }

    public required string ConfirmRerunAllTitle { get; init; }

    /// <summary>{0}：一個 run 最多重跑幾次（<see cref="Tuning.MaxReruns"/>）。</summary>
    public required string ConfirmRerunAllBody { get; init; }

    public required string ConfirmRerunAllButton { get; init; }

    public required string ConfirmCancelRunTitle { get; init; }

    public required string ConfirmCancelRunBody { get; init; }

    public required string ConfirmCancelRunButton { get; init; }

    public required string ConfirmForceCancelTitle { get; init; }

    public required string ConfirmForceCancelBody { get; init; }

    public required string ConfirmForceCancelButton { get; init; }

    /// <summary>左邊不執行的按鈕（「取消」會和「取消這次執行」混淆）。</summary>
    public required string ConfirmNo { get; init; }

    /// <summary>{0}：repo 的 full_name；{1}：workflow；{2}：run 編號。</summary>
    public required string ConfirmTarget { get; init; }

    /// <summary>{0}：第幾次嘗試；{1}：分支。沒有分支時改用 <see cref="RowAttempt"/>。</summary>
    public required string ConfirmAttemptBranch { get; init; }

    // ── 寫入的送出與結果（第二階段 spec §9.4、§3.3、§5.2）────────────────
    public required string WriteSending { get; init; }

    public required string WriteAcceptedRerun { get; init; }

    /// <summary>取消與強制取消共用。</summary>
    public required string WriteAcceptedCancel { get; init; }

    public required string WriteUncertain { get; init; }

    /// <summary>收到 5xx 的結果不確定（第二階段 spec §3.3 第 11 列）：有回應，但可能已經生效；{0}：狀態碼。</summary>
    public required string WriteUncertainServerError { get; init; }

    public required string WriteRejectedRateLimited { get; init; }

    public required string WriteRejectedUnauthorized { get; init; }

    /// <summary>{0}：需要的權限（X-Accepted-GitHub-Permissions 的原文；沒有時是 <c>actions=write</c>）。</summary>
    public required string WriteRejectedInsufficientPermission { get; init; }

    public required string WriteRejectedForbidden { get; init; }

    public required string WriteRejectedNotFound { get; init; }

    public required string WriteRejectedConflict { get; init; }

    public required string WriteRejectedUnprocessable { get; init; }

    public required string WriteRejectedMoved { get; init; }

    /// <summary>
    /// 其他狀態碼（WriteRejectReason.Other：400、410、表上沒列的…；5xx 是結果不確定，不用這一項）；{0}：狀態碼。
    /// 名稱不用 Other 結尾：結尾是 One／Other 的一組是單數與複數（<see cref="UiText.Plural"/>）。
    /// </summary>
    public required string WriteRejectedGeneric { get; init; }

    /// <summary>接在被拒與 5xx 的結果不確定的說明後面（另起一段）；{0}：伺服器回應的 message 原文。</summary>
    public required string WriteServerMessage { get; init; }

    public required string WriteNotSentWritesDisabled { get; init; }

    public required string WriteNotSentNotLoggedIn { get; init; }

    public required string WriteNotSentVersionError { get; init; }

    public required string WriteNotSentRateLimitPaused { get; init; }

    public required string WriteNotSentOffline { get; init; }

    public required string WriteNotSentMissingPermission { get; init; }

    /// <summary>{0}：不能用的原因（<c>ActionBlock*</c>）。</summary>
    public required string WriteNotSentUnavailable { get; init; }

    public required string WriteNotSentAttemptChanged { get; init; }

    public required string WriteNotSentInFlight { get; init; }

    public required string WriteNotSentNotFound { get; init; }

    public required string WriteNotSentQueueTimeout { get; init; }

    public required string WriteNotSentUnexpected { get; init; }

    // ── 寫入項目停用的原因（第二階段 spec §7 的原因代碼）──────────────────
    public required string ActionBlockInFlight { get; init; }

    public required string ActionBlockMissingPermission { get; init; }

    /// <summary>接在 <see cref="ActionBlockMissingPermission"/> 後面（第二階段 spec §4.3），自帶前面的標點。</summary>
    public required string ActionBlockCheckConnectionHint { get; init; }

    public required string ActionBlockNotLoggedIn { get; init; }

    public required string ActionBlockVersionError { get; init; }

    public required string ActionBlockOffline { get; init; }

    public required string ActionBlockRateLimitPaused { get; init; }

    public required string ActionBlockNotCompleted { get; init; }

    public required string ActionBlockCompleted { get; init; }

    public required string ActionBlockNoRerunnableJobs { get; init; }

    public required string ActionBlockNoFailedJobs { get; init; }

    public required string ActionBlockAwaitingApproval { get; init; }

    public required string ActionBlockRerunPending { get; init; }

    public required string ActionBlockWorkflowDisabled { get; init; }

    /// <summary>{0}：可以重跑的天數（<see cref="Tuning.RerunWindowDays"/>）。</summary>
    public required string ActionBlockTooOld { get; init; }

    public required string ActionBlockRerunLimit { get; init; }

    public required string ActionBlockCancelPending { get; init; }

    /// <summary>{0}：取消受理後多少分鐘才能強制取消（<see cref="Tuning.CancelGraceBeforeForce"/>）。</summary>
    public required string ActionBlockForceNotYet { get; init; }

    // ── 設定頁（spec §11.2）──────────────────────────────────────────
    public required string SettingsAccount { get; init; }

    public required string SettingsAuthMethod { get; init; }

    public required string SettingsSignedInAs { get; init; }

    public required string SettingsTokenSource { get; init; }

    public required string SettingsCheckConnection { get; init; }

    public required string SettingsPat { get; init; }

    public required string SettingsClear { get; init; }

    public required string SettingsIgnoreEnv { get; init; }

    public required string SettingsScope { get; init; }

    public required string SettingsIncludeForks { get; init; }

    public required string SettingsIncludeCollaborators { get; init; }

    public required string SettingsNotifications { get; init; }

    public required string SettingsNotifyFailure { get; init; }

    public required string SettingsNotifySuccess { get; init; }

    public required string SettingsNotifyWatched { get; init; }

    public required string SettingsFailureToastName { get; init; }

    public required string SettingsSuccessToastName { get; init; }

    /// <summary>{0}：欄位名稱；{1}：下限；{2}：上限。</summary>
    public required string SettingsRangeLabel { get; init; }

    /// <summary>{0}：欄位名稱；{1}：下限；{2}：上限。</summary>
    public required string SettingsRangeError { get; init; }

    public required string SettingsFailedLog { get; init; }

    public required string SettingsLogTailLinesName { get; init; }

    /// <summary>「寫入操作」卡片（第二階段 spec §9.5）的標題；下面兩項是開關與說明。</summary>
    public required string SettingsWrite { get; init; }

    public required string SettingsAllowWriteActions { get; init; }

    /// <summary>引用 run 選單寫入項目的名稱（Menu*），改選單名稱時要一起改（TextTableTests 會檢查）。</summary>
    public required string SettingsAllowWriteActionsHint { get; init; }

    public required string SettingsGeneral { get; init; }

    public required string SettingsRunAtStartup { get; init; }

    public required string SettingsMinimizeToTray { get; init; }

    public required string SettingsAlwaysOnTop { get; init; }

    /// <summary>{0}：間隔天數。功能名稱是「檢查新版本」，不要寫成「自動更新」：那是輪詢間隔的名稱。</summary>
    public required string SettingsCheckForUpdates { get; init; }

    public required string SettingsLanguage { get; init; }

    /// <summary>語言選項一律用自己的語言顯示，兩種語言相同。</summary>
    public required string SettingsLanguageEnglish { get; init; }

    /// <summary>語言選項一律用自己的語言顯示，兩種語言相同。</summary>
    public required string SettingsLanguageTraditionalChinese { get; init; }

    public required string SettingsData { get; init; }

    /// <summary>設定頁最下方的目前版本。{0}：版本號，不含 <c>+</c> 後面的 build metadata。</summary>
    public required string SettingsVersionLabel { get; init; }

    /// <summary>設定頁最下方的入口，也是它開啟的對話框的標題。</summary>
    public required string SettingsNotices { get; init; }

    /// <summary>對話框內文的報讀名稱。</summary>
    public required string SettingsNoticesFullText { get; init; }

    public required string SettingsNoticesFailed { get; init; }

    public required string SettingsSignedIn { get; init; }

    public required string SettingsNotSignedIn { get; init; }

    public required string SettingsVerifying { get; init; }

    public required string SettingsSourceGhStored { get; init; }

    public required string SettingsSourceGhEnvironment { get; init; }

    public required string SettingsSourcePat { get; init; }

    public required string SettingsSourceNone { get; init; }

    public required string SettingsChecking { get; init; }

    /// <summary>{0}：判讀結果。</summary>
    public required string SettingsScopes { get; init; }

    public required string SettingsNoScopeIssue { get; init; }

    public required string SettingsPatHintDefault { get; init; }

    public required string SettingsPatHintClear { get; init; }

    public required string SettingsPatHintClearNeedsGhCli { get; init; }

    public required string SettingsVerifyingPat { get; init; }

    public required string SettingsPatRequired { get; init; }

    public required string SettingsPatClearNeedsGhCli { get; init; }

    public required string SettingsPatVersionError { get; init; }

    public required string SettingsPatFormat { get; init; }

    public required string SettingsPatTooLong { get; init; }

    public required string SettingsPatRejectedAgain { get; init; }

    public required string SettingsPatUnauthorized { get; init; }

    public required string SettingsPatRateLimited { get; init; }

    /// <summary>{0}：狀態碼或連線失敗。</summary>
    public required string SettingsPatVerifyFailed { get; init; }

    /// <summary>{0}：錯誤說明。</summary>
    public required string SettingsCredentialWriteFailed { get; init; }

    /// <summary>{0}：錯誤說明。</summary>
    public required string SettingsCredentialDeleteFailed { get; init; }

    /// <summary>{0}：例外訊息。</summary>
    public required string SettingsSaveFailed { get; init; }

    /// <summary><see cref="Services.SettingsService.Save"/> 拒絕寫入的原因，會填進 <see cref="SettingsSaveFailed"/>。</summary>
    public required string SettingsSaveRefusedFileKept { get; init; }

    // ── repo 管理頁（spec §11.1）──────────────────────────────────────
    public required string ReposSkippedOnly { get; init; }

    public required string ReposList { get; init; }

    /// <summary>{0}：repo 總數（單數）。</summary>
    public required string ReposCountTotalOne { get; init; }

    /// <summary>{0}：repo 總數（複數）。</summary>
    public required string ReposCountTotalOther { get; init; }

    public required string ReposCountMonitored { get; init; }

    public required string ReposCountSkipped { get; init; }

    public required string ReposCategoryOwn { get; init; }

    public required string ReposCategoryOrganization { get; init; }

    public required string ReposCategoryCollaborator { get; init; }

    public required string ReposCategoryFork { get; init; }

    public required string ReposMonitored { get; init; }

    public required string ReposSkipped { get; init; }

    public required string ReposMute { get; init; }

    public required string ReposUnmute { get; init; }

    public required string ReposForce { get; init; }

    public required string ReposUnforce { get; init; }

    public required string ReposMutedForceTooltip { get; init; }

    public required string ReposMonitoredForceTooltip { get; init; }

    /// <summary>{0}：不能強制納入的原因（略過原因）。</summary>
    public required string ReposNotForcible { get; init; }

    public required string ReposNoRepos { get; init; }

    public required string ReposNoMatch { get; init; }
}
