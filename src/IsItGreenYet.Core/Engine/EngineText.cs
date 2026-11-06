using System.Globalization;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Localization;

namespace IsItGreenYet.Core.Engine;

/// <summary>
/// Engine 產生的畫面文字。spec 有原文的照原文（§10.2 橫幅、§7.4 tooltip、§5.4、§4.2 E7、§10.9）；
/// spec 沒有給文字的（例如 job 清單拿不到）用最短的說明，並在註解標明。
/// </summary>
/// <remarks>
/// 畫面文字一律明確傳入語言（Engine 用自己設定裡的語言，不看執行緒的 CurrentUICulture），字串在 <see cref="TextTable"/>。
/// 只寫進 error.log 的除錯文字（<see cref="ForbiddenBurstLog"/>、<see cref="TargetErrorLog"/>、<see cref="WriteErrorLog"/>）維持中文、不翻。
/// </remarks>
internal static class EngineText
{
    /// <summary>橫幅與 tooltip 裡的時間（spec §10.2 範例 <c>10:45</c>）。</summary>
    public const string ShortTimeFormat = "HH:mm";

    /// <summary>離線橫幅的「最後更新」（spec §10.2 範例 <c>10:07:33</c>）。</summary>
    public const string LongTimeFormat = "HH:mm:ss";

    /// <summary>tooltip 原因行的分隔，同 tooltip 第二行（spec §7.4）。</summary>
    public const string Separator = " · ";

    /// <summary>連線層錯誤沒有狀態碼（GitHubClient 記 0）。</summary>
    private const int NoStatusCode = 0;

    // ── 橫幅（spec §10.2）─────────────────────────────────────────────
    public static string Unauthorized(AppLanguage language) => UiText.Of(language).BannerUnauthorized;

    public static string UnauthorizedFromEnvironment(AppLanguage language) => UiText.Of(language).CommonUnauthorizedFromEnvironment;

    public static string VersionError(AppLanguage language) => UiText.Of(language).BannerVersionError;

    public static string Deprecated(AppLanguage language) => UiText.Of(language).BannerDeprecated;

    public static string UnexpectedError(AppLanguage language) => UiText.Of(language).CommonUnexpectedError;

    // ── tooltip 原因行（spec §7.4）────────────────────────────────────
    public static string TrayUnauthorized(AppLanguage language) => UiText.Of(language).TrayIssueUnauthorized;

    public static string TrayOffline(AppLanguage language) => UiText.Of(language).TrayIssueOffline;

    /// <summary>spec §7.4 沒有列這兩種的原因文字，沿用橫幅的說法（英文的 5xx 縮短成 GitHub errors，見 TextTables.English）。</summary>
    public static string TrayServerErrors(AppLanguage language) => UiText.Of(language).TrayIssueServerErrors;

    public static string TrayVersionError(AppLanguage language) => UiText.Of(language).TrayIssueVersionError;

    // ── repo 的問題標示 ────────────────────────────────────────────────
    /// <summary>同一個 repo 有多個問題時，problem_note 的分隔。</summary>
    public static string ProblemSeparator(AppLanguage language) => UiText.Of(language).ProblemSeparator;

    /// <summary>spec §5.4 表格最後一列。</summary>
    public static string ActionsNotFound(AppLanguage language) => UiText.Of(language).ProblemActionsNotFound;

    /// <summary>spec §4.2 E7。</summary>
    public static string PullRequestsUnavailable(AppLanguage language) => UiText.Of(language).ProblemPullRequestsUnavailable;

    // ── 使用者操作 ────────────────────────────────────────────────────
    /// <summary>spec §10.9 第 4 點。</summary>
    public static string NoFailedJobs(AppLanguage language) => UiText.Of(language).ActionNoFailedJobs;

    /// <summary>spec 沒有給文字：未登入時不送任何 API（spec §13.2）。</summary>
    public static string NotLoggedIn(AppLanguage language) => UiText.Of(language).ActionNotLoggedIn;

    /// <summary>spec 沒有給文字。</summary>
    public static string RepoUnknown(AppLanguage language) => UiText.Of(language).ActionRepoUnknown;

    /// <summary>spec §4.4：X-OAuth-Scopes 有值但不含 repo。</summary>
    public static string ScopesWithoutRepo(AppLanguage language) => UiText.Of(language).ActionScopesWithoutRepo;

    public static string PrimaryLimit(string time, AppLanguage language) => UiText.Format(UiText.Of(language).BannerPrimaryLimit, time);

