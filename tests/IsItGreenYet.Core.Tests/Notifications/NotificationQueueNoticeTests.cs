using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Tests.TestSupport;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Notifications;

/// <summary>新版本提示進佇列之後的合併、鎖定與版面；只有 run 的行為在 NotificationQueueTests。</summary>
public class NotificationQueueNoticeTests
{
    private const string RepoFullName = "octo-org/sample-app";
    private const string Version = "0.3.0";

    private const string AvailableTitle = "有新版本：0.3.0";
    private const string OpenLine = "點這裡開啟 release 頁面";
    private const string UpdateLine = "有新版本：0.3.0（點這裡開啟）";

    private static readonly DateTimeOffset Start = new(2026, 11, 4, 2, 0, 0, TimeSpan.Zero);

    /// <summary>刻意和 Tuning 的預設值不同，確認停留時間用的是呼叫端傳入的設定值。</summary>
    private static readonly TimeSpan FailureDuration = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan SuccessDuration = TimeSpan.FromSeconds(7);

    private static readonly TimeSpan Window = Tuning.NotificationMergeWindow;

    private static readonly Uri ReleaseUrl = new("https://github.com/marvin3991/is-it-green-yet/releases/tag/v0.3.0");

    private static readonly UpdateNotice Notice = new(Version, ReleaseUrl);

    private static readonly RunInfo BaseRun = new(
        Id: 1001, RepoId: 2001, WorkflowId: 3001, WorkflowName: "CI", Path: ".github/workflows/ci.yml",
        RunNumber: 42, RunAttempt: 1, DisplayTitle: "修正登入流程", Event: "push", Status: "completed", Conclusion: "failure",
        HeadBranch: "feature/login", HeadSha: "0123456789abcdef0123456789abcdef01234567", HeadRepoId: 2001,
        HeadRepoFullName: RepoFullName, HeadRepoHtmlUrl: "https://github.com/octo-org/sample-app",
        ActorId: 502, ActorLogin: "alice", TriggeringActorLogin: "alice", PullRequestNumbers: [],
        RunStartedAt: Start.AddMinutes(-10), CreatedAt: Start.AddMinutes(-10), UpdatedAt: Start.AddMinutes(-1),
        HtmlUrl: "https://github.com/octo-org/sample-app/actions/runs/1001");

    [Fact]
    public void 只有新版本時單筆版面_OpenUrl是release()
    {
        var (time, queue) = NewQueue();

        queue.AddNotice(Notice);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        Assert.Equal(AvailableTitle, toast.Title);
        var line = Assert.Single(toast.Lines);
        Assert.Equal(OpenLine, line.Text);
        Assert.Equal(ReleaseUrl, line.Url);
        Assert.Equal(ToastTone.Neutral, line.Tone);
        Assert.Null(toast.MoreText);
        Assert.Equal(ToastTone.Neutral, toast.Tone);
        Assert.Equal(ReleaseUrl, toast.OpenUrl);

        // 已經送出的內容不會再出一次。
        Assert.Null(Flush(queue));
    }

    [Fact]
    public void 新版本也等合併視窗()
    {
        var (time, queue) = NewQueue();

        queue.AddNotice(Notice);
        time.Advance(Window - TimeSpan.FromMilliseconds(1));
        Assert.Null(Flush(queue));

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.NotNull(Flush(queue));
    }

    [Fact]
    public void 新版本不重新起算run的合併視窗()
    {
        var (time, queue) = NewQueue();

        queue.Add([Planned(1, "failure")]);
        time.Advance(TimeSpan.FromSeconds(1));
        queue.AddNotice(Notice);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.NotNull(Flush(queue));
    }

    [Fact]
    public void run不重新起算新版本的合併視窗()
    {
        var (time, queue) = NewQueue();

        queue.AddNotice(Notice);
        time.Advance(TimeSpan.FromSeconds(1));
        queue.Add([Planned(1, "failure")]);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.NotNull(Flush(queue));
    }

