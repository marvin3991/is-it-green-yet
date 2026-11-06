using System.Globalization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Tracking;

public class StatusAggregatorTests
{
    private const long RepoA = 100;
    private const long RepoB = 101;

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlySet<long> NoStaleRepos = new HashSet<long>();

    // ── spec §7.3 整體狀態 ──────────────────────────────────────────────

    [Fact]
    public void 失敗優先於進行中()
    {
        var keys = new[]
        {
            Key(workflowId: 1, failing: true, lastDecisive: RunCategory.Failure),
            Key(workflowId: 2, pending: true, lastDecisive: RunCategory.Success),
            Key(workflowId: 3, lastDecisive: RunCategory.Success),
        };

        var summary = StatusAggregator.Aggregate(keys, NoStaleRepos, ConnectionIssue.None);

        Assert.Equal(OverallState.Failure, summary.State);
        Assert.Equal(1, summary.PendingCount);
        Assert.Equal(1, summary.FailingCount);
    }

    [Fact]
    public void 只有灰色或未知的鍵算無資料()
    {
        var keys = new[]
        {
            Key(workflowId: 1, lastDecisive: null),
            Key(workflowId: 2, lastDecisive: null),
        };

        var summary = StatusAggregator.Aggregate(keys, NoStaleRepos, ConnectionIssue.None);

        Assert.Equal(OverallState.NoData, summary.State);
        Assert.Equal(0, summary.PendingCount);
        Assert.Equal(0, summary.FailingCount);
    }

    [Fact]
    public void 一個成功其餘灰色算綠()
    {
        var keys = new[]
        {
            Key(workflowId: 1, lastDecisive: RunCategory.Success),
            Key(workflowId: 2, lastDecisive: null),
            Key(workflowId: 3, lastDecisive: null),
        };

        var summary = StatusAggregator.Aggregate(keys, NoStaleRepos, ConnectionIssue.None);

        Assert.Equal(OverallState.Success, summary.State);
    }

    [Fact]
    public void 沒有任何鍵算無資料()
    {
        var summary = StatusAggregator.Aggregate([], NoStaleRepos, ConnectionIssue.None);

        Assert.Equal(new StatusSummary(OverallState.NoData, 0, 0, 0, ConnectionIssue.None), summary);
    }

    [Fact]
    public void 無法更新的repo不計進行中但失敗仍計()
    {
        var keys = new[]
        {
            Key(workflowId: 1, repoId: RepoA, pending: true, failing: true, lastDecisive: RunCategory.Failure),
            Key(workflowId: 2, repoId: RepoA, pending: true, lastDecisive: RunCategory.Success),
            Key(workflowId: 3, repoId: RepoB, pending: true, lastDecisive: RunCategory.Success),
        };

        var summary = StatusAggregator.Aggregate(keys, new HashSet<long> { RepoA }, ConnectionIssue.None);

        Assert.Equal(OverallState.Failure, summary.State);
        Assert.Equal(1, summary.PendingCount);
        Assert.Equal(1, summary.FailingCount);
        Assert.Equal(1, summary.StaleRepoCount);
    }

    [Fact]
    public void 連線問題不改變狀態色()
    {
        const ConnectionIssue issues = ConnectionIssue.Offline | ConnectionIssue.Unauthorized;
        var scenarios = new (KeyEvaluation[] Keys, OverallState Expected)[]
        {
            ([Key(workflowId: 1, failing: true, lastDecisive: RunCategory.Failure)], OverallState.Failure),
            ([Key(workflowId: 1, pending: true)], OverallState.Pending),
            ([Key(workflowId: 1, lastDecisive: RunCategory.Success)], OverallState.Success),
            ([], OverallState.NoData),
        };

        foreach (var (keys, expected) in scenarios)
        {
            var withIssues = StatusAggregator.Aggregate(keys, NoStaleRepos, issues);
            var withoutIssues = StatusAggregator.Aggregate(keys, NoStaleRepos, ConnectionIssue.None);

            Assert.Equal(expected, withIssues.State);
            Assert.Equal(withoutIssues.State, withIssues.State);
            Assert.Equal(issues, withIssues.Issues);
        }
    }

