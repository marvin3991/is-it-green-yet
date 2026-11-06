using System.Globalization;
using System.Net;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tests.Auth;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>寫入專用的送出路徑（第二階段 spec §3.2、§3.3、§5.2～§5.4）：再檢查、截止時間、寫入間隔、不重送、結果分類。</summary>
public class MonitorEngineWriteTests
{
    private const long FailedRunId = 9001;
    private const long RunningRunId = 9002;

    private const long OtherRepoId = 2002;

    private const string RunsPrefix = "/repos/octo-org/sample-app/actions/runs/";
    private const string RerunFailedPath = RunsPrefix + "9001/rerun-failed-jobs";
    private const string CancelPath = RunsPrefix + "9002/cancel";
    private const string FailedRunDetailPath = RunsPrefix + "9001";
    private const string RunningRunDetailPath = RunsPrefix + "9002";
    private const string JobsPath = RunsPrefix + "9001/jobs?filter=latest&per_page=100";

    /// <summary>權限不足的 403（第二階段 spec §3.3 第 4 列）。</summary>
    private const string NotAccessibleMessage = "Resource not accessible by personal access token";

    /// <summary>error.log 裡寫入回應錯誤的那一筆的標題。</summary>
    private const string WriteErrorContext = "寫入回應錯誤";

    /// <summary>模擬的 5xx 與其他錯誤回應的 message（後面另接假 token，驗證遮罩）。</summary>
    private const string ServerErrorMessage = "Server Error";

    /// <summary>重建資料庫退避期間連跑幾步（時間不前進），每一步都發布沿用上一份內容的快照。</summary>
    private const int CarryOverSteps = 3;

    /// <summary>模擬限流回應的 retry-after（秒）。</summary>
    private const int RetryAfterSeconds = 60;

    private static readonly DateTimeOffset Start = EngineHarness.Start;

    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>背景工作的保險上限；正常情況下等待的條件都會立刻成立，不靠時間流逝。</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    /// <summary>每分鐘上限的視窗要再等多久才空出一格（短於 MutativeMaxQueueWait）。</summary>
    private static readonly TimeSpan ShortQueue = TimeSpan.FromSeconds(10);

    /// <summary>「POST 在第 14 秒才送出」：截止時間前 1 秒才放行。</summary>
    private static readonly TimeSpan LateSend = Tuning.MutativeMaxQueueWait - TimeSpan.FromSeconds(1);

    /// <summary>寫入功能打開、預設 repo 有一筆一天前失敗的 run（9001）；<paramref name="arrange"/> 在建立 Engine 之前呼叫。</summary>
    private static EngineHarness Create(Action<EngineHarness>? arrange = null) =>
        EngineHarness.Create(h =>
        {
            h.Settings.AllowWriteActions = true;
            h.Main.Runs.Add(FakeData.Run(FailedRunId, 42, "completed", "failure", Start.AddDays(-1), Start.AddDays(-1).AddMinutes(5)));
            arrange?.Invoke(h);
        });

    /// <summary>另一筆進行中的 run（9002），可以取消。</summary>
    private static void AddRunning(EngineHarness h) =>
        h.Main.Runs.Add(FakeData.Run(RunningRunId, 43, "in_progress", null, Start.AddMinutes(-5)));

    /// <summary>送出寫入，加上保險上限：實作錯誤地卡在等待時（例如測試沒有推進時間的寫入間隔），測試失敗而不是一直等。</summary>
    private static Task<WriteResult> Send(EngineHarness h, WriteAction action, long runId, int attempt = 1, long repoId = FakeData.RepoId) =>
        h.Engine.SendWriteAsync(action, repoId, runId, attempt, None).WaitAsync(Guard);

    private static Task<WriteResult> Rerun(EngineHarness h, int attempt = 1) => Send(h, WriteAction.RerunFailedJobs, FailedRunId, attempt);

    private static Task<WriteResult> CancelRunning(EngineHarness h) => Send(h, WriteAction.Cancel, RunningRunId);

    /// <summary>第一次檢查就停下的寫入：不排隊、不等節流器就回傳。還沒完成代表進了等待，直接失敗。</summary>
    private static async Task<WriteResult> Immediately(Task<WriteResult> pending)
    {
        Assert.True(pending.IsCompleted, "這個寫入應該在第一次檢查就回傳，不排隊等待。");
        return await pending;
    }

    // ── 送出前的檢查（第二階段 spec §5.3 第 3 點、§5.4）──────────────────

