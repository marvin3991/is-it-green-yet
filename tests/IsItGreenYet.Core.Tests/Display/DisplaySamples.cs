using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Tests.Display;

/// <summary>Display 測試共用的手寫假資料（假 repo <c>octo-org/sample-app</c>），各測試用 <c>with</c> 改需要的欄位。</summary>
internal static class DisplaySamples
{
    public const long RepoId = 100;
    public const long ForkRepoId = 200;
    public const string RepoHtmlUrl = "https://github.com/octo-org/sample-app";
    public const string ForkHtmlUrl = "https://github.com/fork-user/sample-app";

    /// <summary>40 碼的假 sha，前 7 碼同需求範例 <c>Commit dcf27ad</c>。</summary>
    public const string Sha = "dcf27ad4e5f60718293a4b5c6d7e8f9012345678";

    public const string OtherSha = "0123456789abcdef0123456789abcdef01234567";

    public static readonly DateTimeOffset CreatedAt = new(2026, 10, 24, 1, 0, 0, TimeSpan.Zero);

    public static RepoInfo Repo { get; } = new(
        Id: RepoId,
        FullName: "octo-org/sample-app",
        Name: "sample-app",
        OwnerId: 1,
        OwnerLogin: "octo-org",
        OwnerType: "Organization",
        IsPrivate: false,
        IsFork: false,
        IsArchived: false,
        IsDisabled: false,
        DefaultBranch: "main",
        HtmlUrl: RepoHtmlUrl,
        PushedAt: CreatedAt,
        Category: RepoCategory.Organization);

    /// <summary>本 repo main 分支上 alice 觸發的 push run。</summary>
    public static RunInfo Run { get; } = new(
        Id: 5001,
        RepoId: RepoId,
        WorkflowId: 11,
        WorkflowName: "CI",
        Path: ".github/workflows/ci.yml",
        RunNumber: 128,
        RunAttempt: 1,
        DisplayTitle: "Fix login",
        Event: "push",
        Status: "completed",
        Conclusion: "success",
        HeadBranch: "main",
        HeadSha: Sha,
        HeadRepoId: RepoId,
        HeadRepoFullName: "octo-org/sample-app",
        HeadRepoHtmlUrl: RepoHtmlUrl,
        ActorId: 9,
        ActorLogin: "alice",
        TriggeringActorLogin: "alice",
        PullRequestNumbers: [],
        RunStartedAt: CreatedAt,
        CreatedAt: CreatedAt,
        UpdatedAt: CreatedAt.AddMinutes(3),
        HtmlUrl: RepoHtmlUrl + "/actions/runs/5001");

    public static PullRequestInfo PullRequest(int number, string headSha, long? headRepoId, long repoId = RepoId) => new(
        RepoId: repoId,
        Number: number,
        HeadRepoId: headRepoId,
        HeadRepoFullName: headRepoId == ForkRepoId ? "fork-user/sample-app" : "octo-org/sample-app",
        HeadRef: "feature/login",
        HeadSha: headSha,
        HtmlUrl: $"{RepoHtmlUrl}/pull/{number}");
}
