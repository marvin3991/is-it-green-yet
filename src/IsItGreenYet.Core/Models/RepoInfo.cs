namespace IsItGreenYet.Core.Models;

/// <summary>repo 和使用者的關係（spec §5.1）。</summary>
public enum RepoCategory
{
    Own,
    Organization,
    Collaborator,
}

/// <summary>
/// 一個 repo。<see cref="Id"/> 是穩定鍵，改名或轉移後不變；<see cref="FullName"/> 只用來顯示與組網址。
/// </summary>
public sealed record RepoInfo(
    long Id,
    string FullName,
    string Name,
    long OwnerId,
    string OwnerLogin,
    string OwnerType,
    bool IsPrivate,
    bool IsFork,
    bool IsArchived,
    bool IsDisabled,
    string DefaultBranch,
    string HtmlUrl,
    DateTimeOffset? PushedAt,
    RepoCategory Category);

/// <summary>使用者在 repo 管理頁做的選擇，存在 DB，不寫進設定檔。</summary>
public sealed record RepoOverride(bool Muted, bool ForceIncluded)
{
    public static RepoOverride None { get; } = new(false, false);
}