    [Fact]
    public async Task 寫入功能關閉時回未送出而且不送POST()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(FailedRunId, 42, "completed", "failure", Start.AddDays(-1), Start.AddDays(-1).AddMinutes(5))));
        await h.DrainAsync();

        var result = await Immediately(Rerun(h));

        Assert.Equal(new WriteResult(WriteAction.RerunFailedJobs, WriteOutcome.NotSent, NotSent: NotSentReason.WritesDisabled,
            SnapshotSequence: h.Last.Sequence), result);
        Assert.Empty(h.GitHub.Posts);

        // 開關是即時讀的：打開之後照常送出（關閉時沒有留下送出中）。
        h.Settings.AllowWriteActions = true;
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.StepAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
    }

    [Theory]
    [InlineData(WriteAction.RerunFailedJobs, "rerun-failed-jobs")]
    [InlineData(WriteAction.RerunAll, "rerun")]
    public async Task 可以重跑時送出POST並回受理(WriteAction action, string endpoint)
    {
        await using var h = Create();
        await h.DrainAsync();
        var sequence = h.Last.Sequence;

        var result = await Send(h, action, FailedRunId);

        Assert.Equal(new WriteResult(action, WriteOutcome.Accepted, StatusCode: (int)HttpStatusCode.Created, SnapshotSequence: sequence), result);
        var post = Assert.Single(h.GitHub.Handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Equal(RunsPrefix + "9001/" + endpoint, post.Uri.PathAndQuery);
        Assert.Equal("Bearer " + Fixture.FakeToken, post.Header("Authorization"));
        Assert.Null(post.Header("If-None-Match"));
        Assert.Equal(0, post.ContentLength);
    }

    [Fact]
    public async Task 對話框開著時run被重跑回已有新的嘗試且不送POST()
    {
        await using var h = Create();
        await h.DrainAsync();

        // 對話框上是第 1 次嘗試；這期間有人從網頁重跑，第 2 次嘗試開始執行，下一次輪詢就看到了。
        var run = h.Main.Runs.Single(candidate => RepoWorld.RunId(candidate) == FailedRunId);
        run["run_attempt"] = 2;
        run["status"] = "in_progress";
        run["conclusion"] = null;
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal(2, h.Last.Actions[FailedRunId].RunAttempt);

        AssertNotSent(await Immediately(Rerun(h, attempt: 1)), NotSentReason.AttemptChanged);
        Assert.Empty(h.GitHub.Posts);

        // 未送出不進入操作後追蹤：不排 E6。
        await h.StepAsync();
        Assert.DoesNotContain(FailedRunDetailPath, h.GitHub.ApiRequests);
    }

    [Fact]
    public async Task 對話框開著時repo被靜音回找不到且不送POST()
    {
        await using var h = Create();
        await h.DrainAsync();

        // 查表裡沒有的 run、和 repo 對不上的 run 都是找不到。
        AssertNotSent(await Immediately(Send(h, WriteAction.RerunFailedJobs, runId: 9999)), NotSentReason.NotFound);
        AssertNotSent(await Immediately(Send(h, WriteAction.RerunFailedJobs, FailedRunId, repoId: OtherRepoId)), NotSentReason.NotFound);

        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.DrainAsync();
        Assert.False(h.Last.Actions.ContainsKey(FailedRunId));

        AssertNotSent(await Immediately(Rerun(h)), NotSentReason.NotFound);
        Assert.Empty(h.GitHub.Posts);
    }

    [Fact]
    public async Task 這個動作依run狀態不能用時回不能操作並帶原因()
    {
        await using var h = Create();
        await h.DrainAsync();

        // 已完成的 run 不能取消（第二階段 spec §7.1）：Engine 自己檢查，不只靠 App 停用選單（§5.3 第 4 點）。
        var result = await Immediately(Send(h, WriteAction.Cancel, FailedRunId));

        AssertNotSent(result, NotSentReason.Unavailable);
        Assert.Equal(ActionBlock.Completed, result.Block);
        Assert.Empty(h.GitHub.Posts);
    }

    [Fact]
    public async Task 同一筆run送出中再送回送出中()
    {
        using var postSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.OnRequest = request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postSeen.Set();
                release.Wait(Guard);
            }
        };

        var first = Task.Run(() => Rerun(h));
        Assert.True(postSeen.Wait(Guard));

        // 第一個寫入還在等回應：同一筆 run 的其他動作也不能送（第二階段 spec §5.3）。
        AssertNotSent(await Immediately(Send(h, WriteAction.RerunAll, FailedRunId)), NotSentReason.InFlight);

        release.Set();
        Assert.Equal(WriteOutcome.Accepted, (await first.WaitAsync(Guard)).Outcome);
        Assert.Equal([RerunFailedPath], h.GitHub.Posts);
    }

    [Fact]
    public async Task 寫入回傳後下一份快照發布前再送同一筆重跑回未送出()
    {
        await using var h = Create();
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);

        // 下一份快照發布前，查表還是送出前的那一份：送出中的紀錄留著，再按一次也不送（第二階段 spec §5.3 第 2 點）。
        AssertNotSent(await Immediately(Rerun(h)), NotSentReason.InFlight);
        Assert.Single(h.GitHub.Posts);

        // 套用這次寫入的那一步發布快照之後才移除：那一份快照已經不是送出中。
        await h.StepAsync();
        Assert.NotEqual(ActionBlock.InFlight, h.Last.Actions[FailedRunId].RerunFailedJobs);
        await AssertInFlightAsync(h, expected: false);
        Assert.Single(h.GitHub.Posts);
    }

    [Theory]
    [InlineData(WriteAction.RerunFailedJobs)]
    [InlineData(WriteAction.RerunAll)]
    public async Task 受理後下一份快照發布後再送同一筆重跑回不能操作而且不送POST(WriteAction action)
    {
        await using var h = Create();
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Send(h, action, FailedRunId)).Outcome);

        // 套用這次寫入的那一步發布快照：送出中移除，同一份快照已經是待確認的重跑（第二階段 spec §5.3 第 2 點）。
        // 這一步的 E6 讀回的 run 還沒更新（同一個 attempt、仍是已完成的失敗）。
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(1, h.GitHub.Count(FailedRunDetailPath));
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunAll);

        // 兩種重跑都不能再送：不能操作（待確認的重跑），不送 POST。
        foreach (var again in new[] { WriteAction.RerunFailedJobs, WriteAction.RerunAll })
        {
            var result = await Immediately(Send(h, again, FailedRunId));
            AssertNotSent(result, NotSentReason.Unavailable);
            Assert.Equal(ActionBlock.RerunPending, result.Block);
        }

        Assert.Single(h.GitHub.Posts);
    }

    [Fact]
    public async Task 離線時回未送出而且不等()
    {
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.NetworkDown = true;
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.True(h.Last.Offline);
        h.GitHub.NetworkDown = false;

        // 送了也一定失敗，而且會變成結果不確定（第二階段 spec §5.4）：不等、不送。
        var pending = Rerun(h);

        Assert.True(pending.IsCompleted);
        AssertNotSent(await pending, NotSentReason.Offline);
        Assert.Empty(h.GitHub.Posts);
    }

    [Fact]
    public async Task 限流暫停中回未送出而且不等()
    {
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.TooManyRequests, "rate limited", ("retry-after", "60")));
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal(BannerKind.SecondaryLimit, h.Last.Banner?.Kind);

        // 複製 log 會等暫停結束再送；寫入不等，否則可能在你按下去很久之後才送出（第二階段 spec §5.3 第 3 點）。
        var pending = Rerun(h);

        Assert.True(pending.IsCompleted);
        AssertNotSent(await pending, NotSentReason.RateLimitPaused);
        Assert.Empty(h.GitHub.Posts);
    }

    [Fact]
    public async Task 等待中進入全域暫停時不會送出()
    {
        using var jobsSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.Override(JobsPath, _ => h.GitHub.Error(HttpStatusCode.TooManyRequests, "rate limited", ("retry-after", "60")));
        h.GitHub.OnRequest = request => BlockOn(request, JobsPath, jobsSeen, release);

        // 複製失敗 log 佔著使用者操作那一條：寫入通過第一次檢查後排在它後面等。
        var copy = Task.Run(() => h.Engine.CopyFailedLogAsync(FakeData.RepoId, FailedRunId, None));
        Assert.True(jobsSeen.Wait(Guard));
        var write = Rerun(h);
        Assert.False(write.IsCompleted);

        // 複製 log 的請求遇到限流而全域暫停；寫入輪到時在節流器的鎖內再檢查一次，不送出。
        release.Set();
        await copy.WaitAsync(Guard);

        AssertNotSent(await write.WaitAsync(Guard), NotSentReason.RateLimitPaused);
        Assert.Empty(h.GitHub.Posts);
    }

    [Fact]
    public async Task 等待中進入未登入時不會送出()
    {
        using var jobsSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.Override(JobsPath, _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"));
        h.GitHub.OnRequest = request => BlockOn(request, JobsPath, jobsSeen, release);

        var copy = Task.Run(() => h.Engine.CopyFailedLogAsync(FakeData.RepoId, FailedRunId, None));
        Assert.True(jobsSeen.Wait(Guard));
        var write = Rerun(h);
        Assert.False(write.IsCompleted);

        // 複製 log 收到 401、重新取得 token 拿回同一個值：進入未登入（P1 §13.2）。寫入不能再用被拒的 token 送出。
        h.Tokens.Returns(Fixture.FakeToken);
        release.Set();
        await copy.WaitAsync(Guard);

        AssertNotSent(await write.WaitAsync(Guard), NotSentReason.NotLoggedIn);
        Assert.Empty(h.GitHub.Posts);
    }

    [Fact]
    public async Task 等待期間發布了快照寫入仍照常送出()
    {
        await using var h = Create();
        await h.DrainAsync();
        await MonitorEngineReviewTests.FillRequestWindowAsync(h);
        h.Clock.Advance(Tuning.RequestWindow - ShortQueue);

        var write = Rerun(h);
        Assert.False(write.IsCompleted);

        // 等待期間迴圈照常發布快照、換上新的查表（輪詢讓寫入先送，不送請求）。新的查表裡這筆 run 是送出中，
        // 那是自己造成的，送出前的再檢查不看它（第二階段 spec §5.3）。
        Assert.Equal(0, await h.StepAsync());
        Assert.Equal(0, await h.StepAsync());
        Assert.Equal(ActionBlock.InFlight, h.Last.Actions[FailedRunId].RerunFailedJobs);

        h.Clock.Advance(ShortQueue);

        Assert.Equal(WriteOutcome.Accepted, (await write.WaitAsync(Guard)).Outcome);
        Assert.Equal([RerunFailedPath], h.GitHub.Posts);
    }

    // ── run 狀態的再檢查：公開查表被送出中或全域代碼蓋掉時（第二階段 spec §5.3 第 3.1 點、第 4 點）──

    [Fact]
    public async Task 取消排隊期間run完成時回不能操作而且不送POST()
    {
        using var jobsSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        h.GitHub.OnRequest = request => BlockOn(request, JobsPath, jobsSeen, release);

        // 複製失敗 log 佔著使用者操作那一條：取消通過第一次檢查後排在它後面等。這段期間輪詢照常送。
        var copy = Task.Run(() => h.Engine.CopyFailedLogAsync(FakeData.RepoId, FailedRunId, None));
        Assert.True(jobsSeen.Wait(Guard));
        var cancel = CancelRunning(h);
        Assert.False(cancel.IsCompleted);

        // 排隊期間輪詢看到 9002 已經完成。公開的查表因為自己的送出中，四個動作都是送出中，看不到「已完成」。
        FakeData.Complete(h.Main.Runs.Single(run => RepoWorld.RunId(run) == RunningRunId), "success", h.Clock.GetUtcNow());
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal(ActionBlock.InFlight, h.Last.Actions[RunningRunId].Cancel);

        // 輪到時在節流器的鎖內再檢查 run 狀態：已完成不能取消，不送 POST。
        release.Set();
        await copy.WaitAsync(Guard);
        var result = await cancel.WaitAsync(Guard);

        AssertNotSent(result, NotSentReason.Unavailable);
        Assert.Equal(ActionBlock.Completed, result.Block);
        Assert.Empty(h.GitHub.Posts);
    }

    [Fact]
    public async Task 排隊期間發布過快照而排隊太久之後取消已完成的run回不能操作而且不送POST()
    {
        await using var h = Create();
        await h.DrainAsync();
        await MonitorEngineReviewTests.FillRequestWindowAsync(h);

        // 重跑 9001 排隊等節流器，期間發布的快照把 9001 的四個動作都標成送出中。
        var write = Rerun(h);
        Assert.Equal(0, await h.StepAsync());
        Assert.Equal(ActionBlock.InFlight, h.Last.Actions[FailedRunId].Cancel);
        h.Clock.Advance(Tuning.MutativeMaxQueueWait);
        AssertNotSent(await write.WaitAsync(Guard), NotSentReason.QueueTimeout);

        // 排隊太久就移除送出中；下一份快照發布前公開查表仍是送出中，對已完成的 9001 送取消照樣看得到「已完成」。
        var result = await Immediately(Send(h, WriteAction.Cancel, FailedRunId));

        AssertNotSent(result, NotSentReason.Unavailable);
        Assert.Equal(ActionBlock.Completed, result.Block);
        Assert.Empty(h.GitHub.Posts);
    }

    [Fact]
    public async Task 全域暫停剛結束而快照還沒更新時取消已完成的run回不能操作而且不送POST()
    {
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.TooManyRequests, "rate limited",
            ("retry-after", RetryAfterSeconds.ToString(CultureInfo.InvariantCulture))));
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal(ActionBlock.RateLimitPaused, h.Last.Actions[FailedRunId].Cancel);

        // 暫停照時間到期，下一份快照還沒組出：公開查表仍是全域代碼，即時狀態已經不在暫停。
        h.Clock.Advance(TimeSpan.FromSeconds(RetryAfterSeconds));
        var result = await Immediately(Send(h, WriteAction.Cancel, FailedRunId));

        AssertNotSent(result, NotSentReason.Unavailable);
        Assert.Equal(ActionBlock.Completed, result.Block);
        Assert.Empty(h.GitHub.Posts);
    }

    // ── 截止時間與寫入間隔（第二階段 spec §3.2、§5.3 第 3 點）──────────────

    [Fact]
    public async Task 排隊超過15秒回排隊太久()
    {
        await using var h = Create();
        await h.DrainAsync();
        await MonitorEngineReviewTests.FillRequestWindowAsync(h);

        // 視窗要 60 秒後才空出一格：等滿 MutativeMaxQueueWait 就放棄，不是一直等到放行。
        var write = Rerun(h);
        h.Clock.Advance(Tuning.MutativeMaxQueueWait - TimeSpan.FromMilliseconds(1));
        Assert.False(write.IsCompleted);
        h.Clock.Advance(TimeSpan.FromMilliseconds(1));

        AssertNotSent(await write.WaitAsync(Guard), NotSentReason.QueueTimeout);
        Assert.Empty(h.GitHub.Posts);

        // 放棄之後不留下送出中：視窗空出來之後可以再送。
        await AssertInFlightAsync(h, expected: false);
        h.Clock.Advance(Tuning.RequestWindow);
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
    }

    [Fact]
    public async Task POST在第14秒才送出而回應較慢時不是結果不確定()
    {
        using var postSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = Create();
        await h.DrainAsync();
        await MonitorEngineReviewTests.FillRequestWindowAsync(h);
        h.GitHub.AbortWhenCancelled = true;
        h.GitHub.OnRequest = request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postSeen.Set();
                release.Wait(Guard);
            }
        };

        // 視窗在第 14 秒空出一格。
        h.Clock.Advance(Tuning.RequestWindow - LateSend);
        var write = Rerun(h);

        // 在另一條執行緒推進時間：計時器的回呼接著送出 POST，POST 卡在等回應。
        var advance = Task.Run(() => h.Clock.Advance(LateSend));
        Assert.True(postSeen.Wait(Guard));

        // 回應到之前已經超過 15 秒的截止時間。截止時間只管排隊，不會取消已經送出的請求（逾時交給 HttpTimeout），
        // 否則已經送到伺服器的寫入會被當成結果不確定。
        h.Clock.Advance(Tuning.MutativeMaxQueueWait - LateSend + TimeSpan.FromSeconds(1));
        release.Set();
        await advance.WaitAsync(Guard);

        var result = await write.WaitAsync(Guard);
        Assert.Equal(WriteOutcome.Accepted, result.Outcome);
        Assert.Equal((int)HttpStatusCode.Created, result.StatusCode);
        Assert.Single(h.GitHub.Posts);
    }

    [Fact]
    public async Task 兩個寫入之間隔1秒()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);

        // 不同的 run 也要等：兩個寫入請求之間至少隔 MutativeRequestSpacing（第二階段 spec §3.2）。
        var second = CancelRunning(h);
        h.Clock.Advance(Tuning.MutativeRequestSpacing - TimeSpan.FromMilliseconds(1));
        Assert.False(second.IsCompleted);
        Assert.Single(h.GitHub.Posts);

        h.Clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(WriteOutcome.Accepted, (await second.WaitAsync(Guard)).Outcome);
        Assert.Equal([RerunFailedPath, CancelPath], h.GitHub.Posts);
    }

    // ── 不重送：只有 401 換到新 token 重送一次（第二階段 spec §3.2、P1 §13.2）──

    [Fact]
    public async Task _401換到新token後重送一次()
    {
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.Override(RerunFailedPath, _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"), times: 1);
        h.Tokens.Returns(EngineHarness.OtherFakeToken);

        // 401 代表伺服器沒有執行：換到新 token 就重送一次。重送也是寫入，照樣等滿寫入的間隔。
        var write = Rerun(h);
        Assert.False(write.IsCompleted);
        Assert.Single(h.GitHub.Posts);
        h.Clock.Advance(Tuning.MutativeRequestSpacing);

        var result = await write.WaitAsync(Guard);
        Assert.Equal(WriteOutcome.Accepted, result.Outcome);
        var posts = h.GitHub.Handler.Requests.Where(request => request.Method == HttpMethod.Post).ToList();
        Assert.Equal(2, posts.Count);
        Assert.Equal("Bearer " + Fixture.FakeToken, posts[0].Header("Authorization"));
        Assert.Equal("Bearer " + EngineHarness.OtherFakeToken, posts[1].Header("Authorization"));

        // 待確認重跑的送出時間是重送的那一次。
        Assert.Equal(h.Clock.GetUtcNow(), Assert.Single(h.Cache.GetPendingReruns()).RequestedAt);
    }

    [Fact]
    public async Task _401後token相同不重送()
    {
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.Override(RerunFailedPath, _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"));
        h.Tokens.Returns(Fixture.FakeToken);

        var result = await Rerun(h);

        Assert.Equal(WriteOutcome.Rejected, result.Outcome);
        Assert.Equal(WriteRejectReason.Unauthorized, result.Rejected);
        Assert.Equal((int)HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Single(h.GitHub.Posts);

        // 進入未登入；之後也不再送。
        await h.AdvanceAsync(Tuning.MutativeMaxQueueWait);
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);
        Assert.Single(h.GitHub.Posts);
    }

    [Fact]
    public async Task _401換到新token重送仍401時進入未登入而且不再送()
    {
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.Override(RerunFailedPath, _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"));
        h.Tokens.Returns(EngineHarness.OtherFakeToken);

        var write = Rerun(h);
        h.Clock.Advance(Tuning.MutativeRequestSpacing);
        var result = await write.WaitAsync(Guard);

        // 重送仍 401：不再重取 token、不送第三次（P1 §13.2 第 2 點）。
        Assert.Equal(WriteRejectReason.Unauthorized, result.Rejected);
        Assert.Equal(2, h.GitHub.Posts.Count);
        await h.AdvanceAsync(Tuning.MutativeMaxQueueWait);
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);
        Assert.Equal(2, h.GitHub.Posts.Count);
    }

    [Fact]
    public async Task 逾時回結果不確定而且不重送()
    {
        using var postSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.AbortWhenCancelled = true;
        h.GitHub.OnRequest = request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postSeen.Set();
                release.Wait(Guard);
            }
        };

        var write = Task.Run(() => Rerun(h));
        Assert.True(postSeen.Wait(Guard));

        // 等滿 HttpTimeout 還沒有回應：GitHubClient 逾時。請求可能已經生效（第二階段 spec §5.2）。
        h.Clock.Advance(Tuning.HttpTimeout);
        release.Set();
        var result = await write.WaitAsync(Guard);

        Assert.Equal(new WriteResult(WriteAction.RerunFailedJobs, WriteOutcome.Uncertain, SnapshotSequence: h.Last.Sequence), result);
        await h.AdvanceAsync(Tuning.MutativeMaxQueueWait);
        Assert.Single(h.GitHub.Posts);
    }

    // ── 被拒的分類（第二階段 spec §3.3）────────────────────────────────

    [Fact]
    public async Task 權限不足的403停用這個repo()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        h.GitHub.Override(RerunFailedPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, NotAccessibleMessage,
            ("X-Accepted-GitHub-Permissions", "actions=write")));

        var result = await Rerun(h);

        Assert.Equal(new WriteResult(WriteAction.RerunFailedJobs, WriteOutcome.Rejected, (int)HttpStatusCode.Forbidden,
            WriteRejectReason.InsufficientPermission, ServerMessage: NotAccessibleMessage, AcceptedPermissions: "actions=write",
            SnapshotSequence: h.Last.Sequence), result);

        // 這個 repo 的寫入改成停用（第二階段 spec §4.3）：同一個 repo 的其他 run 馬上再送就是缺少權限，不送 POST。
        AssertNotSent(await Immediately(CancelRunning(h)), NotSentReason.MissingPermission);
        Assert.Single(h.GitHub.Posts);

        await h.StepAsync();
        Assert.Equal(ActionBlock.MissingPermission, h.Last.Actions[RunningRunId].Cancel);
        Assert.Equal(ActionBlock.MissingPermission, h.Last.Actions[FailedRunId].RerunAll);
    }

    [Fact]
    public async Task _409回被拒並帶伺服器message()
    {
        const string message = "Cannot cancel a workflow run that is completed.";
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        h.GitHub.Override(CancelPath, _ => h.GitHub.Error(HttpStatusCode.Conflict, message));

        var result = await CancelRunning(h);

        Assert.Equal(new WriteResult(WriteAction.Cancel, WriteOutcome.Rejected, (int)HttpStatusCode.Conflict, WriteRejectReason.Conflict,
            ServerMessage: message, SnapshotSequence: h.Last.Sequence), result);
        Assert.DoesNotContain(WriteErrorContext, h.ErrorLogText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task _3xx回位置改變並重抓repo清單(HttpStatusCode status)
    {
        const string movedPath = "/repos/octo-org/sample-app-renamed/actions/runs/9001/rerun-failed-jobs";
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.Override(RerunFailedPath, _ => FakeHttpHandler.Status(status,
            new Dictionary<string, string> { ["Location"] = FakeGitHub.ApiBase + movedPath }));
        var before = h.GitHub.ApiRequests.Count;

        var result = await Rerun(h);

        // 寫入不跟隨轉址（第二階段 spec §3.2）：先讓 repo 清單更新成新名稱，你再按一次才送到新的位置。
        Assert.Equal(WriteOutcome.Rejected, result.Outcome);
        Assert.Equal(WriteRejectReason.Moved, result.Rejected);
        Assert.Equal((int)status, result.StatusCode);
        Assert.Equal([RerunFailedPath], h.GitHub.Posts);

        await h.DrainAsync();
        Assert.Contains(EngineHarness.ListAPath, h.GitHub.ApiRequests.Skip(before));
        Assert.Equal([RerunFailedPath], h.GitHub.Posts);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task _5xx回結果不確定並寫error_log而且重跑記成待確認(HttpStatusCode status)
    {
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.Override(RerunFailedPath, _ => h.GitHub.Error(status, ServerErrorMessage + " " + Fixture.FakeToken));

        var result = await Rerun(h);

        // 5xx 可能已經生效（第二階段 spec §3.2；GitHub 處理太久會自己回 5xx）：結果不確定，帶狀態碼與伺服器 message。
        Assert.Equal(WriteOutcome.Uncertain, result.Outcome);
        Assert.Null(result.Rejected);
        Assert.Equal((int)status, result.StatusCode);
        Assert.StartsWith(ServerErrorMessage, result.ServerMessage, StringComparison.Ordinal);
        AssertWriteErrorLogged(h, "RerunFailedJobs", status);

        // 同其他結果不確定的重跑：寫進 pending_reruns；套用的那一步發布之後兩種重跑都擋住，不送第二個 POST（§6.2、§7.3）。
        Assert.Equal(new PendingRerun(FailedRunId, FakeData.RepoId, 1, h.Clock.GetUtcNow(), Notify: true),
            Assert.Single(h.Cache.GetPendingReruns()));
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunAll);
        foreach (var again in new[] { WriteAction.RerunFailedJobs, WriteAction.RerunAll })
        {
            var blocked = await Immediately(Send(h, again, FailedRunId));
            AssertNotSent(blocked, NotSentReason.Unavailable);
            Assert.Equal(ActionBlock.RerunPending, blocked.Block);
        }

        Assert.Single(h.GitHub.Posts);
    }

    [Fact]
    public async Task 取消收到5xx回結果不確定而且只查一次E6()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        h.GitHub.Override(CancelPath, _ => h.GitHub.Error(HttpStatusCode.BadGateway, ServerErrorMessage + " " + Fixture.FakeToken));

        var result = await CancelRunning(h);

        Assert.Equal(WriteOutcome.Uncertain, result.Outcome);
        Assert.Equal((int)HttpStatusCode.BadGateway, result.StatusCode);
        AssertWriteErrorLogged(h, "Cancel", HttpStatusCode.BadGateway);
        Assert.Empty(h.Cache.GetPendingReruns());

        // 取消的結果不確定（§6.2）：不標成取消中，一般取消仍可用；第一次 E6 有回應就結束。
        Assert.Equal(1, await h.StepAsync());
        Assert.Empty(h.Last.Cancelling);
        Assert.Equal(ActionBlock.None, h.Last.Actions[RunningRunId].Cancel);
        await h.AdvanceInStepsAsync(Tuning.QuietMax * 2, Tuning.ActiveRepoInterval);
        Assert.Equal(1, h.GitHub.Count(RunningRunDetailPath));
        Assert.Equal([CancelPath], h.GitHub.Posts);
    }

    [Theory]
    [InlineData((HttpStatusCode)418)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task 表上沒列的狀態碼回被拒並寫error_log(HttpStatusCode status)
    {
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.Override(RerunFailedPath, _ => h.GitHub.Error(status, ServerErrorMessage + " " + Fixture.FakeToken));

        var result = await Rerun(h);

        Assert.Equal(WriteOutcome.Rejected, result.Outcome);
        Assert.Equal(WriteRejectReason.Other, result.Rejected);
        Assert.Equal((int)status, result.StatusCode);
        AssertWriteErrorLogged(h, "RerunFailedJobs", status);

        // 被拒不是待確認的重跑。
        Assert.Empty(h.Cache.GetPendingReruns());
        Assert.Single(h.GitHub.Posts);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task _400回被拒但不寫error_log(HttpStatusCode status)
    {
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.Override(RerunFailedPath, _ => h.GitHub.Error(status, "Bad Request"));

        var result = await Rerun(h);

        Assert.Equal(WriteOutcome.Rejected, result.Outcome);
        Assert.Equal(WriteRejectReason.Other, result.Rejected);
        Assert.Equal((int)status, result.StatusCode);
        Assert.DoesNotContain(WriteErrorContext, h.ErrorLogText, StringComparison.Ordinal);

        // 寫入回 400／410 不當成 API 版本錯誤（§3.3 第 9 列）：輪詢照常。
        h.Engine.Post(new EngineCommand.RefreshAll());
        Assert.True(await h.DrainAsync() > 0);
        Assert.NotEqual(BannerKind.VersionError, h.Last.Banner?.Kind);
    }

    [Fact]
    public async Task 寫入收到限流時全域暫停()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        h.GitHub.Override(RerunFailedPath, _ => h.GitHub.Error(HttpStatusCode.TooManyRequests, "rate limited", ("retry-after", "60")));

        var result = await Rerun(h);

        // 限流照 P1 §6.4 全域暫停：之後的寫入也不送。
        Assert.Equal(WriteRejectReason.RateLimited, result.Rejected);
        AssertNotSent(await Immediately(CancelRunning(h)), NotSentReason.RateLimitPaused);
        await h.StepAsync();
        Assert.Equal(BannerKind.SecondaryLimit, h.Last.Banner?.Kind);
        Assert.Single(h.GitHub.Posts);
    }

    // ── 待確認的重跑與操作後追蹤的交接（第二階段 spec §5.3 第 2 點、§6.2）──

    [Theory]
    [InlineData(WriteAction.RerunFailedJobs)]
    [InlineData(WriteAction.RerunAll)]
    public async Task 重跑受理時寫入待確認重跑(WriteAction action)
    {
        await using var h = Create();
        await h.DrainAsync();

        Assert.Equal(WriteOutcome.Accepted, (await Send(h, action, FailedRunId)).Outcome);

        // 拿到結果就直接寫進 DB，不經下一步（結束前一刻送出的重跑也不漏記）。
        Assert.Equal(new PendingRerun(FailedRunId, FakeData.RepoId, 1, h.Clock.GetUtcNow(), Notify: true),
            Assert.Single(h.Cache.GetPendingReruns()));
    }

    [Fact]
    public async Task 重跑結果不確定也寫入待確認重跑()
    {
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.Override(RerunFailedPath, _ => throw new HttpRequestException("模擬的連線中斷"));

        Assert.Equal(WriteOutcome.Uncertain, (await Rerun(h)).Outcome);

        Assert.Equal(new PendingRerun(FailedRunId, FakeData.RepoId, 1, h.Clock.GetUtcNow(), Notify: true),
            Assert.Single(h.Cache.GetPendingReruns()));
    }

    [Fact]
    public async Task 待確認重跑寫入失敗仍回受理並排E6()
    {
        await using var h = Create();
        await h.DrainAsync();
        MonitorEngineReviewTests.Execute(h.Workspace.Paths.CacheDbPath,
            "CREATE TRIGGER block_pending BEFORE INSERT ON pending_reruns BEGIN SELECT RAISE(ABORT, '測試注入的寫入失敗'); END;");

        var result = await Rerun(h);

        Assert.Equal(WriteOutcome.Accepted, result.Outcome);
        Assert.Contains("測試注入的寫入失敗", h.ErrorLogText, StringComparison.Ordinal);
        Assert.Empty(h.Cache.GetPendingReruns());

        var before = h.GitHub.ApiRequests.Count;
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(FailedRunDetailPath, h.GitHub.ApiRequests[before]);
    }

    [Fact]
    public async Task 取消不寫待確認重跑()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();

        var result = await CancelRunning(h);

        Assert.Equal(WriteOutcome.Accepted, result.Outcome);
        Assert.Equal((int)HttpStatusCode.Accepted, result.StatusCode);
        Assert.Equal([CancelPath], h.GitHub.Posts);
        Assert.Empty(h.Cache.GetPendingReruns());
    }

    [Theory]
    [InlineData(WriteOutcome.Accepted)]
    [InlineData(WriteOutcome.Rejected)]
    [InlineData(WriteOutcome.Uncertain)]
    public async Task 有回應的寫入都用使用者優先序排一次E6(WriteOutcome expected)
    {
        await using var h = Create();
        await h.DrainAsync();
        if (expected == WriteOutcome.Rejected)
        {
            h.GitHub.Override(RerunFailedPath, _ => h.GitHub.Error(HttpStatusCode.Conflict, "Conflict"));
        }
        else if (expected == WriteOutcome.Uncertain)
        {
            h.GitHub.Override(RerunFailedPath, _ => throw new HttpRequestException("模擬的連線中斷"));
        }

        Assert.Equal(expected, (await Rerun(h)).Outcome);

        // 同一刻全量刷新：探測、runs、PR 清單都到期，E6 仍排在最前面（P1 §6.3 第 1 級）。
        h.Engine.Post(new EngineCommand.RefreshAll());
        var before = h.GitHub.ApiRequests.Count;
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(FailedRunDetailPath, h.GitHub.ApiRequests[before]);

        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(FailedRunDetailPath));
    }

    // ── 例外、結束與 log ──────────────────────────────────────────────

    [Fact]
    public async Task 前一個使用者操作的結果套用失敗時寫入的結果照常套用()
    {
        await using var h = Create();
        await h.DrainAsync();
        MonitorEngineReviewTests.Execute(h.Workspace.Paths.CacheDbPath,
            "CREATE TRIGGER block_account BEFORE INSERT ON account BEGIN SELECT RAISE(ABORT, '測試注入的寫入失敗'); END;");

        // 檢查連線的 E1 成功時交給下一步記下帳號（寫 DB 會失敗），接著寫入的結果也交給下一步。
        Assert.True((await h.Engine.CheckConnectionAsync(None)).Ok);
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);

        // 同一步依序套用：前一個失敗只寫 error.log，寫入的結果照常套用（E6 照排、送出中在這一步發布快照後移除）。
        var before = h.GitHub.ApiRequests.Count;
        Assert.Equal(1, await h.StepAsync());
        Assert.Contains("測試注入的寫入失敗", h.ErrorLogText, StringComparison.Ordinal);
        Assert.Equal(FailedRunDetailPath, h.GitHub.ApiRequests[before]);
        await AssertInFlightAsync(h, expected: false);
    }

    [Fact]
    public async Task 未預期例外時清除送出中()
    {
        var thrown = 0;
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.OnRequest = request =>
        {
            if (request.Method == HttpMethod.Post && Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw new InvalidOperationException("測試注入的未預期例外");
            }
        };

        var result = await Rerun(h);

        // 交給 handler 之後才丟出：請求可能已經送到，當成結果不確定（第二階段 spec §5.2），寫 error.log。
        Assert.Equal(WriteOutcome.Uncertain, result.Outcome);
        Assert.Contains("測試注入的未預期例外", h.ErrorLogText, StringComparison.Ordinal);

        await h.StepAsync();
        Assert.Equal(BannerKind.UnexpectedError, h.Last.Banner?.Kind);
        await AssertInFlightAsync(h, expected: false);
    }

    [Fact]
    public async Task 程式結束時等待中的寫入回未送出()
    {
        await using var h = Create();
        await h.DrainAsync();
        await MonitorEngineReviewTests.FillRequestWindowAsync(h);
        var write = Rerun(h);
        Assert.False(write.IsCompleted);

        await h.Engine.DisposeAsync().AsTask().WaitAsync(Guard);

        // App 結束時被取消：不送、不當成未預期錯誤（App 不顯示，同複製 log）。
        AssertNotSent(await write.WaitAsync(Guard), NotSentReason.ShuttingDown);
        Assert.Empty(h.GitHub.Posts);
        Assert.DoesNotContain("Exception", h.ErrorLogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 程式結束時送出中的重跑回結果不確定而且已寫入待確認重跑()
    {
        using var postSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = Create();
        await h.DrainAsync();
        h.GitHub.AbortWhenCancelled = true;
        h.GitHub.OnRequest = request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postSeen.Set();
                release.Wait(Guard);
            }
        };

        var write = Task.Run(() => Rerun(h));
        Assert.True(postSeen.Wait(Guard));

        // POST 已經交給 handler 才結束 App：請求可能已經生效（第二階段 spec §5.2）。DisposeAsync 等這個寫入結束，
        // 結束時 pending_reruns 已經寫好，重新啟動後才能接著追（§5.3 第 2 點）。
        var dispose = h.Engine.DisposeAsync().AsTask();
        release.Set();
        await dispose.WaitAsync(Guard);

        Assert.Equal(WriteOutcome.Uncertain, (await write.WaitAsync(Guard)).Outcome);
        Assert.Equal(FailedRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);
        Assert.DoesNotContain("Exception", h.ErrorLogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task api_log有一行POST而且沒有token()
    {
        await using var h = Create();
        await h.DrainAsync();

        await Rerun(h);

        var log = h.ApiLogText;
        var line = Assert.Single(log.Split('\n'), entry => entry.Contains("\tPOST\t", StringComparison.Ordinal));
        Assert.Contains("\tPOST\t" + RerunFailedPath + "\t201\t", line, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.FakeToken, log, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", log, StringComparison.Ordinal);
    }

    // ── 發布時釋放（controller 的 ruling：只在 BuildSnapshot 成功發布時釋放）──

    /// <remarks>改名失敗靠 Windows 的檔案共用規則（開著的檔案沒有允許刪除就不能改名），所以只在 Windows 上執行。</remarks>
    [WindowsFact]
    public async Task 資料庫重建等待期間發布的快照不釋放送出中()
    {
        await using var h = Create();
        await h.DrainAsync();
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "workflows");

        // 另一個程式開著 cache.db（沒有允許刪除），改名重建一定失敗。
        using (new FileStream(h.Workspace.Paths.CacheDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            // E4 第 1 頁多了一筆 run：重新讀這個 repo 的資料時遇到損毀，改名重建失敗而退避；之後每一步沿用上一份快照的內容。
            h.Main.Runs.Insert(0, FakeData.Run(9003, 44, "completed", "success", Start.AddMinutes(1), Start.AddMinutes(2)));
            await h.AdvanceAsync(Tuning.QuietMax);
            Assert.Contains("重建快取資料庫", h.ErrorLogText, StringComparison.Ordinal);

            Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
            for (var step = 0; step < CarryOverSteps; step++)
            {
                await h.StepAsync();
            }

            // 沿用的查表沒有這筆待確認的操作：這種快照發布時不能移除送出中，否則再按一次就會再送（第二階段 spec §5.3 第 2 點）。
            AssertNotSent(await Immediately(Rerun(h)), NotSentReason.InFlight);
            Assert.Single(h.GitHub.Posts);
        }

        // 重建成功、BuildSnapshot 重新組出快照並發布之後才移除。
        await h.AdvanceAsync(Tuning.RepoErrorBackoffMax);
        Assert.Equal(BannerKind.DatabaseRecovered, h.Last.Banner?.Kind);
        Assert.True(h.Last.Actions.ContainsKey(FailedRunId));
        await AssertInFlightAsync(h, expected: false);
        Assert.Single(h.GitHub.Posts);
    }

    private static void AssertNotSent(WriteResult result, NotSentReason reason)
    {
        Assert.Equal(WriteOutcome.NotSent, result.Outcome);
        Assert.Equal(reason, result.NotSent);
        Assert.Equal(0, result.StatusCode);
        Assert.Null(result.Rejected);
    }

    /// <summary>§3.3 第 11 列：error.log 有一筆寫入回應錯誤（動作、repo、狀態碼、message）；message 經過遮罩。</summary>
    private static void AssertWriteErrorLogged(EngineHarness h, string action, HttpStatusCode status)
    {
        var log = h.ErrorLogText;
        Assert.Contains(WriteErrorContext, log, StringComparison.Ordinal);
        Assert.Contains(action + " " + FakeData.RepoName, log, StringComparison.Ordinal);
        Assert.Contains($"狀態碼 {(int)status}", log, StringComparison.Ordinal);
        Assert.Contains(ServerErrorMessage, log, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.FakeToken, log, StringComparison.Ordinal);
    }

    /// <summary>
    /// 用「取消已完成的 run 9001」探測 9001 是否還在送出中：還在回送出中，已移除回不能操作（已完成）。兩種都不送 POST，
    /// 也不受之後的「待確認重跑」影響。
    /// </summary>
    private static async Task AssertInFlightAsync(EngineHarness h, bool expected)
    {
        var probe = await Immediately(Send(h, WriteAction.Cancel, FailedRunId));
        if (expected)
        {
            AssertNotSent(probe, NotSentReason.InFlight);
            return;
        }

        AssertNotSent(probe, NotSentReason.Unavailable);
        Assert.Equal(ActionBlock.Completed, probe.Block);
    }

    /// <summary>請求是 <paramref name="pathAndQuery"/> 時通知測試，並擋住直到測試放行。</summary>
    private static void BlockOn(HttpRequestMessage request, string pathAndQuery, ManualResetEventSlim seen, ManualResetEventSlim release)
    {
        if (request.RequestUri!.PathAndQuery == pathAndQuery)
        {
            seen.Set();
            release.Wait(Guard);
        }
    }
}
