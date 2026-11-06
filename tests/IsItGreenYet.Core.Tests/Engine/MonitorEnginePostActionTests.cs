using System.Net;
using System.Text.Json.Nodes;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Filtering;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Tests.Auth;
using IsItGreenYet.Core.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>
/// 操作後追蹤（第二階段 spec §6、§8）：重跑等新的 attempt（放棄條件、重新啟動後接著追）、取消中與強制取消、
/// 被拒只查一次 E6、重跑後自動「完成時通知我」與 ETA 樣本。
/// </summary>
public class MonitorEnginePostActionTests
{
    private const long FailedRunId = 9001;
    private const long RunningRunId = 9002;
    private const long OthersRunId = 9003;

    /// <summary>另一個帳號（同 MonitorEngineReviewTests）：它的 run 成功不會觸發 N2。</summary>
    private const long BobId = 1002;
    private const string BobLogin = "bob";

    private const string RunsPrefix = "/repos/octo-org/sample-app/actions/runs/";
    private const string FailedDetailPath = RunsPrefix + "9001";
    private const string RunningDetailPath = RunsPrefix + "9002";
    private const string RerunFailedPath = RunsPrefix + "9001/rerun-failed-jobs";
    private const string CancelPath = RunsPrefix + "9002/cancel";
    private const string ForceCancelPath = RunsPrefix + "9002/force-cancel";

    /// <summary>沒有開啟中 PR 的功能分支：上面的 run 不屬於任何追蹤鍵（P1 §7.2），失敗不會觸發 N1。</summary>
    private const string UntrackedBranch = "feature/x";

    /// <summary>啟動時略過 pending_reruns 讀不懂的一列時 error.log 的標題（MonitorEngine.PostAction.cs），後面接「（run N）」。</summary>
    private const string PendingRerunSkippedLogTitle = "略過讀不懂的待確認重跑";

    /// <summary>單一失敗 run 的提示標題（預設語言）。</summary>
    private const string FailureToastTitle = "失敗：octo-org/sample-app · CI #42";

    /// <summary>單一成功 run 的提示標題（預設語言）。</summary>
    private const string SuccessToastTitle = "成功：octo-org/sample-app · CI #42";

    /// <summary>pending_reruns 裡 repo 不在清單上的一列（舊版本留下的孤兒）：run id。</summary>
    private const long OrphanRunId = 7777;

    /// <summary>pending_reruns 裡 repo 不在清單上的一列（舊版本留下的孤兒）：repo id。</summary>
    private const long OrphanRepoId = 999999;

    /// <summary>設定不納入 fork 時，靠強制納入才監看的 fork：repo id。</summary>
    private const long ForkRepoId = 2100;

    /// <summary>靠強制納入才監看的 fork：名稱。</summary>
    private const string ForkName = "octo-org/forked-tool";

    /// <summary>靠強制納入才監看的 fork 上的 run。</summary>
    private const long ForkRunId = 9100;

    /// <summary>設定不納入協作 repo 時，靠強制納入才監看的協作 repo：repo id。</summary>
    private const long CollaboratorRepoId = 2006;

    /// <summary>靠強制納入才監看的協作 repo：名稱。</summary>
    private const string CollaboratorName = "carol/shared-tool";

    /// <summary>靠強制納入才監看的協作 repo 的擁有者（不是我）。</summary>
    private const long CollaboratorOwnerId = 4001;

    /// <summary>靠強制納入才監看的協作 repo 上的 run。</summary>
    private const long CollaboratorRunId = 9401;

    /// <summary>
    /// 重建後重讀 DB 時讀不到的表：LoadFromDatabase 在帳號、repo 與狀態之後、「完成時通知我」的標記之前讀它；
    /// 監看的其他步驟不讀不寫（只有靜音與強制納入會寫），刪掉之後不影響後面的監看。
    /// </summary>
    private const string UnreadableTable = "repo_overrides";

    /// <summary>讀 <see cref="UnreadableTable"/> 時 SQLite 的錯誤訊息（SQLITE_ERROR，不是損毀）。</summary>
    private const string UnreadableTableError = "no such table: " + UnreadableTable;

    private static readonly DateTimeOffset Start = EngineHarness.Start;

    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>背景工作的保險上限；正常情況下等待的條件都會立刻成立，不靠時間流逝。</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    /// <summary>重跑確認與取消前 5 分鐘的 E6 間隔：ActiveRepoInterval × k（額度充足，k = 1）。</summary>
    private static readonly TimeSpan Interval = Tuning.ActiveRepoInterval;

    /// <summary>重跑的第 2 次嘗試跑了多久（耗時的期望值）。</summary>
    private static readonly TimeSpan RerunDuration = TimeSpan.FromMinutes(3);

    /// <summary>逐秒推進等重建的上限：下一次寫入 repos 表最晚在 QuietMax 之內，多留一倍餘裕。</summary>
    private static readonly int RebuildStepLimit = 2 * (int)(Tuning.QuietMax / Tuning.EngineTick);

    /// <summary>寫入功能打開、預設 repo 有一筆一天前失敗的 run（9001，main 的 latest）；<paramref name="arrange"/> 在建立 Engine 之前呼叫。</summary>
    private static EngineHarness Create(Action<EngineHarness>? arrange = null) =>
        EngineHarness.Create(h =>
        {
            h.Settings.AllowWriteActions = true;
            h.Main.Runs.Add(FailedRunJson());
            arrange?.Invoke(h);
        });

    private static JsonObject FailedRunJson(string branch = "main") =>
        FakeData.Run(FailedRunId, 42, "completed", "failure", Start.AddDays(-1), Start.AddDays(-1).AddMinutes(5),
            branch: branch, sha: branch == "main" ? FakeData.MainSha : FakeData.FeatureSha);

    /// <summary>9001 不在 E4 第 1 頁：只有預設分支補抓（E5）查得到，P1 的輪詢不會再查這筆已完成的 run（spec §6.1）。</summary>
    private static void MoveOffFirstPage(EngineHarness h)
    {
        h.Main.Runs.Clear();
        h.Main.OlderRuns.Add(FailedRunJson());
    }

    /// <summary>9001 在沒有追蹤鍵的分支上（E4 第 1 頁）。</summary>
    private static void UseUntrackedBranch(EngineHarness h)
    {
        h.Main.Runs.Clear();
        h.Main.Runs.Add(FailedRunJson(UntrackedBranch));
    }

    /// <summary>另一筆進行中的 run（9002，E4 第 1 頁），可以取消。</summary>
    private static void AddRunning(EngineHarness h) =>
        h.Main.Runs.Add(FakeData.Run(RunningRunId, 43, "in_progress", null, Start.AddMinutes(-5)));

    /// <summary>
    /// 另一個帳號在沒有追蹤鍵的分支上進行中的 run（9003，E4 第 1 頁）：成功不觸發 N2、失敗不觸發 N1，
    /// 完成時只有「完成時通知我」會通知（N3）。
    /// </summary>
    private static void AddOthersRunning(EngineHarness h) =>
        h.Main.Runs.Add(FakeData.Run(OthersRunId, 44, "in_progress", null, Start.AddMinutes(-5),
            branch: UntrackedBranch, sha: FakeData.FeatureSha, actorId: BobId, actorLogin: BobLogin));

    /// <summary>
    /// 改名重建之後、寫回之前（ResetAfterDatabaseRecovery 一開始讀時間）：在新的資料庫建 trigger，擋住 <paramref name="table"/> 的新增。
    /// </summary>
    /// <returns>查詢 trigger 建好了沒有。</returns>
    private static Func<bool> BlockInsertsAfterRebuild(EngineHarness h, string table, string message)
    {
        var blocked = 0;
        h.Clock.BeforeGetUtcNow = () =>
        {
            if (Volatile.Read(ref blocked) == 0 && EngineClock.CalledFrom("ResetAfterDatabaseRecovery")
                && Interlocked.Exchange(ref blocked, 1) == 0)
            {
                MonitorEngineReviewTests.Execute(h.Workspace.Paths.CacheDbPath,
                    "CREATE TRIGGER block_" + table + " BEFORE INSERT ON " + table + " BEGIN SELECT RAISE(ABORT, '" + message + "'); END;");
            }
        };

        return () => Volatile.Read(ref blocked) == 1;
    }

    /// <summary>
    /// 改名重建之後、寫回與重讀之前（ResetAfterDatabaseRecovery 一開始讀時間）：對新的資料庫做一次 <paramref name="action"/>（參數是 cache.db 的路徑）。
    /// 接在原本的 BeforeGetUtcNow 後面執行，可以和 <see cref="BlockInsertsAfterRebuild"/> 一起用。
    /// </summary>
    /// <returns>查詢做過了沒有。</returns>
    private static Func<bool> OnceAfterRebuild(EngineHarness h, Action<string> action)
    {
        var done = 0;
        var previous = h.Clock.BeforeGetUtcNow;
        h.Clock.BeforeGetUtcNow = () =>
        {
            previous?.Invoke();
            if (Volatile.Read(ref done) == 0 && EngineClock.CalledFrom("ResetAfterDatabaseRecovery")
                && Interlocked.Exchange(ref done, 1) == 0)
            {
                action(h.Workspace.Paths.CacheDbPath);
            }
        };

        return () => Volatile.Read(ref done) == 1;
    }

    /// <summary>新的資料庫刪掉 <see cref="UnreadableTable"/>：重讀 DB 讀到它時丟出不是損毀類的 SqliteException。</summary>
    private static Func<bool> FailReloadAfterRebuild(EngineHarness h) =>
        OnceAfterRebuild(h, path => MonitorEngineReviewTests.Execute(path, "DROP TABLE " + UnreadableTable + ";"));

    /// <summary>
    /// 每次重建之後（ResetAfterDatabaseRecovery 讀時間時）記下當時已經送出的 API 請求數：第 n 筆是第 n 次重建。
    /// 接在原本的 BeforeGetUtcNow 後面執行，可以和 <see cref="OnceAfterRebuild"/> 一起用。
    /// </summary>
    /// <returns>查詢目前記下的請求數。</returns>
    private static Func<IReadOnlyList<int>> RequestsAtRebuilds(EngineHarness h)
    {
        var counts = new List<int>();
        var gate = new Lock();
        var previous = h.Clock.BeforeGetUtcNow;
        h.Clock.BeforeGetUtcNow = () =>
        {
            previous?.Invoke();
            if (!EngineClock.CalledFrom("ResetAfterDatabaseRecovery"))
            {
                return;
            }

            var rebuilds = h.Engine.DatabaseRebuildCount;
            lock (gate)
            {
                if (counts.Count < rebuilds)
                {
                    counts.Add(h.GitHub.ApiRequests.Count);
                }
            }
        };

        return () =>
        {
            lock (gate)
            {
                return counts.ToList();
            }
        };
    }

    private static JsonObject RunJson(EngineHarness h, long runId) => h.Main.AllRuns.Single(run => RepoWorld.RunId(run) == runId);

    /// <summary>GitHub 開始新的一次嘗試：run_attempt 加 1、回到進行中，run_started_at 重設。</summary>
    private static void StartNewAttempt(JsonObject run, DateTimeOffset at)
    {
        run["run_attempt"] = run["run_attempt"]!.GetValue<int>() + 1;
        run["status"] = "in_progress";
        run["conclusion"] = null;
        run["run_started_at"] = FakeData.Time(at);
        run["updated_at"] = FakeData.Time(at);
    }

