using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Notifications;

public class NotificationPlannerTests
{
    private const long RepoId = 2001;
    private const long OtherRepoId = 2002;
    private const long MyUserId = 501;
    private const long OtherUserId = 502;
    private const string RepoFullName = "octo-org/sample-app";

    /// <summary>程式啟動時間，也是每個 repo 預設的時間界線 T0（spec §9.3）。</summary>
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = T0.AddHours(1);

    private static readonly NotificationOptions AllOn = new(TrackedFailure: true, MyRunSucceeded: true, WatchedRunDone: true);

    /// <summary>界線之後完成、別人觸發的失敗；各測試用 with 改出需要的欄位。</summary>
    private static readonly RunInfo BaseRun = new(
        Id: 1001, RepoId: RepoId, WorkflowId: 3001, WorkflowName: "CI", Path: ".github/workflows/ci.yml",
        RunNumber: 42, RunAttempt: 1, DisplayTitle: "修正登入流程", Event: "push", Status: "completed", Conclusion: "failure",
        HeadBranch: "main", HeadSha: "0123456789abcdef0123456789abcdef01234567", HeadRepoId: RepoId,
        HeadRepoFullName: RepoFullName, HeadRepoHtmlUrl: "https://github.com/octo-org/sample-app",
        ActorId: OtherUserId, ActorLogin: "alice", TriggeringActorLogin: "alice", PullRequestNumbers: [],
        RunStartedAt: T0.AddMinutes(20), CreatedAt: T0.AddMinutes(20), UpdatedAt: T0.AddMinutes(30),
        HtmlUrl: "https://github.com/octo-org/sample-app/actions/runs/1001");

    // ── Plan ─────────────────────────────────────────────────────────

    [Fact]
    public void 去重()
    {
        var store = new FakeNotifiedStore();
        var planner = new NotificationPlanner(store);
        var observation = Observe(BaseRun, tracked: true);

        var first = Plan(planner, observation);
        var second = Plan(planner, observation);

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(store.Entries);
    }

    [Fact]
    public void 同一批裡重複的觀察只通知一次()
    {
        var store = new FakeNotifiedStore();
        var planner = new NotificationPlanner(store);
        var observation = Observe(BaseRun, tracked: true);

        // 例如同一筆 run 同時出現在 E4 第 1 頁與 E6 的回應裡。
        var result = Plan(planner, observation, observation);

        Assert.Single(result);
        Assert.Single(store.Entries);
    }

    [Fact]
    public void 早於時間界線只寫suppressed()
    {
        var store = new FakeNotifiedStore();
        var planner = new NotificationPlanner(store);
        var run = BaseRun with { UpdatedAt = T0.AddHours(-3) };

        var result = Plan(planner, Observe(run, tracked: true));

        Assert.Empty(result);
        var entry = Assert.Single(store.Entries);
        Assert.Equal(new NotifiedEntry(1001, 1, "failure", NotificationKind.TrackedFailure, Suppressed: true, Now), entry);

        // 寫進 notified 之後，同一組鍵不會再被判定一次。
        Assert.Empty(Plan(planner, Observe(run, tracked: true)));
        Assert.Single(store.Entries);
    }

    [Fact]
    public void 早於時間界線而不符合任何條件的也寫suppressed()
    {
        var store = new FakeNotifiedStore();
        var planner = new NotificationPlanner(store);
        var run = BaseRun with { Conclusion = "success", UpdatedAt = T0.AddHours(-3) };

        var result = Plan(planner, Observe(run));

        Assert.Empty(result);
        var entry = Assert.Single(store.Entries);
        Assert.True(entry.Suppressed);
        Assert.Null(entry.Kind);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("timed_out")]
    [InlineData("startup_failure")]
    public void 界線後的追蹤失敗要通知(string conclusion)
    {
        var store = new FakeNotifiedStore();
        var planner = new NotificationPlanner(store);
        var run = BaseRun with { Conclusion = conclusion };

        var result = Plan(planner, Observe(run, tracked: true));

        var notification = Assert.Single(result);
        Assert.Equal(NotificationKind.TrackedFailure, notification.Kind);
        Assert.Equal(run, notification.Run);
        Assert.Equal(RepoFullName, notification.RepoFullName);
        Assert.Equal(RunCategory.Failure, notification.State.Category);
        Assert.Equal(new NotifiedEntry(1001, 1, conclusion, NotificationKind.TrackedFailure, Suppressed: false, Now), Assert.Single(store.Entries));
    }

