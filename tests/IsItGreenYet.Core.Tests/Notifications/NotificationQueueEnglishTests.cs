using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Tests.TestSupport;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Notifications;

/// <summary>提示視窗的標題與內文的英文（spec §9.4 的英文版）；同 NotificationQueueTests 的假資料。</summary>
public class NotificationQueueEnglishTests
{
    private const string RepoFullName = "octo-org/sample-app";

    private static readonly DateTimeOffset Start = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);

    private static readonly RunInfo BaseRun = new(
        Id: 1001, RepoId: 2001, WorkflowId: 3001, WorkflowName: "CI", Path: ".github/workflows/ci.yml",
        RunNumber: 42, RunAttempt: 1, DisplayTitle: "Fix login", Event: "push", Status: "completed", Conclusion: "failure",
        HeadBranch: "feature/login", HeadSha: "0123456789abcdef0123456789abcdef01234567", HeadRepoId: 2001,
        HeadRepoFullName: RepoFullName, HeadRepoHtmlUrl: "https://github.com/octo-org/sample-app",
        ActorId: 502, ActorLogin: "alice", TriggeringActorLogin: "alice", PullRequestNumbers: [],
        RunStartedAt: Start.AddMinutes(-10), CreatedAt: Start.AddMinutes(-10), UpdatedAt: Start.AddMinutes(-1),
        HtmlUrl: "https://github.com/octo-org/sample-app/actions/runs/1001");

    [Fact]
    public void 單筆的標題與內文()
    {
        var (time, queue) = NewQueue();
        queue.Add([Planned(1, "timed_out")]);
        time.Advance(Tuning.NotificationMergeWindow);

        var toast = Flush(queue);

        Assert.Equal("Failed (timed out): octo-org/sample-app · CI #1", toast.Title);
        Assert.Equal("Fix login · feature/login · by alice", Assert.Single(toast.Lines).Text);
    }

    [Fact]
    public void 彙總的標題計數與還有N筆()
    {
        var (time, queue) = NewQueue();
        queue.Add([Planned(1, "failure"), Planned(2, "success"), Planned(3, "cancelled"), Planned(4, "success"), Planned(5, "skipped"), Planned(6, "success")]);
        time.Advance(Tuning.NotificationMergeWindow);

        var toast = Flush(queue);

        Assert.Equal("6 runs changed (1 failed, 3 succeeded, 2 other)", toast.Title);
        Assert.Equal("Failed: octo-org/sample-app · CI #1", toast.Lines[0].Text);
        Assert.Equal("Cancelled: octo-org/sample-app · CI #3", toast.Lines[2].Text);
        Assert.Equal("1 more", toast.MoreText);
    }

    [Fact]
    public void 鎖定期間的彙總()
    {
        var (time, queue) = NewQueue();
        queue.SetLocked(true);
        queue.Add([Planned(1, "failure")]);
        time.Advance(Tuning.NotificationMergeWindow);
        Assert.Null(queue.TryFlush(Duration, Duration, AppLanguage.English));

        queue.SetLocked(false);

        Assert.Equal("Changes while locked (1 failed)", Flush(queue).Title);
    }

    [Fact]
    public void 加入時的狀態是中文也用送出時的語言()
    {
        var (time, queue) = NewQueue();
        var run = BaseRun with { Id = 7, RunNumber = 7, Conclusion = "failure" };
        queue.Add([new PlannedNotification(run, RunStateMapper.Map(run, AppLanguage.TraditionalChinese), RepoFullName, NotificationKind.TrackedFailure)]);
        time.Advance(Tuning.NotificationMergeWindow);

        Assert.Equal("Failed: octo-org/sample-app · CI #7", Flush(queue).Title);
    }

    [Fact]
    public void 新版本提示的英文文字()
    {
        var notice = new UpdateNotice("0.3.0", new Uri("https://github.com/marvin3991/is-it-green-yet/releases/tag/v0.3.0"));
        var (time, single) = NewQueue();
        var merged = new NotificationQueue(time);
        single.AddNotice(notice);
        merged.Add([Planned(1, "failure")]);
        merged.AddNotice(notice);
        time.Advance(Tuning.NotificationMergeWindow);

        var only = Flush(single);
        var withRun = Flush(merged);

        Assert.Equal("New version available: 0.3.0", only.Title);
        Assert.Equal("Click to open the release page", Assert.Single(only.Lines).Text);
        Assert.Equal("New version: 0.3.0 (click to open)", withRun.Lines[^1].Text);
    }

    private static (ManualTimeProvider Time, NotificationQueue Queue) NewQueue()
    {
        var time = new ManualTimeProvider(Start);
        return (time, new NotificationQueue(time));
    }

    private static PlannedNotification Planned(long id, string conclusion)
    {
        var run = BaseRun with { Id = id, RunNumber = (int)id, Conclusion = conclusion, HtmlUrl = $"https://github.com/octo-org/sample-app/actions/runs/{id}" };
        return new PlannedNotification(run, RunStateMapper.Map(run, AppLanguage.English), RepoFullName, NotificationKind.TrackedFailure);
    }

    private static ToastContent Flush(NotificationQueue queue)
    {
        var toast = queue.TryFlush(Duration, Duration, AppLanguage.English);
        Assert.NotNull(toast);
        return toast;
    }
}
