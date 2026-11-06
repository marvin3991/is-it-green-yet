using IsItGreenYet.Core.Filtering;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Filtering;

public class RunFilterTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    private static readonly RepoInfo SampleApp = Repo(2001, "sample-app");

    private static readonly RepoInfo SampleDocs = Repo(2002, "sample-docs");

    /// <summary>
    /// 1 追蹤中的失敗、2 PR 的成功、3 排程進行中、4 等待核准、5 未追蹤的失敗、6 取消且分支與 actor 缺值、7 中英混合標題。
    /// </summary>
    private static readonly IReadOnlyList<RunRow> Rows =
    [
        Row(1, SampleApp, "CI", "push", "completed", "failure", "main", "alice", "Fix login", trackedFailure: true),
        Row(2, SampleApp, "Lint", "pull_request", "completed", "success", "feature/login", "bob", "建置流程調整"),
        Row(3, SampleDocs, "CI", "schedule", "in_progress", null, "main", "alice", "Nightly build"),
        Row(4, SampleDocs, "Deploy", "workflow_dispatch", "waiting", null, "release/1.0", "carol", "部署到正式環境"),
        Row(5, SampleApp, "CI", "push", "completed", "failure", "old-branch", "bob", "Old failure"),
        Row(6, SampleDocs, "CI", "push", "completed", "cancelled", null, null, "Docs cleanup"),
        Row(7, SampleApp, "Release", "push", "completed", "success", "main", "alice", "修正 CI 建置腳本"),
    ];

    [Fact]
    public void 同欄位OR()
    {
        var workflows = RunFilter.Apply(Rows, RunFilterCriteria.Empty with { Workflows = Set("Lint", "Deploy") });
        var statuses = RunFilter.Apply(Rows, RunFilterCriteria.Empty with { Statuses = new HashSet<DisplayState> { DisplayState.Failure, DisplayState.Waiting } });
        var events = RunFilter.Apply(Rows, RunFilterCriteria.Empty with { Events = Set("schedule", "workflow_dispatch") });

        Assert.Equal(new long[] { 2, 4 }, Ids(workflows));
        Assert.Equal(new long[] { 1, 4, 5 }, Ids(statuses));
        Assert.Equal(new long[] { 3, 4 }, Ids(events));
    }

    [Fact]
    public void 跨欄位AND()
    {
        var criteria = RunFilterCriteria.Empty with
        {
            Workflows = Set("CI", "Release"),
            Branches = Set("main"),
            Actors = Set("alice", "carol"),
        };

        var result = RunFilter.Apply(Rows, criteria);

        Assert.Equal(new long[] { 1, 3, 7 }, Ids(result));
        // 再加一欄 Repo，必須同時符合。
        Assert.Equal(new long[] { 3 }, Ids(RunFilter.Apply(Rows, criteria with { Repos = Set("octo-org/sample-docs") })));
    }

    [Fact]
    public void Workflow以名稱比對跨repo同名一起選到()
    {
        var result = RunFilter.Apply(Rows, RunFilterCriteria.Empty with { Workflows = Set("CI") });

        Assert.Equal(new long[] { 1, 3, 5, 6 }, Ids(result));
    }

    [Theory]
    [InlineData("Ci", new long[] { 1, 3, 5, 6, 7 })]
    [InlineData("ci", new long[] { 1, 3, 5, 6, 7 })]
    [InlineData("建置", new long[] { 2, 7 })]
    [InlineData("ci 建置", new long[] { 7 })]
    [InlineData("NIGHTLY", new long[] { 3 })]
    public void 搜尋不分大小寫含中英(string search, long[] expected)
    {
        var result = RunFilter.Apply(Rows, RunFilterCriteria.Empty with { SearchText = search });

        Assert.Equal(expected, Ids(result));
    }

    [Theory]
    [InlineData("sample-docs", new long[] { 3, 4, 6 })]
    [InlineData("Nightly", new long[] { 3 })]
    [InlineData("Deploy", new long[] { 4 })]
    [InlineData("release/1.0", new long[] { 4 })]
    [InlineData("feature/", new long[] { 2 })]
    // actor 與 event 不在搜尋範圍（spec §10.4 只比對四個欄位）。
    [InlineData("carol", new long[0])]
    [InlineData("workflow_dispatch", new long[0])]
    public void 搜尋比對repo標題workflow分支(string search, long[] expected)
    {
        var result = RunFilter.Apply(Rows, RunFilterCriteria.Empty with { SearchText = search });

        Assert.Equal(expected, Ids(result));
    }

    [Fact]
    public void 只看需要注意()
    {
        var result = RunFilter.Apply(Rows, RunFilterCriteria.Empty with { AttentionOnly = true });

        // 追蹤中的失敗，加上所有未完成與等待核准的 run（Q13）；未追蹤的失敗、成功、取消不算。
        Assert.Equal(new long[] { 1, 3, 4 }, Ids(result));
    }

    [Fact]
    public void 只看需要注意可以和其他條件一起用()
    {
        var criteria = RunFilterCriteria.Empty with { AttentionOnly = true, Repos = Set("octo-org/sample-docs"), SearchText = "部署" };

        var result = RunFilter.Apply(Rows, criteria);

        Assert.Equal(new long[] { 4 }, Ids(result));
    }

    [Fact]
    public void 空條件回原清單()
    {
        var result = RunFilter.Apply(Rows, RunFilterCriteria.Empty);

        Assert.Equal(Rows, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 只有空白的搜尋文字視為沒有搜尋(string search)
    {
        var result = RunFilter.Apply(Rows, RunFilterCriteria.Empty with { SearchText = search });

        Assert.Equal(Rows, result);
    }

    [Fact]
    public void 搜尋文字去掉頭尾空白()
    {
        var result = RunFilter.Apply(Rows, RunFilterCriteria.Empty with { SearchText = "  Nightly  " });

        Assert.Equal(new long[] { 3 }, Ids(result));
    }

    [Fact]
    public void 沒有分支或觸發者的列不符合該欄的篩選()
    {
        var branches = RunFilter.Apply(Rows, RunFilterCriteria.Empty with { Branches = Set("main") });
        var actors = RunFilter.Apply(Rows, RunFilterCriteria.Empty with { Actors = Set("alice") });
        // head_branch 為 null 的列在搜尋時不丟例外。
        var search = RunFilter.Apply(Rows, RunFilterCriteria.Empty with { SearchText = "cleanup" });

        Assert.Equal(new long[] { 1, 3, 7 }, Ids(branches));
        Assert.Equal(new long[] { 1, 3, 7 }, Ids(actors));
        Assert.Equal(new long[] { 6 }, Ids(search));
    }

    [Fact]
    public void 已勾選但清單裡沒有的值只是沒有符合的列()
    {
        var result = RunFilter.Apply(Rows, RunFilterCriteria.Empty with { Repos = Set("octo-org/removed-repo") });

        Assert.Empty(result);
    }

    [Fact]
    public void 篩選不改動原清單()
    {
        var before = Ids(Rows);

        RunFilter.Apply(Rows, RunFilterCriteria.Empty with { AttentionOnly = true, Workflows = Set("CI") });

        // 摘要列的「N 筆執行」以原清單計算，篩選不影響（spec §7.4、§10.4）。
        Assert.Equal(before, Ids(Rows));
    }

    [Fact]
    public void 空清單回空結果()
    {
        Assert.Empty(RunFilter.Apply([], RunFilterCriteria.Empty with { SearchText = "CI" }));
        Assert.Empty(RunFilter.Apply([], RunFilterCriteria.Empty));
    }

    [Fact]
    public void 參數為null時丟例外()
    {
        Assert.Throws<ArgumentNullException>(() => RunFilter.Apply(null!, RunFilterCriteria.Empty));
        Assert.Throws<ArgumentNullException>(() => RunFilter.Apply(Rows, null!));
    }

    private static long[] Ids(IEnumerable<RunRow> rows) => [.. rows.Select(r => r.Run.Id)];

    private static HashSet<string> Set(params string[] values) => [.. values];

    private static RepoInfo Repo(long id, string name) => new(
        Id: id, FullName: $"octo-org/{name}", Name: name, OwnerId: 1, OwnerLogin: "octo-org", OwnerType: "Organization",
        IsPrivate: false, IsFork: false, IsArchived: false, IsDisabled: false, DefaultBranch: "main",
        HtmlUrl: $"https://github.com/octo-org/{name}", PushedAt: Start, Category: RepoCategory.Organization);

    private static RunRow Row(
        long id, RepoInfo repo, string workflow, string @event, string status, string? conclusion,
        string? branch, string? actor, string title, bool trackedFailure = false)
    {
        var run = new RunInfo(
            Id: id, RepoId: repo.Id, WorkflowId: 3000 + id, WorkflowName: workflow, Path: ".github/workflows/ci.yml",
            RunNumber: (int)id, RunAttempt: 1, DisplayTitle: title, Event: @event, Status: status, Conclusion: conclusion,
            HeadBranch: branch, HeadSha: "0123456789abcdef0123456789abcdef01234567", HeadRepoId: repo.Id,
            HeadRepoFullName: repo.FullName, HeadRepoHtmlUrl: repo.HtmlUrl, ActorId: actor is null ? null : 500 + id,
            ActorLogin: actor, TriggeringActorLogin: actor, PullRequestNumbers: [], RunStartedAt: Start.AddMinutes(-id),
            CreatedAt: Start.AddMinutes(-id), UpdatedAt: Start, HtmlUrl: $"{repo.HtmlUrl}/actions/runs/{id}");
        return new RunRow(run, repo, RunStateMapper.Map(run), workflow, trackedFailure);
    }
}