    [Fact]
    public void tooltip文字格式()
    {
        var summary = new StatusSummary(OverallState.Pending, 2, 0, 0, ConnectionIssue.None);
        var lastUpdatedLocal = new DateTimeOffset(2026, 10, 24, 10, 7, 30, TimeSpan.FromHours(8));

        var text = StatusAggregator.TrayTooltip(summary, lastUpdatedLocal, null);

        Assert.Equal("IsItGreenYet\n2 進行中 · 0 失敗 · 更新於 10:07", text);
    }

    // ── 補充：邊界與例外 ────────────────────────────────────────────────

    [Fact]
    public void 只有進行中算黃燈()
    {
        var keys = new[]
        {
            Key(workflowId: 1, pending: true, lastDecisive: null),
            Key(workflowId: 2, lastDecisive: RunCategory.Success),
        };

        var summary = StatusAggregator.Aggregate(keys, NoStaleRepos, ConnectionIssue.None);

        Assert.Equal(OverallState.Pending, summary.State);
        Assert.Equal(1, summary.PendingCount);
        Assert.Equal(0, summary.FailingCount);
    }

    [Fact]
    public void 失敗與進行中同時成立時兩者都計()
    {
        // 例如 #10 失敗、#11 正在跑：同一個鍵既 pending 又 failing。
        var keys = new[] { Key(workflowId: 1, pending: true, failing: true, lastDecisive: RunCategory.Failure) };

        var summary = StatusAggregator.Aggregate(keys, NoStaleRepos, ConnectionIssue.None);

        Assert.Equal(OverallState.Failure, summary.State);
        Assert.Equal(1, summary.PendingCount);
        Assert.Equal(1, summary.FailingCount);
    }

    [Fact]
    public void 超過90天的舊失敗不亮紅也不亮綠()
    {
        var keys = new[] { Key(workflowId: 1, failing: false, lastDecisive: RunCategory.Failure) };

        var summary = StatusAggregator.Aggregate(keys, NoStaleRepos, ConnectionIssue.None);

        Assert.Equal(OverallState.NoData, summary.State);
        Assert.Equal(0, summary.FailingCount);
    }

    [Fact]
    public void 無法更新的repo只剩進行中時不算黃燈()
    {
        var keys = new[]
        {
            Key(workflowId: 1, repoId: RepoA, pending: true, lastDecisive: RunCategory.Success),
            Key(workflowId: 2, repoId: RepoB, lastDecisive: null),
        };

        var summary = StatusAggregator.Aggregate(keys, new HashSet<long> { RepoA }, ConnectionIssue.None);

        Assert.Equal(OverallState.Success, summary.State);
        Assert.Equal(0, summary.PendingCount);
    }

    [Fact]
    public void 無法更新的repo數以集合大小計()
    {
        // 沒有鍵的 repo（例如整個 repo 都抓不到資料）也算進「N 個 repo 無法更新」。
        var summary = StatusAggregator.Aggregate([], new HashSet<long> { RepoA, RepoB }, ConnectionIssue.None);

        Assert.Equal(2, summary.StaleRepoCount);
        Assert.Equal(OverallState.NoData, summary.State);
    }

    [Fact]
    public void 重複的鍵只算一次()
    {
        var keys = new[]
        {
            Key(workflowId: 1, pending: true, failing: true, lastDecisive: RunCategory.Failure),
            Key(workflowId: 1, pending: true, failing: true, lastDecisive: RunCategory.Failure),
        };

        var summary = StatusAggregator.Aggregate(keys, NoStaleRepos, ConnectionIssue.None);

        Assert.Equal(1, summary.PendingCount);
        Assert.Equal(1, summary.FailingCount);
    }