    [Fact]
    public void 新版本和一筆run合併_兩行且OpenUrl是run()
    {
        var (time, queue) = NewQueue();

        queue.Add([Planned(BaseRun with { Conclusion = "success" })]);
        queue.AddNotice(Notice);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        Assert.Equal("成功：octo-org/sample-app · CI #42", toast.Title);
        Assert.Equal(["修正登入流程 · feature/login · by alice", UpdateLine], toast.Lines.Select(l => l.Text));
        Assert.Equal(ReleaseUrl, toast.Lines[1].Url);
        Assert.Equal(ToastTone.Neutral, toast.Lines[1].Tone);
        Assert.Equal(new Uri(BaseRun.HtmlUrl), toast.OpenUrl);
        Assert.Equal(ToastTone.Success, toast.Tone);
    }

    [Fact]
    public void 新版本和多筆run合併_OpenUrl是null()
    {
        var (time, queue) = NewQueue();

        queue.Add([Planned(1, "failure"), Planned(2, "success")]);
        queue.AddNotice(Notice);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        Assert.Equal("2 筆執行有變化（1 失敗、1 成功）", toast.Title);
        Assert.Equal(3, toast.Lines.Count);
        Assert.Equal(RunUrl(1), toast.Lines[0].Url);
        Assert.Equal(RunUrl(2), toast.Lines[1].Url);
        Assert.Equal(UpdateLine, toast.Lines[2].Text);
        Assert.Equal(ReleaseUrl, toast.Lines[2].Url);
        Assert.Null(toast.OpenUrl);
        Assert.Equal(ToastTone.Failure, toast.Tone);
    }

    [Fact]
    public void 超過5筆run時新版本行不會被擠掉()
    {
        var (time, queue) = NewQueue();

        queue.Add([.. Enumerable.Range(1, 7).Select(i => Planned(i, "failure"))]);
        queue.AddNotice(Notice);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        Assert.Equal(Tuning.SummaryToastMaxLines + 1, toast.Lines.Count);
        Assert.Equal(UpdateLine, toast.Lines[^1].Text);
        Assert.Equal("還有 2 筆", toast.MoreText);
    }

    [Fact]
    public void 提示開著時新版本併入()
    {
        var (time, queue) = NewQueue();

        queue.Add([Planned(1, "success")]);
        time.Advance(Window);
        var first = RequireFlush(queue);
        Assert.Equal(SuccessDuration, first.Duration);

        queue.AddNotice(Notice);
        time.Advance(Window);
        var merged = RequireFlush(queue);

        Assert.Equal("成功：octo-org/sample-app · CI #1", merged.Title);
        Assert.Equal(2, merged.Lines.Count);
        Assert.Equal(UpdateLine, merged.Lines[1].Text);
        Assert.Equal(RunUrl(1), merged.OpenUrl);
        Assert.Equal(FailureDuration, merged.Duration);
    }

    [Fact]
    public void 新版本提示開著時run併入_新版本行保留()
    {
        var (time, queue) = NewQueue();

        queue.AddNotice(Notice);
        time.Advance(Window);
        Assert.Equal(AvailableTitle, RequireFlush(queue).Title);

        queue.Add([Planned(1, "failure")]);
        time.Advance(Window);
        var withOneRun = RequireFlush(queue);

        Assert.Equal("失敗：octo-org/sample-app · CI #1", withOneRun.Title);
        Assert.Equal(2, withOneRun.Lines.Count);
        Assert.Equal(UpdateLine, withOneRun.Lines[1].Text);
        Assert.Equal(RunUrl(1), withOneRun.OpenUrl);

        queue.Add([Planned(2, "success")]);
        time.Advance(Window);
        var withTwoRuns = RequireFlush(queue);

        Assert.Equal("2 筆執行有變化（1 失敗、1 成功）", withTwoRuns.Title);
        Assert.Equal(3, withTwoRuns.Lines.Count);
        Assert.Equal(UpdateLine, withTwoRuns.Lines[2].Text);
        Assert.Null(withTwoRuns.OpenUrl);
    }

    [Fact]
    public void 新版本提示開著時重複的run不再更新提示()
    {
        var (time, queue) = NewQueue();
        var same = Planned(1, "failure");

        queue.Add([same]);
        queue.AddNotice(Notice);
        time.Advance(Window);
        RequireFlush(queue);

        queue.Add([same]);
        time.Advance(Window);

        Assert.Null(Flush(queue));
    }

