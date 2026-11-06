using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Tracking;

/// <summary>追蹤鍵的來源（spec §7.2 規則 1、2）。</summary>
public enum KeyKind
{
    DefaultBranch,
    PullRequestBranch,
}

/// <summary>同一個 workflow、同一個來源 repo、同一個分支上的 run 共用一個鍵。</summary>
public sealed record TrackingKey(long RepoId, long WorkflowId, long HeadRepoId, string HeadBranch);

/// <summary>
/// 一個追蹤鍵的判定結果。
/// <see cref="Pending"/> 是 spec 的 keyPending；<see cref="LastDecisive"/> 只會是 Success、Failure 或 null。
/// </summary>
public sealed record KeyEvaluation(
    TrackingKey Key,
    KeyKind Kind,
    RunInfo Latest,
    bool Pending,
    RunCategory? LastDecisive,
    IReadOnlyList<long> LastDecisiveGroupRunIds,
    DateTimeOffset? LastDecisiveAt,
    bool Failing);

/// <summary>
/// 判定一個 repo 的追蹤鍵所需的全部資料。
/// <see cref="OpenPullRequests"/> 為 null 代表 PR 追蹤停用（例如沒有 Pull requests 權限），只產生預設分支鍵。
/// </summary>
public sealed record RepoTrackingInput(
    RepoInfo Repo,
    IReadOnlyCollection<WorkflowInfo> Workflows,
    IReadOnlyCollection<RunInfo> Runs,
    IReadOnlyDictionary<long, IReadOnlyList<RunAttemptRecord>> AttemptsByRun,
    IReadOnlyCollection<PullRequestInfo>? OpenPullRequests);