    [Fact]
    public void tooltip沒有更新時間時寫尚未更新()
    {
        var summary = new StatusSummary(OverallState.NoData, 0, 0, 0, ConnectionIssue.None);

        var text = StatusAggregator.TrayTooltip(summary, null, null);

        Assert.Equal("IsItGreenYet\n0 進行中 · 0 失敗 · 尚未更新", text);
    }

    [Fact]
    public void tooltip有原因時再加一行()
    {
        var summary = new StatusSummary(OverallState.Failure, 1, 3, 2, ConnectionIssue.Offline);
        var lastUpdatedLocal = new DateTimeOffset(2026, 10, 24, 23, 59, 0, TimeSpan.FromHours(8));

        var text = StatusAggregator.TrayTooltip(summary, lastUpdatedLocal, "離線");

        Assert.Equal("IsItGreenYet\n1 進行中 · 3 失敗 · 更新於 23:59\n離線", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void tooltip原因空白時不加行(string issueLine)
    {
        var summary = new StatusSummary(OverallState.Success, 0, 0, 0, ConnectionIssue.None);

        var text = StatusAggregator.TrayTooltip(summary, null, issueLine);

        Assert.Equal("IsItGreenYet\n0 進行中 · 0 失敗 · 尚未更新", text);
    }

    [Fact]
    public void tooltip的時間不轉時區也不受文化設定影響()
    {
        var original = CultureInfo.CurrentCulture;
        var dottedTime = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        dottedTime.DateTimeFormat.TimeSeparator = ".";
        try
        {
            CultureInfo.CurrentCulture = dottedTime;
            var summary = new StatusSummary(OverallState.Pending, 2, 0, 0, ConnectionIssue.None);
            // 呼叫端已轉成本地時間（-05:00）；這裡照原樣顯示，不再轉換。
            var lastUpdatedLocal = new DateTimeOffset(2026, 10, 23, 21, 7, 0, TimeSpan.FromHours(-5));

            var text = StatusAggregator.TrayTooltip(summary, lastUpdatedLocal, null);

            Assert.Equal("IsItGreenYet\n2 進行中 · 0 失敗 · 更新於 21:07", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void 參數為null時丟出ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => StatusAggregator.Aggregate(null!, NoStaleRepos, ConnectionIssue.None));
        Assert.Throws<ArgumentNullException>(() => StatusAggregator.Aggregate([], null!, ConnectionIssue.None));
        Assert.Throws<ArgumentNullException>(() => StatusAggregator.TrayTooltip(null!, null, null));
    }

    // ── 測試資料（手寫假資料）────────────────────────────────────────────

    private static KeyEvaluation Key(
        long workflowId,
        long repoId = RepoA,
        bool pending = false,
        bool failing = false,
        RunCategory? lastDecisive = null)
    {
        var run = new RunInfo(
            Id: workflowId * 1000, RepoId: repoId, WorkflowId: workflowId, WorkflowName: "CI",
            Path: ".github/workflows/ci.yml", RunNumber: 1, RunAttempt: 1, DisplayTitle: "Run", Event: "push",
            Status: pending ? "in_progress" : "completed", Conclusion: pending ? null : "success",
            HeadBranch: "main", HeadSha: "aaa", HeadRepoId: repoId, HeadRepoFullName: "octo-org/sample-app",
            HeadRepoHtmlUrl: "https://github.com/octo-org/sample-app", ActorId: 9, ActorLogin: "alice",
            TriggeringActorLogin: "alice", PullRequestNumbers: [], RunStartedAt: Now, CreatedAt: Now, UpdatedAt: Now,
            HtmlUrl: "https://github.com/octo-org/sample-app/actions/runs/1");

        return new KeyEvaluation(
            new TrackingKey(repoId, workflowId, repoId, "main"),
            KeyKind.DefaultBranch,
            run,
            pending,
            lastDecisive,
            lastDecisive is null ? [] : [run.Id],
            lastDecisive is null ? null : Now,
            failing);
    }
}