    [Fact]
    public void Dismissed後新版本行不再併入()
    {
        var (time, queue) = NewQueue();

        queue.AddNotice(Notice);
        time.Advance(Window);
        RequireFlush(queue);
        queue.Dismissed();

        queue.Add([Planned(1, "failure")]);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        var line = Assert.Single(toast.Lines);
        Assert.Equal(RunUrl(1), line.Url);
        Assert.Equal(FailureDuration, toast.Duration);
    }

    [Fact]
    public void 鎖定時不出_解鎖後只有新版本時不是鎖定彙總()
    {
        var (time, queue) = NewQueue();

        queue.SetLocked(true);
        queue.AddNotice(Notice);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(Flush(queue));

        queue.SetLocked(false);
        var toast = RequireFlush(queue);

        Assert.Equal(AvailableTitle, toast.Title);
        Assert.Equal(ReleaseUrl, toast.OpenUrl);
        Assert.Null(Flush(queue));
    }

    [Fact]
    public void 提示開著時鎖定_期間只有新版本_解鎖後併入開著的提示()
    {
        var (time, queue) = NewQueue();

        queue.Add([Planned(1, "failure")]);
        time.Advance(Window);
        RequireFlush(queue);

        queue.SetLocked(true);
        queue.AddNotice(Notice);
        time.Advance(Window);
        Assert.Null(Flush(queue));

        queue.SetLocked(false);
        var toast = RequireFlush(queue);

        Assert.Equal("失敗：octo-org/sample-app · CI #1", toast.Title);
        Assert.Equal(2, toast.Lines.Count);
        Assert.Equal(UpdateLine, toast.Lines[1].Text);
    }

    [Fact]
    public void 鎖定期間有run也有新版本_解鎖後是鎖定彙總加新版本行()
    {
        var (time, queue) = NewQueue();

        queue.SetLocked(true);
        queue.Add([Planned(1, "failure")]);
        queue.AddNotice(Notice);
        time.Advance(TimeSpan.FromMinutes(5));
        queue.SetLocked(false);
        var toast = RequireFlush(queue);

        Assert.Equal("鎖定期間的變化（1 失敗）", toast.Title);
        Assert.Equal(2, toast.Lines.Count);
        Assert.Equal(RunUrl(1), toast.Lines[0].Url);
        Assert.Equal(UpdateLine, toast.Lines[1].Text);
        Assert.Null(toast.OpenUrl);
    }

    [Fact]
    public void 鎖定彙總不帶之前顯示中的新版本行()
    {
        var (time, queue) = NewQueue();

        queue.AddNotice(Notice);
        time.Advance(Window);
        RequireFlush(queue);
        Assert.Equal(Notice, queue.TakeFlushedNotice());

        queue.SetLocked(true);
        queue.Add([Planned(1, "failure")]);
        time.Advance(Window);
        queue.SetLocked(false);
        var toast = RequireFlush(queue);

        Assert.Equal("鎖定期間的變化（1 失敗）", toast.Title);
        Assert.Equal(RunUrl(1), Assert.Single(toast.Lines).Url);
        Assert.Null(queue.TakeFlushedNotice());
    }

    [Fact]
    public void DropNotice後不出()
    {
        var (time, queue) = NewQueue();

        queue.AddNotice(Notice);
        queue.DropNotice();
        time.Advance(Window);

        Assert.Null(Flush(queue));
        Assert.Null(queue.TakeFlushedNotice());
    }

    [Fact]
    public void DropNotice不影響排隊中的run()
    {
        var (time, queue) = NewQueue();

        queue.Add([Planned(1, "failure")]);
        queue.AddNotice(Notice);
        queue.DropNotice();
        time.Advance(Window);
        var toast = RequireFlush(queue);

        Assert.Equal(RunUrl(1), Assert.Single(toast.Lines).Url);
        Assert.Null(queue.TakeFlushedNotice());
    }

    [Fact]
    public void DropNotice後顯示中的新版本行不再併入()
    {
        var (time, queue) = NewQueue();

        queue.AddNotice(Notice);
        time.Advance(Window);
        RequireFlush(queue);
        queue.DropNotice();

        queue.Add([Planned(1, "failure")]);
        time.Advance(Window);

        Assert.Equal(RunUrl(1), Assert.Single(RequireFlush(queue).Lines).Url);
    }

