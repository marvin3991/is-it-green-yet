using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Polling;

namespace IsItGreenYet.Core.Tests.Polling;

public class IntervalPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    // spec §6.2 可重算的例子（k = 1，未鎖定）。
    [Theory]
    [InlineData(5 * 60, 30)]
    [InlineData(15 * 60, 30)]
    [InlineData(60 * 60, 120)]
    [InlineData(150 * 60, 300)]
    [InlineData(24 * 60 * 60, 300)]
    public void 安靜repo的間隔照spec例子表(int secondsSinceLastActivity, int expectedSeconds)
    {
        var interval = IntervalPolicy.RepoRunsInterval(
            repoRunning: false,
            lastActivity: Now.AddSeconds(-secondsSinceLastActivity),
            now: Now,
            mode: RefreshMode.Auto,
            k: 1,
            locked: false);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), interval);
    }

    [Fact]
    public void repoRunning為15秒()
    {
        // 不論 lastActivity 多久以前、甚至沒有，只要有未完成的 run 就是 15 秒。
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            IntervalPolicy.RepoRunsInterval(true, Now.AddDays(-30), Now, RefreshMode.Auto, 1, false));
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            IntervalPolicy.RepoRunsInterval(true, null, Now, RefreshMode.Auto, 1, false));
    }

    [Fact]
    public void pushed_at算進lastActivity()
    {
        var oldRun = Run(createdAt: Now.AddDays(-3), updatedAt: Now.AddDays(-3).AddMinutes(5));
        var pushedAt = Now.AddMinutes(-10);

        var lastActivity = IntervalPolicy.LastActivity([oldRun], pushedAt);

        Assert.Equal(pushedAt, lastActivity);
        // push 後 10 分鐘：600 ÷ 30 = 20 → 下限 30 秒；沒算進 pushed_at 的話會是 300 秒。
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            IntervalPolicy.RepoRunsInterval(false, lastActivity, Now, RefreshMode.Auto, 1, false));
    }

    [Fact]
    public void run的created_at與updated_at取較晚者()
    {
        var runs = new[]
        {
            Run(createdAt: Now.AddHours(-5), updatedAt: Now.AddHours(-4)),
            Run(createdAt: Now.AddHours(-2), updatedAt: Now.AddHours(-1)),
        };

        Assert.Equal(Now.AddHours(-1), IntervalPolicy.LastActivity(runs, Now.AddHours(-3)));
        Assert.Equal(Now.AddHours(-1), IntervalPolicy.LastActivity(runs, null));
    }

    [Fact]
    public void 時區不同的時間戳依實際時刻比較()
    {
        // 10:30+08:00 = 02:30Z，比 02:00Z 的 pushed_at 晚。
        var taipeiRun = Run(
            createdAt: new DateTimeOffset(2026, 10, 24, 10, 30, 0, TimeSpan.FromHours(8)),
            updatedAt: new DateTimeOffset(2026, 10, 24, 10, 30, 0, TimeSpan.FromHours(8)));

        var lastActivity = IntervalPolicy.LastActivity([taipeiRun], Now);

        Assert.Equal(Now.AddMinutes(30), lastActivity);
    }

    [Fact]
    public void 沒有任何活動用上限()
    {
        var lastActivity = IntervalPolicy.LastActivity([], null);

        Assert.Null(lastActivity);
        Assert.Equal(
            TimeSpan.FromSeconds(300),
            IntervalPolicy.RepoRunsInterval(false, lastActivity, Now, RefreshMode.Auto, 1, false));
    }

    [Fact]
    public void 未來的lastActivity用下限()
    {
        // GitHub 時間戳比本機快（時鐘誤差）時，經過時間視為 0，不出現負值。
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            IntervalPolicy.RepoRunsInterval(false, Now.AddMinutes(10), Now, RefreshMode.Auto, 1, false));
    }

    [Fact]
    public void 鎖定下限300秒()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(300),
            IntervalPolicy.RepoRunsInterval(true, Now, Now, RefreshMode.Auto, 1, locked: true));
        Assert.Equal(
            TimeSpan.FromSeconds(300),
            IntervalPolicy.RepoRunsInterval(false, Now.AddHours(-1), Now, RefreshMode.Auto, 1, locked: true));
        // 已經超過下限時不再拉長：300 × 2 = 600。
        Assert.Equal(
            TimeSpan.FromSeconds(600),
            IntervalPolicy.RepoRunsInterval(false, Now.AddDays(-1), Now, RefreshMode.Auto, 2, locked: true));
        Assert.Equal(
            TimeSpan.FromSeconds(300),
            IntervalPolicy.Periodic(Tuning.ProbeInterval, RefreshMode.Auto, 1, locked: true, followsRefreshMode: true));
        Assert.Equal(
            Tuning.PullListTtl,
            IntervalPolicy.Periodic(Tuning.PullListTtl, RefreshMode.Auto, 1, locked: true, followsRefreshMode: false));
    }

    // spec §6.4 的倍率表。
    [Theory]
    [InlineData(0.30, 1.0)]
    [InlineData(0.20, 2.0)]
    [InlineData(0.10, 2.5)]
    [InlineData(0.05, 5.0)]
    [InlineData(0.02, 8.0)]
    public void 低額度倍率表(double ratio, double expectedK)
    {
        Assert.Equal(expectedK, IntervalPolicy.StretchFactor(ratio), precision: 10);
    }

    [Theory]
    [InlineData(null, 1.0)]
    [InlineData(0.25, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(0.0, 8.0)]
    [InlineData(-0.1, 8.0)]
    [InlineData(double.NaN, 1.0)]
    public void 額度比例的邊界(double? ratio, double expectedK)
    {
        // null（還不知道額度）與 NaN（無法計算）都當成不拉長；ratio = 0 時是上限。
        Assert.Equal(expectedK, IntervalPolicy.StretchFactor(ratio), precision: 10);
    }

    [Theory]
    [InlineData(2.0, 60, 3600, 30, 240)]
    [InlineData(8.0, 240, 14400, 120, 960)]
    public void k套用到探測與週期目標(double k, int probeSeconds, int pullListSeconds, int runningSeconds, int quietHourSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(probeSeconds),
            IntervalPolicy.Periodic(Tuning.ProbeInterval, RefreshMode.Auto, k, false, followsRefreshMode: true));
        Assert.Equal(
            TimeSpan.FromSeconds(pullListSeconds),
            IntervalPolicy.Periodic(Tuning.PullListTtl, RefreshMode.Auto, k, false, followsRefreshMode: false));
        Assert.Equal(
            TimeSpan.FromSeconds(runningSeconds),
            IntervalPolicy.RepoRunsInterval(true, Now, Now, RefreshMode.Auto, k, false));
        // 1 小時前：3600 ÷ 30 = 120，再乘 k。
        Assert.Equal(
            TimeSpan.FromSeconds(quietHourSeconds),
            IntervalPolicy.RepoRunsInterval(false, Now.AddHours(-1), Now, RefreshMode.Auto, k, false));

        // 其他週期目標（E6、PR 清單、workflow 清單、補抓、完整清單）也一樣乘 k。
        foreach (var baseInterval in new[]
                 {
                     Tuning.QuietMax, Tuning.PullListTtlActive, Tuning.WorkflowsRefresh, Tuning.SeedRecheck, Tuning.RepoListRefresh,
                 })
        {
            Assert.Equal(
                baseInterval * k,
                IntervalPolicy.Periodic(baseInterval, RefreshMode.Auto, k, false, followsRefreshMode: false));
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    public void k小於1時當成1(double k)
    {
        // k 只會是 1 或 StretchMin～StretchMax（§6.4）；不合理的值不能讓間隔變短。
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            IntervalPolicy.RepoRunsInterval(true, Now, Now, RefreshMode.Auto, k, false));
        Assert.Equal(
            Tuning.ProbeInterval,
            IntervalPolicy.Periodic(Tuning.ProbeInterval, RefreshMode.Auto, k, false, followsRefreshMode: true));
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(double.PositiveInfinity)]
    public void k超過上限時當成上限(double k)
    {
        Assert.Equal(
            Tuning.ActiveRepoInterval * Tuning.StretchMax,
            IntervalPolicy.RepoRunsInterval(true, Now, Now, RefreshMode.Auto, k, false));
        Assert.Equal(
            Tuning.RepoErrorBackoffMax * Tuning.StretchMax,
            IntervalPolicy.ErrorBackoff(Tuning.QuietMin, TimeSpan.FromDays(1), k));
    }

    [Fact]
    public void 固定模式()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), IntervalPolicy.FixedModeInterval(RefreshMode.Seconds30));
        Assert.Equal(TimeSpan.FromSeconds(60), IntervalPolicy.FixedModeInterval(RefreshMode.Minute1));
        Assert.Equal(TimeSpan.FromSeconds(300), IntervalPolicy.FixedModeInterval(RefreshMode.Minutes5));
        Assert.Null(IntervalPolicy.FixedModeInterval(RefreshMode.Auto));
        Assert.Null(IntervalPolicy.FixedModeInterval(RefreshMode.Paused));

        // base 換成該秒數：有 run 在跑也一樣，安靜很久也一樣。
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            IntervalPolicy.RepoRunsInterval(true, Now, Now, RefreshMode.Minute1, 1, false));
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            IntervalPolicy.RepoRunsInterval(false, Now.AddDays(-1), Now, RefreshMode.Seconds30, 1, false));

        // 探測跟著固定秒數，其他週期目標不跟。
        Assert.Equal(
            TimeSpan.FromSeconds(300),
            IntervalPolicy.Periodic(Tuning.ProbeInterval, RefreshMode.Minutes5, 1, false, followsRefreshMode: true));
        Assert.Equal(
            Tuning.PullListTtl,
            IntervalPolicy.Periodic(Tuning.PullListTtl, RefreshMode.Minutes5, 1, false, followsRefreshMode: false));

        // k 與鎖定下限照舊。
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            IntervalPolicy.RepoRunsInterval(false, Now, Now, RefreshMode.Seconds30, 2, false));
        Assert.Equal(
            TimeSpan.FromSeconds(300),
            IntervalPolicy.Periodic(Tuning.ProbeInterval, RefreshMode.Seconds30, 1, locked: true, followsRefreshMode: true));
    }

    [Fact]
    public void 暫停回null()
    {
        Assert.Null(IntervalPolicy.RepoRunsInterval(true, Now, Now, RefreshMode.Paused, 1, false));
        Assert.Null(IntervalPolicy.RepoRunsInterval(false, Now.AddDays(-1), Now, RefreshMode.Paused, 2, true));
        Assert.Null(IntervalPolicy.Periodic(Tuning.ProbeInterval, RefreshMode.Paused, 1, false, followsRefreshMode: true));
        Assert.Null(IntervalPolicy.Periodic(Tuning.RepoListRefresh, RefreshMode.Paused, 1, false, followsRefreshMode: false));
    }

    [Fact]
    public void 錯誤退避不短於正常間隔()
    {
        // spec 的 k = 4 案例：max(1200, min(1200 × 2, 600 × 4)) = 2400。
        Assert.Equal(
            TimeSpan.FromSeconds(2400),
            IntervalPolicy.ErrorBackoff(TimeSpan.FromSeconds(1200), TimeSpan.FromSeconds(1200), 4));
        // 上限 600 秒比正常間隔短時，仍用正常間隔。
        Assert.Equal(
            Tuning.PullListTtl,
            IntervalPolicy.ErrorBackoff(Tuning.PullListTtl, Tuning.PullListTtl, 1));
        // 倍增到上限為止。
        Assert.Equal(TimeSpan.FromSeconds(60), IntervalPolicy.ErrorBackoff(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 1));
        Assert.Equal(TimeSpan.FromSeconds(120), IntervalPolicy.ErrorBackoff(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), 1));
        Assert.Equal(TimeSpan.FromSeconds(600), IntervalPolicy.ErrorBackoff(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(480), 1));
        Assert.Equal(TimeSpan.FromSeconds(600), IntervalPolicy.ErrorBackoff(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(600), 1));
    }

    [Fact]
    public void 錯誤退避的上一次間隔不合理時不溢位也不變短()
    {
        Assert.Equal(TimeSpan.FromSeconds(600), IntervalPolicy.ErrorBackoff(TimeSpan.FromSeconds(30), TimeSpan.MaxValue, 1));
        Assert.Equal(TimeSpan.FromSeconds(30), IntervalPolicy.ErrorBackoff(TimeSpan.FromSeconds(30), TimeSpan.Zero, 1));
        Assert.Equal(TimeSpan.FromSeconds(30), IntervalPolicy.ErrorBackoff(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(-90), 1));
    }

    [Fact]
    public void 不合理的基本間隔丟例外()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IntervalPolicy.Periodic(TimeSpan.Zero, RefreshMode.Auto, 1, false, followsRefreshMode: false));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IntervalPolicy.Periodic(TimeSpan.FromSeconds(-1), RefreshMode.Auto, 1, false, followsRefreshMode: false));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IntervalPolicy.ErrorBackoff(TimeSpan.Zero, TimeSpan.FromSeconds(30), 1));
    }

    [Fact]
    public void runs為null時丟例外()
    {
        Assert.Throws<ArgumentNullException>(() => IntervalPolicy.LastActivity(null!, Now));
    }

    private static RunInfo Run(DateTimeOffset createdAt, DateTimeOffset updatedAt) => new(
        Id: 1, RepoId: 2, WorkflowId: 3, WorkflowName: "CI", Path: ".github/workflows/ci.yml",
        RunNumber: 7, RunAttempt: 1, DisplayTitle: "Fix", Event: "push", Status: "completed", Conclusion: "success",
        HeadBranch: "main", HeadSha: "abc1234", HeadRepoId: 2, HeadRepoFullName: "octo-org/sample-app",
        HeadRepoHtmlUrl: "https://github.com/octo-org/sample-app", ActorId: 9, ActorLogin: "alice",
        TriggeringActorLogin: "alice", PullRequestNumbers: [], RunStartedAt: createdAt, CreatedAt: createdAt, UpdatedAt: updatedAt,
        HtmlUrl: "https://github.com/octo-org/sample-app/actions/runs/1");
}