    public static string SecondaryLimitWithRetryAfter(string time, AppLanguage language) =>
        UiText.Format(UiText.Of(language).BannerSecondaryRetryAfter, time);

    /// <summary>第 SecondaryBackoffPersistentLevel 次起主畫面顯示「持續被限流」（spec §6.4）。</summary>
    public static string SecondaryLimitWithoutRetryAfter(int level, bool persistent, string time, AppLanguage language)
    {
        var text = UiText.Of(language);
        return UiText.Format(persistent ? text.BannerSecondaryPersistent : text.BannerSecondary, level, time);
    }

    /// <summary>§6.4 第 5 列的全域暫停：沒有退避等級，沿用次級限流沒有 retry-after 時的說法。</summary>
    public static string ForbiddenBurst(string time, AppLanguage language) => UiText.Format(UiText.Of(language).BannerForbiddenBurst, time);

    /// <summary>寫進 error.log 的除錯文字（不翻）。</summary>
    public static string ForbiddenBurstLog => string.Create(CultureInfo.InvariantCulture,
        $"{Tuning.ForbiddenBurstWindow.TotalSeconds:0} 秒內有 {Tuning.ForbiddenBurstRepos} 個以上不同 repo 回傳無法分類的 403，" +
        $"可能是沒寫明的次級限流，全域暫停 {Tuning.MinRateLimitWait.TotalSeconds:0} 秒（Q23）。");

    /// <summary>寫進 error.log 的除錯文字（不翻）：回應要求的暫停超過上限，留下原本的秒數方便查 header 為什麼異常。</summary>
    public static string RateLimitWaitCappedLog(TimeSpan requested, string reason) => string.Create(CultureInfo.InvariantCulture,
        $"回應要求暫停 {requested.TotalSeconds:0} 秒（{reason}），超過上限 {Tuning.MaxRateLimitWait.TotalSeconds:0} 秒，改用上限；到期後會再送一個請求。");

    /// <summary>寫進 error.log 的除錯文字（不翻）：刪掉持續損毀的資料庫之前留下大小與備份份數，不寫檔案內容。</summary>
    public static string DatabaseDiscardedLog(long sizeBytes, int numberedBackups) => string.Create(CultureInfo.InvariantCulture,
        $"持續損毀的重試：這份是這次執行自己建的、之後沒有設過靜音或強制納入，也讀不到 repo_overrides 的資料列，直接刪除不留檔。大小 {sizeBytes} bytes，磁碟上有 {numberedBackups} 份編號備份。");

    public static string Offline(string? lastUpdated, AppLanguage language) =>
        UiText.Format(UiText.Of(language).BannerOffline, UpdatedText(lastUpdated, language));

    public static string ServerErrors(string? lastUpdated, AppLanguage language) =>
        UiText.Format(UiText.Of(language).BannerServerErrors, UpdatedText(lastUpdated, language));

    public static string TrayRateLimited(string time, AppLanguage language) => UiText.Format(UiText.Of(language).TrayIssueRateLimited, time);

    public static string StaleRepos(int count, AppLanguage language)
    {
        var text = UiText.Of(language);
        return UiText.Plural(count, text.StaleReposOne, text.StaleReposOther);
    }

    /// <summary>§6.4 第 3、4 列：權限不足標示缺的權限，存取被拒附上 message 原文。</summary>
    public static string Forbidden(bool insufficientPermission, string? acceptedPermissions, string? acceptedOAuthScopes, string? message, AppLanguage language)
    {
        var text = UiText.Of(language);
        if (insufficientPermission)
        {
            var needed = !string.IsNullOrWhiteSpace(acceptedPermissions) ? acceptedPermissions : acceptedOAuthScopes;
            return string.IsNullOrWhiteSpace(needed) ? text.ProblemInsufficientPermission : UiText.Format(text.ProblemInsufficientPermissionNeeds, needed);
        }

        return string.IsNullOrWhiteSpace(message) ? text.ProblemAccessDenied : UiText.Format(text.ProblemAccessDeniedMessage, message);
    }

    /// <summary>spec §10.9 第 3 點：「=== job 名稱（結論）===」。</summary>
    public static string JobHeader(string name, string conclusion, AppLanguage language) =>
        UiText.Format(UiText.Of(language).ActionJobHeader, name, conclusion);

