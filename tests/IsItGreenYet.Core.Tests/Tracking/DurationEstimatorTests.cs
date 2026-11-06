using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Tracking;

public class DurationEstimatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    private static readonly RunInfo Template = new(
        Id: 101, RepoId: 1, WorkflowId: 11, WorkflowName: "CI", Path: ".github/workflows/ci.yml",
        RunNumber: 128, RunAttempt: 1, DisplayTitle: "Fix login", Event: "push", Status: "in_progress", Conclusion: null,
        HeadBranch: "main", HeadSha: "dcf27ad", HeadRepoId: 1, HeadRepoFullName: "octo-org/sample-app",
        HeadRepoHtmlUrl: "https://github.com/octo-org/sample-app", ActorId: 9, ActorLogin: "alice",
        TriggeringActorLogin: "alice", PullRequestNumbers: [], RunStartedAt: Now.AddMinutes(-5),
        CreatedAt: Now.AddMinutes(-6), UpdatedAt: Now, HtmlUrl: "https://github.com/octo-org/sample-app/actions/runs/101");

    private static RunInfo InProgress(DateTimeOffset? runStartedAt) =>
        Template with { Status = "in_progress", Conclusion = null, RunStartedAt = runStartedAt };

    private static IReadOnlyList<TimeSpan> Minutes(params int[] minutes) =>
        minutes.Select(m => TimeSpan.FromMinutes(m)).ToList();

    private static IReadOnlyList<TimeSpan> Same(TimeSpan sample, int count) =>
        Enumerable.Repeat(sample, count).ToList();

    // ── 中位數（§8.2）───────────────────────────────────────────────────

    [Fact]
    public void 樣本0筆不預估()
    {
        Assert.Null(DurationEstimator.Median([]));

        var eta = DurationEstimator.Estimate(InProgress(Now.AddMinutes(-4)), [], Now);

        Assert.NotNull(eta);
        Assert.Equal(TimeSpan.FromMinutes(4), eta.Elapsed);
        Assert.Null(eta.Median);
        Assert.Null(eta.Remaining);
        Assert.Null(eta.Overrun);
        Assert.Null(eta.Progress);
    }

    [Fact]
    public void 樣本2筆不預估()
    {
        var samples = Minutes(5, 6);

        Assert.Null(DurationEstimator.Median(samples));

        var eta = DurationEstimator.Estimate(InProgress(Now.AddMinutes(-4)), samples, Now);

        Assert.NotNull(eta);
        Assert.Equal(TimeSpan.FromMinutes(4), eta.Elapsed);
        Assert.Null(eta.Median);
        Assert.Null(eta.Remaining);
        Assert.Null(eta.Overrun);
        Assert.Null(eta.Progress);
    }

    [Fact]
    public void 樣本3筆取中位數()
    {
        // 傳入順序是新到舊，不是大小順序。
        Assert.Equal(TimeSpan.FromMinutes(6), DurationEstimator.Median(Minutes(7, 5, 6)));
    }

    [Fact]
    public void 偶數筆取中間兩筆平均()
    {
        var samples = new[] { 40, 10, 30, 20 }.Select(s => TimeSpan.FromSeconds(s)).ToList();

        Assert.Equal(TimeSpan.FromSeconds(25), DurationEstimator.Median(samples));
    }

    [Fact]
    public void 剛好10筆全部採用()
    {
        // 第 10 筆（最舊）被排除的話，中位數會是 5m。
        var samples = Minutes(1, 2, 3, 4, 5, 6, 7, 8, 9, 100);

        Assert.Equal(TimeSpan.FromSeconds(330), DurationEstimator.Median(samples));
    }

    [Fact]
    public void 超過10筆只取最近10筆()
    {
        // 第 11 筆是極端值；算進去的話中位數會變成 6m。
        var samples = Minutes(1, 2, 3, 4, 5, 6, 7, 8, 9, 10).Append(TimeSpan.FromHours(1000)).ToList();

        Assert.Equal(TimeSpan.FromSeconds(330), DurationEstimator.Median(samples));
    }

    [Fact]
    public void 負的樣本當成0()
    {
        // 和完成耗時「負值當 0」同一條規則；不夾住的話中位數會是 -1m。
        Assert.Equal(TimeSpan.Zero, DurationEstimator.Median(Minutes(-5, -1, 4)));
    }

    [Fact]
    public void 極大樣本取平均不溢位()
    {
        var samples = Same(TimeSpan.MaxValue, 4);

        Assert.Equal(TimeSpan.MaxValue, DurationEstimator.Median(samples));
    }

    [Fact]
    public void 不改動呼叫端的樣本清單()
    {
        var samples = new[] { TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6) };

        DurationEstimator.Median(samples);

        Assert.Equal(new[] { TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6) }, samples);
    }

    // ── 進行中的預估（§8.2）─────────────────────────────────────────────

    [Fact]
    public void 在預估內回傳剩餘時間與進度()
    {
        var eta = DurationEstimator.Estimate(InProgress(Now - TimeSpan.FromSeconds(252)), Same(TimeSpan.FromMinutes(7), 3), Now);

        Assert.NotNull(eta);
        Assert.Equal(TimeSpan.FromSeconds(252), eta.Elapsed);
        Assert.Equal(TimeSpan.FromMinutes(7), eta.Median);
        Assert.Equal(TimeSpan.FromSeconds(168), eta.Remaining);
        Assert.Null(eta.Overrun);
        Assert.NotNull(eta.Progress);
        Assert.Equal(0.6, eta.Progress.Value, precision: 10);
    }

    [Fact]
    public void 剩餘時間保留原始精度由顯示層進位()
    {
        // 「約剩 3m」的無條件進位是 DisplayFormatter.Approx 的事，這裡回傳實際差值 2m10s。
        var eta = DurationEstimator.Estimate(InProgress(Now - TimeSpan.FromSeconds(170)), Same(TimeSpan.FromMinutes(5), 3), Now);

        Assert.NotNull(eta);
        Assert.Equal(TimeSpan.FromSeconds(130), eta.Remaining);
    }

    [Fact]
    public void 剛好等於預估算在預估內()
    {
        var eta = DurationEstimator.Estimate(InProgress(Now.AddMinutes(-3)), Same(TimeSpan.FromMinutes(3), 3), Now);

        Assert.NotNull(eta);
        Assert.Equal(TimeSpan.Zero, eta.Remaining);
        Assert.Null(eta.Overrun);
        Assert.Equal(1.0, eta.Progress);
    }

    [Fact]
    public void 超過預估回傳Overrun()
    {
        var median = TimeSpan.FromSeconds(400);
        var eta = DurationEstimator.Estimate(InProgress(Now - TimeSpan.FromSeconds(580)), Same(median, 3), Now);

        Assert.NotNull(eta);
        Assert.Equal(TimeSpan.FromSeconds(580), eta.Elapsed);
        Assert.Equal(median, eta.Median);
        Assert.Null(eta.Remaining);
        Assert.Equal(TimeSpan.FromMinutes(3), eta.Overrun);
        // 超過預估時進度條滿格（改用 Warning 色由 App 決定）。
        Assert.Equal(1.0, eta.Progress);
    }

    [Fact]
    public void 預估為0時不出現NaN()
    {
        var zeros = Same(TimeSpan.Zero, 3);

        var atStart = DurationEstimator.Estimate(InProgress(Now), zeros, Now);
        var later = DurationEstimator.Estimate(InProgress(Now.AddSeconds(-10)), zeros, Now);

        Assert.NotNull(atStart);
        Assert.Equal(TimeSpan.Zero, atStart.Remaining);
        Assert.Null(atStart.Overrun);
        Assert.Equal(1.0, atStart.Progress);

        Assert.NotNull(later);
        Assert.Null(later.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(10), later.Overrun);
        Assert.Equal(1.0, later.Progress);
    }

    [Fact]
    public void 極大預估值的進度仍在0到1之間()
    {
        var eta = DurationEstimator.Estimate(InProgress(Now.AddMinutes(-1)), Same(TimeSpan.MaxValue, 3), Now);

        Assert.NotNull(eta);
        Assert.Equal(TimeSpan.MaxValue - TimeSpan.FromMinutes(1), eta.Remaining);
        Assert.NotNull(eta.Progress);
        Assert.InRange(eta.Progress.Value, 0.0, 1.0);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("requested")]
    [InlineData("pending")]
    public void 排隊中回null(string status)
    {
        var run = Template with { Status = status, Conclusion = null, RunStartedAt = Now.AddMinutes(-2) };

        Assert.Null(DurationEstimator.Estimate(run, Same(TimeSpan.FromMinutes(3), 3), Now));
    }

    [Theory]
    [InlineData("completed", "success")]
    [InlineData("completed", "failure")]
    [InlineData("completed", "cancelled")]
    [InlineData("completed", "action_required")]
    [InlineData("waiting", null)]
    [InlineData("brand_new_status", null)]
    [InlineData(null, null)]
    public void 只有進行中的run才預估(string? status, string? conclusion)
    {
        var run = Template with { Status = status, Conclusion = conclusion };

        Assert.Null(DurationEstimator.Estimate(run, Same(TimeSpan.FromMinutes(3), 3), Now));
    }

    [Fact]
    public void 開始時間在未來時elapsed為0()
    {
        // GitHub 的時鐘比本機快 30 秒：不得出現負的已執行時間或大於預估的剩餘時間。
        var run = InProgress(Now.AddSeconds(30));

        var withSamples = DurationEstimator.Estimate(run, Same(TimeSpan.FromMinutes(3), 3), Now);
        var withoutSamples = DurationEstimator.Estimate(run, [], Now);

        Assert.NotNull(withSamples);
        Assert.Equal(TimeSpan.Zero, withSamples.Elapsed);
        Assert.Equal(TimeSpan.FromMinutes(3), withSamples.Remaining);
        Assert.Null(withSamples.Overrun);
        Assert.Equal(0.0, withSamples.Progress);

        Assert.NotNull(withoutSamples);
        Assert.Equal(TimeSpan.Zero, withoutSamples.Elapsed);
    }

    [Fact]
    public void 進行中的run_started_at缺值也改用created_at()
    {
        var run = Template with { Status = "in_progress", RunStartedAt = null, CreatedAt = Now.AddMinutes(-4) };

        var eta = DurationEstimator.Estimate(run, [], Now);

        Assert.NotNull(eta);
        Assert.Equal(TimeSpan.FromMinutes(4), eta.Elapsed);
    }

    [Fact]
    public void 不同時區的時間戳相減結果一樣()
    {
        var run = InProgress(Now - TimeSpan.FromSeconds(252));
        var taipeiNow = Now.ToOffset(TimeSpan.FromHours(8));

        var eta = DurationEstimator.Estimate(run, [], taipeiNow);

        Assert.NotNull(eta);
        Assert.Equal(TimeSpan.FromSeconds(252), eta.Elapsed);
    }

    // ── 已完成的耗時（§8.1）─────────────────────────────────────────────

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("timed_out")]
    [InlineData("cancelled")]
    [InlineData("skipped")]
    [InlineData("brand_new")]
    public void 完成耗時是updated_at減run_started_at(string conclusion)
    {
        // created_at 比 run_started_at 早很多（例如重跑），不得拿來算。
        var run = Template with
        {
            Status = "completed",
            Conclusion = conclusion,
            CreatedAt = Now.AddHours(-2),
            RunStartedAt = Now.AddMinutes(-10),
            UpdatedAt = Now.AddMinutes(-10) + TimeSpan.FromSeconds(192),
        };

        Assert.Equal(TimeSpan.FromSeconds(192), DurationEstimator.CompletedDuration(run));
    }

    [Theory]
    [InlineData("queued", null)]
    [InlineData("in_progress", null)]
    [InlineData("waiting", null)]
    [InlineData("completed", "action_required")]
    [InlineData("brand_new_status", null)]
    [InlineData(null, null)]
    public void 未完成的run沒有完成耗時(string? status, string? conclusion)
    {
        var run = Template with { Status = status, Conclusion = conclusion };

        Assert.Null(DurationEstimator.CompletedDuration(run));
    }

    [Fact]
    public void run_started_at缺值改用created_at()
    {
        var run = Template with
        {
            Status = "completed",
            Conclusion = "success",
            RunStartedAt = null,
            CreatedAt = Now.AddMinutes(-10),
            UpdatedAt = Now.AddMinutes(-7),
        };

        Assert.Equal(TimeSpan.FromMinutes(3), DurationEstimator.CompletedDuration(run));
    }

    [Fact]
    public void 完成時間早於開始時間時耗時為0()
    {
        var run = Template with
        {
            Status = "completed",
            Conclusion = "success",
            RunStartedAt = Now,
            UpdatedAt = Now.AddSeconds(-5),
        };

        Assert.Equal(TimeSpan.Zero, DurationEstimator.CompletedDuration(run));
    }

    [Fact]
    public void 完成耗時不受時間戳的時區影響()
    {
        var run = Template with
        {
            Status = "completed",
            Conclusion = "success",
            RunStartedAt = Now,
            UpdatedAt = (Now + TimeSpan.FromSeconds(45)).ToOffset(TimeSpan.FromHours(8)),
        };

        Assert.Equal(TimeSpan.FromSeconds(45), DurationEstimator.CompletedDuration(run));
    }

    // ── 排隊時間（§8.2）─────────────────────────────────────────────────

    [Fact]
    public void 第1次嘗試的run_started_at等於created_at時排隊時間不變()
    {
        // 第 1 次嘗試的 run_started_at 和 created_at 相同：結果和第一階段的 now − created_at 一樣。
        var queuedAt = Now - TimeSpan.FromSeconds(65);
        var run = Template with { Status = "queued", RunAttempt = 1, CreatedAt = queuedAt, RunStartedAt = queuedAt };

        Assert.Equal(TimeSpan.FromSeconds(65), DurationEstimator.QueuedDuration(run, Now));
    }

    [Fact]
    public void 重跑剛送出時run_attempt還是1也從新的run_started_at算()
    {
        // 重跑剛送出的過渡期：回應的 run_attempt 還是 1，run_started_at 已經是這次嘗試的開始時間（比 created_at 晚 30 分鐘）。
        var attemptStarted = Now - TimeSpan.FromSeconds(65);
        var run = Template with
        {
            Status = "queued",
            RunAttempt = 1,
            CreatedAt = attemptStarted.AddMinutes(-30),
            RunStartedAt = attemptStarted,
        };

        Assert.Equal(TimeSpan.FromSeconds(65), DurationEstimator.QueuedDuration(run, Now));
    }

    [Fact]
    public void 重跑的排隊時間從這次嘗試的run_started_at算()
    {
        // 重跑時 GitHub 保留最初的 created_at，run_started_at 換成這次嘗試的開始時間（比 created_at 晚 30 分鐘）。
        var attemptStarted = Now - TimeSpan.FromSeconds(65);
        var run = Template with
        {
            Status = "queued",
            RunAttempt = 2,
            CreatedAt = attemptStarted.AddMinutes(-30),
            RunStartedAt = attemptStarted,
        };

        Assert.Equal(TimeSpan.FromSeconds(65), DurationEstimator.QueuedDuration(run, Now));
    }

    [Fact]
    public void run_started_at缺值時排隊時間退回created_at()
    {
        var run = Template with { Status = "queued", RunAttempt = 2, RunStartedAt = null, CreatedAt = Now.AddMinutes(-4) };

        Assert.Equal(TimeSpan.FromMinutes(4), DurationEstimator.QueuedDuration(run, Now));
    }

    [Fact]
    public void run_started_at在未來時排隊時間為0()
    {
        // GitHub 的時鐘比本機快 30 秒：不得出現負的排隊時間。
        var run = Template with { Status = "queued", RunAttempt = 2, CreatedAt = Now.AddMinutes(-30), RunStartedAt = Now.AddSeconds(30) };

        Assert.Equal(TimeSpan.Zero, DurationEstimator.QueuedDuration(run, Now));
    }

    // ── 參數檢查 ───────────────────────────────────────────────────────

    [Fact]
    public void null參數丟ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => DurationEstimator.CompletedDuration(null!));
        Assert.Throws<ArgumentNullException>(() => DurationEstimator.Median(null!));
        Assert.Throws<ArgumentNullException>(() => DurationEstimator.Estimate(null!, [], Now));
        Assert.Throws<ArgumentNullException>(() => DurationEstimator.Estimate(InProgress(Now), null!, Now));
        Assert.Throws<ArgumentNullException>(() => DurationEstimator.QueuedDuration(null!, Now));
    }
}
