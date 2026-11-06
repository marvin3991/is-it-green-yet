using System.Globalization;
using System.Text.Json.Nodes;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>
/// 算成失敗的鍵，最後結論那一組裡最新的一筆失敗不在最近一次 E4 第 1 頁時，每 <see cref="Tuning.FailingRunRecheck"/> 用 E6 重查一次
/// （規格沒有這一項，見 docs/spec-deviations.md）：在 GitHub 上被刪掉的失敗 run 只有 E6 會回 404，
/// 不重查的話它一直是那個鍵的最後結論，系統匣一直是紅色。
/// </summary>
public class MonitorEngineFailingRecheckTests
{
    private const long SuccessRunId = 9001;
    private const long FailedRunId = 9002;
    private const string RunPathPrefix = "/repos/octo-org/sample-app/actions/runs/";

    /// <summary>較早那次成功的 commit，和失敗那次（<see cref="FakeData.MainSha"/>）分在不同組。</summary>
    private const string OlderSha = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c";

    /// <summary>成功與最新的失敗之間，另一次失敗的 commit。</summary>
    private const string MiddleSha = "5a4b3c2d1e0f5a4b3c2d1e0f5a4b3c2d1e0f5a4b";

    private const int PullNumber = 7;
    private const string FeatureBranch = "feature/login";

    /// <summary>每一步推進一個 QuietMax：安靜的 repo 每一步至少送一次 E4。</summary>
    private static readonly TimeSpan Step = Tuning.QuietMax;

    /// <summary>
    /// 改動 GitHub 上的資料之後，第一次重查一定已經送出的時間：下一次 E4（QuietMax 內）看到它不在第 1 頁，再過一個重查間隔；
    /// 多留一個 QuietMax 的餘裕。第二次重查最早在改動之後兩個重查間隔，不會落在這段時間裡。
    /// </summary>
    private static readonly TimeSpan FirstRecheck = Tuning.FailingRunRecheck + Tuning.QuietMax * 2;

    private static readonly DateTimeOffset Start = EngineHarness.Start;

    [Fact]
    public async Task 預設分支上被刪掉的失敗run查到404後不再算失敗()
    {
        var failed = FailedRun();
        await using var h = await StartFailingAsync(failed);

        h.Main.Runs.Remove(failed);
        await h.AdvanceInStepsAsync(FirstRecheck, Step);

        Assert.Equal(1, h.GitHub.Count(RunPath(FailedRunId)));
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
        Assert.Equal(0, h.Last.Summary.FailingCount);
        Assert.DoesNotContain(h.Last.Rows, row => row.Run.Id == FailedRunId);

        // 404 之後不再重查。
        await h.AdvanceInStepsAsync(Tuning.FailingRunRecheck * 2, Step);
        Assert.Equal(1, h.GitHub.Count(RunPath(FailedRunId)));
    }

