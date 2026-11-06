using System.Collections.Frozen;
using System.Net;
using System.Text.RegularExpressions;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Filtering;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Status;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>
/// 給命令列讀的狀態表（第二階段 spec §10.2、§10.3、§10.5）：內容改變才立即寫、每 60 秒的心跳、寫入失敗後的重試、
/// 結束時的 stopped_at，以及命令列刷新的序號與完成判斷。
/// </summary>
public class MonitorEngineStatusTests
{
    private const string OtherRepoName = "octo-org/other-app";
    private const long OtherRepoId = 2002;

    /// <summary>沒有開啟中 PR 的功能分支：上面的 run 不屬於任何追蹤鍵（P1 §7.2）。</summary>
    private const string UntrackedBranch = "feature/x";

    /// <summary>非 ASCII 的標題：狀態表裡跳脫成 <c>\uXXXX</c>，解析後照原樣（第二階段 spec §10.3）。</summary>
    private const string ChineseTitle = "修正登入按鈕";

    /// <summary>ASCII 的最大字元；status_json 只能有這個範圍的字元（第二階段 spec §11.3）。</summary>
    private const char MaxAscii = '\u007F';

    /// <summary>模擬狀態表持續寫不進去（例如磁碟滿或唯讀）的 trigger 與它的錯誤訊息。UPSERT 第一次是 INSERT、之後是 UPDATE，兩種都擋。</summary>
    private const string InjectedWriteFailure = "測試注入的狀態表寫入失敗";
    private const string BlockStatusWrites =
        "CREATE TRIGGER block_status_insert BEFORE INSERT ON status_snapshot BEGIN SELECT RAISE(ABORT, '" + InjectedWriteFailure + "'); END;"
        + "CREATE TRIGGER block_status_update BEFORE UPDATE ON status_snapshot BEGIN SELECT RAISE(ABORT, '" + InjectedWriteFailure + "'); END;";
    private const string UnblockStatusWrites = "DROP TRIGGER block_status_insert; DROP TRIGGER block_status_update;";

    /// <summary>寫狀態表失敗時 error.log 的標題（MonitorEngine.Status.cs）。</summary>
    private const string StatusWriteLogTitle = "寫入狀態表";

    /// <summary>初始化時讀不懂狀態表那一列時 error.log 的標題（MonitorEngine.Status.cs）。</summary>
    private const string StatusLoadLogTitle = "讀取狀態表的刷新序號";

    /// <summary>時間不前進時連跑幾步，確認不會每一步都重試、都寫 error.log。</summary>
    private const int IdleSteps = 20;

    /// <summary>空轉檢查的上限：迴圈每醒來一次發布一份快照，正常情況下觀察期間最多醒來幾次（同 MonitorEngineReviewTests）。</summary>
    private const int MaxIdleSnapshots = 5;

    /// <summary>次級限流的 retry-after（秒）：全域暫停比一次心跳間隔長很多。</summary>
    private const int RetryAfterSeconds = 600;

    private static readonly DateTimeOffset Start = EngineHarness.Start;

    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly TimeSpan Heartbeat = Tuning.StatusHeartbeatInterval;

    private static readonly TimeSpan Tick = Tuning.EngineTick;

    /// <summary>背景工作的保險上限；正常情況下等待的條件都會立刻成立，不靠時間流逝。</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    /// <summary>觀察背景迴圈有沒有空轉的真實時間（同 MonitorEngineReviewTests）。</summary>
    private static readonly TimeSpan SpinObservation = TimeSpan.FromMilliseconds(200);

    /// <summary>等背景迴圈的條件成立時，每次檢查之間的真實時間。</summary>
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(10);

    // ── 寫入時機（§10.2）──────────────────────────────────────────────

    [Fact]
    public async Task 快照發布後寫入狀態表()
    {
        await using var h = CreateQuiet();

        await h.DrainAsync();

        var row = Row(h);
        Assert.Equal(Start, row.WrittenAt);
        Assert.Equal(Start, row.HeartbeatAt);
        Assert.Null(row.StoppedAt);
        Assert.Equal(EngineHarness.AppVersion, row.AppVersion);
        Assert.Equal((0L, 0L, false), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));

