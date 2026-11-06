using System.Globalization;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Display;

/// <summary>網頁連結，一律以 API 回傳的 html_url 為基底（spec §10.7）。</summary>
/// <remarks>
/// 開啟前一定要先過 <see cref="IsAllowed"/>。交給瀏覽器時用 <see cref="Uri.AbsoluteUri"/>（保留編碼），
/// 不要用 <c>ToString()</c>，它會把部分編碼還原。
/// html_url 無法解析成絕對網址時回傳空的相對 Uri：它一定不會通過 IsAllowed，呼叫端照 §10.7 不開、寫 error.log，不會丟例外。
/// </remarks>
public static class WebLinks
{
    private const char PathSeparator = '/';

    /// <summary>專案自己的 repo 頁（設定頁底部的連結）。</summary>
    public static Uri ProjectRepo { get; } = new($"https://{Tuning.WebHost}/{Tuning.ProjectRepoFullName}");

    public static Uri RepoActions(RepoInfo repo) => Build(repo.HtmlUrl, "actions");

    public static Uri PullRequest(RepoInfo repo, int number) =>
        Build(repo.HtmlUrl, "pull/" + number.ToString(CultureInfo.InvariantCulture));

    /// <summary>sha 正常只有十六進位字元；仍整段編碼，避免異常值跳出 commit 路徑。</summary>
    public static Uri Commit(RepoInfo repo, string sha) =>
        Build(repo.HtmlUrl, "commit/" + Uri.EscapeDataString(sha ?? string.Empty));

    /// <summary>分支頁；分支名依 <c>/</c> 切段後各段編碼。head_branch 為 null 時回 null。</summary>
    /// <remarks>
    /// 基底是來源 repo 的 html_url（fork 的分支要開 fork 那邊）。來源 repo 的 html_url 缺值時，
    /// 來源就是本 repo 才退回 repo.html_url；fork 則回 null，避免開到本 repo 的同名分支（§10.5）。
    /// </remarks>
    public static Uri? Branch(RunInfo run, RepoInfo repo)
    {
        if (string.IsNullOrEmpty(run.HeadBranch))
        {
            return null;
        }

        var baseUrl = BranchBase(run, repo);
        if (baseUrl is null)
        {
            return null;
        }

        // 整個分支名一起編碼會把 / 變成 %2F，GitHub 認不得；所以逐段編碼，# % ? 空白與中文都在段內處理。
        var segments = run.HeadBranch.Split(PathSeparator).Select(Uri.EscapeDataString);
        return Build(baseUrl, "tree/" + string.Join(PathSeparator, segments));
    }

    /// <summary>只允許 https 且 host 為 github.com 的網址被開啟。</summary>
    /// <remarks>另外要求預設連接埠、不帶使用者資訊：API 給的 github.com 網址不會有這兩者。</remarks>
    public static bool IsAllowed(Uri uri) =>
        uri is { IsAbsoluteUri: true }
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, Tuning.WebHost, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0;

    private static string? BranchBase(RunInfo run, RepoInfo repo)
    {
        if (!string.IsNullOrWhiteSpace(run.HeadRepoHtmlUrl))
        {
            return run.HeadRepoHtmlUrl;
        }

        return run.HeadRepoId == repo.Id ? repo.HtmlUrl : null;
    }

    private static Uri Build(string? baseUrl, string relativePath)
    {
        var text = (baseUrl ?? string.Empty).TrimEnd(PathSeparator) + PathSeparator + relativePath;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : new Uri(string.Empty, UriKind.Relative);
    }
}