    [Fact]
    public void 送出之後呼叫端拿走之前DropNotice_不再回傳()
    {
        var (time, queue) = NewQueue();

        queue.AddNotice(Notice);
        time.Advance(Window);
        RequireFlush(queue);
        queue.DropNotice();

        Assert.Null(queue.TakeFlushedNotice());
    }

    [Fact]
    public void TakeFlushedNotice只回傳一次()
    {
        var (time, queue) = NewQueue();

        Assert.Null(queue.TakeFlushedNotice());

        queue.AddNotice(Notice);
        Assert.Null(queue.TakeFlushedNotice());
        time.Advance(Window);
        Assert.Null(queue.TakeFlushedNotice());

        RequireFlush(queue);

        Assert.Equal(Notice, queue.TakeFlushedNotice());
        Assert.Null(queue.TakeFlushedNotice());
    }

    [Fact]
    public void 只有run送出時TakeFlushedNotice是null()
    {
        var (time, queue) = NewQueue();

        queue.Add([Planned(1, "failure")]);
        time.Advance(Window);
        RequireFlush(queue);

        Assert.Null(queue.TakeFlushedNotice());
    }

    [Fact]
    public void 較新的新版本取代排隊中的新版本()
    {
        var (time, queue) = NewQueue();
        var newer = new UpdateNotice("0.4.0", new Uri("https://github.com/marvin3991/is-it-green-yet/releases/tag/v0.4.0"));

        queue.SetLocked(true);
        queue.AddNotice(Notice);
        queue.AddNotice(newer);
        time.Advance(Window);
        queue.SetLocked(false);
        var toast = RequireFlush(queue);

        Assert.Equal("有新版本：0.4.0", toast.Title);
        Assert.Equal(newer, queue.TakeFlushedNotice());
    }

    [Fact]
    public void 新版本提示開著時又有更新的版本_換成新的()
    {
        var (time, queue) = NewQueue();
        var newer = new UpdateNotice("0.4.0", new Uri("https://github.com/marvin3991/is-it-green-yet/releases/tag/v0.4.0"));

        queue.AddNotice(Notice);
        time.Advance(Window);
        RequireFlush(queue);

        queue.AddNotice(newer);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        Assert.Equal("有新版本：0.4.0", toast.Title);
        Assert.Equal(newer.Url, toast.OpenUrl);
        Assert.Equal(newer, queue.TakeFlushedNotice());
    }

    [Fact]
    public void 新版本的停留用失敗秒數()
    {
        var (time, single) = NewQueue();
        var withRun = new NotificationQueue(time);

        single.AddNotice(Notice);
        withRun.Add([Planned(1, "success")]);
        withRun.AddNotice(Notice);
        time.Advance(Window);

        Assert.Equal(FailureDuration, RequireFlush(single).Duration);
        Assert.Equal(FailureDuration, RequireFlush(withRun).Duration);
    }

    [Fact]
    public void AddNotice參數為null時丟例外()
    {
        var (_, queue) = NewQueue();

        Assert.Throws<ArgumentNullException>(() => queue.AddNotice(null!));
    }

    private static (ManualTimeProvider Time, NotificationQueue Queue) NewQueue()
    {
        var time = new ManualTimeProvider(Start);
        return (time, new NotificationQueue(time));
    }

    private static PlannedNotification Planned(long id, string conclusion) =>
        Planned(BaseRun with
        {
            Id = id,
            RunNumber = (int)id,
            Conclusion = conclusion,
            HtmlUrl = RunUrl(id).ToString(),
        });

    private static PlannedNotification Planned(RunInfo run) =>
        new(run, RunStateMapper.Map(run), RepoFullName, NotificationKind.TrackedFailure);

    private static Uri RunUrl(long id) => new($"https://github.com/octo-org/sample-app/actions/runs/{id}");

    private static ToastContent? Flush(NotificationQueue queue) => queue.TryFlush(FailureDuration, SuccessDuration);

    private static ToastContent RequireFlush(NotificationQueue queue)
    {
        var toast = Flush(queue);
        Assert.NotNull(toast);
        return toast;
    }
}