    [Fact]
    public void 更新時間剛好等於界線也要通知()
    {
        var planner = new NotificationPlanner(new FakeNotifiedStore());
        var run = BaseRun with { UpdatedAt = T0 };

        // spec §9.3 只壓下 updated_at < T0 的 run。
        var result = Plan(planner, Observe(run, tracked: true));

        Assert.Equal(NotificationKind.TrackedFailure, Assert.Single(result).Kind);
    }

    [Fact]
    public void 未追蹤的失敗不通知()
    {
        var store = new FakeNotifiedStore();
        var planner = new NotificationPlanner(store);
        // 我觸發、但不在追蹤範圍內的失敗也不通知（Q11）。
        var run = BaseRun with { ActorId = MyUserId, ActorLogin = "me" };

        var result = Plan(planner, Observe(run, tracked: false));

        Assert.Empty(result);
        // 界線後、不符合任何條件的觀察不寫 notified（spec 只要求寫入已通知與 suppressed 的）。
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void 我觸發的成功要通知()
    {
        var store = new FakeNotifiedStore();
        var planner = new NotificationPlanner(store);
        // 比對的是 actor.id，login 改名也一樣認得；不限追蹤範圍。
        var run = BaseRun with { Conclusion = "success", ActorId = MyUserId, ActorLogin = "renamed-me", HeadBranch = "feature/login" };

        var result = Plan(planner, Observe(run, tracked: false));

        var notification = Assert.Single(result);
        Assert.Equal(NotificationKind.MyRunSucceeded, notification.Kind);
        Assert.Equal(new NotifiedEntry(1001, 1, "success", NotificationKind.MyRunSucceeded, Suppressed: false, Now), Assert.Single(store.Entries));
    }

    [Fact]
    public void 別人觸發的成功不通知()
    {
        var planner = new NotificationPlanner(new FakeNotifiedStore());
        // 照需求只看 actor（Q10）：我重跑別人的 run 不算；追蹤中的成功也不通知。
        var run = BaseRun with { Conclusion = "success", ActorId = OtherUserId, TriggeringActorLogin = "me" };

        var result = Plan(planner, Observe(run, tracked: true));

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(null, MyUserId)]
    [InlineData(MyUserId, null)]
    public void 帳號id或actor缺值時不算我觸發的成功(long? myUserId, long? actorId)
    {
        var planner = new NotificationPlanner(new FakeNotifiedStore());
        var run = BaseRun with { Conclusion = "success", ActorId = actorId };

        var result = planner.Plan([Observe(run)], myUserId, AllOn, _ => T0, Now);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("skipped")]
    public void 完成時通知我不受界線限制(string conclusion)
    {
        var store = new FakeNotifiedStore();
        var planner = new NotificationPlanner(store);
        // 程式關閉期間完成的 run，啟動後仍要通知（Q14）。
        var run = BaseRun with { Conclusion = conclusion, UpdatedAt = T0.AddHours(-3) };

        var result = Plan(planner, Observe(run, watched: true));

        Assert.Equal(NotificationKind.WatchedRunDone, Assert.Single(result).Kind);
        Assert.Equal(new NotifiedEntry(1001, 1, conclusion, NotificationKind.WatchedRunDone, Suppressed: false, Now), Assert.Single(store.Entries));
    }

    [Fact]
    public void 灰色結論只有完成時通知我會通知()
    {
        var planner = new NotificationPlanner(new FakeNotifiedStore());
        var tracked = BaseRun with { Id = 1001, Conclusion = "cancelled", ActorId = MyUserId };
        var watched = BaseRun with { Id = 1002, Conclusion = "cancelled" };

        var result = Plan(planner, Observe(tracked, tracked: true), Observe(watched, watched: true));

        var notification = Assert.Single(result);
        Assert.Equal(1002, notification.Run.Id);
        Assert.Equal(NotificationKind.WatchedRunDone, notification.Kind);
    }

    /// <summary>
    /// N1（失敗）與 N2（成功）的結論類別互斥，同一筆 run 不可能字面上同時符合；
    /// 這裡用「追蹤中＋我觸發＋完成時通知我」讓一筆 run 同時符合多項條件，驗證只發一次。
    /// </summary>
    [Theory]
    [InlineData("failure", false)]
    [InlineData("success", false)]
    [InlineData("failure", true)]
    [InlineData("success", true)]
    public void 同一筆同時符合N1與N2只發一次(string conclusion, bool watched)
    {
        var store = new FakeNotifiedStore();
        var planner = new NotificationPlanner(store);
        var run = BaseRun with { Conclusion = conclusion, ActorId = MyUserId };

        var result = Plan(planner, Observe(run, tracked: true, watched: watched));

        Assert.Single(result);
        Assert.Single(store.Entries);
    }

    [Theory]
    [InlineData("failure", true, NotificationKind.WatchedRunDone)]
    [InlineData("success", true, NotificationKind.WatchedRunDone)]
    [InlineData("failure", false, NotificationKind.TrackedFailure)]
    [InlineData("success", false, NotificationKind.MyRunSucceeded)]
    public void 同時符合多項時以完成時通知我為種類(string conclusion, bool watchedRunDoneOn, NotificationKind expected)
    {
        var planner = new NotificationPlanner(new FakeNotifiedStore());
        var run = BaseRun with { Conclusion = conclusion, ActorId = MyUserId };
        var options = AllOn with { WatchedRunDone = watchedRunDoneOn };

        var result = planner.Plan([Observe(run, tracked: true, watched: true)], MyUserId, options, _ => T0, Now);

        // N3 開著時種類一律是 WatchedRunDone，不因界線前後而不同，Engine 可以據此取消標記。
        Assert.Equal(expected, Assert.Single(result).Kind);
    }

    [Fact]
    public void run_attempt遞增時重新通知()
    {
        var store = new FakeNotifiedStore();
        var planner = new NotificationPlanner(store);
        var first = BaseRun with { RunAttempt = 1 };
        var rerun = BaseRun with { RunAttempt = 2, UpdatedAt = BaseRun.UpdatedAt.AddMinutes(10) };

        var firstResult = Plan(planner, Observe(first, tracked: true));
        var rerunResult = Plan(planner, Observe(rerun, tracked: true));

        Assert.Equal(1, Assert.Single(firstResult).Run.RunAttempt);
        Assert.Equal(2, Assert.Single(rerunResult).Run.RunAttempt);
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void 結論不同就是不同的去重鍵()
    {
        var store = new FakeNotifiedStore();
        var planner = new NotificationPlanner(store);
        var watched = BaseRun with { Conclusion = "cancelled" };
        var changed = BaseRun with { Conclusion = "failure" };

        Plan(planner, Observe(watched, watched: true));
        var result = Plan(planner, Observe(changed, tracked: true));

        // 鍵是 (run_id, run_attempt, conclusion)（spec §9.2）。
        Assert.Single(result);
        Assert.Equal(2, store.Entries.Count);
    }

    [Theory]
    [InlineData("failure", true, false, false, false, true, true)]
    [InlineData("success", false, true, false, true, false, true)]
    [InlineData("cancelled", false, false, true, true, true, false)]
    public void 關閉選項後不通知(
        string conclusion, bool tracked, bool mine, bool watched,
        bool trackedFailureOn, bool myRunSucceededOn, bool watchedRunDoneOn)
    {
        var store = new FakeNotifiedStore();
        var planner = new NotificationPlanner(store);
        var run = BaseRun with { Conclusion = conclusion, ActorId = mine ? MyUserId : OtherUserId };
        var observation = Observe(run, tracked, watched);
        var options = new NotificationOptions(trackedFailureOn, myRunSucceededOn, watchedRunDoneOn);

        var result = planner.Plan([observation], MyUserId, options, _ => T0, Now);

        Assert.Empty(result);
        Assert.Empty(store.Entries);
        // 對照：選項全開時同一筆會通知，確認是選項擋下的。
        Assert.Single(new NotificationPlanner(new FakeNotifiedStore()).Plan([observation], MyUserId, AllOn, _ => T0, Now));
    }

    [Fact]
    public void 重啟後不重發()
    {
        var store = new FakeNotifiedStore();
        var notified = Observe(BaseRun, tracked: true);
        var suppressed = Observe(BaseRun with { Id = 1002, UpdatedAt = T0.AddHours(-3) }, tracked: true);

        var beforeRestart = Plan(new NotificationPlanner(store), notified, suppressed);
        // 同一個 store 交給新的 planner，模擬重啟後從 cache.db 讀回 notified。
        var afterRestart = Plan(new NotificationPlanner(store), notified, suppressed);

        Assert.Equal(1001, Assert.Single(beforeRestart).Run.Id);
        Assert.Empty(afterRestart);
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void 每個repo用自己的時間界線()
    {
        var planner = new NotificationPlanner(new FakeNotifiedStore());
        // 另一個 repo 是 T0 + 45 分鐘才取消靜音，界線是操作的時間。
        var unmutedAt = T0.AddMinutes(45);
        var sameRepo = BaseRun with { Id = 1001, UpdatedAt = T0.AddMinutes(30) };
        var otherRepo = BaseRun with { Id = 1002, RepoId = OtherRepoId, UpdatedAt = T0.AddMinutes(30) };

        var result = planner.Plan(
            [Observe(sameRepo, tracked: true), Observe(otherRepo, tracked: true)],
            MyUserId, AllOn, repoId => repoId == OtherRepoId ? unmutedAt : T0, Now);

        Assert.Equal(1001, Assert.Single(result).Run.Id);
    }

    [Theory]
    [InlineData("in_progress", null)]
    [InlineData("queued", null)]
    [InlineData("waiting", null)]
    [InlineData("completed", "action_required")]
    public void 未完成或等待核准的觀察不處理也不寫入(string status, string? conclusion)
    {
        var store = new FakeNotifiedStore();
        var planner = new NotificationPlanner(store);
        var run = BaseRun with { Status = status, Conclusion = conclusion };

        var result = Plan(planner, Observe(run, tracked: true, watched: true));

        Assert.Empty(result);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void 沒有完成觀察時回空清單()
    {
        var store = new FakeNotifiedStore();

        var result = new NotificationPlanner(store).Plan([], MyUserId, AllOn, _ => T0, Now);

        Assert.Empty(result);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void 參數為null時丟例外()
    {
        var planner = new NotificationPlanner(new FakeNotifiedStore());

        Assert.Throws<ArgumentNullException>(() => new NotificationPlanner(null!));
        Assert.Throws<ArgumentNullException>(() => planner.Plan(null!, MyUserId, AllOn, _ => T0, Now));
        Assert.Throws<ArgumentNullException>(() => planner.Plan([], MyUserId, null!, _ => T0, Now));
        Assert.Throws<ArgumentNullException>(() => planner.Plan([], MyUserId, AllOn, null!, Now));
        Assert.Throws<ArgumentNullException>(() => NotificationPlanner.IsNewCompletion(null, null!));
    }

    // ── IsNewCompletion ──────────────────────────────────────────────

    [Fact]
    public void 未完成轉完成()
    {
        var previous = Attempt(1, "in_progress", null);

        Assert.True(NotificationPlanner.IsNewCompletion(previous, BaseRun));
    }

    [Fact]
    public void 第一次看到就已完成()
    {
        // 新 repo、E5／E6 補抓、PR 分支補抓第一次看到時就已經完成。
        Assert.True(NotificationPlanner.IsNewCompletion(null, BaseRun));
    }

    [Fact]
    public void 同attempt已完成不再算()
    {
        var previous = Attempt(1, "completed", "failure");

        Assert.False(NotificationPlanner.IsNewCompletion(previous, BaseRun));
    }

    [Fact]
    public void 追蹤範圍變動不會產生新的完成觀察()
    {
        // 功能分支上早已完成的失敗，因為剛開了 PR 而變成追蹤中：同一個 attempt 已經看過完成，不算新的完成（spec §9.1）。
        var previous = Attempt(1, "completed", "failure");
        var nowTracked = BaseRun with { HeadBranch = "feature/login" };

        Assert.False(NotificationPlanner.IsNewCompletion(previous, nowTracked));
    }

    [Fact]
    public void 新attempt算新的()
    {
        var previous = Attempt(1, "completed", "failure");
        var rerunDone = BaseRun with { RunAttempt = 2, Conclusion = "success" };
        var rerunRunning = BaseRun with { RunAttempt = 2, Status = "in_progress", Conclusion = null };

        Assert.True(NotificationPlanner.IsNewCompletion(previous, rerunDone));
        // 新 attempt 還沒完成就不是完成觀察。
        Assert.False(NotificationPlanner.IsNewCompletion(previous, rerunRunning));
    }

    [Fact]
    public void 較舊的attempt不算新的完成()
    {
        // API 回了比本地紀錄舊的 attempt（多區域資料延遲）：那是過時的資料，不是新的完成。
        var previous = Attempt(2, "in_progress", null);
        var staleAttempt = BaseRun with { RunAttempt = 1, Status = "completed", Conclusion = "failure" };

        Assert.False(NotificationPlanner.IsNewCompletion(previous, staleAttempt));
    }

    [Theory]
    [InlineData("waiting", null)]
    [InlineData("completed", "action_required")]
    public void 等待核准不算完成(string status, string? conclusion)
    {
        var current = BaseRun with { Status = status, Conclusion = conclusion };

        Assert.False(NotificationPlanner.IsNewCompletion(null, current));
        Assert.False(NotificationPlanner.IsNewCompletion(Attempt(1, "queued", null), current));
    }

    [Fact]
    public void 等待核准後完成算新的()
    {
        var previous = Attempt(1, "completed", "action_required");

        Assert.True(NotificationPlanner.IsNewCompletion(previous, BaseRun));
    }

    [Theory]
    [InlineData("in_progress", null)]
    [InlineData("brand_new_status", null)]
    [InlineData(null, null)]
    public void 未完成或不認得的狀態不算完成(string? status, string? conclusion)
    {
        var current = BaseRun with { Status = status, Conclusion = conclusion };

        Assert.False(NotificationPlanner.IsNewCompletion(null, current));
    }

    [Fact]
    public void completed但結論缺值算完成()
    {
        // completed 加 null 結論歸灰（spec §7.1），也是最終結果。
        var current = BaseRun with { Conclusion = null };

        Assert.True(NotificationPlanner.IsNewCompletion(Attempt(1, "in_progress", null), current));
    }

    [Fact]
    public void previous不是同一個run時丟例外()
    {
        var previous = new RunAttemptRecord(9999, 1, "in_progress", null, T0, T0);

        Assert.Throws<ArgumentException>(() => NotificationPlanner.IsNewCompletion(previous, BaseRun));
    }

    private static RunAttemptRecord Attempt(int attempt, string? status, string? conclusion) =>
        new(BaseRun.Id, attempt, status, conclusion, BaseRun.RunStartedAt, BaseRun.UpdatedAt.AddMinutes(-5));

    private static CompletionObservation Observe(RunInfo run, bool tracked = false, bool watched = false) =>
        new(run, RunStateMapper.Map(run), RepoFullName, tracked, watched);

    private static IReadOnlyList<PlannedNotification> Plan(NotificationPlanner planner, params CompletionObservation[] completions) =>
        planner.Plan(completions, MyUserId, AllOn, _ => T0, Now);
}
