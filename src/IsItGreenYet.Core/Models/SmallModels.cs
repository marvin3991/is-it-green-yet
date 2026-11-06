namespace IsItGreenYet.Core.Models;

/// <summary>開啟中的 PR（E7）。<see cref="HeadRepoId"/> 文件標 required，保守起見仍可為 null。</summary>
public sealed record PullRequestInfo(
    long RepoId,
    int Number,
    long? HeadRepoId,
    string? HeadRepoFullName,
    string HeadRef,
    string HeadSha,
    string HtmlUrl);

/// <summary>run 底下的一個 job（E8），只在複製失敗 log 時使用。</summary>
public sealed record JobInfo(
    long Id,
    string Name,
    string Status,
    string? Conclusion,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? HtmlUrl);

/// <summary>目前登入的 GitHub 帳號（E1）。比對「是不是我」一律用 <see cref="UserId"/>。</summary>
public sealed record AccountInfo(long UserId, string Login);

/// <summary>專案自己的最新 release（E15），只用於檢查新版本。兩個欄位都可能缺：200 就算檢查完成，缺值由 UpdateCheckRules 判斷。</summary>
public sealed record ReleaseInfo(string? TagName, string? HtmlUrl);

/// <summary>「自動更新」下拉選單的選項（spec §10.3）。</summary>
public enum RefreshMode
{
    Auto,
    Seconds30,
    Minute1,
    Minutes5,
    Paused,
}
