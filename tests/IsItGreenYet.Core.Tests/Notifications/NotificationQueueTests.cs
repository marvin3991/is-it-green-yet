using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Tests.TestSupport;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Notifications;

public class NotificationQueueTests
{
    private const string RepoFullName = "octo-org/sample-app";

    private static readonly DateTimeOffset Start = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    /// <summary>刻意和 Tuning 的預設值不同，確認停留時間用的是呼叫端傳入的設定值。</summary>
    private static readonly TimeSpan FailureDuration = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan SuccessDuration = TimeSpan.FromSeconds(7);

    private static readonly TimeSpan Window = Tuning.NotificationMergeWindow;

    private static readonly RunInfo BaseRun = new(
        Id: 1001, RepoId: 2001, WorkflowId: 3001, WorkflowName: "CI", Path: ".github/workflows/ci.yml",
        RunNumber: 42, RunAttempt: 1, DisplayTitle: "修正登入流程", Event: "push", Status: "completed", Conclusion: "failure",
        HeadBranch: "feature/login", HeadSha: "0123456789abcdef0123456789abcdef01234567", HeadRepoId: 2001,
        HeadRepoFullName: RepoFullName, HeadRepoHtmlUrl: "https://github.com/octo-org/sample-app",
        ActorId: 502, ActorLogin: "alice", TriggeringActorLogin: "alice", PullRequestNumbers: [],
        RunStartedAt: Start.AddMinutes(-10), CreatedAt: Start.AddMinutes(-10), UpdatedAt: Start.AddMinutes(-1),
        HtmlUrl: "https://github.com/octo-org/sample-app/actions/runs/1001");

    [Fact]
    public void _2秒內合併成一則()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.Add([Planned(1, "failure")]);
        time.Advance(TimeSpan.FromSeconds(1));
        queue.Add([Planned(2, "success")]);
        time.Advance(TimeSpan.FromMilliseconds(900));

        // 合併視窗從第一筆起算，還沒滿 2 秒不出。
        Assert.Null(Flush(queue));

        time.Advance(TimeSpan.FromMilliseconds(100));
        var toast = RequireFlush(queue);

