using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Polling;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Engine;

public class PollSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    private static readonly PollTarget Runs2001 = new(TargetKind.Runs, RepoId: 2001);

    private static readonly PollTarget Pulls2001 = new(TargetKind.PullRequests, RepoId: 2001);

    private static readonly PollTarget Probe = new(TargetKind.Probe);

    [Fact]
    public void 到期才取出()
    {
        var time = new ManualTimeProvider(Start);
        var scheduler = new PollScheduler(time);
        scheduler.Schedule(Runs2001, TimeSpan.FromSeconds(30), RequestPriority.RunsPoll);

        Assert.Null(scheduler.TakeNextDue());
        Assert.Equal(TimeSpan.FromSeconds(30), scheduler.TimeUntilNextDue());

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(scheduler.TakeNextDue());
        Assert.Equal(TimeSpan.FromSeconds(1), scheduler.TimeUntilNextDue());

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, scheduler.TimeUntilNextDue());
        Assert.Equal(Runs2001, scheduler.TakeNextDue());

        // 取出後就不在排程裡，不會再被取出第二次。
        Assert.Empty(scheduler.Scheduled);
        Assert.Null(scheduler.TakeNextDue());
        Assert.Null(scheduler.TimeUntilNextDue());
    }

    [Fact]
    public void 到期時間只看單調時鐘()
    {
        var time = new ManualTimeProvider(Start);
        var scheduler = new PollScheduler(time);
        scheduler.Schedule(Runs2001, TimeSpan.FromSeconds(30), RequestPriority.RunsPoll);

        // 牆鐘往回調一小時、單調時鐘照走：排程照常到期（spec §15）。
        time.SetUtcNow(Start.AddHours(-1));
        time.AdvanceMonotonic(TimeSpan.FromSeconds(30));

        Assert.Equal(Runs2001, scheduler.TakeNextDue());
    }

    [Fact]
    public void 同時到期依優先序()
    {
        var time = new ManualTimeProvider(Start);
        var scheduler = new PollScheduler(time);
        var workflows = new PollTarget(TargetKind.Workflows, RepoId: 2001);

        scheduler.ScheduleNow(workflows, RequestPriority.Maintenance);
        scheduler.ScheduleNow(Pulls2001, RequestPriority.PullRequests);
        scheduler.ScheduleNow(Probe, RequestPriority.Probe);
        scheduler.ScheduleNow(Runs2001, RequestPriority.RunningRepo);

        Assert.Equal(Runs2001, scheduler.TakeNextDue());
        Assert.Equal(Probe, scheduler.TakeNextDue());
        Assert.Equal(Pulls2001, scheduler.TakeNextDue());
        Assert.Equal(workflows, scheduler.TakeNextDue());
        Assert.Null(scheduler.TakeNextDue());
    }

    [Fact]
    public void 同一級時到期越久的先送()
    {
        var time = new ManualTimeProvider(Start);
        var scheduler = new PollScheduler(time);
        var runs2002 = new PollTarget(TargetKind.Runs, RepoId: 2002);

        scheduler.ScheduleNow(Runs2001, RequestPriority.RunsPoll);
        time.Advance(TimeSpan.FromSeconds(10));
        scheduler.ScheduleNow(runs2002, RequestPriority.RunsPoll);

        Assert.Equal(Runs2001, scheduler.TakeNextDue());
        Assert.Equal(runs2002, scheduler.TakeNextDue());
    }

    [Fact]
    public void 老化後低優先序先於新的高優先序()
    {
        var time = new ManualTimeProvider(Start);
        var scheduler = new PollScheduler(time);
        var repoList = new PollTarget(TargetKind.RepoList);

        // 第 7 級的 repo 清單已經排隊 5 分鐘：每滿 PriorityAgingStep 升一級，最高升到第 2 級（spec §6.3）。
        scheduler.ScheduleNow(repoList, RequestPriority.Maintenance);
        time.Advance(Tuning.PriorityAgingStep * 5);
        scheduler.ScheduleNow(Probe, RequestPriority.Probe);

        Assert.Equal(repoList, scheduler.TakeNextDue());
        Assert.Equal(Probe, scheduler.TakeNextDue());
    }

    [Fact]
    public void 老化不足時高優先序先送()
    {
        var time = new ManualTimeProvider(Start);
        var scheduler = new PollScheduler(time);
        var repoList = new PollTarget(TargetKind.RepoList);

        scheduler.ScheduleNow(repoList, RequestPriority.Maintenance);
        time.Advance(Tuning.PriorityAgingStep - TimeSpan.FromSeconds(1));
        scheduler.ScheduleNow(Probe, RequestPriority.Probe);

        Assert.Equal(Probe, scheduler.TakeNextDue());
        Assert.Equal(repoList, scheduler.TakeNextDue());
    }

    [Fact]
    public void 重新排程會取代原本的到期時間()
    {
        var time = new ManualTimeProvider(Start);
        var scheduler = new PollScheduler(time);

        scheduler.Schedule(Runs2001, TimeSpan.FromSeconds(300), RequestPriority.RunsPoll);
        scheduler.ScheduleNow(Runs2001, RequestPriority.PushedRepo);

        Assert.Single(scheduler.Scheduled);
        Assert.Equal(Runs2001, scheduler.TakeNextDue());
    }

    [Fact]
    public void RemoveRepo移除該repo所有目標()
    {
        var time = new ManualTimeProvider(Start);
        var scheduler = new PollScheduler(time);
        var runs2002 = new PollTarget(TargetKind.Runs, RepoId: 2002);
        PollTarget[] repo2001 =
        [
            Runs2001,
            Pulls2001,
            new(TargetKind.Workflows, RepoId: 2001),
            new(TargetKind.RunDetail, RepoId: 2001, ItemId: 9001),
            new(TargetKind.DefaultBranchSeed, RepoId: 2001, ItemId: 161335),
            new(TargetKind.PullRequestSeed, RepoId: 2001, Sha: "1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d"),
        ];

        foreach (var target in repo2001)
        {
            scheduler.Schedule(target, TimeSpan.FromSeconds(60), RequestPriority.RunsPoll);
        }

        scheduler.Schedule(runs2002, TimeSpan.FromSeconds(60), RequestPriority.RunsPoll);
        scheduler.Schedule(Probe, TimeSpan.FromSeconds(30), RequestPriority.Probe);

        scheduler.RemoveRepo(2001);

        Assert.Equal(new HashSet<PollTarget> { runs2002, Probe }, scheduler.Scheduled.ToHashSet());

        scheduler.Remove(Probe);
        Assert.Equal([runs2002], scheduler.Scheduled);
    }
}