    /// <summary>spec §4.2 E9；連線失敗沒有狀態碼，改寫「連線失敗」。</summary>
    public static string LogUnavailable(int statusCode, AppLanguage language) =>
        UiText.Format(UiText.Of(language).ActionLogUnavailable, StatusText(statusCode, language));

    /// <summary>spec 沒有給文字（E8 失敗）。</summary>
    public static string JobsUnavailable(int statusCode, AppLanguage language) =>
        UiText.Format(UiText.Of(language).ActionJobsUnavailable, StatusText(statusCode, language));

    /// <summary>spec 沒有給文字。</summary>
    public static string Copied(int copied, int failed, AppLanguage language)
    {
        var text = UiText.Of(language);
        var done = UiText.Plural(copied, text.ActionCopiedOne, text.ActionCopiedOther);
        return failed == 0 ? done : done + UiText.Format(text.ActionCopiedMissing, failed);
    }

    /// <summary>spec 沒有給文字：限流期間不送使用者操作的請求。</summary>
    public static string Paused(string time, AppLanguage language) => UiText.Format(UiText.Of(language).ActionPaused, time);

    /// <summary>spec §11.2 / §5.4：「看得到 N 個 repo（其中私有 M 個）」。</summary>
    public static string VisibleRepos(int visible, int privateRepos, AppLanguage language)
    {
        var text = UiText.Of(language);
        return UiText.Plural(visible, text.ActionVisibleReposOne, text.ActionVisibleReposOther, privateRepos);
    }

    /// <summary>spec 沒有給文字（檢查連線失敗）。</summary>
    public static string ConnectionFailed(int statusCode, AppLanguage language) =>
        UiText.Format(UiText.Of(language).ActionConnectionFailed, StatusText(statusCode, language));

    /// <summary>spec 沒有給文字（檢查連線時 repo 清單拿不到）。</summary>
    public static string ReposUnavailable(int statusCode, AppLanguage language) =>
        UiText.Format(UiText.Of(language).ActionReposUnavailable, StatusText(statusCode, language));

    /// <summary>寫進 error.log 的除錯文字（不翻）。</summary>
    public static string TargetErrorLog(PollTarget target, string outcome, int statusCode, string? message) =>
        string.Create(CultureInfo.InvariantCulture, $"{target.Kind} repo={target.RepoId}：{outcome}（{LogStatusText(statusCode)}）{message}");

    /// <summary>寫入回 5xx 或表上沒列的狀態碼（第二階段 spec §3.3 第 11 列），寫進 error.log 的除錯文字（不翻）。</summary>
    public static string WriteErrorLog(WriteAction action, string repoFullName, int statusCode, string? message) =>
        string.Create(CultureInfo.InvariantCulture, $"{action} {repoFullName}：{LogStatusText(statusCode)} {message}");

    /// <summary>檢查新版本回應失敗，寫進 error.log 的除錯文字（不翻）。</summary>
    public static string UpdateCheckFailedLog(string outcome, int statusCode) =>
        string.Create(CultureInfo.InvariantCulture, $"{outcome}（{LogStatusText(statusCode)}）");

    /// <summary>寫進 error.log 的除錯文字（不翻）。</summary>
    public static string UpdateCheckBadTagLog(string? tag) => "release 的 tag 解析不了，當成沒有新版：" + (tag ?? "（缺少 tag_name）");

    /// <summary>寫進 error.log 的除錯文字（不翻）。</summary>
    public static string UpdateCheckBadUrlLog(string? tag, string? url) => $"release {tag} 的網址不允許或缺少，不通知：{url ?? "（缺少 html_url）"}";

    /// <summary>寫進 error.log 的除錯文字（不翻）。</summary>
    public static string UpdateCheckBadCurrentLog(string version) => $"目前版本 {version} 解析不了，這次執行不檢查。";

    private static string StatusText(int statusCode, AppLanguage language)
    {
        var text = UiText.Of(language);
        return statusCode == NoStatusCode ? text.CommonConnectionFailed : UiText.Format(text.CommonStatusCode, statusCode);
    }

    /// <summary>error.log 用的狀態碼文字（不翻）。</summary>
    private static string LogStatusText(int statusCode) =>
        statusCode == NoStatusCode ? "連線失敗" : string.Create(CultureInfo.InvariantCulture, $"狀態碼 {statusCode}");

    private static string UpdatedText(string? lastUpdated, AppLanguage language)
    {
        var text = UiText.Of(language);
        return lastUpdated is null ? text.CommonNeverUpdated : UiText.Format(text.CommonLastUpdated, lastUpdated);
    }
}