        var payload = Payload(row);
        Assert.Equal(EngineHarness.AppVersion, payload.AppVersion);
        Assert.Equal(Start, payload.SnapshotAt);
        Assert.Equal("success", payload.Overall);
        Assert.True(payload.SignedIn);
        Assert.False(payload.ApiVersionError);
        Assert.Equal("auto", payload.RefreshMode);
        Assert.Equal(new StatusCounts(Failing: 0, InProgress: 0), payload.Counts);
        Assert.Equal(1, payload.MonitoredRepos);
        Assert.Equal(0, payload.StaleRepos);
        Assert.Equal(new StatusConnection(Offline: false, PausedUntil: null, ServerErrors: false), payload.Connection);
        Assert.Equal(Start, payload.LastUpdatedAt);
        Assert.Empty(payload.Runs);
    }

    [Fact]
    public async Task 內容不變時每60秒仍更新心跳()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();

        await h.AdvanceInStepsAsync(Heartbeat, Tick);

        // 心跳也是整列重寫（spec §10.2）；內容沒變，snapshotAt 沿用上一次的。
        var row = Row(h);
        Assert.Equal(Start + Heartbeat, row.HeartbeatAt);
        Assert.Equal(Start + Heartbeat, row.WrittenAt);
        Assert.Equal(Start, Payload(row).SnapshotAt);
    }

    [Fact]
    public async Task 內容不變時60秒內不重寫()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        var before = h.GitHub.ApiRequests.Count;

        await h.AdvanceInStepsAsync(Heartbeat - Tick, Tick);

        // 期間照常輪詢（探測），每一步都發布快照，但內容沒變也還不到心跳的時間。
        Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(before));
        Assert.True(h.Last.LastUpdatedUtc > Start);
        var row = Row(h);
        Assert.Equal(Start, row.WrittenAt);
        Assert.Equal(Start, row.HeartbeatAt);
    }

    [Fact]
    public async Task 全域暫停期間內容不變時不重寫()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden,
            "You have exceeded a secondary rate limit. Please wait a few minutes before you try again.",
            ("retry-after", RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))), times: 1);

        await h.AdvanceAsync(Tuning.ProbeInterval);
        var pausedAt = Start + Tuning.ProbeInterval;
        var until = h.Last.Banner?.Until;
        Assert.Equal(pausedAt + TimeSpan.FromSeconds(RetryAfterSeconds), until);
        var row = Row(h);
        Assert.Equal(pausedAt, row.WrittenAt);
        Assert.Equal(until, Payload(row).Connection.PausedUntil);
        Assert.Equal(pausedAt, Payload(row).SnapshotAt);

        // pausedUntil 是暫停時記下的固定值，不是「現在 + 剩餘時間」：每一步的內容都一樣，60 秒內不重寫。
        // 牆鐘和單調時鐘不會一直同步（例如系統校時）：牆鐘多走 1 秒時，「現在 + 剩餘時間」就會變。
        h.Clock.Manual.SetUtcNow(h.Clock.GetUtcNow() + Tick);
        await h.AdvanceInStepsAsync(Heartbeat - Tick, Tick);
        Assert.Equal(pausedAt, Row(h).HeartbeatAt);

        // 心跳看單調時鐘（距上一次寫入 60 秒）；寫進去的時間是牆鐘，多了校時的那 1 秒。
        await h.AdvanceAsync(Tick);
        row = Row(h);
        Assert.Equal(pausedAt + Heartbeat + Tick, row.HeartbeatAt);
        Assert.Equal(pausedAt, Payload(row).SnapshotAt);
        Assert.Equal(until, Payload(row).Connection.PausedUntil);
    }

    [Fact]
    public async Task 自動更新暫停時心跳照樣寫()
    {
        await using var h = CreateQuiet(h => h.Settings.RefreshMode = RefreshMode.Paused);
        await h.DrainAsync();

        await h.AdvanceInStepsAsync(Heartbeat, Tick);

        // Engine 還在跑（spec §10.2）：心跳不看自動更新的設定。
        var row = Row(h);
        Assert.Equal(Start + Heartbeat, row.HeartbeatAt);
        Assert.Equal("paused", Payload(row).RefreshMode);
    }

    [Fact]
    public async Task 內容改變時立即寫並更新snapshotAt()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Start, Row(h).WrittenAt);

        var changedAt = Start.AddSeconds(10);
        h.Main.Runs.Insert(0, FakeData.Run(9002, 43, "completed", "failure", Start.AddSeconds(2), Start.AddSeconds(8)));
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        var row = Row(h);
        Assert.Equal(changedAt, row.WrittenAt);
        Assert.Equal(changedAt, row.HeartbeatAt);
        var payload = Payload(row);
        Assert.Equal(changedAt, payload.SnapshotAt);
        Assert.Equal("failure", payload.Overall);
        Assert.Equal(new StatusCounts(Failing: 1, InProgress: 0), payload.Counts);
    }

    [Fact]
    public async Task 寫入失敗後下一次整列重寫()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        MonitorEngineReviewTests.Execute(h.Workspace.Paths.CacheDbPath, BlockStatusWrites);

        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();
        Assert.Equal(1, FailureCount(h));
        Assert.Equal(1, Payload(Row(h)).MonitoredRepos);
        MonitorEngineReviewTests.Execute(h.Workspace.Paths.CacheDbPath, UnblockStatusWrites);

        // 失敗後等一個心跳間隔才重試（不是每一步都重試）。
        await h.AdvanceInStepsAsync(Heartbeat - Tick, Tick);
        Assert.Equal(1, Payload(Row(h)).MonitoredRepos);

        await h.AdvanceAsync(Tick);
        var retriedAt = Start + Heartbeat;
        var row = Row(h);
        Assert.Equal(retriedAt, row.WrittenAt);
        Assert.Equal(retriedAt, row.HeartbeatAt);
        Assert.Equal(0, Payload(row).MonitoredRepos);
        Assert.Equal("no_data", Payload(row).Overall);
        Assert.Equal(1, FailureCount(h));
    }

    [Fact]
    public async Task 寫入持續失敗時error_log不每步增加()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        MonitorEngineReviewTests.Execute(h.Workspace.Paths.CacheDbPath, BlockStatusWrites);
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));

        for (var step = 0; step < IdleSteps; step++)
        {
            await h.StepAsync();
        }

        Assert.Equal(1, FailureCount(h));
        await h.AdvanceInStepsAsync(Heartbeat - Tick, Tick);
        Assert.Equal(1, FailureCount(h));

        await h.AdvanceAsync(Tick);
        Assert.Equal(2, FailureCount(h));

        // 只寫 error.log、不影響輪詢（spec §10.2、§14）：沒有未預期錯誤的提示，探測照常送出。
        Assert.NotEqual(BannerKind.UnexpectedError, h.Last.Banner?.Kind);
        var count = h.GitHub.ApiRequests.Count;
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(count));
    }

    [Fact]
    public async Task 寫入一直失敗時背景迴圈不空轉()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        MonitorEngineReviewTests.Execute(h.Workspace.Paths.CacheDbPath, BlockStatusWrites);
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));

        // 時鐘不前進：失敗後等一個心跳間隔才重試，迴圈應該停下來等，不是一直空轉、每一步都重試。
        await h.Engine.StartAsync(None);
        await WaitUntilAsync(() => FailureCount(h) > 0);
        var before = h.Snapshots.Count;
        await Task.Delay(SpinObservation);

        Assert.True(h.Snapshots.Count - before <= MaxIdleSnapshots, $"背景迴圈空轉：發布了 {h.Snapshots.Count - before} 份快照");
        Assert.Equal(1, FailureCount(h));
    }

    [Fact]
    public async Task 執行中重建資料庫後整列重寫而且序號沿用()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.DrainAsync();
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        // 下一次寫入 repos 表（記下成功時間）時才讀到損毀，改名重建（同 MonitorEngineReviewTests）。
        await h.AdvanceAsync(Tuning.QuietMax);
        Assert.Equal(BannerKind.DatabaseRecovered, h.Last.Banner?.Kind);

        // 新的資料庫裡也有狀態表的資料列；序號沿用記憶體裡的值（不從空白的資料庫重讀成 0），正在等的命令列才等得到。
        var row = Row(h);
        Assert.Equal((1L, 1L, false), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
        Assert.Null(row.StoppedAt);
        Assert.Equal(1, Payload(row).MonitoredRepos);
    }

    [Fact]
    public async Task 寫狀態表時遇到損毀就改名重建並立即整列重寫()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "status_snapshot");

        // 內容改變，寫狀態表時才讀到損毀：交給下一步改名重建（spec §15），不是只寫 error.log 就等下一次心跳。
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();
        Assert.Contains(StatusWriteLogTitle, h.ErrorLogText, StringComparison.Ordinal);

        // 重建後不等寫入失敗後的重試間隔：新的資料庫在重建的那一步就有狀態表的資料列。
        await h.StepAsync();
        Assert.Equal(BannerKind.DatabaseRecovered, h.Last.Banner?.Kind);
        var row = Row(h);
        Assert.Equal(Start, row.WrittenAt);
        Assert.Null(row.StoppedAt);
    }

    [Fact]
    public async Task 結束時寫stopped_at而且下次啟動清掉()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        var stoppedAt = Start.AddSeconds(10);

        await h.Engine.DisposeAsync();

        var stopped = Row(h);
        Assert.Equal(stoppedAt, stopped.StoppedAt);
        Assert.Equal(stoppedAt, stopped.WrittenAt);
        Assert.Equal(Start, Payload(stopped).SnapshotAt);

        await h.RestartAsync();
        await h.StepAsync();

        Assert.Null(Row(h).StoppedAt);
    }

    [Fact]
    public async Task 狀態表的時間欄讀不懂時仍完成初始化並整列覆寫()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.DrainAsync();
        await h.RestartAsync();
        MonitorEngineReviewTests.Execute(h.Workspace.Paths.CacheDbPath, "UPDATE status_snapshot SET heartbeat_at = '不是時間';");
        var before = h.GitHub.ApiRequests.Count;

        // 讀不懂的那一列不能讓初始化一直退避重試：寫 error.log、序號當成 0，照常驗證帳號、輪詢。
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal("/user", h.GitHub.ApiRequests[before]);
        Assert.Contains(StatusLoadLogTitle, h.ErrorLogText, StringComparison.Ordinal);
        Assert.NotEqual(BannerKind.UnexpectedError, h.Last.Banner?.Kind);

        // 下一次寫入整列覆寫：讀得懂了，序號從 0 起算。
        var row = Row(h);
        Assert.Equal(Start, row.HeartbeatAt);
        Assert.Equal((0L, 0L, false), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
        Assert.Null(row.StoppedAt);
    }

    // ── 欄位（§10.3）──────────────────────────────────────────────────

    [Theory]
    [InlineData("completed", "failure", "failure")]
    [InlineData("in_progress", null, "in_progress")]
    [InlineData("completed", "success", "success")]
    public async Task overall照系統匣的整體狀態(string status, string? conclusion, string expected)
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, status, conclusion, Start.AddMinutes(-30), Start.AddMinutes(-20))));

        await h.DrainAsync();

        var payload = Payload(Row(h));
        Assert.Equal(expected, payload.Overall);
        Assert.Equal(new StatusCounts(h.Last.Summary.FailingCount, h.Last.Summary.PendingCount), payload.Counts);
    }

    [Fact]
    public async Task 只有灰色結論的鍵時overall是no_data()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "cancelled", Start.AddMinutes(-30), Start.AddMinutes(-20))));

        await h.DrainAsync();

        // 「全部成功」要至少一個鍵的 lastDecisive 是成功，命令列不自己從 counts 推（spec §10.3）。
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);
        var payload = Payload(Row(h));
        Assert.Equal("no_data", payload.Overall);
        Assert.Equal(new StatusCounts(Failing: 0, InProgress: 0), payload.Counts);
        Assert.Equal(1, payload.MonitoredRepos);
    }

    [Fact]
    public async Task 版本錯誤時apiVersionError是true()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Gone, "This API version is no longer supported."));

        await h.AdvanceAsync(Tuning.ProbeInterval);

        Assert.True(h.Last.Summary.Issues.HasFlag(ConnectionIssue.VersionError));
        var payload = Payload(Row(h));
        Assert.True(payload.ApiVersionError);
        Assert.Equal(new StatusConnection(Offline: false, PausedUntil: null, ServerErrors: false), payload.Connection);
    }

    [Fact]
    public async Task signedIn在剛啟動時是null()
    {
        await using var h = CreateQuiet(h => h.GitHub.NetworkDown = true);

        // 離線啟動：E1 還沒驗證完（spec §10.3）。
        await h.StepAsync();
        Assert.Equal(AuthStatus.Unknown, h.Last.Auth);
        Assert.Null(Payload(Row(h)).SignedIn);

        h.GitHub.NetworkDown = false;
        await h.AdvanceAsync(Tuning.OfflineProbeMax);
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
        Assert.True(Payload(Row(h)).SignedIn);
    }

    [Fact]
    public async Task lastUpdatedAt取監看中repo最近一次成功()
    {
        await using var h = CreateQuiet(h =>
        {
            var other = h.GitHub.AddRepo(FakeData.Repo(OtherRepoId, OtherRepoName, Start.AddDays(-2)));
            other.Runs.Add(FakeData.Run(9101, 7, "completed", "success", Start.AddDays(-1), Start.AddDays(-1),
                headRepoId: OtherRepoId, repo: OtherRepoName));
        });
        await h.DrainAsync();
        Assert.Equal(Start, Payload(Row(h)).LastUpdatedAt);

        // /user/repos 的探測、/rate_limit 等不屬於 repo 的成功不算：snapshot 的最後更新時間往前走，狀態表的不動。
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.Equal(Start + Tuning.ProbeInterval, h.Last.LastUpdatedUtc);
        Assert.Equal(Start, Payload(Row(h)).LastUpdatedAt);

        // 預設 repo 的 E4 開始失敗，另一個 repo 照常成功：取兩者最近的一次。
        h.GitHub.Override(EngineHarness.RunsPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"));
        await h.AdvanceInStepsAsync(Tuning.QuietMax, Tuning.ProbeInterval);
        var states = h.Cache.GetRepoStates();
        var otherSuccess = states[OtherRepoId].LastSuccessAt;
        Assert.Equal(Start, states[FakeData.RepoId].LastSuccessAt);
        Assert.True(otherSuccess > Start);
        Assert.Equal(otherSuccess, Payload(Row(h)).LastUpdatedAt);

        // 另一個 repo 靜音之後不再監看：它的成功時間不算。
        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();
        Assert.Equal(Start, Payload(Row(h)).LastUpdatedAt);
    }

    [Fact]
    public async Task runs只放需要注意的run而且中文標題照存()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9002, 43, "in_progress", null, Start.AddMinutes(-10), branch: UntrackedBranch));
            h.Main.Runs.Add(FakeData.Run(9003, 44, "completed", "success", Start.AddMinutes(-20), Start.AddMinutes(-15), branch: "feature/y"));
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-25), title: ChineseTitle));
        });

        await h.DrainAsync();

        // 同主清單的「只看需要注意」（P1 Q13）：追蹤中的失敗與未完成的 run，排序同主清單；完成的成功不放。
        var row = Row(h);
        Assert.All(row.StatusJson, c => Assert.True(c <= MaxAscii, $"非 ASCII 字元 U+{(int)c:X4}"));
        var payload = Payload(row);
        var attention = RunFilter.Apply(h.Last.Rows, RunFilterCriteria.Empty with { AttentionOnly = true });
        Assert.Equal(attention.Select(r => r.Run.Id), payload.Runs.Select(run => run.RunId));
        Assert.Equal([9002L, 9001L], payload.Runs.Select(run => run.RunId));

        var running = payload.Runs[0];
        Assert.Equal("pending", running.State);
        Assert.Equal("in_progress", running.Status);
        Assert.Null(running.Conclusion);
        Assert.Equal(UntrackedBranch, running.Branch);

        var failed = payload.Runs[1];
        Assert.Equal(new StatusRun(
            Repo: FakeData.RepoName,
            Workflow: FakeData.WorkflowName,
            RunId: 9001,
            RunNumber: 42,
            Attempt: 1,
            Title: ChineseTitle,
            Branch: "main",
            Event: "push",
            Status: "completed",
            Conclusion: "failure",
            State: "failure",
            Cancelling: false,
            StartedAt: Start.AddMinutes(-30),
            Url: "https://github.com/octo-org/sample-app/actions/runs/9001"), failed);
    }

    [Theory]
    [InlineData(RefreshMode.Auto, "auto")]
    [InlineData(RefreshMode.Seconds30, "30s")]
    [InlineData(RefreshMode.Minute1, "1m")]
    [InlineData(RefreshMode.Minutes5, "5m")]
    [InlineData(RefreshMode.Paused, "paused")]
    public async Task refreshMode用settings_json的寫法(RefreshMode mode, string expected)
    {
        await using var h = CreateQuiet(h => h.Settings.RefreshMode = mode);

        await h.DrainAsync();

        Assert.Equal(expected, Payload(Row(h)).RefreshMode);
    }

    [Theory]
    [InlineData(AuthStatus.Unknown, null)]
    [InlineData(AuthStatus.Ok, true)]
    [InlineData(AuthStatus.Unauthorized, false)]
    public void signedIn照認證狀態(AuthStatus auth, bool? expected)
    {
        var payload = MonitorEngine.BuildStatusPayload(Snapshot([]) with { Auth = auth }, EngineHarness.AppVersion, Start, null, null);

        Assert.Equal(expected, payload.SignedIn);
    }

    [Fact]
    public void 狀態表的欄位從快照轉換()
    {
        var pausedUntil = Start.AddMinutes(10);
        var lastSuccess = Start.AddMinutes(-1);
        var snapshot = Snapshot([]) with
        {
            Summary = new StatusSummary(OverallState.Pending, PendingCount: 2, FailingCount: 0, StaleRepoCount: 1,
                ConnectionIssue.ServerErrors | ConnectionIssue.VersionError | ConnectionIssue.RateLimited),
            Loading = new LoadingProgress(Completed: 3, Total: 5),
            Offline = true,
            RefreshMode = RefreshMode.Minutes5,
            LastUpdatedUtc = Start,
        };

        var payload = MonitorEngine.BuildStatusPayload(snapshot, EngineHarness.AppVersion, Start, pausedUntil, lastSuccess);

        Assert.Equal(EngineHarness.AppVersion, payload.AppVersion);
        Assert.Equal(Start, payload.SnapshotAt);
        Assert.Equal("in_progress", payload.Overall);
        Assert.True(payload.ApiVersionError);
        Assert.Equal("5m", payload.RefreshMode);
        Assert.Equal(new StatusCounts(Failing: 0, InProgress: 2), payload.Counts);
        Assert.Equal(5, payload.MonitoredRepos);
        Assert.Equal(1, payload.StaleRepos);
        Assert.Equal(new StatusConnection(Offline: true, PausedUntil: pausedUntil, ServerErrors: true), payload.Connection);

        // lastUpdatedAt 是呼叫端傳的監看中 repo 最近一次成功，不是 snapshot 的最後更新時間（會被 /user、/rate_limit 更新）。
        Assert.Equal(lastSuccess, payload.LastUpdatedAt);
    }

    [Fact]
    public void run的判定類別與取消中照快照轉換()
    {
        RunRow[] rows =
        [
            MainListRow(9001, "in_progress", null, trackedFailure: false),
            MainListRow(9002, "waiting", null, trackedFailure: false),
            MainListRow(9003, "completed", "failure", trackedFailure: true),
            MainListRow(9004, "completed", "cancelled", trackedFailure: true),
            MainListRow(9005, "completed", "success", trackedFailure: false),
        ];
        var snapshot = Snapshot(rows) with
        {
            Cancelling = new Dictionary<long, CancellingInfo> { [9001] = new(Start, ForceSent: false) }.ToFrozenDictionary(),
        };

        var payload = MonitorEngine.BuildStatusPayload(snapshot, EngineHarness.AppVersion, Start, null, null);

        // 灰色（取消後重跑前的那一次失敗仍算追蹤中的失敗）寫成 neutral；完成的成功不是「需要注意」，不放。
        Assert.Equal(
            [(9001L, "pending", true), (9002L, "waiting", false), (9003L, "failure", false), (9004L, "neutral", false)],
            payload.Runs.Select(run => (run.RunId, run.State, run.Cancelling)));
    }

    // ── 命令列刷新（§10.5）────────────────────────────────────────────

    [Fact]
    public async Task 命令列刷新時started加1()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        StatusSnapshotRow? atFirstRequest = null;
        h.GitHub.OnRequest = _ => atFirstRequest ??= h.Cache.GetStatusSnapshot();

        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.StepAsync();

        // 收到指令就立即寫（送出這次刷新的第一個請求之前）。
        Assert.NotNull(atFirstRequest);
        Assert.Equal((1L, 0L), (atFirstRequest.RefreshStartedSeq, atFirstRequest.RefreshDoneSeq));
        var row = Row(h);
        Assert.Equal((1L, 0L), (row.RefreshStartedSeq, row.RefreshDoneSeq));
    }

    [Fact]
    public async Task 收到命令列刷新時已離線立即完成並標partial()
    {
        await using var h = CreateQuiet();
        await GoOfflineAsync(h);
        var count = h.GitHub.Handler.Requests.Count;

        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.StepAsync();

        // 不做全量刷新：離線時只會排一次離線探測，探測的結果不能當成刷新完成（spec §10.5 第 1 點）。
        var row = Row(h);
        Assert.Equal((1L, 1L, true), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
        Assert.True(Payload(row).Connection.Offline);
        Assert.Equal(count, h.GitHub.Handler.Requests.Count);
    }

    [Fact]
    public async Task 收到時未登入立即完成並標partial()
    {
        await using var h = CreateQuiet(h =>
        {
            h.Tokens.Returns(EngineHarness.OtherFakeToken);
            h.GitHub.Override("/user", _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"));
        });
        await h.DrainAsync();
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);

        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.StepAsync();

        var row = Row(h);
        Assert.Equal((1L, 1L, true), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
        Assert.False(Payload(row).SignedIn);
    }

    [Fact]
    public async Task 收到時全域暫停中立即完成並標partial()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden,
            "You have exceeded a secondary rate limit. Please wait a few minutes before you try again.",
            ("retry-after", RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))), times: 1);
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.True(h.Last.Summary.Issues.HasFlag(ConnectionIssue.RateLimited));

        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.StepAsync();

        var row = Row(h);
        Assert.Equal((1L, 1L, true), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
    }

    [Fact]
    public async Task 收到時版本錯誤立即完成並標partial()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Gone, "This API version is no longer supported."));
        await h.AdvanceAsync(Tuning.ProbeInterval);

        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.StepAsync();

        var row = Row(h);
        Assert.Equal((1L, 1L, true), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
    }

    [Fact]
    public async Task 認證還在確認時照常刷新()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        await h.RestartAsync();
        var before = h.GitHub.ApiRequests.Count;
        StatusSnapshotRow? atVerify = null;
        h.GitHub.OnRequest = request =>
        {
            if (request.RequestUri!.AbsolutePath == "/user")
            {
                atVerify ??= h.Cache.GetStatusSnapshot();
            }
        };

        // 剛啟動、E1 還沒驗證完（認證狀態是 Unknown）就收到：不算被擋住，照常全量刷新。
        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.StepAsync();

        Assert.NotNull(atVerify);
        Assert.Equal((1L, 0L), (atVerify.RefreshStartedSeq, atVerify.RefreshDoneSeq));
        Assert.Null(Payload(atVerify).SignedIn);

        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.ProbeInterval);

        var row = Row(h);
        Assert.Equal((1L, 1L, false), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
        Assert.Contains(EngineHarness.RunsPath, h.GitHub.ApiRequests.Skip(before));
    }

    [Fact]
    public async Task 刷新的目標都處理完時done追上started()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        var runs = h.GitHub.Count(EngineHarness.RunsPath);
        var pulls = h.GitHub.Count(EngineHarness.PullsPath);

        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.DrainAsync();

        var row = Row(h);
        Assert.Equal((1L, 1L, false), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
        Assert.Equal(runs + 1, h.GitHub.Count(EngineHarness.RunsPath));
        Assert.Equal(pulls + 1, h.GitHub.Count(EngineHarness.PullsPath));
    }

    [Fact]
    public async Task 第一次刷新完成時done還小於第二個訊號的序號()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.StepAsync();
        var first = Row(h);
        Assert.Equal((1L, 0L), (first.RefreshStartedSeq, first.RefreshDoneSeq));

        // 第二個命令列這時讀到 s0 = 1、d0 = 0，要等 done ≥ max(s0, d0) + 1 = 2（spec §10.5 命令列端第 5 點）。
        h.Engine.Post(new EngineCommand.CliRefresh());
        var seen = new List<(long Started, long Done)>();
        for (var step = 0; step < IdleSteps; step++)
        {
            var sent = await h.StepAsync();
            var row = Row(h);
            seen.Add((row.RefreshStartedSeq, row.RefreshDoneSeq));
            if (sent == 0)
            {
                break;
            }
        }

        // 第一次刷新完成的那一步，第二次刷新已經開始：done = 1 < 2，第二個命令列不會拿第一次的結果當成自己的。
        var firstDone = seen.First(pair => pair.Done >= 1);
        Assert.Equal((2L, 1L), firstDone);
        Assert.Equal((2L, 2L), seen[^1]);
    }

    [Fact]
    public async Task 刷新途中進入全域暫停時結束並標partial()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.RunsPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden,
            "You have exceeded a secondary rate limit. Please wait a few minutes before you try again.",
            ("retry-after", RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))), times: 1);

        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.DrainAsync();

        var row = Row(h);
        Assert.Equal((1L, 1L, true), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
        Assert.NotNull(Payload(row).Connection.PausedUntil);
    }

    [Fact]
    public async Task 刷新目標被靜音移除也算處理完()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.StepAsync();
        Assert.Equal(0, Row(h).RefreshDoneSeq);
        var count = h.GitHub.ApiRequests.Count;

        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();

        // 已移出排程的目標算處理完（spec §10.5 第 4 點），不是被全域狀態擋住，也沒有超時：不標 partial。
        var row = Row(h);
        Assert.Equal((1L, 1L, false), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
        Assert.DoesNotContain(EngineHarness.RunsPath, h.GitHub.ApiRequests.Skip(count));
    }

    [Fact]
    public async Task 超過90秒強制收尾並標partial()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        h.GitHub.Override("/user", _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"));
        await h.RestartAsync();
        var before = h.GitHub.ApiRequests.Count;

        // E1 一直回 5xx：認證還在確認（不算被擋住），排好的目標卻一直送不出去。
        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.StepAsync();
        await h.AdvanceInStepsAsync(Tuning.RefreshCompletionMax - Tick, TimeSpan.FromSeconds(10));
        Assert.Equal((1L, 0L), (Row(h).RefreshStartedSeq, Row(h).RefreshDoneSeq));

        await h.AdvanceAsync(Tick);

        var row = Row(h);
        Assert.Equal((1L, 1L, true), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
        Assert.DoesNotContain(EngineHarness.RunsPath, h.GitHub.ApiRequests.Skip(before));
    }

    [Fact]
    public async Task 刷新等待中背景迴圈不空轉()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        h.GitHub.Override("/user", _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"));
        await h.RestartAsync();

        // 時鐘不前進：刷新的目標送不出去，迴圈要等到 E1 重試、心跳或刷新的上限，不是一直空轉。
        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.Engine.StartAsync(None);
        await WaitUntilAsync(() => h.Cache.GetStatusSnapshot()?.RefreshStartedSeq == 1);
        var before = h.Snapshots.Count;
        await Task.Delay(SpinObservation);

        Assert.True(h.Snapshots.Count - before <= MaxIdleSnapshots, $"背景迴圈空轉：發布了 {h.Snapshots.Count - before} 份快照");
        Assert.Equal(0, Row(h).RefreshDoneSeq);
    }

    [Fact]
    public async Task 刷新中再收到訊號會在結束後再做一次()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        var runs = h.GitHub.Count(EngineHarness.RunsPath);
        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.StepAsync();

        // 進行中又收到（不管幾個）：不合併進這一次、也不動 started，結束後再做一次（最多一次）。
        h.Engine.Post(new EngineCommand.CliRefresh());
        h.Engine.Post(new EngineCommand.CliRefresh());
        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.StepAsync();
        Assert.Equal(1, Row(h).RefreshStartedSeq);

        await h.DrainAsync();

        var row = Row(h);
        Assert.Equal((2L, 2L, false), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
        Assert.Equal(runs + 2, h.GitHub.Count(EngineHarness.RunsPath));
    }

    [Fact]
    public async Task 開始刷新後全量刷新丟例外時這一次立即結束並標partial()
    {
        const string injected = "測試注入的全量刷新例外";
        await using var h = CreateQuiet();
        await h.DrainAsync();
        var armed = 1;
        h.Clock.BeforeGetUtcNow = () =>
        {
            if (Volatile.Read(ref armed) == 1 && EngineClock.CalledFrom("FullRefresh") && Interlocked.Exchange(ref armed, 0) == 1)
            {
                throw new InvalidOperationException(injected);
            }
        };

        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.StepAsync();

        // started 已經加 1：done 立即追上並標 partial，正在等的命令列不必等到逾時；例外照常寫 error.log。
        Assert.Equal(0, Volatile.Read(ref armed));
        var row = Row(h);
        Assert.Equal((1L, 1L, true), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
        Assert.Contains(injected, h.ErrorLogText, StringComparison.Ordinal);

        // 之後的刷新照常。
        h.Clock.BeforeGetUtcNow = null;
        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.DrainAsync();
        row = Row(h);
        Assert.Equal((2L, 2L, false), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
    }

    [Fact]
    public async Task 重新啟動後序號接續()
    {
        await using var h = CreateQuiet();
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.DrainAsync();
        Assert.Equal((1L, 1L), (Row(h).RefreshStartedSeq, Row(h).RefreshDoneSeq));

        await h.RestartAsync();
        await h.StepAsync();
        Assert.Equal((1L, 1L), (Row(h).RefreshStartedSeq, Row(h).RefreshDoneSeq));

        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.DrainAsync();

        var row = Row(h);
        Assert.Equal((2L, 2L, false), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
    }

    // ── 輔助 ───────────────────────────────────────────────────────────

    /// <summary>預設 repo 有一筆一天前成功的 run：安靜間隔是上限 300 秒，期間只有每 30 秒的探測，狀態表的內容不變。</summary>
    private static EngineHarness CreateQuiet(Action<EngineHarness>? arrange = null) =>
        EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            arrange?.Invoke(h);
        });

    private static StatusSnapshotRow Row(EngineHarness h)
    {
        var row = h.Cache.GetStatusSnapshot();
        Assert.NotNull(row);
        return row;
    }

    private static StatusPayload Payload(StatusSnapshotRow row)
    {
        var payload = StatusJson.Deserialize<StatusPayload>(row.StatusJson);
        Assert.NotNull(payload);
        return payload;
    }

    private static int FailureCount(EngineHarness h) => Regex.Matches(h.ErrorLogText, InjectedWriteFailure).Count;

    /// <summary>暖機後網路中斷：全量刷新的 3 個請求都是連線層錯誤，轉成離線（同 MonitorEngineTests）。</summary>
    private static async Task GoOfflineAsync(EngineHarness h)
    {
        await h.DrainAsync();
        h.GitHub.NetworkDown = true;
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.True(h.Last.Offline);
    }

    /// <summary>等背景迴圈讓 <paramref name="condition"/> 成立；超過 <see cref="Guard"/> 就失敗。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var guard = new CancellationTokenSource(Guard);
        while (!condition())
        {
            await Task.Delay(PollDelay, guard.Token);
        }
    }

    /// <summary>只有 <paramref name="rows"/> 的快照；其他欄位是空的或預設值。</summary>
    private static MonitorSnapshot Snapshot(IReadOnlyList<RunRow> rows) => new(
        rows,
        new StatusSummary(OverallState.NoData, PendingCount: 0, FailingCount: 0, StaleRepoCount: 0, ConnectionIssue.None),
        LastUpdatedUtc: null,
        RateLimit: null,
        Repos: [],
        Banner: null,
        new LoadingProgress(Completed: 0, Total: 0),
        AuthStatus.Ok,
        TokenSource.GhStored,
        TrayIssueLine: null,
        Offline: false,
        RefreshMode.Auto,
        WatchedRunIds: FrozenSet<long>.Empty,
        Eta: FrozenDictionary<long, EtaInfo>.Empty,
        CompletedDurations: FrozenDictionary<long, TimeSpan>.Empty,
        PullRequestNumbers: FrozenDictionary<long, IReadOnlyList<int>>.Empty);

    /// <summary>主清單上預設 repo 的一列；<paramref name="trackedFailure"/> 是主清單判斷的「追蹤中的失敗」。</summary>
    private static RunRow MainListRow(long id, string status, string? conclusion, bool trackedFailure)
    {
        var repo = new RepoInfo(FakeData.RepoId, FakeData.RepoName, "sample-app", FakeData.OrgId, FakeData.OrgLogin, "Organization",
            IsPrivate: false, IsFork: false, IsArchived: false, IsDisabled: false, "main", "https://github.com/" + FakeData.RepoName,
            Start.AddDays(-2), RepoCategory.Organization);
        var run = new RunInfo(id, FakeData.RepoId, FakeData.WorkflowId, FakeData.WorkflowName, FakeData.WorkflowPath, RunNumber: 1,
            RunAttempt: 1, "Fix login button", "push", status, conclusion, "main", FakeData.MainSha, FakeData.RepoId, FakeData.RepoName,
            "https://github.com/" + FakeData.RepoName, FakeData.MyId, FakeData.MyLogin, FakeData.MyLogin, [], Start, Start, Start,
            $"https://github.com/{FakeData.RepoName}/actions/runs/{id}");
        return new RunRow(run, repo, RunStateMapper.Map(run), FakeData.WorkflowName, trackedFailure);
    }
}
