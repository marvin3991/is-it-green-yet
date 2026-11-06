namespace IsItGreenYet.Core.Models;

/// <summary>
/// 一筆 workflow run 的最新 attempt（E4／E6）。時間一律是 UTC。
/// <see cref="Status"/>／<see cref="Conclusion"/> 保留 API 原字串，判讀交給 RunStateMapper。
/// </summary>
public sealed record RunInfo(
    long Id,
    long RepoId,
    long WorkflowId,
    string? WorkflowName,
    string Path,
    int RunNumber,
    int RunAttempt,
    string DisplayTitle,
    string Event,
    string? Status,
    string? Conclusion,
    string? HeadBranch,
    string HeadSha,
    long HeadRepoId,
    string? HeadRepoFullName,
    string? HeadRepoHtmlUrl,
    long? ActorId,
    string? ActorLogin,
    string? TriggeringActorLogin,
    IReadOnlyList<int> PullRequestNumbers,
    DateTimeOffset? RunStartedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string HtmlUrl);

/// <summary>
/// 看過的某一次 attempt。API 只回最新 attempt，所以重跑前的結論要靠本地保存（spec §7.2）。
/// </summary>
public sealed record RunAttemptRecord(
    long RunId,
    int Attempt,
    string? Status,
    string? Conclusion,
    DateTimeOffset? RunStartedAt,
    DateTimeOffset UpdatedAt);