    [Fact]
    public async Task PR分支上被刪掉的失敗run查到404後不再算失敗()
    {
        const long prRunId = 9003;
        var failed = FakeData.Run(prRunId, 5, "completed", "failure", Start.AddHours(-1), Start.AddHours(-1),
            branch: FeatureBranch, sha: FakeData.FeatureSha, eventName: "pull_request", pullRequests: [PullNumber]);
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(SuccessRun(FakeData.MainSha));
            h.Main.Runs.Add(failed);
            h.Main.Pulls.Add(FakeData.Pull(PullNumber, FeatureBranch, FakeData.FeatureSha));
        });
        await h.DrainAsync();
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);

        h.Main.Runs.Remove(failed);
        await h.AdvanceInStepsAsync(FirstRecheck, Step);

        Assert.Equal(1, h.GitHub.Count(RunPath(prRunId)));
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
        Assert.DoesNotContain(h.Last.Rows, row => row.Run.Id == prRunId);
    }

    /// <summary>同一個鍵連續兩次失敗都刪掉：較新的查到 404 之後，換上來的較舊那筆立刻查，不再多等一個重查間隔。</summary>
    [Fact]
    public async Task 連續刪掉兩次失敗時下一筆立刻查()
    {
        const long olderFailedId = 9004;
        var olderFailed = FakeData.Run(olderFailedId, 41, "completed", "failure", Start.AddMinutes(-90), Start.AddMinutes(-90), sha: MiddleSha);
        var failed = FailedRun();
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(SuccessRun(OlderSha));
            h.Main.Runs.Add(olderFailed);
            h.Main.Runs.Add(failed);
        });
        await h.DrainAsync();
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);

        h.Main.Runs.Remove(failed);
        h.Main.Runs.Remove(olderFailed);
        await h.AdvanceInStepsAsync(FirstRecheck, Step);

        Assert.Equal(1, h.GitHub.Count(RunPath(FailedRunId)));
        Assert.Equal(1, h.GitHub.Count(RunPath(olderFailedId)));
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>
    /// 同一個 commit 的一組裡有成功也有兩次失敗：只查最新的那筆失敗，成功的不查；它刪掉之後換同一組較舊的失敗立刻查，
    /// 那筆還在，這一組仍算失敗。
    /// </summary>
    [Fact]
    public async Task 同一組只查最新的一筆失敗刪掉後換下一筆()
    {
        const long successSameShaId = 9005;
        const long newestFailedId = 9006;
        var failed = FailedRun();
        var successSameSha = FakeData.Run(successSameShaId, 43, "completed", "success", Start.AddMinutes(-50), Start.AddMinutes(-50));
        var newestFailed = FakeData.Run(newestFailedId, 44, "completed", "failure", Start.AddMinutes(-40), Start.AddMinutes(-40));
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(SuccessRun(OlderSha));
            h.Main.Runs.Add(failed);
            h.Main.Runs.Add(successSameSha);
            h.Main.Runs.Add(newestFailed);
        });
        await h.DrainAsync();
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);

        foreach (var run in new[] { failed, successSameSha, newestFailed })
        {
            h.Main.Runs.Remove(run);
            h.Main.OlderRuns.Add(run);
        }

        await h.AdvanceInStepsAsync(FirstRecheck, Step);

        Assert.Equal(1, h.GitHub.Count(RunPath(newestFailedId)));
        Assert.Equal(0, h.GitHub.Count(RunPath(FailedRunId)));
        Assert.Equal(0, h.GitHub.Count(RunPath(successSameShaId)));

        h.Main.OlderRuns.Remove(newestFailed);
        await h.AdvanceInStepsAsync(Tuning.FailingRunRecheck, Step);

        Assert.Equal(2, h.GitHub.Count(RunPath(newestFailedId)));
        Assert.Equal(1, h.GitHub.Count(RunPath(FailedRunId)));
        Assert.Equal(0, h.GitHub.Count(RunPath(successSameShaId)));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Last.Rows, row => row.Run.Id == newestFailedId);
    }

    /// <summary>還在的失敗 run 只是被擠出第 1 頁：每小時查一次，不用 repo 的輪詢間隔，仍算失敗。</summary>
    [Fact]
    public async Task 不在第1頁但還在的失敗run每小時重查一次仍算失敗()
    {
        var failed = FailedRun();
        await using var h = await StartFailingAsync(failed);

        h.Main.Runs.Remove(failed);
        h.Main.OlderRuns.Add(failed);
        await h.AdvanceInStepsAsync(FirstRecheck, Step);

        Assert.Equal(1, h.GitHub.Count(RunPath(FailedRunId)));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);

        await h.AdvanceInStepsAsync(Tuning.FailingRunRecheck, Step);

        Assert.Equal(2, h.GitHub.Count(RunPath(FailedRunId)));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == FailedRunId);
    }

    /// <summary>
    /// 重查拿到的是同一個已完成的失敗（第一次 200、之後 304）：不再通知一次。擋下來的是 spec §9.1 的完成觀察
    /// （同一個 attempt 本地已經是完成，不算新的完成），還沒走到 §9.2 的去重。
    /// </summary>
    [Fact]
    public async Task 重查還在的失敗run不重複通知()
    {
        var failed = FakeData.Run(FailedRunId, 42, "in_progress", null, Start.AddMinutes(-1), Start);
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(SuccessRun(OlderSha));
            h.Main.Runs.Add(failed);
        });
        await h.DrainAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(10));
        FakeData.Complete(failed, "failure", h.Clock.GetUtcNow());
        await h.AdvanceAsync(TimeSpan.FromSeconds(5));
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);
        Assert.Single(h.Toasts);

        h.Main.Runs.Remove(failed);
        h.Main.OlderRuns.Add(failed);
        await h.AdvanceInStepsAsync(FirstRecheck + Tuning.FailingRunRecheck, Step);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        var path = RunPath(FailedRunId);
        Assert.Equal([200, 304], h.GitHub.Responses.Where(r => r.PathAndQuery == path).Select(r => r.Status));
        Assert.Single(h.Toasts);
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
    }

    /// <summary>E4 第 1 頁就看得到它還在，不另外查。</summary>
    [Fact]
    public async Task 在第1頁的失敗run不重查()
    {
        await using var h = await StartFailingAsync(FailedRun());

        await h.AdvanceInStepsAsync(Tuning.FailingRunRecheck * 3, Step);

        Assert.Equal(0, h.GitHub.Count(RunPath(FailedRunId)));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
    }

    /// <summary>只重查算成失敗的鍵：最後結論是成功的鍵就算不在第 1 頁也不查。</summary>
    [Fact]
    public async Task 成功的鍵不在第1頁也不重查()
    {
        var success = SuccessRun(OlderSha);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(success));
        await h.DrainAsync();
        Assert.Equal(OverallState.Success, h.Last.Summary.State);

        h.Main.Runs.Remove(success);
        h.Main.OlderRuns.Add(success);
        await h.AdvanceInStepsAsync(Tuning.FailingRunRecheck * 3, Step);

        Assert.Equal(0, h.GitHub.Count(RunPath(SuccessRunId)));
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>超過 TrackedFailureMaxAgeDays 的失敗已經不算失敗（Q6），不在第 1 頁也不查。</summary>
    [Fact]
    public async Task 超過90天的失敗不重查()
    {
        var old = Start.AddDays(-(Tuning.TrackedFailureMaxAgeDays + 1));
        var failed = FakeData.Run(FailedRunId, 42, "completed", "failure", old, old);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(failed));
        await h.DrainAsync();
        Assert.Equal(0, h.Last.Summary.FailingCount);

        h.Main.Runs.Remove(failed);
        h.Main.OlderRuns.Add(failed);
        await h.AdvanceInStepsAsync(Tuning.FailingRunRecheck * 3, Step);

        Assert.Equal(0, h.GitHub.Count(RunPath(FailedRunId)));
        Assert.Equal(0, h.Last.Summary.FailingCount);
    }

    /// <summary>自動更新暫停時不自動送任何輪詢（spec §6.2），重查也不送。</summary>
    [Fact]
    public async Task 暫停自動更新時不重查()
    {
        var failed = FailedRun();
        await using var h = EngineHarness.Create(h =>
        {
            h.Settings.RefreshMode = RefreshMode.Paused;
            h.Main.Runs.Add(SuccessRun(OlderSha));
            h.Main.Runs.Add(failed);
        });
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);

        h.Main.Runs.Remove(failed);
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        await h.AdvanceInStepsAsync(Tuning.FailingRunRecheck * 3, Step);

        Assert.Equal(0, h.GitHub.Count(RunPath(FailedRunId)));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
    }

    /// <summary>預設分支上：較早的成功（第 1 頁）與較新的失敗，總狀態是失敗。</summary>
    private static async Task<EngineHarness> StartFailingAsync(JsonObject failed)
    {
        var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(SuccessRun(OlderSha));
            h.Main.Runs.Add(failed);
        });
        await h.DrainAsync();
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.Equal(1, h.Last.Summary.FailingCount);
        return h;
    }

    private static string RunPath(long runId) => RunPathPrefix + runId.ToString(CultureInfo.InvariantCulture);

    private static JsonObject SuccessRun(string sha) =>
        FakeData.Run(SuccessRunId, 40, "completed", "success", Start.AddHours(-2), Start.AddHours(-2), sha: sha);

    private static JsonObject FailedRun() =>
        FakeData.Run(FailedRunId, 42, "completed", "failure", Start.AddHours(-1), Start.AddHours(-1));
}