    private static Task<WriteResult> Send(EngineHarness h, WriteAction action, long runId, int attempt = 1) =>
        h.Engine.SendWriteAsync(action, FakeData.RepoId, runId, attempt, None).WaitAsync(Guard);

    private static Task<WriteResult> Rerun(EngineHarness h) => Send(h, WriteAction.RerunFailedJobs, FailedRunId);

    private static Task<WriteResult> CancelRunning(EngineHarness h) => Send(h, WriteAction.Cancel, RunningRunId);

    private static RunRow Row(EngineHarness h, long runId) => h.Last.Rows.Single(row => row.Run.Id == runId);

    private static async Task MuteAsync(EngineHarness h)
    {
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.DrainAsync();
        Assert.False(h.RepoRow(FakeData.RepoId).Monitored);
    }

    private static async Task UnmuteAsync(EngineHarness h)
    {
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, RepoOverride.None));
        await h.DrainAsync();
        Assert.True(h.RepoRow(FakeData.RepoId).Monitored);
    }

    /// <summary>
    /// 寫入的 POST 還在等回應時（寫入不經過步驟的閘門）做 <paramref name="whileInFlight"/>，做完才放行。
    /// 回傳時寫入路徑已經跑完，結果還沒套用：下一步才套用（<c>_userEffects</c>）。
    /// </summary>
    private static async Task<WriteResult> AnswerAfterAsync(EngineHarness h, Func<Task<WriteResult>> send, Func<Task> whileInFlight)
    {
        using var postSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        h.GitHub.OnRequest = request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postSeen.Set();
                release.Wait(Guard);
            }
        };

        var write = Task.Run(send);
        Assert.True(postSeen.Wait(Guard));
        try
        {
            await whileInFlight();
        }
        finally
        {
            release.Set();
        }

        var result = await write.WaitAsync(Guard);
        h.GitHub.OnRequest = null;
        return result;
    }

    /// <summary>每次推進一個 tick 跑一步，直到 <paramref name="done"/> 成立。</summary>
    private static async Task StepUntilAsync(EngineHarness h, Func<bool> done)
    {
        for (var step = 0; !done(); step++)
        {
            Assert.True(step < RebuildStepLimit);
            h.Clock.Advance(Tuning.EngineTick);
            await h.StepAsync();
        }
    }

    /// <summary>
    /// 重跑已追蹤之後 repo 不再列出、資料庫重建：重建清空了 _repos，這個 repo 不在 FinishRound 的 known 裡，輪不到 RemoveRepo。
    /// 跑完時重建後的第一輪清單已經抓完。
    /// </summary>
    private static async Task RebuildWhileUnlistedAsync(EngineHarness h)
    {
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());
        h.Main.Listed = false;
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        var rebuilds = h.Engine.DatabaseRebuildCount;
        await h.AdvanceAsync(Tuning.QuietMax);

        Assert.Equal(rebuilds + 1, h.Engine.DatabaseRebuildCount);
        Assert.True(h.Last.RepoListFetched);
        Assert.Empty(h.Cache.GetRepos());
    }

    // ── 重跑：一直追到新的 attempt（第二階段 spec §6.1、§6.2）────────────

    [Fact]
    public async Task 重跑舊的已完成run後一定查到新的attempt()
    {
        await using var h = Create(MoveOffFirstPage);
        await h.DrainAsync();
        Assert.Contains(h.Last.Rows, row => row.Run.Id == FailedRunId);

        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(1, h.GitHub.Count(FailedDetailPath));

        // GitHub 在第一次 E6 之後才開始第 2 次嘗試：本地仍顯示已完成，只有操作後追蹤的 E6 會再去查。
        StartNewAttempt(RunJson(h, FailedRunId), h.Clock.GetUtcNow());
        await h.AdvanceAsync(Interval);

        Assert.Equal(2, h.GitHub.Count(FailedDetailPath));
        Assert.Equal(2, Row(h, FailedRunId).Run.RunAttempt);
        Assert.Empty(h.Cache.GetPendingReruns());

        // 新的 attempt 還沒完成：轉成「完成時通知我」（spec §8.1 第 2 步）。
        Assert.Contains(FailedRunId, h.Last.WatchedRunIds);
        Assert.Contains(FailedRunId, h.Cache.GetWatched());
    }

    [Fact]
    public async Task 重跑結果不確定時也繼續追到新的attempt()
    {
        await using var h = Create(MoveOffFirstPage);
        await h.DrainAsync();
        h.GitHub.Override(RerunFailedPath, _ => throw new HttpRequestException("模擬的連線中斷"));

        Assert.Equal(WriteOutcome.Uncertain, (await Rerun(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);

        // 其實已經生效：之後的 E6 看到第 2 次嘗試。
        StartNewAttempt(RunJson(h, FailedRunId), h.Clock.GetUtcNow());
        await h.AdvanceAsync(Interval);

        Assert.Equal(2, Row(h, FailedRunId).Run.RunAttempt);
        Assert.Empty(h.Cache.GetPendingReruns());
        Assert.Contains(FailedRunId, h.Last.WatchedRunIds);
    }

    [Fact]
    public async Task 第一次E6還看不到新attempt時繼續追()
    {
        await using var h = Create();
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);

        // 第一次 E6（使用者操作的優先序）與之後兩次都還是第 1 次嘗試：每 ActiveRepoInterval 再查一次，重跑一直停用。
        Assert.Equal(1, await h.StepAsync());
        await h.AdvanceAsync(Interval);
        await h.AdvanceAsync(Interval);
        Assert.Equal(3, h.GitHub.Count(FailedDetailPath));
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);
        Assert.Single(h.Cache.GetPendingReruns());

        StartNewAttempt(RunJson(h, FailedRunId), h.Clock.GetUtcNow());
        await h.AdvanceAsync(Interval);

        Assert.Equal(4, h.GitHub.Count(FailedDetailPath));
        Assert.Equal(2, h.Last.Actions[FailedRunId].RunAttempt);
        Assert.Equal(ActionBlock.NotCompleted, h.Last.Actions[FailedRunId].RerunFailedJobs);
        Assert.Empty(h.Cache.GetPendingReruns());
    }

    /// <param name="neverSent">
    /// 寫入那一層丟出取消以外的例外：可能根本沒有送出，也當成結果不確定（Task 5），照同一個放棄規則收掉，
    /// 不會讓重跑在這次執行剩下的時間一直停用（controller 的 ruling）。
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 放棄要超過5分鐘而且收到3次回應(bool neverSent)
    {
        await using var h = Create();
        await h.DrainAsync();
        if (neverSent)
        {
            h.GitHub.Override(RerunFailedPath, _ => throw new InvalidOperationException("測試注入的未預期例外"));
        }

        Assert.Equal(neverSent ? WriteOutcome.Uncertain : WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());

        // 30 秒內已經收到 3 次回應，但送出還不到 RerunConfirmTimeout：不放棄。
        await h.AdvanceInStepsAsync(Interval * 2, Interval);
        Assert.Equal(3, h.GitHub.Count(FailedDetailPath));
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);

        await h.AdvanceInStepsAsync(Tuning.RerunConfirmTimeout - Interval * 3, Interval);
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);
        Assert.Single(h.Cache.GetPendingReruns());

        // 送出滿 RerunConfirmTimeout 的那一次回應：兩個條件都成立，放棄並刪除資料列，不通知（spec §8.1 第 4 步）。
        await h.AdvanceAsync(Interval);
        Assert.Equal(ActionBlock.None, h.Last.Actions[FailedRunId].RerunFailedJobs);
        Assert.Empty(h.Cache.GetPendingReruns());
        Assert.DoesNotContain(FailedRunId, h.Last.WatchedRunIds);

        // 已完成、在第 1 頁的 run：放棄後不再查 E6。
        var details = h.GitHub.Count(FailedDetailPath);
        await h.AdvanceInStepsAsync(Tuning.QuietMax, Interval);
        Assert.Equal(details, h.GitHub.Count(FailedDetailPath));
    }

    [Fact]
    public async Task 鎖定中重跑仍每15秒查一次()
    {
        await using var h = Create();
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);

        // 剛送出就鎖定：其他目標套用 LockedMinInterval，重跑的確認不套用（spec §6.2，最多 5 分鐘、額度可以忽略）。
        h.Engine.Post(new EngineCommand.SetLocked(true));
        Assert.Equal(1, await h.StepAsync());
        await h.AdvanceInStepsAsync(Interval * 3, Interval);

        Assert.Equal(4, h.GitHub.Count(FailedDetailPath));
    }

    [Fact]
    public async Task 離線期間不算回應次數()
    {
        await using var h = Create();
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());

        h.GitHub.NetworkDown = true;
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.True(h.Last.Offline);

        // 離線超過 RerunConfirmTimeout：只送離線探測，E6 送不出去，不算回應。
        await h.AdvanceInStepsAsync(Tuning.RerunConfirmTimeout + Interval, Interval);
        Assert.Equal(1, h.GitHub.Count(FailedDetailPath));

        h.GitHub.NetworkDown = false;
        await h.AdvanceAsync(Tuning.OfflineProbeMax);
        Assert.False(h.Last.Offline);

        // 恢復後接著追：時間早已超過，但送出後只收到 2 次回應，還不放棄。
        Assert.Equal(2, h.GitHub.Count(FailedDetailPath));
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);

        await h.AdvanceAsync(Interval);
        Assert.Equal(3, h.GitHub.Count(FailedDetailPath));
        Assert.Equal(ActionBlock.None, h.Last.Actions[FailedRunId].RerunFailedJobs);
        Assert.Empty(h.Cache.GetPendingReruns());
    }

    [Fact]
    public async Task 自動更新暫停時重跑仍查到新attempt()
    {
        await using var h = Create();
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();

        // 暫停的是週期性輪詢；你按下去的寫入照送，操作後追蹤也照常（spec §5.4、§6.2）。
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());
        StartNewAttempt(RunJson(h, FailedRunId), h.Clock.GetUtcNow());
        await h.AdvanceAsync(Interval);

        Assert.Equal(2, h.GitHub.Count(FailedDetailPath));
        Assert.Equal(2, h.Last.Actions[FailedRunId].RunAttempt);
        Assert.Empty(h.Cache.GetPendingReruns());
    }

    [Fact]
    public async Task 自動更新暫停時取消中仍會清除()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await CancelRunning(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());
        Assert.True(h.Last.Cancelling.ContainsKey(RunningRunId));

        // 切到暫停：追蹤取消的 E6 不移出排程（spec §6.2），E4 不再輪詢。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        FakeData.Complete(RunJson(h, RunningRunId), "cancelled", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Interval);

        Assert.Equal(2, h.GitHub.Count(RunningDetailPath));
        Assert.Empty(h.Last.Cancelling);
        Assert.Equal(ActionBlock.Completed, h.Last.Actions[RunningRunId].Cancel);
    }

    // ── 重新啟動與資料庫重建（第二階段 spec §6.2 第 6 點）─────────────────

    [Fact]
    public async Task 重跑受理後立刻結束App重新啟動仍接著追()
    {
        await using var h = Create(MoveOffFirstPage);
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);

        // 受理後還沒輪到下一步就結束 App：待確認的重跑已經寫進 pending_reruns（spec §5.3 第 2 點）。
        await h.RestartAsync();
        await h.DrainAsync();

        // 啟動時讀回、重新追蹤：排 E6，重跑仍停用（controller 的 ruling：讀回的也算待確認）。
        Assert.Equal(1, h.GitHub.Count(FailedDetailPath));
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);

        StartNewAttempt(RunJson(h, FailedRunId), h.Clock.GetUtcNow());
        await h.AdvanceAsync(Interval);

        Assert.Equal(2, h.GitHub.Count(FailedDetailPath));
        Assert.Equal(2, Row(h, FailedRunId).Run.RunAttempt);
        Assert.Empty(h.Cache.GetPendingReruns());
        Assert.Contains(FailedRunId, h.Last.WatchedRunIds);
    }

    [Fact]
    public async Task 重跑送出途中結束App重新啟動仍接著追()
    {
        using var postSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = Create(MoveOffFirstPage);
        await h.DrainAsync();
        h.GitHub.AbortWhenCancelled = true;
        h.GitHub.Override(RerunFailedPath, _ =>
        {
            postSeen.Set();
            release.Wait(Guard);
            return FakeHttpHandler.Json("{}", HttpStatusCode.Created);
        });

        var write = Task.Run(() => Rerun(h));
        Assert.True(postSeen.Wait(Guard));

        // POST 已經交給 handler 才結束 App：被取消、結果不確定，結束前已經寫進 pending_reruns（spec §5.2、§5.3 第 2 點）。
        var dispose = h.Engine.DisposeAsync().AsTask();
        release.Set();
        await dispose.WaitAsync(Guard);
        Assert.Equal(WriteOutcome.Uncertain, (await write.WaitAsync(Guard)).Outcome);
        Assert.Equal(FailedRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);

        // 其實已經生效：重新啟動後讀回的 E6 看到第 2 次嘗試就結束追蹤。
        h.GitHub.AbortWhenCancelled = false;
        StartNewAttempt(RunJson(h, FailedRunId), h.Clock.GetUtcNow());
        await h.RestartAsync();
        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(FailedDetailPath));
        Assert.Equal(2, Row(h, FailedRunId).Run.RunAttempt);
        Assert.Empty(h.Cache.GetPendingReruns());
        Assert.Contains(FailedRunId, h.Last.WatchedRunIds);
    }

    [Fact]
    public async Task 重新啟動後收到3次回應仍看不到就放棄並刪除()
    {
        await using var h = Create(MoveOffFirstPage);
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        await h.RestartAsync();

        // 單調時間戳不能跨程序比較：重新啟動後視為已超過 RerunConfirmTimeout，只看回應次數（spec §6.2 第 6 點）。
        await h.DrainAsync();
        await h.AdvanceAsync(Interval);
        Assert.Equal(2, h.GitHub.Count(FailedDetailPath));
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);
        Assert.Single(h.Cache.GetPendingReruns());

        await h.AdvanceAsync(Interval);
        Assert.Equal(3, h.GitHub.Count(FailedDetailPath));
        Assert.Empty(h.Cache.GetPendingReruns());
        Assert.Equal(ActionBlock.None, h.Last.Actions[FailedRunId].RerunFailedJobs);
        Assert.DoesNotContain(FailedRunId, h.Last.WatchedRunIds);

        await h.AdvanceInStepsAsync(Tuning.QuietMax, Interval);
        Assert.Equal(3, h.GitHub.Count(FailedDetailPath));
    }

    [Fact]
    public async Task pending_reruns有一列時間讀不懂時仍完成初始化而且其他列照常追()
    {
        const long unreadableRunId = 9100;
        var unreadable = unreadableRunId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var h = Create(MoveOffFirstPage);
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        await h.RestartAsync();

        // 另一列的時間欄不是這個程式寫的格式（例如被別的工具改過）。
        h.Cache.UpsertPendingRerun(new PendingRerun(unreadableRunId, FakeData.RepoId, PreviousAttempt: 1, h.Clock.GetUtcNow(), Notify: true));
        MonitorEngineReviewTests.Execute(h.Workspace.Paths.CacheDbPath,
            "UPDATE pending_reruns SET requested_at = '不是時間' WHERE run_id = " + unreadable + ";");

        // 讀不懂的那一列略過並寫 error.log，不讓初始化一直退避重試；9001 照常讀回、排 E6、重跑仍停用。
        await h.DrainAsync();
        Assert.Contains(PendingRerunSkippedLogTitle + "（run " + unreadable + "）", h.ErrorLogText, StringComparison.Ordinal);
        Assert.NotEqual(BannerKind.UnexpectedError, h.Last.Banner?.Kind);
        Assert.Equal(1, h.GitHub.Count(FailedDetailPath));
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);

        StartNewAttempt(RunJson(h, FailedRunId), h.Clock.GetUtcNow());
        await h.AdvanceAsync(Interval);

        Assert.Equal(2, Row(h, FailedRunId).Run.RunAttempt);
        Assert.DoesNotContain(h.Cache.GetPendingReruns(), row => row.RunId == FailedRunId);
        Assert.Contains(FailedRunId, h.Last.WatchedRunIds);
    }

    [Fact]
    public async Task 資料庫重建時待確認重跑寫回失敗仍照常追蹤而且監看不停()
    {
        const string injected = "測試注入的待確認重跑寫回失敗";
        await using var h = Create();
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        // 改名重建之後、寫回之前（ResetAfterDatabaseRecovery 一開始讀時間）：在新的資料庫擋住 pending_reruns 的寫入。
        var blocked = 0;
        h.Clock.BeforeGetUtcNow = () =>
        {
            if (Volatile.Read(ref blocked) == 0 && EngineClock.CalledFrom("ResetAfterDatabaseRecovery")
                && Interlocked.Exchange(ref blocked, 1) == 0)
            {
                MonitorEngineReviewTests.Execute(h.Workspace.Paths.CacheDbPath,
                    "CREATE TRIGGER block_pending BEFORE INSERT ON pending_reruns BEGIN SELECT RAISE(ABORT, '" + injected + "'); END;");
            }
        };

        // 下一次寫入 repos 表時讀到損毀、改名重建；寫回失敗只回報，重建照樣做完（重讀 DB、重新排程）。
        await h.AdvanceAsync(Tuning.QuietMax);
        Assert.Equal(1, Volatile.Read(ref blocked));
        Assert.Contains(injected, h.ErrorLogText, StringComparison.Ordinal);
        Assert.Empty(h.Cache.GetPendingReruns());
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);

        // 記憶體裡的追蹤照常：重新抓回這個 repo 之後看到新的 attempt 就結束；監看沒有停。
        StartNewAttempt(RunJson(h, FailedRunId), h.Clock.GetUtcNow());
        var before = h.GitHub.ApiRequests.Count;
        await h.AdvanceInStepsAsync(Interval * 2, Interval);

        // 新的 attempt 還沒完成：照常轉成「完成時通知我」（§8.1）。
        Assert.Equal(2, h.Last.Actions[FailedRunId].RunAttempt);
        Assert.Contains(FailedRunId, h.Last.WatchedRunIds);
        Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(before));
    }

    [Fact]
    public async Task 資料庫重建時帳號寫回失敗仍沿用記憶體裡的帳號而且監看不停()
    {
        const string injected = "測試注入的帳號寫回失敗";
        await using var h = Create(h =>
        {
            AddRunning(h);
            AddOthersRunning(h);
        });
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetWatched(OthersRunId, Watched: true));
        await h.StepAsync();
        Assert.Contains(OthersRunId, h.Cache.GetWatched());
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        var blocked = BlockInsertsAfterRebuild(h, "account", injected);

        // 下一次寫入 repos 表時讀到損毀、改名重建；帳號寫回失敗只回報，「完成時通知我」的標記照常寫回，重建照樣做完（重讀 DB、重新排程）。
        await h.AdvanceAsync(Tuning.QuietMax);
        Assert.True(blocked());
        Assert.Contains(injected, h.ErrorLogText, StringComparison.Ordinal);
        Assert.Null(h.Cache.GetAccount());
        Assert.Contains(OthersRunId, h.Cache.GetWatched());

        // 記憶體裡的帳號照常沿用：重新抓回來之後自己的 run 成功照常發 N2（比對 actor.id，spec §9.1）；監看沒有停。
        FakeData.Complete(RunJson(h, RunningRunId), "success", h.Clock.GetUtcNow());
        var before = h.GitHub.ApiRequests.Count;
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(1), Interval);

        Assert.Equal("成功：octo-org/sample-app · CI #43", Assert.Single(h.Toasts).Title);
        Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(before));
    }

    [Fact]
    public async Task 資料庫重建時完成時通知我的標記寫回失敗仍照常通知而且監看不停()
    {
        const string injected = "測試注入的完成時通知我標記寫回失敗";
        await using var h = Create(AddOthersRunning);
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());
        h.Engine.Post(new EngineCommand.SetWatched(OthersRunId, Watched: true));
        await h.StepAsync();
        Assert.Contains(OthersRunId, h.Last.WatchedRunIds);
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        var blocked = BlockInsertsAfterRebuild(h, "watched_runs", injected);

        // 下一次寫入 repos 表時讀到損毀、改名重建；標記寫回失敗只回報，後面的待確認重跑照常寫回，重建照樣做完（重讀 DB、重新排程）。
        await h.AdvanceAsync(Tuning.QuietMax);
        Assert.True(blocked());
        Assert.Contains(injected, h.ErrorLogText, StringComparison.Ordinal);
        Assert.Empty(h.Cache.GetWatched());
        Assert.Single(h.Cache.GetPendingReruns());
        Assert.Contains(OthersRunId, h.Last.WatchedRunIds);

        // 記憶體裡的標記照常：重新抓回來之後完成就發 N3、取消標記（spec §9.1）；監看沒有停。
        FakeData.Complete(RunJson(h, OthersRunId), "success", h.Clock.GetUtcNow());
        var before = h.GitHub.ApiRequests.Count;
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(1), Interval);

        Assert.Equal("成功：octo-org/sample-app · CI #44", Assert.Single(h.Toasts).Title);
        Assert.DoesNotContain(OthersRunId, h.Last.WatchedRunIds);
        Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(before));
    }

    [Fact]
    public async Task 資料庫重建後重讀失敗仍保留完成時通知我的標記而且監看不停()
    {
        await using var h = Create(AddOthersRunning);
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetWatched(OthersRunId, Watched: true));
        await h.StepAsync();
        Assert.Contains(OthersRunId, h.Cache.GetWatched());
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        var failed = FailReloadAfterRebuild(h);

        // 下一次寫入 repos 表時讀到損毀、改名重建；標記照常寫回，重讀 DB 讀到一半失敗（不是損毀，不會再改名重建）只回報，補回標記與重新排程照常做。
        await h.AdvanceAsync(Tuning.QuietMax);
        Assert.True(failed());
        Assert.Contains(UnreadableTableError, h.ErrorLogText, StringComparison.Ordinal);
        Assert.Contains(OthersRunId, h.Cache.GetWatched());
        Assert.Contains(OthersRunId, h.Last.WatchedRunIds);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));

        // 重新抓回來之後完成就發 N3、取消標記（spec §9.1）；監看沒有停，重讀失敗不是損毀，之後也不會再改名重建。
        FakeData.Complete(RunJson(h, OthersRunId), "success", h.Clock.GetUtcNow());
        var before = h.GitHub.ApiRequests.Count;
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(1), Interval);

        Assert.Equal("成功：octo-org/sample-app · CI #44", Assert.Single(h.Toasts).Title);
        Assert.DoesNotContain(OthersRunId, h.Last.WatchedRunIds);
        Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(before));
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
    }

    [Fact]
    public async Task 資料庫重建時帳號寫回與重讀都失敗仍沿用記憶體裡的帳號而且監看不停()
    {
        const string injected = "測試注入的帳號寫回失敗";
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        var blocked = BlockInsertsAfterRebuild(h, "account", injected);
        var failed = FailReloadAfterRebuild(h);

        // 下一次寫入 repos 表時讀到損毀、改名重建；帳號寫回失敗，重讀 DB 讀到的帳號是空的、接著讀到一半失敗，都只回報。
        await h.AdvanceAsync(Tuning.QuietMax);
        Assert.True(blocked());
        Assert.True(failed());
        Assert.Contains(injected, h.ErrorLogText, StringComparison.Ordinal);
        Assert.Contains(UnreadableTableError, h.ErrorLogText, StringComparison.Ordinal);
        Assert.Null(h.Cache.GetAccount());

        // 記憶體裡的帳號照常沿用：重新抓回來之後自己的 run 成功照常發 N2（比對 actor.id，spec §9.1）；監看沒有停。
        FakeData.Complete(RunJson(h, RunningRunId), "success", h.Clock.GetUtcNow());
        var before = h.GitHub.ApiRequests.Count;
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(1), Interval);

        Assert.Equal("成功：octo-org/sample-app · CI #43", Assert.Single(h.Toasts).Title);
        Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(before));
    }

    [Fact]
    public async Task 資料庫重建後重讀時新的資料庫也損毀就再重建一次而且標記不遺失()
    {
        await using var h = Create(AddOthersRunning);
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetWatched(OthersRunId, Watched: true));
        await h.StepAsync();
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        var corrupted = OnceAfterRebuild(h, path => MonitorEngineReviewTests.CorruptTable(path, UnreadableTable));

        // 下一次寫入 repos 表時讀到損毀、改名重建；重讀時新的資料庫也讀到損毀，記下來交給下一個步驟再重建一次（spec §15）。
        // 第一次留備份；第二次是這個程序自己重建的檔、沒有設過靜音或強制納入，不留。
        await h.AdvanceAsync(Tuning.QuietMax);
        await h.StepAsync();
        Assert.True(corrupted());
        Assert.Equal(2, h.Engine.DatabaseRebuildCount);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        Assert.Equal(BannerKind.DatabaseRecovered, h.Last.Banner?.Kind);
        Assert.Contains(OthersRunId, h.Cache.GetWatched());
        Assert.Contains(OthersRunId, h.Last.WatchedRunIds);

        // 第二次重建之後照常：完成就發 N3、取消標記；監看沒有停。
        FakeData.Complete(RunJson(h, OthersRunId), "success", h.Clock.GetUtcNow());
        var before = h.GitHub.ApiRequests.Count;
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(1), Interval);

        Assert.Equal("成功：octo-org/sample-app · CI #44", Assert.Single(h.Toasts).Title);
        Assert.DoesNotContain(OthersRunId, h.Last.WatchedRunIds);
        Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(before));
    }

    /// <remarks>改名失敗靠 Windows 的檔案共用規則（開著的檔案沒有允許刪除就不能改名），所以只在 Windows 上執行。</remarks>
    [WindowsFact]
    public async Task 資料庫在步驟開頭重建後重讀時新的資料庫也損毀就先不送請求而且同一步結尾再重建一次()
    {
        await using var h = Create(AddOthersRunning);
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetWatched(OthersRunId, Watched: true));
        await h.StepAsync();
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        var corrupted = OnceAfterRebuild(h, path => MonitorEngineReviewTests.CorruptTable(path, UnreadableTable));
        var requestsAtRebuilds = RequestsAtRebuilds(h);

        // 另一個程式開著 cache.db（沒有允許刪除）：下一次寫入 repos 表時讀到損毀，步驟結尾改名重建失敗而退避。
        using (new FileStream(h.Workspace.Paths.CacheDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await h.AdvanceAsync(Tuning.QuietMax);
            Assert.Contains("重建快取資料庫", h.ErrorLogText, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
            Assert.False(corrupted());
        }

        // 檔案放開之後，退避結束的那一步在步驟開頭改名重建；重讀時新的資料庫也讀到損毀：這一步不送請求
        // （不對已知損毀的資料庫抓 repo 清單、扣額度），交給同一步結尾再重建一次（spec §15）。第一次留備份，第二次不留。
        await h.AdvanceAsync(Tuning.RepoErrorBackoffMax);
        Assert.True(corrupted());
        Assert.Equal(2, h.Engine.DatabaseRebuildCount);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        var rebuilds = requestsAtRebuilds();
        Assert.Equal(2, rebuilds.Count);
        Assert.Empty(h.GitHub.ApiRequests.Skip(rebuilds[0]).Take(rebuilds[1] - rebuilds[0]));
        Assert.Equal(BannerKind.DatabaseRecovered, h.Last.Banner?.Kind);
        Assert.Contains(OthersRunId, h.Cache.GetWatched());
        Assert.Contains(OthersRunId, h.Last.WatchedRunIds);

        // 第二次重建之後照常：完成就發 N3、取消標記；監看沒有停。
        FakeData.Complete(RunJson(h, OthersRunId), "success", h.Clock.GetUtcNow());
        var before = h.GitHub.ApiRequests.Count;
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(1), Interval);

        Assert.Equal("成功：octo-org/sample-app · CI #44", Assert.Single(h.Toasts).Title);
        Assert.DoesNotContain(OthersRunId, h.Last.WatchedRunIds);
        Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(before));
    }

    /// <remarks>
    /// 鎖住重讀又損毀時「照常重新排程」的取捨：不能改成重建後讀到損毀就不排程，否則再改名重建失敗時排程一直是空的、監看停到改名成功為止。
    /// 改名失敗靠 Windows 的檔案共用規則（開著的檔案沒有允許刪除就不能改名），所以只在 Windows 上執行。
    /// </remarks>
    [WindowsFact]
    public async Task 資料庫重建後重讀時新的資料庫也損毀而且再改名重建失敗時監看不停()
    {
        await using var h = Create(AddOthersRunning);
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetWatched(OthersRunId, Watched: true));
        await h.StepAsync();
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        FileStream? held = null;
        var corrupted = OnceAfterRebuild(h, path =>
        {
            // 重讀時新的資料庫也讀到損毀，而且另一個程式開著新的 cache.db：再改名重建一定失敗。
            MonitorEngineReviewTests.CorruptTable(path, UnreadableTable);
            held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        });

        try
        {
            // 另一個程式開著 cache.db（沒有允許刪除）：下一次寫入 repos 表時讀到損毀，步驟結尾改名重建失敗而退避。
            using (new FileStream(h.Workspace.Paths.CacheDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                await h.AdvanceAsync(Tuning.QuietMax);
                Assert.Empty(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
                Assert.False(corrupted());
            }

            // 檔案放開之後，退避結束的那一步在步驟開頭改名重建，重讀時讀到損毀；再重建時新的 cache.db 開著，丟棄失敗而退避。
            var before = h.GitHub.ApiRequests.Count;
            await h.AdvanceAsync(Tuning.RepoErrorBackoffMax);
            Assert.True(corrupted());
            Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));

            // 退避期間監看照常（重建時已經重新排程），不會停到改名重建成功為止。
            await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(1), Interval);
            Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
            Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(before));
        }
        finally
        {
            held?.Dispose();
        }

        // 檔案放開之後，下一次重試就改名重建；完成就發 N3、取消標記。
        FakeData.Complete(RunJson(h, OthersRunId), "success", h.Clock.GetUtcNow());
        var released = h.GitHub.ApiRequests.Count;
        await h.AdvanceAsync(Tuning.RepoErrorBackoffMax);
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(1), Interval);

        Assert.Equal(2, h.Engine.DatabaseRebuildCount);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        Assert.Equal("成功：octo-org/sample-app · CI #44", Assert.Single(h.Toasts).Title);
        Assert.DoesNotContain(OthersRunId, h.Last.WatchedRunIds);
        Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(released));
    }

    [Fact]
    public async Task 資料庫重建後待確認重跑仍在()
    {
        await using var h = Create();
        await h.DrainAsync();
        var sentAt = h.Clock.GetUtcNow();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        // 下一次寫入 repos 表（記下成功時間）時讀到損毀：改名重建，記憶體裡的待確認重跑寫回新的資料庫（同完成時通知我的標記）。
        await h.AdvanceAsync(Tuning.QuietMax);
        Assert.Equal(BannerKind.DatabaseRecovered, h.Last.Banner?.Kind);
        Assert.Equal(new PendingRerun(FailedRunId, FakeData.RepoId, 1, sentAt, Notify: true), Assert.Single(h.Cache.GetPendingReruns()));
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);

        // 重新抓回這個 repo 之後照常追：看到新的 attempt 就結束。
        StartNewAttempt(RunJson(h, FailedRunId), h.Clock.GetUtcNow());
        await h.AdvanceInStepsAsync(Interval * 2, Interval);

        Assert.Equal(2, h.Last.Actions[FailedRunId].RunAttempt);
        Assert.Empty(h.Cache.GetPendingReruns());
    }

    [Fact]
    public async Task 寫入待確認重跑時讀到損毀而重建後仍寫進新的資料庫而且重新啟動後接著追()
    {
        await using var h = Create();
        await h.DrainAsync();
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "pending_reruns");

        // 寫入路徑寫 pending_reruns 時讀到損毀（spec §5.3 第 2 點）：下一步開頭先改名重建，之後才套用重跑的結果。
        // 改名重建時記憶體裡還沒有這一筆，套用時要寫進當下（新的）資料庫，重新啟動後才接得上（spec §6.2 第 6 點）。
        var sentAt = h.Clock.GetUtcNow();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        await h.StepAsync();
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        Assert.Equal(new PendingRerun(FailedRunId, FakeData.RepoId, 1, sentAt, Notify: true), Assert.Single(h.Cache.GetPendingReruns()));

        await h.RestartAsync();
        await h.DrainAsync();
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);
    }

    /// <remarks>改名失敗靠 Windows 的檔案共用規則（開著的檔案沒有允許刪除就不能改名），所以只在 Windows 上執行。</remarks>
    [WindowsFact]
    public async Task 資料庫改名重建退避期間送出的重跑在重建後仍寫進新的資料庫而且重新啟動後接著追()
    {
        await using var h = Create();
        await h.DrainAsync();
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        // 另一個程式開著 cache.db（沒有允許刪除）：下一次寫入 repos 表時讀到損毀，改名重建失敗而退避。
        // 退避期間送出重跑：寫入路徑把 pending_reruns 寫進舊的資料庫，套用結果的那一步還沒跑。
        DateTimeOffset sentAt;
        using (new FileStream(h.Workspace.Paths.CacheDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await h.AdvanceAsync(Tuning.QuietMax);
            Assert.Empty(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
            sentAt = h.Clock.GetUtcNow();
            Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        }

        // 檔案放開之後，退避結束的那一步先改名重建（舊的資料庫連同那一列改名保留），之後才套用重跑的結果：要寫進新的資料庫。
        h.Clock.Advance(Tuning.RepoErrorBackoffMax);
        await h.StepAsync();
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        Assert.Equal(new PendingRerun(FailedRunId, FakeData.RepoId, 1, sentAt, Notify: true), Assert.Single(h.Cache.GetPendingReruns()));

        await h.RestartAsync();
        await h.DrainAsync();
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);
    }

    /// <remarks>改名失敗靠 Windows 的檔案共用規則（開著的檔案沒有允許刪除就不能改名），所以只在 Windows 上執行。</remarks>
    [WindowsFact]
    public async Task 資料庫改名重建退避期間送出的重跑在重建兩次後仍寫進新的資料庫而且重新啟動後接著追()
    {
        await using var h = Create();
        await h.DrainAsync();
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        var corrupted = OnceAfterRebuild(h, path => MonitorEngineReviewTests.CorruptTable(path, UnreadableTable));

        // 另一個程式開著 cache.db（沒有允許刪除）：下一次寫入 repos 表時讀到損毀，改名重建失敗而退避；退避期間送出重跑。
        DateTimeOffset sentAt;
        using (new FileStream(h.Workspace.Paths.CacheDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await h.AdvanceAsync(Tuning.QuietMax);
            Assert.False(corrupted());
            sentAt = h.Clock.GetUtcNow();
            Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        }

        // 檔案放開之後，退避結束的那一步開頭改名重建，重讀時新的資料庫也讀到損毀：這一步不套用使用者操作的結果，
        // 同一步結尾再改名重建一次。下一步才套用重跑的結果，要寫進第二次重建的資料庫。
        h.Clock.Advance(Tuning.RepoErrorBackoffMax);
        await h.StepAsync();
        Assert.True(corrupted());
        Assert.Equal(2, h.Engine.DatabaseRebuildCount);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        await h.DrainAsync();
        Assert.Equal(2, h.Engine.DatabaseRebuildCount);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        Assert.Equal(new PendingRerun(FailedRunId, FakeData.RepoId, 1, sentAt, Notify: true), Assert.Single(h.Cache.GetPendingReruns()));

        await h.RestartAsync();
        await h.DrainAsync();
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);
    }

    [Theory]
    [InlineData(WriteAction.RerunFailedJobs, WriteOutcome.Accepted)]
    [InlineData(WriteAction.RerunFailedJobs, WriteOutcome.Uncertain)]
    [InlineData(WriteAction.RerunAll, WriteOutcome.Accepted)]
    public async Task repo被移除的那一步送出重跑時不追蹤也不留下待確認的重跑(WriteAction action, WriteOutcome outcome)
    {
        await using var h = Create();
        await h.DrainAsync();
        h.Main.Listed = false;
        await h.AdvanceAsync(Tuning.RepoListRefresh);
        if (outcome == WriteOutcome.Uncertain)
        {
            h.GitHub.Override(RerunFailedPath, _ => throw new HttpRequestException("模擬的連線中斷"));
        }

        // 第二輪不見：完整清單的請求進行中送出重跑（寫入不經過步驟的閘門）。寫入路徑寫的那一列被這一步結尾的 RemoveRepo→DeleteRepo 刪掉，
        // 下一步才套用重跑的結果，不能再寫回去（spec §5.4、第二階段 §6.2）。
        WriteResult? rerun = null;
        h.GitHub.OnRequest = request =>
        {
            if (rerun is null && request.RequestUri!.PathAndQuery == EngineHarness.ListAPath)
            {
                rerun = Send(h, action, FailedRunId).GetAwaiter().GetResult();
            }
        };

        await h.AdvanceAsync(Tuning.RepoListRefresh);

        Assert.Equal(outcome, rerun?.Outcome);
        await AssertNoOrphanPendingRerunAsync(h);
    }

    [Fact]
    public async Task 重跑的回應晚於repo被移除時不追蹤也不留下待確認的重跑()
    {
        using var postSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = Create();
        await h.DrainAsync();
        h.Main.Listed = false;
        await h.AdvanceAsync(Tuning.RepoListRefresh);
        h.Clock.Advance(Tuning.RepoListRefresh);
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

        // POST 還在等回應時第二輪不見、repo 被移除；放行之後寫入路徑才寫那一列（寫入晚於 DeleteRepo），套用結果時要把它刪掉。
        await h.DrainAsync();
        Assert.DoesNotContain(h.Cache.GetRepos(), repo => repo.Id == FakeData.RepoId);
        release.Set();
        Assert.Equal(WriteOutcome.Accepted, (await write.WaitAsync(Guard)).Outcome);
        Assert.Equal(FailedRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);

        await AssertNoOrphanPendingRerunAsync(h);
    }

    /// <summary>repo 已經不在 repos 表裡：pending_reruns 沒有留下這筆重跑，改名重建與重新啟動之後也沒有（重新啟動後讀回不會排 E6，永遠清不掉）。</summary>
    private static async Task AssertNoOrphanPendingRerunAsync(EngineHarness h)
    {
        await h.DrainAsync();
        Assert.DoesNotContain(h.Cache.GetRepos(), repo => repo.Id == FakeData.RepoId);
        await AssertNoPendingRerunAsync(h);
    }

    /// <summary>pending_reruns 是空的，改名重建與重新啟動之後也是。</summary>
    private static async Task AssertNoPendingRerunAsync(EngineHarness h)
    {
        Assert.Empty(h.Cache.GetPendingReruns());

        // 記憶體裡也沒有追蹤這一筆：有的話改名重建時 WriteBackPendingReruns 會把孤兒列寫回新的資料庫。要在重新啟動之前驗（重新啟動會清掉記憶體）。
        var rebuilds = h.Engine.DatabaseRebuildCount;
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "status_snapshot");
        await h.AdvanceAsync(Tuning.StatusHeartbeatInterval);
        await h.DrainAsync();
        Assert.True(h.Engine.DatabaseRebuildCount > rebuilds);
        Assert.Empty(h.Cache.GetPendingReruns());

        await h.RestartAsync();
        await h.DrainAsync();
        Assert.Empty(h.Cache.GetPendingReruns());
    }

    // ── repo 不在清單上的待確認重跑（第二階段 spec §6.2，輪數同 spec §5.4）──

    [Fact]
    public async Task 已追蹤的重跑在重建後repo連續兩輪沒有列出就清除()
    {
        await using var h = Create();
        await RebuildWhileUnlistedAsync(h);

        // 只不見一輪，可能只是暫時漏列：還不清。
        Assert.Equal(FailedRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);

        await h.AdvanceAsync(Tuning.RepoListRefresh);

        await AssertNoOrphanPendingRerunAsync(h);
    }

    [Fact]
    public async Task 重跑的回應跨過重建而且repo不再列出時連續兩輪沒有列出就清除()
    {
        await using var h = Create();
        await h.DrainAsync();
        h.Main.Listed = false;

        // POST 還在等回應時重建：清單還沒抓回來，不知道 repo 還在不在，這時套用的結果照常追蹤。
        var result = await AnswerAfterAsync(h, () => Rerun(h), async () =>
        {
            MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
            await StepUntilAsync(h, () => h.Engine.DatabaseRebuildCount > 0);
            Assert.False(h.Last.RepoListFetched);
        });
        Assert.Equal(WriteOutcome.Accepted, result.Outcome);

        // 套用結果之後清單才抓完：第 1 輪不見，還不清。套用的這一步排的 E6 送不出去（repo 還沒抓回來），這一步不送請求，所以要另外走。
        await h.StepAsync();
        await h.DrainAsync();
        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(FailedRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);

        await h.AdvanceAsync(Tuning.RepoListRefresh);

        await AssertNoOrphanPendingRerunAsync(h);
    }

    [Fact]
    public async Task 重跑在重建後repo只漏列一輪又回來時仍然追蹤而且完成時通知()
    {
        // 關掉「我的 run 成功」（N2）：這筆是我的 run，成功時的提示只可能來自重跑自動標記的「完成時通知我」（N3）。
        await using var h = Create(h => h.Settings.NotifyMyRunSuccess = false);
        await RebuildWhileUnlistedAsync(h);

        var run = RunJson(h, FailedRunId);
        StartNewAttempt(run, h.Clock.GetUtcNow());
        h.Main.Listed = true;
        await h.AdvanceAsync(Tuning.RepoListRefresh);
        Assert.Contains(FailedRunId, h.Last.WatchedRunIds);
        Assert.Empty(h.Cache.GetPendingReruns());

        FakeData.Complete(run, "success", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.QuietMax);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        Assert.Equal(SuccessToastTitle, Assert.Single(h.Toasts).Title);
        Assert.Equal(nameof(NotificationKind.WatchedRunDone), NotifiedKind(h, FailedRunId, attempt: 2));
    }

    [Fact]
    public async Task 待確認的重跑沒有連續兩輪不見就不清除()
    {
        await using var h = Create();
        await RebuildWhileUnlistedAsync(h);

        // 下一輪回來：不見的輪數歸零（這筆重跑還沒收到放棄所需的回應次數）。
        h.Main.Listed = true;
        await h.AdvanceAsync(Tuning.RepoListRefresh);
        Assert.True(h.RepoRow(FakeData.RepoId).Monitored);

        // 再一次重建後又不見：這是新的第 1 輪，還不清。
        h.Main.Listed = false;
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        var rebuilds = h.Engine.DatabaseRebuildCount;
        await h.AdvanceAsync(Tuning.QuietMax);

        Assert.Equal(rebuilds + 1, h.Engine.DatabaseRebuildCount);
        Assert.Empty(h.Cache.GetRepos());
        Assert.Equal(FailedRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);
    }

    [Fact]
    public async Task 清單抓不完整的那幾輪不算repo沒有列出()
    {
        await using var h = Create();
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());
        h.Main.Listed = false;
        h.GitHub.Override(EngineHarness.ListAPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "模擬的伺服器錯誤"));
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        await h.AdvanceAsync(Tuning.QuietMax);

        // 這一輪中途失敗就不算數（spec §5.4）：失敗幾輪都不清。退避後不會比正常間隔短，所以每 RepoListRefresh 重試一次。
        var requests = h.GitHub.Count(EngineHarness.ListAPath);
        await h.AdvanceInStepsAsync(Tuning.RepoListRefresh * Tuning.RemovalConfirmRounds, Tuning.RepoListRefresh);
        Assert.Equal(requests + Tuning.RemovalConfirmRounds, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.False(h.Last.RepoListFetched);
        Assert.Single(h.Cache.GetPendingReruns());

        // 恢復之後才開始算：第 1 輪不見還不清，第 2 輪才清。
        h.GitHub.RemoveOverrides(EngineHarness.ListAPath);
        await h.AdvanceAsync(Tuning.RepoListRefresh);
        Assert.True(h.Last.RepoListFetched);
        Assert.Single(h.Cache.GetPendingReruns());

        await h.AdvanceAsync(Tuning.RepoListRefresh);

        await AssertNoOrphanPendingRerunAsync(h);
    }

    [Fact]
    public async Task 重新啟動讀回repo不在清單上的待確認重跑時連續兩輪沒有列出就清除而其他列不受影響()
    {
        await using var h = Create();
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());
        h.Cache.UpsertPendingRerun(new PendingRerun(OrphanRunId, OrphanRepoId, 1, Start, Notify: true));

        await h.RestartAsync();
        await h.DrainAsync();
        Assert.Equal(2, h.Cache.GetPendingReruns().Count);

        await h.AdvanceAsync(Tuning.RepoListRefresh);

        Assert.Equal(FailedRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);
    }

    // ── 送出之後、套用之前 repo 停止監看或被移除（第二階段 spec §6.2、§6.3）──

    [Theory]
    [InlineData(WriteAction.RerunFailedJobs, WriteOutcome.Accepted)]
    [InlineData(WriteAction.RerunFailedJobs, WriteOutcome.Uncertain)]
    [InlineData(WriteAction.RerunAll, WriteOutcome.Accepted)]
    public async Task 重跑的回應晚於repo被靜音時不追蹤也不留下待確認的重跑(WriteAction action, WriteOutcome outcome)
    {
        await using var h = Create();
        await h.DrainAsync();
        if (outcome == WriteOutcome.Uncertain)
        {
            h.GitHub.Override(RerunFailedPath, _ => throw new HttpRequestException("模擬的連線中斷"));
        }

        // POST 還在等回應時靜音：OnStopMonitoring 已經跑過，當時還沒有追蹤可以清。放行之後寫入路徑才寫那一列，套用結果時要把它刪掉。
        var result = await AnswerAfterAsync(h, () => Send(h, action, FailedRunId), () => MuteAsync(h));
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(FailedRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);

        await h.DrainAsync();
        Assert.Empty(h.Cache.GetPendingReruns());

        await h.RestartAsync();
        await h.DrainAsync();
        Assert.Empty(h.Cache.GetPendingReruns());

        // 取消靜音之後不是待確認：重跑立刻可用。
        await UnmuteAsync(h);
        Assert.Equal(ActionBlock.None, h.Last.Actions[FailedRunId].RerunFailedJobs);
    }

    [Fact]
    public async Task 重跑的回應晚於repo被靜音時取消靜音後不多發通知()
    {
        await using var h = Create(UseUntrackedBranch);
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await AnswerAfterAsync(h, () => Rerun(h), () => MuteAsync(h))).Outcome);
        await h.DrainAsync();

        // 靜音期間重跑跑完：不屬於追蹤鍵，N1 不會發；還記著待確認的話，取消靜音後會看到新的 attempt、直接發 N3。
        var run = RunJson(h, FailedRunId);
        StartNewAttempt(run, h.Clock.GetUtcNow());
        FakeData.Complete(run, "failure", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.QuietMax);

        await UnmuteAsync(h);
        await h.AdvanceAsync(Tuning.QuietMax);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        Assert.Empty(h.Toasts);
    }

    [Fact]
    public async Task 重跑的回應跨過重建而且repo暫時算過舊時仍然追蹤()
    {
        await using var h = Create(h => h.Main.SetPushedAt(Start.AddDays(-200)));
        await h.DrainAsync();
        Assert.True(h.RepoRow(FakeData.RepoId).Monitored);

        // 重建後本地沒有 run：清單抓回來時第 6 條算過舊，E4 的首次檢查回來才轉回監看。在這之間套用重跑的結果。
        var result = await AnswerAfterAsync(h, () => Rerun(h), async () =>
        {
            MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
            await StepUntilAsync(h, () => h.Last.Repos.SingleOrDefault(row => row.Repo.Id == FakeData.RepoId) is { Monitored: false, Reason: SkipReason.Stale });
        });
        Assert.Equal(WriteOutcome.Accepted, result.Outcome);

        await h.DrainAsync();
        Assert.True(h.RepoRow(FakeData.RepoId).Monitored);
        Assert.Equal(FailedRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);
    }

    [Fact]
    public async Task 取消的回應晚於repo被靜音時不顯示取消中()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();

        // POST 還在等回應時靜音：OnStopMonitoring 已經跑過。之後不輪詢這個 repo、看不到完成，記成取消中就清不掉。
        var result = await AnswerAfterAsync(h, () => CancelRunning(h), () => MuteAsync(h));
        Assert.Equal(WriteOutcome.Accepted, result.Outcome);

        await h.DrainAsync();
        Assert.Empty(h.Last.Cancelling);

        await UnmuteAsync(h);
        Assert.Empty(h.Last.Cancelling);
        Assert.Equal(ActionBlock.None, h.Last.Actions[RunningRunId].Cancel);
    }

    [Fact]
    public async Task 強制取消的回應晚於repo被靜音時不顯示取消中()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await CancelRunning(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());
        await h.AdvanceInStepsAsync(Tuning.CancelGraceBeforeForce, Interval);
        Assert.True(h.Last.Actions[RunningRunId].ShowForceCancel);

        // 靜音清掉了原本的取消中；強制取消的結果不能再新增一筆。
        var result = await AnswerAfterAsync(h, () => Send(h, WriteAction.ForceCancel, RunningRunId), () => MuteAsync(h));
        Assert.Equal(WriteOutcome.Accepted, result.Outcome);

        await h.DrainAsync();
        Assert.Empty(h.Last.Cancelling);

        await UnmuteAsync(h);
        Assert.Empty(h.Last.Cancelling);
        Assert.Equal(ActionBlock.None, h.Last.Actions[RunningRunId].Cancel);
    }

    [Fact]
    public async Task 取消的回應晚於repo被移除時不顯示取消中()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        h.Main.Listed = false;
        await h.AdvanceAsync(Tuning.RepoListRefresh);
        h.Clock.Advance(Tuning.RepoListRefresh);

        // POST 還在等回應時第二輪不見、repo 被移除：ForgetRepoPostActions 已經跑過，之後也沒有 E6 清得掉。
        var result = await AnswerAfterAsync(h, () => CancelRunning(h), async () =>
        {
            await h.DrainAsync();
            Assert.DoesNotContain(h.Cache.GetRepos(), repo => repo.Id == FakeData.RepoId);
        });
        Assert.Equal(WriteOutcome.Accepted, result.Outcome);

        await h.DrainAsync();
        Assert.Empty(h.Last.Cancelling);
    }

    // ── 重建之後才知道 repo 不再監看（第二階段 spec §6.2、§14.3 強制納入遺失）──

    /// <summary>設定不納入 fork，fork 上有一筆 run，強制納入之後監看中。</summary>
    private static async Task<EngineHarness> CreateForcedForkAsync(string status, string? conclusion)
    {
        var h = Create(h =>
        {
            h.Settings.IncludeForks = false;
            h.GitHub.AddRepo(FakeData.Repo(ForkRepoId, ForkName, Start.AddDays(-1), fork: true)).Runs.Add(
                FakeData.Run(ForkRunId, 5, status, conclusion, Start.AddDays(-1), repo: ForkName, headRepoId: ForkRepoId));
        });
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetOverride(ForkRepoId, new RepoOverride(Muted: false, ForceIncluded: true)));
        await h.DrainAsync();
        Assert.True(h.RepoRow(ForkRepoId).Monitored);
        return h;
    }

    /// <summary>下一次寫入 repos 表（記下成功時間）時讀到損毀：改名重建，並跑完重建後的第一輪清單。</summary>
    private static async Task RebuildAsync(EngineHarness h)
    {
        var rebuilds = h.Engine.DatabaseRebuildCount;
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        await h.AdvanceAsync(Tuning.QuietMax);
        Assert.Equal(rebuilds + 1, h.Engine.DatabaseRebuildCount);
        Assert.True(h.Last.RepoListFetched);
    }

    [Fact]
    public async Task 已追蹤的重跑在重建後強制納入遺失而fork不再監看時清除()
    {
        await using var h = await CreateForcedForkAsync("completed", "failure");
        var rerun = h.Engine.SendWriteAsync(WriteAction.RerunFailedJobs, ForkRepoId, ForkRunId, 1, None).WaitAsync(Guard);
        Assert.Equal(WriteOutcome.Accepted, (await rerun).Outcome);
        await h.StepAsync();
        Assert.Equal(ForkRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);

        // 重建清空了 _classes：這一次分類沒有「之前在監看」，走不到 OnStopMonitoring；fork 又還在清單上，連續兩輪的清除也碰不到。
        await RebuildAsync(h);
        Assert.Equal(SkipReason.Fork, h.RepoRow(ForkRepoId).Reason);

        await AssertNoPendingRerunAsync(h);
    }

    [Fact]
    public async Task 取消中在重建後強制納入遺失而fork不再監看時清除()
    {
        await using var h = await CreateForcedForkAsync("in_progress", null);
        var cancel = h.Engine.SendWriteAsync(WriteAction.Cancel, ForkRepoId, ForkRunId, 1, None).WaitAsync(Guard);
        Assert.Equal(WriteOutcome.Accepted, (await cancel).Outcome);
        await h.StepAsync();
        Assert.True(h.Last.Cancelling.ContainsKey(ForkRunId));

        await RebuildAsync(h);

        Assert.Equal(SkipReason.Fork, h.RepoRow(ForkRepoId).Reason);
        Assert.Empty(h.Last.Cancelling);
    }

    [Fact]
    public async Task 已追蹤的重跑在重建後repo已封存時清除()
    {
        await using var h = Create();
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        await h.StepAsync();
        h.Main.Repo["archived"] = true;

        await RebuildAsync(h);
        Assert.Equal(SkipReason.Archived, h.RepoRow(FakeData.RepoId).Reason);

        await AssertNoPendingRerunAsync(h);
    }

    [Fact]
    public async Task 已追蹤的重跑在重建後強制納入遺失而協作repo不再列出時連續兩輪清除()
    {
        await using var h = Create(h =>
        {
            h.Settings.IncludeCollaboratorRepos = true;
            h.GitHub.AddRepo(
                FakeData.Repo(CollaboratorRepoId, CollaboratorName, Start.AddDays(-1), ownerId: CollaboratorOwnerId, ownerType: "User", push: true),
                collaborator: true).Runs.Add(
                FakeData.Run(CollaboratorRunId, 5, "completed", "failure", Start.AddDays(-1), repo: CollaboratorName, headRepoId: CollaboratorRepoId));
        });
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetOverride(CollaboratorRepoId, new RepoOverride(Muted: false, ForceIncluded: true)));
        await h.StepAsync();
        h.Settings.IncludeCollaboratorRepos = false;
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.DrainAsync();
        Assert.True(h.RepoRow(CollaboratorRepoId).Monitored);
        var rerun = h.Engine.SendWriteAsync(WriteAction.RerunFailedJobs, CollaboratorRepoId, CollaboratorRunId, 1, None).WaitAsync(Guard);
        Assert.Equal(WriteOutcome.Accepted, (await rerun).Outcome);
        await h.StepAsync();

        // 強制納入遺失之後不需要清單 B：這個 repo 不再列出，走連續兩輪的清除。
        await RebuildAsync(h);
        Assert.DoesNotContain(h.Cache.GetRepos(), repo => repo.Id == CollaboratorRepoId);
        Assert.Equal(CollaboratorRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);

        await h.AdvanceAsync(Tuning.RepoListRefresh);

        await AssertNoPendingRerunAsync(h);
    }

    [Fact]
    public async Task 已追蹤的重跑在重建後repo暫時算過舊時仍然追蹤()
    {
        await using var h = Create(h => h.Main.SetPushedAt(Start.AddDays(-200)));
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        await h.StepAsync();

        // 重建後本地沒有 run：清單抓回來時第 6 條算過舊，E4 的首次檢查回來才轉回監看。這段期間不能清。
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        await StepUntilAsync(h, () => h.Last.Repos.SingleOrDefault(row => row.Repo.Id == FakeData.RepoId) is { Monitored: false, Reason: SkipReason.Stale });
        Assert.Equal(FailedRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);

        await h.DrainAsync();
        Assert.True(h.RepoRow(FakeData.RepoId).Monitored);
        Assert.Equal(FailedRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);
    }

    [Fact]
    public async Task 重新啟動讀回靜音中repo的待確認重跑時保留而且取消靜音後接著追()
    {
        await using var h = Create();
        await h.DrainAsync();
        await MuteAsync(h);
        h.Cache.UpsertPendingRerun(new PendingRerun(FailedRunId, FakeData.RepoId, 1, h.Clock.GetUtcNow(), Notify: true));

        // 重新啟動讀回的每一筆都重新追蹤（spec §6.2 第 6 點），分類也是讀 DB 時就有的：重新分類時不算「重建之後才知道」。
        await h.RestartAsync();
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.RepoListRefresh);
        Assert.Equal(FailedRunId, Assert.Single(h.Cache.GetPendingReruns()).RunId);

        await UnmuteAsync(h);
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunFailedJobs);
    }

    [Fact]
    public async Task 重跑status是startup_failure的run要看到新attempt才結束()
    {
        await using var h = Create(h =>
        {
            h.Main.Runs.Clear();
            h.Main.Runs.Add(FakeData.Run(FailedRunId, 42, "startup_failure", null, Start.AddDays(-1), Start.AddDays(-1).AddMinutes(1)));
        });
        await h.DrainAsync();
        Assert.Equal(ActionBlock.None, h.Last.Actions[FailedRunId].RerunAll);
        Assert.Equal(WriteOutcome.Accepted, (await Send(h, WriteAction.RerunAll, FailedRunId)).Outcome);

        // status 本身是 startup_failure 的 run 是已完成（spec §7.1）：status 不是 completed，但不能因此當成重跑已經開始。
        Assert.Equal(1, await h.StepAsync());
        await h.AdvanceAsync(Interval);
        Assert.Equal(2, h.GitHub.Count(FailedDetailPath));
        Assert.Equal(ActionBlock.RerunPending, h.Last.Actions[FailedRunId].RerunAll);
        Assert.Single(h.Cache.GetPendingReruns());

        StartNewAttempt(RunJson(h, FailedRunId), h.Clock.GetUtcNow());
        await h.AdvanceAsync(Interval);

        Assert.Equal(2, h.Last.Actions[FailedRunId].RunAttempt);
        Assert.Empty(h.Cache.GetPendingReruns());
    }

    // ── 待確認的重跑與取消分開記（controller 的 ruling）────────────────────

    [Theory]
    [InlineData(WriteOutcome.Accepted)]
    [InlineData(WriteOutcome.Uncertain)]
    [InlineData(WriteOutcome.Rejected)]
    public async Task 只有受理或結果不確定的重跑是待確認而且看到新attempt就清除(WriteOutcome outcome)
    {
        await using var h = Create();
        await h.DrainAsync();
        if (outcome == WriteOutcome.Uncertain)
        {
            h.GitHub.Override(RerunFailedPath, _ => throw new HttpRequestException("模擬的連線中斷"));
        }
        else if (outcome == WriteOutcome.Rejected)
        {
            h.GitHub.Override(RerunFailedPath, _ => h.GitHub.Error(HttpStatusCode.Conflict, "Conflict"));
        }

        Assert.Equal(outcome, (await Rerun(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());

        // 被拒代表伺服器沒有執行：不是待確認的重跑，重跑照常可用（spec §6.2 第 1 點只有受理與結果不確定寫 pending_reruns）。
        var pending = outcome != WriteOutcome.Rejected;
        var expected = pending ? ActionBlock.RerunPending : ActionBlock.None;
        Assert.Equal(expected, h.Last.Actions[FailedRunId].RerunFailedJobs);
        Assert.Equal(expected, h.Last.Actions[FailedRunId].RerunAll);
        Assert.Equal(pending ? 1 : 0, h.Cache.GetPendingReruns().Count);
        if (pending)
        {
            // 寫入的鎖內再檢查讀同一份待確認：不送 POST。
            var again = await Immediately(Rerun(h));
            AssertNotSent(again, NotSentReason.Unavailable);
            Assert.Equal(ActionBlock.RerunPending, again.Block);
        }

        // 第 2 次嘗試（這裡已經是完成的失敗）被 E4 看到：待確認的重跑結束，第 2 次嘗試可以再重跑。
        var run = RunJson(h, FailedRunId);
        StartNewAttempt(run, h.Clock.GetUtcNow());
        FakeData.Complete(run, "failure", h.Clock.GetUtcNow());
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Equal(2, h.Last.Actions[FailedRunId].RunAttempt);
        Assert.Equal(ActionBlock.None, h.Last.Actions[FailedRunId].RerunFailedJobs);
        Assert.Empty(h.Cache.GetPendingReruns());
    }

    [Fact]
    public async Task 重跑待確認時新attempt的取消受理後兩者各自追到結束()
    {
        await using var h = Create();
        await h.DrainAsync();

        // 上一次執行：GitHub 已經開始第 2 次嘗試、本地也看到了，pending_reruns 的資料列卻還沒刪掉就結束（例如刪除失敗）。
        var run = RunJson(h, FailedRunId);
        StartNewAttempt(run, h.Clock.GetUtcNow());
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        h.Cache.UpsertPendingRerun(new PendingRerun(FailedRunId, FakeData.RepoId, PreviousAttempt: 1, h.Clock.GetUtcNow(), Notify: true));
        await h.RestartAsync();

        // 重新啟動後的第一步（驗證帳號）：讀回的待確認重跑還在，本地的第 2 次嘗試未完成，可以取消。
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(ActionBlock.None, h.Last.Actions[FailedRunId].Cancel);
        Assert.Equal(WriteOutcome.Accepted, (await Send(h, WriteAction.Cancel, FailedRunId, attempt: 2)).Outcome);

        // 取消不蓋掉待確認的重跑：E6 看到第 2 次嘗試，重跑照自己的條件結束（刪資料列、轉成完成時通知我），取消中繼續等完成。
        await h.DrainAsync();
        Assert.Empty(h.Cache.GetPendingReruns());
        Assert.Contains(FailedRunId, h.Last.WatchedRunIds);
        Assert.True(h.Last.Cancelling.ContainsKey(FailedRunId));

        FakeData.Complete(run, "cancelled", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Interval);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        Assert.Empty(h.Last.Cancelling);
        Assert.Equal(ActionBlock.Completed, h.Last.Actions[FailedRunId].Cancel);
        Assert.Single(h.Toasts);
        Assert.Equal(nameof(NotificationKind.WatchedRunDone), NotifiedKind(h, FailedRunId, attempt: 2));
    }

    // ── 取消中與強制取消（第二階段 spec §6.3、§6.4）──────────────────────

    [Fact]
    public async Task 取消受理後顯示取消中直到完成()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        var sentAt = h.Clock.GetUtcNow();
        Assert.Equal(WriteOutcome.Accepted, (await CancelRunning(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());

        Assert.Equal(new CancellingInfo(sentAt, ForceSent: false), h.Last.Cancelling[RunningRunId]);
        var actions = h.Last.Actions[RunningRunId];
        Assert.Equal(ActionBlock.CancelPending, actions.Cancel);
        Assert.False(actions.ShowForceCancel);
        Assert.Equal(ActionBlock.ForceNotYet, actions.ForceCancel);

        // 已送出取消：再按一次也不送，寫入的鎖內再檢查看得到（spec §6.4、§7.4）。
        var again = await Immediately(CancelRunning(h));
        AssertNotSent(again, NotSentReason.Unavailable);
        Assert.Equal(ActionBlock.CancelPending, again.Block);

        // 不設時間上限（spec §6.3）：run 還沒完成就一直是取消中。
        await h.AdvanceInStepsAsync(Tuning.CancelGraceBeforeForce * 2, Interval);
        Assert.True(h.Last.Cancelling.ContainsKey(RunningRunId));

        FakeData.Complete(RunJson(h, RunningRunId), "cancelled", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.QuietMax);

        Assert.Empty(h.Last.Cancelling);
        Assert.Equal(ActionBlock.Completed, h.Last.Actions[RunningRunId].Cancel);
        Assert.Equal([CancelPath], h.GitHub.Posts);
    }

    [Fact]
    public async Task 取消5分鐘後才出現強制取消()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await CancelRunning(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());

        await h.AdvanceInStepsAsync(Tuning.CancelGraceBeforeForce - Interval, Interval);
        var waiting = h.Last.Actions[RunningRunId];
        Assert.False(waiting.ShowForceCancel);
        Assert.Equal(ActionBlock.ForceNotYet, waiting.ForceCancel);
        var early = await Immediately(Send(h, WriteAction.ForceCancel, RunningRunId));
        AssertNotSent(early, NotSentReason.Unavailable);
        Assert.Equal(ActionBlock.ForceNotYet, early.Block);

        // 取消受理滿 CancelGraceBeforeForce、run 仍未完成：出現強制取消（spec §6.4），一般取消照樣停用。
        await h.AdvanceAsync(Interval);
        var due = h.Last.Actions[RunningRunId];
        Assert.True(due.ShowForceCancel);
        Assert.Equal(ActionBlock.None, due.ForceCancel);
        Assert.Equal(ActionBlock.CancelPending, due.Cancel);
        Assert.Equal([CancelPath], h.GitHub.Posts);
    }

    [Fact]
    public async Task 強制取消受理後不再出現()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        var cancelledAt = h.Clock.GetUtcNow();
        Assert.Equal(WriteOutcome.Accepted, (await CancelRunning(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());
        await h.AdvanceInStepsAsync(Tuning.CancelGraceBeforeForce, Interval);
        Assert.True(h.Last.Actions[RunningRunId].ShowForceCancel);

        Assert.Equal(WriteOutcome.Accepted, (await Send(h, WriteAction.ForceCancel, RunningRunId)).Outcome);
        Assert.Equal(1, await h.StepAsync());

        // 保留取消受理的時間、記下已送出強制取消：強制取消不再出現，原因是已送出取消（計畫的差異表第 4 列）。
        Assert.Equal(new CancellingInfo(cancelledAt, ForceSent: true), h.Last.Cancelling[RunningRunId]);
        var actions = h.Last.Actions[RunningRunId];
        Assert.False(actions.ShowForceCancel);
        Assert.Equal(ActionBlock.CancelPending, actions.ForceCancel);
        var again = await Immediately(Send(h, WriteAction.ForceCancel, RunningRunId));
        AssertNotSent(again, NotSentReason.Unavailable);
        Assert.Equal(ActionBlock.CancelPending, again.Block);
        Assert.Equal([CancelPath, ForceCancelPath], h.GitHub.Posts);
    }

    [Fact]
    public async Task 取消後5分鐘改用安靜間隔()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await CancelRunning(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());

        // 前 CancelGraceBeforeForce 每 ActiveRepoInterval × k 查一次：受理後的第一次加上之後每 15 秒一次，共 300 ÷ 15 + 1 = 21 次。
        await h.AdvanceInStepsAsync(Tuning.CancelGraceBeforeForce, Interval);
        var inGrace = (int)(Tuning.CancelGraceBeforeForce / Interval) + 1;
        Assert.Equal(inGrace, h.GitHub.Count(RunningDetailPath));

        // 之後改用 QuietMax × k（spec §6.2）。
        await h.AdvanceInStepsAsync(Tuning.QuietMax - Interval, Interval);
        Assert.Equal(inGrace, h.GitHub.Count(RunningDetailPath));
        await h.AdvanceAsync(Interval);
        Assert.Equal(inGrace + 1, h.GitHub.Count(RunningDetailPath));
    }

    // ── 只查一次 E6、404、停止監看（第二階段 spec §6.2）───────────────────

    [Theory]
    [InlineData(WriteAction.RerunFailedJobs)]
    [InlineData(WriteAction.Cancel)]
    public async Task 被拒的寫入只查一次E6(WriteAction action)
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        var cancel = action == WriteAction.Cancel;
        var (runId, path, detailPath) = cancel
            ? (RunningRunId, CancelPath, RunningDetailPath)
            : (FailedRunId, RerunFailedPath, FailedDetailPath);
        h.GitHub.Override(path, _ => h.GitHub.Error(HttpStatusCode.Conflict, "Conflict"));

        Assert.Equal(WriteOutcome.Rejected, (await Send(h, action, runId)).Outcome);
        Assert.Equal(1, await h.StepAsync());

        // 只為了把本地狀態對齊伺服器：第一次 E6 有回應就結束，不記成待確認的重跑或取消中。
        await h.AdvanceInStepsAsync(Tuning.QuietMax * 2, Interval);
        Assert.Equal(1, h.GitHub.Count(detailPath));
        Assert.Empty(h.Last.Cancelling);
        Assert.Equal(ActionBlock.None, h.Last.Actions[FailedRunId].RerunFailedJobs);
        Assert.Equal(ActionBlock.None, h.Last.Actions[RunningRunId].Cancel);
        Assert.Empty(h.Cache.GetPendingReruns());
    }

    [Fact]
    public async Task 取消的結果不確定只查一次E6也不顯示取消中()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        h.GitHub.Override(CancelPath, _ => throw new HttpRequestException("模擬的連線中斷"));

        Assert.Equal(WriteOutcome.Uncertain, (await CancelRunning(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());

        // 不知道取消有沒有生效：不標成取消中（spec §6.2），一般取消仍可用；run 本來就未完成，P1 的規則接手。
        Assert.Empty(h.Last.Cancelling);
        Assert.Equal(ActionBlock.None, h.Last.Actions[RunningRunId].Cancel);
        await h.AdvanceInStepsAsync(Tuning.QuietMax * 2, Interval);
        Assert.Equal(1, h.GitHub.Count(RunningDetailPath));
    }

    [Theory]
    [InlineData(WriteAction.RerunFailedJobs)]
    [InlineData(WriteAction.Cancel)]
    public async Task E6回404時清除待確認的操作(WriteAction action)
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        var cancel = action == WriteAction.Cancel;
        var (runId, detailPath) = cancel ? (RunningRunId, RunningDetailPath) : (FailedRunId, FailedDetailPath);

        Assert.Equal(WriteOutcome.Accepted, (await Send(h, action, runId)).Outcome);
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(cancel ? 0 : 1, h.Cache.GetPendingReruns().Count);
        Assert.Equal(cancel, h.Last.Cancelling.ContainsKey(runId));

        // 過了保留期或被刪掉：照 P1 標成已不存在，待確認的操作與 pending_reruns 的資料列一起清除（spec §6.2 第 5 點）。
        h.GitHub.Override(detailPath, _ => h.GitHub.Error(HttpStatusCode.NotFound, "Not Found"));
        await h.AdvanceAsync(Interval);

        Assert.Equal(2, h.GitHub.Count(detailPath));
        Assert.Empty(h.Cache.GetPendingReruns());
        Assert.Empty(h.Last.Cancelling);
        await h.AdvanceInStepsAsync(Tuning.QuietMax, Interval);
        Assert.Equal(2, h.GitHub.Count(detailPath));
    }

    [Fact]
    public async Task repo停止監看時清除待確認的操作()
    {
        await using var h = Create(AddRunning);
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await CancelRunning(h)).Outcome);
        var rerun = Rerun(h);
        h.Clock.Advance(Tuning.MutativeRequestSpacing);
        Assert.Equal(WriteOutcome.Accepted, (await rerun).Outcome);
        await h.DrainAsync();
        Assert.Single(h.Cache.GetPendingReruns());
        Assert.True(h.Last.Cancelling.ContainsKey(RunningRunId));

        // 靜音：不再輪詢這個 repo 的 E6（spec §11.1），待確認的操作一起清除（spec §6.2）。
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.DrainAsync();
        Assert.Empty(h.Cache.GetPendingReruns());
        Assert.Empty(h.Last.Cancelling);
        var details = h.GitHub.Count(FailedDetailPath) + h.GitHub.Count(RunningDetailPath);
        await h.AdvanceInStepsAsync(Interval * 4, Interval);
        Assert.Equal(details, h.GitHub.Count(FailedDetailPath) + h.GitHub.Count(RunningDetailPath));

        // 取消靜音之後也不是待確認：重跑與取消都恢復可用。
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, RepoOverride.None));
        await h.DrainAsync();
        Assert.Equal(ActionBlock.None, h.Last.Actions[FailedRunId].RerunFailedJobs);
        Assert.Equal(ActionBlock.None, h.Last.Actions[RunningRunId].Cancel);
        Assert.Empty(h.Last.Cancelling);
    }

    // ── 重跑後的通知（第二階段 spec §8.1）────────────────────────────────

    [Fact]
    public async Task 重跑非追蹤的舊失敗run再失敗時通知()
    {
        await using var h = Create(UseUntrackedBranch);
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);
        Assert.Equal(1, await h.StepAsync());

        var run = RunJson(h, FailedRunId);
        StartNewAttempt(run, h.Clock.GetUtcNow());
        await h.AdvanceAsync(Interval);
        Assert.Contains(FailedRunId, h.Last.WatchedRunIds);

        // 不屬於任何追蹤鍵，N1 不會發；重跑時自動標記的「完成時通知我」照 N3 通知。
        FakeData.Complete(run, "failure", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.QuietMax);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        var toast = Assert.Single(h.Toasts);
        Assert.Equal(FailureToastTitle, toast.Title);
        Assert.Equal(nameof(NotificationKind.WatchedRunDone), NotifiedKind(h, FailedRunId, attempt: 2));
        Assert.DoesNotContain(FailedRunId, h.Last.WatchedRunIds);
    }

    [Fact]
    public async Task 重跑在一次查詢間隔內就完成時仍通知()
    {
        await using var h = Create(UseUntrackedBranch);
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);

        // 第一次 E6 之前就跑完：新的 attempt 第一次被看到就已經完成，在完成觀察的當下直接發 N3（spec §8.1 第 3 步）。
        var run = RunJson(h, FailedRunId);
        StartNewAttempt(run, h.Clock.GetUtcNow());
        FakeData.Complete(run, "failure", h.Clock.GetUtcNow());
        Assert.Equal(1, await h.StepAsync());
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        var toast = Assert.Single(h.Toasts);
        Assert.Equal(FailureToastTitle, toast.Title);
        Assert.Equal(nameof(NotificationKind.WatchedRunDone), NotifiedKind(h, FailedRunId, attempt: 2));
        Assert.Empty(h.Cache.GetPendingReruns());
        Assert.Empty(h.Cache.GetWatched());
    }

    [Fact]
    public async Task N3關閉時重跑完成不通知()
    {
        await using var h = Create(h =>
        {
            h.Settings.NotifyWatchedRunDone = false;
            UseUntrackedBranch(h);
        });
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);

        // 這則通知照 N3 的開關（spec §8.1）；不屬於追蹤鍵，N1 也不發。
        var run = RunJson(h, FailedRunId);
        StartNewAttempt(run, h.Clock.GetUtcNow());
        FakeData.Complete(run, "failure", h.Clock.GetUtcNow());
        Assert.Equal(1, await h.StepAsync());
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        Assert.Empty(h.Toasts);
        Assert.Empty(h.Cache.GetPendingReruns());
    }

    [Fact]
    public async Task 同一筆同時符合N1時只發一則()
    {
        await using var h = Create();
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);

        var run = RunJson(h, FailedRunId);
        StartNewAttempt(run, h.Clock.GetUtcNow());
        FakeData.Complete(run, "failure", h.Clock.GetUtcNow());
        Assert.Equal(1, await h.StepAsync());
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // main 的 latest 失敗（N1）同時是重跑後的 N3：只發一則，記成 N3（P1 §9.1）。
        var toast = Assert.Single(h.Toasts);
        Assert.Equal(FailureToastTitle, toast.Title);
        Assert.Equal(nameof(NotificationKind.WatchedRunDone), NotifiedKind(h, FailedRunId, attempt: 2));

        // 之後的輪詢再看到同一個結果也不重發（去重鍵，P1 §9.2）。
        await h.AdvanceAsync(Tuning.QuietMax);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);
        Assert.Single(h.Toasts);
    }

    // ── ETA 樣本（第二階段 spec §8.2）──────────────────────────────────

    [Fact]
    public async Task 重跑過的成功不存ETA樣本()
    {
        await using var h = Create();
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);

        var run = RunJson(h, FailedRunId);
        var startedAt = h.Clock.GetUtcNow();
        StartNewAttempt(run, startedAt);
        FakeData.Complete(run, "success", startedAt + RerunDuration);
        Assert.Equal(1, await h.StepAsync());

        // 只重跑失敗的 job 時耗時通常比完整執行短：run_attempt 不是 1 的成功不存樣本（D13）。
        Assert.Equal(2, h.Last.Actions[FailedRunId].RunAttempt);
        Assert.Empty(h.Cache.GetDurationSamples(FakeData.RepoId, FakeData.WorkflowId));
    }

    [Fact]
    public async Task 重跑過的已完成run仍顯示耗時()
    {
        await using var h = Create();
        await h.DrainAsync();
        Assert.Equal(WriteOutcome.Accepted, (await Rerun(h)).Outcome);

        var run = RunJson(h, FailedRunId);
        var startedAt = h.Clock.GetUtcNow();
        StartNewAttempt(run, startedAt);
        FakeData.Complete(run, "success", startedAt + RerunDuration);
        Assert.Equal(1, await h.StepAsync());

        // DurationEstimator 不動：清單上的耗時照樣顯示（spec §8.2）。
        Assert.Equal(RerunDuration, h.Last.CompletedDurations[FailedRunId]);
    }

    // ── 共用 ──────────────────────────────────────────────────────────

    /// <summary>第一次檢查就停下的寫入：不排隊、不等節流器就回傳。還沒完成代表進了等待，直接失敗。</summary>
    private static async Task<WriteResult> Immediately(Task<WriteResult> pending)
    {
        Assert.True(pending.IsCompleted, "這個寫入應該在第一次檢查就回傳，不排隊等待。");
        return await pending;
    }

    private static void AssertNotSent(WriteResult result, NotSentReason reason)
    {
        Assert.Equal(WriteOutcome.NotSent, result.Outcome);
        Assert.Equal(reason, result.NotSent);
        Assert.Equal(0, result.StatusCode);
        Assert.Null(result.Rejected);
    }

    /// <summary>notified 表裡這一組 (run_id, run_attempt) 記的通知種類；沒有這一列或種類是 null 時回 null。</summary>
    private static string? NotifiedKind(EngineHarness h, long runId, int attempt)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = h.Workspace.Paths.CacheDbPath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM notified WHERE run_id = $runId AND run_attempt = $attempt;";
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$attempt", attempt);
        return command.ExecuteScalar() as string;
    }
}
