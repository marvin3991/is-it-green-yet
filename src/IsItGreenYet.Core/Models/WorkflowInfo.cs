namespace IsItGreenYet.Core.Models;

/// <summary>repo 裡的一個 workflow（E3）。</summary>
public sealed record WorkflowInfo(long RepoId, long Id, string Name, string Path, string State, string HtmlUrl)
{
    /// <summary>只有 active 的 workflow 會被追蹤（spec §7.2）。</summary>
    public bool IsActive => string.Equals(State, "active", StringComparison.Ordinal);
}