        Assert.Equal("2 筆執行有變化（1 失敗、1 成功）", toast.Title);
        Assert.Equal([RunUrl(1), RunUrl(2)], toast.Lines.Select(l => l.Url));
        Assert.Null(toast.MoreText);
        Assert.Null(toast.OpenUrl);
        // 已經送出的內容不會再出一次。
        Assert.Null(Flush(queue));
    }

    [Fact]
    public void 超過5筆顯示還有N筆()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.Add([.. Enumerable.Range(1, 7).Select(i => Planned(i, "failure"))]);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        Assert.Equal("7 筆執行有變化（7 失敗）", toast.Title);
        Assert.Equal(Tuning.SummaryToastMaxLines, toast.Lines.Count);
        Assert.Equal([RunUrl(1), RunUrl(2), RunUrl(3), RunUrl(4), RunUrl(5)], toast.Lines.Select(l => l.Url));
        Assert.Equal("還有 2 筆", toast.MoreText);
    }

    [Fact]
    public void 剛好5筆不顯示還有N筆()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.Add([.. Enumerable.Range(1, 5).Select(i => Planned(i, "success"))]);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        Assert.Equal(5, toast.Lines.Count);
        Assert.Null(toast.MoreText);
    }

    [Fact]
    public void 含失敗用失敗語氣與停留()
    {
        var time = new ManualTimeProvider(Start);
        var single = new NotificationQueue(time);
        var mixed = new NotificationQueue(time);

        single.Add([Planned(1, "timed_out")]);
        mixed.Add([Planned(1, "success"), Planned(2, "cancelled"), Planned(3, "failure")]);
        time.Advance(Window);
        var singleToast = RequireFlush(single);
        var mixedToast = RequireFlush(mixed);

        Assert.Equal(ToastTone.Failure, singleToast.Tone);
        Assert.Equal(FailureDuration, singleToast.Duration);
        Assert.Equal(ToastTone.Failure, mixedToast.Tone);
        Assert.Equal(FailureDuration, mixedToast.Duration);
        Assert.Equal("3 筆執行有變化（1 失敗、1 成功、1 其他）", mixedToast.Title);
        // 每一行保有自己的語氣。
        Assert.Equal([ToastTone.Success, ToastTone.Neutral, ToastTone.Failure], mixedToast.Lines.Select(l => l.Tone));
    }

    [Fact]
    public void 成功用成功語氣與停留()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.Add([Planned(1, "success")]);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        Assert.Equal("成功：octo-org/sample-app · CI #1", toast.Title);
        Assert.Equal(ToastTone.Success, toast.Tone);
        Assert.Equal(SuccessDuration, toast.Duration);
    }

    [Theory]
    [InlineData("cancelled", "取消")]
    [InlineData("skipped", "略過")]
    [InlineData("brand_new", "未知（brand_new）")]
    public void 灰色結論用Neutral與成功的停留(string conclusion, string label)
    {
        var time = new ManualTimeProvider(Start);
        var single = new NotificationQueue(time);
        var summary = new NotificationQueue(time);

        single.Add([Planned(1, conclusion)]);
        summary.Add([Planned(1, conclusion), Planned(2, conclusion)]);
        time.Advance(Window);
        var singleToast = RequireFlush(single);
        var summaryToast = RequireFlush(summary);

        Assert.Equal($"{label}：octo-org/sample-app · CI #1", singleToast.Title);
        Assert.Equal(ToastTone.Neutral, singleToast.Tone);
        Assert.Equal(ToastTone.Neutral, Assert.Single(singleToast.Lines).Tone);
        Assert.Equal(SuccessDuration, singleToast.Duration);
        Assert.Equal("2 筆執行有變化（2 其他）", summaryToast.Title);
        Assert.Equal(ToastTone.Neutral, summaryToast.Tone);
        Assert.Equal(SuccessDuration, summaryToast.Duration);
    }

    [Fact]
    public void 成功與灰色混合的彙總用成功語氣()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.Add([Planned(1, "cancelled"), Planned(2, "success")]);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        Assert.Equal("2 筆執行有變化（1 成功、1 其他）", toast.Title);
        Assert.Equal(ToastTone.Success, toast.Tone);
        Assert.Equal(SuccessDuration, toast.Duration);
    }

    [Fact]
    public void 單筆提示的標題與內文()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);
        var run = BaseRun with { Conclusion = "timed_out" };

        queue.Add([Planned(run)]);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        Assert.Equal("失敗（逾時）：octo-org/sample-app · CI #42", toast.Title);
        var line = Assert.Single(toast.Lines);
        Assert.Equal("修正登入流程 · feature/login · by alice", line.Text);
        Assert.Equal(new Uri(run.HtmlUrl), line.Url);
        Assert.Equal(ToastTone.Failure, line.Tone);
        Assert.Null(toast.MoreText);
        Assert.Equal(new Uri(run.HtmlUrl), toast.OpenUrl);
    }

    [Fact]
    public void 彙總每一行用單筆的標題文字()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.Add([Planned(1, "failure"), Planned(2, "success")]);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        Assert.Equal(["失敗：octo-org/sample-app · CI #1", "成功：octo-org/sample-app · CI #2"], toast.Lines.Select(l => l.Text));
    }

    [Fact]
    public void 欄位缺值或含換行時不丟例外且留白()
    {
        var time = new ManualTimeProvider(Start);
        var withNewlines = new NotificationQueue(time);
        var allMissing = new NotificationQueue(time);
        var multiline = BaseRun with
        {
            WorkflowName = null,
            Path = ".github/workflows/build.yml",
            DisplayTitle = "  第一行\r\n第二行\n\n第三行  ",
            HeadBranch = null,
            ActorId = null,
            ActorLogin = null,
        };
        var empty = BaseRun with { WorkflowName = "  ", Path = "", DisplayTitle = "", HeadBranch = null, ActorLogin = null };

        withNewlines.Add([Planned(multiline)]);
        allMissing.Add([Planned(empty)]);
        time.Advance(Window);
        var multilineToast = RequireFlush(withNewlines);
        var emptyToast = RequireFlush(allMissing);

        // name 為 null 時用 path 的檔名（spec §10.5）；缺值的欄位連同分隔符號一起省略。
        Assert.Equal("失敗：octo-org/sample-app · build.yml #42", multilineToast.Title);
        Assert.Equal("第一行 第二行 第三行", Assert.Single(multilineToast.Lines).Text);
        Assert.Equal("失敗：octo-org/sample-app · #42", emptyToast.Title);
        Assert.Equal(string.Empty, Assert.Single(emptyToast.Lines).Text);
    }

    [Fact]
    public void run網址格式錯誤時不丟例外()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.Add([Planned(BaseRun with { HtmlUrl = "not a url" })]);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        // 單筆沒有可開的網址時，點空白處退回開主視窗；行的網址不會通過 https＋github.com 的檢查。
        Assert.Null(toast.OpenUrl);
        Assert.NotEqual(Uri.UriSchemeHttps, Assert.Single(toast.Lines).Url.Scheme);
    }

    [Fact]
    public void 鎖定期間不出_解鎖後一則鎖定期間的變化()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.SetLocked(true);
        Assert.True(queue.Locked);
        queue.Add([Planned(1, "failure")]);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(Flush(queue));
        queue.Add([Planned(2, "success")]);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(Flush(queue));

        queue.SetLocked(false);
        Assert.False(queue.Locked);
        var toast = RequireFlush(queue);

        Assert.Equal("鎖定期間的變化（1 失敗、1 成功）", toast.Title);
        Assert.Equal([RunUrl(1), RunUrl(2)], toast.Lines.Select(l => l.Url));
        Assert.Null(toast.OpenUrl);
        Assert.Equal(ToastTone.Failure, toast.Tone);
        Assert.Null(Flush(queue));
    }

    [Fact]
    public void 鎖定期間只有一筆也合併成鎖定期間的變化()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.SetLocked(true);
        queue.Add([Planned(1, "success")]);
        time.Advance(TimeSpan.FromMinutes(1));
        queue.SetLocked(false);
        var toast = RequireFlush(queue);

        Assert.Equal("鎖定期間的變化（1 成功）", toast.Title);
        Assert.Equal("成功：octo-org/sample-app · CI #1", Assert.Single(toast.Lines).Text);
        Assert.Null(toast.OpenUrl);
    }

    [Fact]
    public void 解鎖時沒有暫存就不顯示()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.SetLocked(true);
        time.Advance(TimeSpan.FromMinutes(5));
        queue.SetLocked(false);
        Assert.Null(Flush(queue));

        // 之後的通知照一般規則顯示，不帶「鎖定期間」的標題。
        queue.Add([Planned(1, "failure")]);
        time.Advance(Window);
        Assert.Equal("失敗：octo-org/sample-app · CI #1", RequireFlush(queue).Title);
    }

    [Fact]
    public void 解鎖後仍等滿合併視窗()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.SetLocked(true);
        queue.Add([Planned(1, "failure")]);
        time.Advance(TimeSpan.FromSeconds(1));
        queue.SetLocked(false);

        Assert.Null(Flush(queue));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("鎖定期間的變化（1 失敗）", RequireFlush(queue).Title);
    }

    [Fact]
    public void 鎖定前顯示中的提示不併入鎖定期間的變化()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.Add([Planned(1, "failure")]);
        time.Advance(Window);
        RequireFlush(queue);
        queue.SetLocked(true);
        queue.Add([Planned(2, "success")]);
        time.Advance(Window);
        queue.SetLocked(false);
        var toast = RequireFlush(queue);

        Assert.Equal("鎖定期間的變化（1 成功）", toast.Title);
        Assert.Equal(RunUrl(2), Assert.Single(toast.Lines).Url);
    }

    [Fact]
    public void 提示還開著時新內容併入()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.Add([Planned(1, "success")]);
        time.Advance(Window);
        var first = RequireFlush(queue);
        Assert.Equal("成功：octo-org/sample-app · CI #1", first.Title);

        queue.Add([Planned(2, "failure")]);
        time.Advance(TimeSpan.FromSeconds(1));
        // 新內容同樣等合併視窗。
        Assert.Null(Flush(queue));
        time.Advance(TimeSpan.FromSeconds(1));
        var merged = RequireFlush(queue);

        Assert.Equal("2 筆執行有變化（1 失敗、1 成功）", merged.Title);
        Assert.Equal([RunUrl(1), RunUrl(2)], merged.Lines.Select(l => l.Url));
        Assert.Null(merged.OpenUrl);
        // 併入失敗後改用失敗的語氣與停留（App 依此重新計時）。
        Assert.Equal(ToastTone.Failure, merged.Tone);
        Assert.Equal(FailureDuration, merged.Duration);
    }

    [Fact]
    public void Dismissed後不再併入()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.Add([Planned(1, "failure")]);
        time.Advance(Window);
        RequireFlush(queue);
        queue.Dismissed();
        queue.Add([Planned(2, "success")]);
        time.Advance(Window);
        var toast = RequireFlush(queue);

        Assert.Equal("成功：octo-org/sample-app · CI #2", toast.Title);
        Assert.Equal(RunUrl(2), Assert.Single(toast.Lines).Url);
        Assert.Equal(RunUrl(2), toast.OpenUrl);
    }

    [Fact]
    public void 重複加入同一筆只列一次()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);
        var same = Planned(1, "failure");

        queue.Add([same, same]);
        time.Advance(Window);
        var toast = RequireFlush(queue);
        queue.Add([same]);
        time.Advance(Window);

        Assert.Equal("失敗：octo-org/sample-app · CI #1", toast.Title);
        // 已經顯示過的同一筆再加入，沒有新內容可出。
        Assert.Null(Flush(queue));
    }

    [Fact]
    public void 合併視窗只看單調時鐘()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        queue.Add([Planned(1, "failure")]);
        // 牆鐘往前跳一小時（NTP 校時）不算經過時間。
        time.SetUtcNow(Start.AddHours(1));
        Assert.Null(Flush(queue));

        // 牆鐘往回調、單調時鐘照走，視窗照樣到期。
        time.SetUtcNow(Start.AddHours(-1));
        time.AdvanceMonotonic(Window);
        Assert.NotNull(Flush(queue));
    }

    [Fact]
    public void 沒有內容時回null()
    {
        var time = new ManualTimeProvider(Start);
        var queue = new NotificationQueue(time);

        Assert.Null(Flush(queue));
        queue.Add([]);
        time.Advance(Window);
        Assert.Null(Flush(queue));

        // 空清單不會提早啟動合併視窗。
        queue.Add([Planned(1, "failure")]);
        Assert.Null(Flush(queue));
        time.Advance(Window);
        Assert.NotNull(Flush(queue));
    }

    [Fact]
    public void 參數為null時丟例外()
    {
        var queue = new NotificationQueue(new ManualTimeProvider(Start));

        Assert.Throws<ArgumentNullException>(() => new NotificationQueue(null!));
        Assert.Throws<ArgumentNullException>(() => queue.Add(null!));
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
