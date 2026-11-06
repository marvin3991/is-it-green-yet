using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Engine;

public class MonitorEngineTests
{
    private const string Api = FakeGitHub.ApiBase;

    private static readonly DateTimeOffset Start = EngineHarness.Start;

    /// <summary>背景工作的保險上限；正常情況下等待的條件都會立刻成立，不靠時間流逝。</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static readonly CancellationToken None = CancellationToken.None;

    // ── 冷啟動與 ETag ─────────────────────────────────────────────────

    [Fact]
    public async Task 冷啟動依序送出user與repo清單與workflows與runs()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddHours(-1), Start.AddMinutes(-55))));

        var sent = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            sent.Add(await h.StepAsync());
        }

        // 每一步最多送一個請求；全部到期的都送完之後就停。
        Assert.Equal([1, 1, 1, 1, 1, 1], sent);
        Assert.Equal(0, await h.StepAsync());
        Assert.Equal(
            [
                "/user",
                "/rate_limit",
                EngineHarness.ListAPath,
                EngineHarness.WorkflowsPath,
                EngineHarness.RunsPath,
                EngineHarness.PullsPath,
            ],
            h.GitHub.ApiRequests);

        var snapshot = h.Last;
        Assert.Equal(AuthStatus.Ok, snapshot.Auth);
        Assert.Equal(new LoadingProgress(1, 1), snapshot.Loading);
        var repo = Assert.Single(snapshot.Repos);
        Assert.True(repo.Monitored);
        Assert.Equal(RepoCategory.Organization, repo.Repo.Category);
        Assert.Equal(9001, Assert.Single(snapshot.Rows).Run.Id);
        Assert.Equal(OverallState.Success, snapshot.Summary.State);
        Assert.Equal(Start, snapshot.LastUpdatedUtc);
        Assert.Equal(new AccountInfo(FakeData.MyId, FakeData.MyLogin), h.Cache.GetAccount());
    }

    [Fact]
    public async Task 第二輪帶ETag且304不改資料()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddHours(-1), Start.AddMinutes(-55))));
        await h.DrainAsync();

        var etag = h.Cache.Get(Api + EngineHarness.RunsPath)?.ETag;
        Assert.NotNull(etag);
        var runsBefore = JsonSerializer.Serialize(h.Cache.GetRuns(FakeData.RepoId));
        var rowsBefore = JsonSerializer.Serialize(h.Last.Rows);

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        var second = h.GitHub.Handler.Requests.Last(r => r.Uri.PathAndQuery == EngineHarness.RunsPath);
        Assert.Equal(etag, second.Header("If-None-Match"));
        Assert.Equal((int)HttpStatusCode.NotModified, h.GitHub.Responses.Last(r => r.PathAndQuery == EngineHarness.RunsPath).Status);
        Assert.Equal(runsBefore, JsonSerializer.Serialize(h.Cache.GetRuns(FakeData.RepoId)));
        Assert.Equal(rowsBefore, JsonSerializer.Serialize(h.Last.Rows));
        Assert.Equal(etag, h.Cache.Get(Api + EngineHarness.RunsPath)?.ETag);
    }

    [Fact]
    public async Task push探測發現pushed_at變動時立即輪詢該repo()
    {
        // run 與 push 都在一天前：安靜間隔是上限 300 秒，正常要到 t=300 才會再輪詢 runs。
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        var before = h.GitHub.ApiRequests.Count;

        h.Main.SetPushedAt(Start.AddSeconds(20));
        await h.AdvanceAsync(Tuning.ProbeInterval);

        var after = h.GitHub.ApiRequests.Skip(before).ToList();
        Assert.Equal(EngineHarness.ProbeAPath, after[0]);
        Assert.Equal(EngineHarness.RunsPath, after[1]);
        Assert.Contains(EngineHarness.WorkflowsPath, after);
        Assert.Contains(EngineHarness.PullsPath, after);
    }

    [Fact]
    public async Task run進行中時15秒輪詢完成後回到安靜間隔()
    {
        var running = FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(running));
        await h.DrainAsync();
        int Runs() => h.GitHub.Count(EngineHarness.RunsPath);
        var first = Runs();

        await h.AdvanceAsync(TimeSpan.FromSeconds(14));
        Assert.Equal(first, Runs());
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(first + 1, Runs());
        Assert.NotNull(h.Last.Eta.GetValueOrDefault(9001));

        FakeData.Complete(running, "success", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        Assert.Equal(first + 2, Runs());

        // 完成後回到安靜公式：剛有活動，下限 30 秒，不再是 15 秒。
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        Assert.Equal(first + 2, Runs());
        await h.AdvanceAsync(Tuning.QuietMin - Tuning.ActiveRepoInterval);
        Assert.Equal(first + 3, Runs());
    }

    // ── 通知 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 啟動前完成的失敗不通知()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20))));
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);
        await h.StepAsync();

        Assert.Empty(h.Toasts);
        // 時間界線之前的完成只記下來（suppressed），系統匣照樣變紅。
        Assert.True(h.Cache.Contains(9001, 1, "failure"));
        Assert.Equal(1, h.Last.Summary.FailingCount);
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
    }

    [Fact]
    public async Task 啟動後完成的追蹤失敗通知一次()
    {
        var running = FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(running));
        await h.DrainAsync();

        await h.AdvanceAsync(TimeSpan.FromSeconds(10));
        FakeData.Complete(running, "failure", h.Clock.GetUtcNow());
        await h.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(h.Toasts);

        await h.AdvanceAsync(Tuning.NotificationMergeWindow);
        var toast = Assert.Single(h.Toasts);
        Assert.Equal("失敗：octo-org/sample-app · CI #42", toast.Title);

        // 再刷新、再輪詢都不會重發。
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);
        Assert.Single(h.Toasts);
    }

    [Fact]
    public async Task E5補抓回的舊失敗不通知()
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));

        // 這筆失敗早已被擠出 E4 第 1 頁，只有 E5 查得到；它在啟動之前就完成，是補抓回來的舊結果（spec §9.3）。
        // 啟動之後才完成、第一次由補抓看到的失敗照常通知（MonitorEngineReviewTests）。
        h.Main.OlderRuns.Add(FakeData.Run(9050, 50, "completed", "failure", Start.AddMinutes(-40), Start.AddMinutes(-30)));
        await h.AdvanceAsync(Tuning.SeedRecheck);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        Assert.Equal(2, h.GitHub.Count(EngineHarness.SeedPath));
        Assert.Empty(h.Toasts);
        Assert.True(h.Cache.Contains(9050, 1, "failure"));
        Assert.Equal(1, h.Last.Summary.FailingCount);
    }

    // ── 認證 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task _401只重試一次後進入未登入並停止輪詢()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Tokens.Returns(EngineHarness.OtherFakeToken);
            h.GitHub.Override("/user", _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"));
        });

        Assert.Equal(2, await h.StepAsync());

        var requests = h.GitHub.Handler.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal("Bearer " + Fixture.FakeToken, requests[0].Header("Authorization"));
        Assert.Equal("Bearer " + EngineHarness.OtherFakeToken, requests[1].Header("Authorization"));
        // 啟動時一次、第一個 401 後一次；重送仍 401 就直接進入未登入，不再重取（spec §13.2 第 2 點）。
        Assert.Equal(2, h.Tokens.Calls);
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);
        Assert.Equal(BannerKind.Unauthorized, h.Last.Banner?.Kind);
        Assert.True(h.Last.Summary.Issues.HasFlag(ConnectionIssue.Unauthorized));
        Assert.Equal("需要重新登入", h.Last.TrayIssueLine);

        h.Engine.Post(new EngineCommand.RefreshAll());
        Assert.Equal(0, await h.StepAsync());
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(2, h.GitHub.Handler.Requests.Count);
    }

    [Fact]
    public async Task 未登入狀態不送任何API()
    {
        await using var h = EngineHarness.Create(h => h.Tokens.Returns((string?)null).Returns((string?)null), defaultToken: false);

        Assert.Equal(0, await h.StepAsync());
        h.Engine.Post(new EngineCommand.RefreshAll());
        h.Engine.Tick();
        Assert.Equal(0, await h.StepAsync());

        var copy = await h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None);
        Assert.False(copy.Success);
        var check = await h.Engine.CheckConnectionAsync(None);
        Assert.False(check.Ok);

        // 未登入期間每 AuthRecheckInterval 只在本機重新取得 token，不打 API（spec §13.2）。
        await h.AdvanceAsync(Tuning.AuthRecheckInterval);
        Assert.Equal(2, h.Tokens.Calls);

        Assert.Empty(h.GitHub.Handler.Requests);
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);
        Assert.Equal(BannerKind.Unauthorized, h.Last.Banner?.Kind);
        Assert.Equal("尚未登入 GitHub。請在終端機執行 gh auth login，或到設定貼上 PAT。", h.Last.Banner?.Text);
    }

    // ── 限流 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 主額度用完時全域暫停到reset()
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "API rate limit exceeded for user ID 1001.",
            ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-used", "5000"),
            ("x-ratelimit-reset", h.GitHub.ResetEpoch(TimeSpan.FromSeconds(600)).ToString(System.Globalization.CultureInfo.InvariantCulture))),
            times: 1);

        await h.AdvanceAsync(Tuning.ProbeInterval);
        var count = h.GitHub.Handler.Requests.Count;

        // 等待 = reset − Date + RateLimitResetMargin = 605 秒（spec §6.4 第 1 列）。
        var pause = TimeSpan.FromSeconds(600) + Tuning.RateLimitResetMargin;
        var banner = h.Last.Banner;
        Assert.Equal(BannerKind.PrimaryLimit, banner?.Kind);
        Assert.Equal(Start + Tuning.ProbeInterval + pause, banner?.Until);
        Assert.Equal("主額度用完，預計 10:10 恢復。", banner?.Text);
        Assert.True(h.Last.Summary.Issues.HasFlag(ConnectionIssue.RateLimited));
        Assert.Equal("被限流，預定 10:10 再試", h.Last.TrayIssueLine);

        await h.AdvanceInStepsAsync(pause - TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        Assert.Equal(count, h.GitHub.Handler.Requests.Count);

        await h.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.True(h.GitHub.Handler.Requests.Count > count);
        Assert.Null(h.Last.Banner);
    }

    [Fact]
    public async Task 次級限流依retry_after暫停()
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden,
            "You have exceeded a secondary rate limit. Please wait a few minutes before you try again.",
            ("retry-after", "120")), times: 1);

        await h.AdvanceAsync(Tuning.ProbeInterval);
        var count = h.GitHub.Handler.Requests.Count;
        Assert.Equal(BannerKind.SecondaryLimit, h.Last.Banner?.Kind);
        Assert.Equal("被 GitHub 限流，10:02 恢復。", h.Last.Banner?.Text);

        await h.AdvanceAsync(TimeSpan.FromSeconds(119));
        Assert.Equal(count, h.GitHub.Handler.Requests.Count);

        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.True(h.GitHub.Handler.Requests.Count > count);
        Assert.Equal(EngineHarness.ProbeAPath, h.GitHub.ApiRequests[count]);
    }

    // ── 全域暫停的上限（防禦性：沒有實際發生過，docs/spec-deviations.md）──

    /// <summary>error.log 裡那一行的固定文字（<see cref="EngineText.RateLimitWaitCappedLog"/>）。</summary>
    private const string PauseCappedLogMarker = "超過上限";

    /// <summary>epoch 能表示的最後一秒（9999-12-31T23:59:59Z）：解析得出來，加上現在的時間會超出 DateTimeOffset 的範圍。</summary>
    private const string LastRepresentableEpoch = "253402300799";

    /// <summary>retry-after 能解析的最大值（int.MaxValue 秒，約 68 年）。</summary>
    private const string LargestRetryAfter = "2147483647";

    [Fact]
    public async Task 主額度的reset異常大時暫停限制在上限_到期後再送()
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => PrimaryLimit(h, Epoch(h.GitHub.ResetEpoch(TimeSpan.FromDays(30)))), times: 1);

        await h.AdvanceAsync(Tuning.ProbeInterval);
        var count = h.GitHub.Handler.Requests.Count;

        var banner = h.Last.Banner;
        Assert.Equal(BannerKind.PrimaryLimit, banner?.Kind);
        Assert.Equal(Start + Tuning.ProbeInterval + Tuning.MaxRateLimitWait, banner?.Until);
        Assert.Equal("主額度用完，預計 11:10 恢復。", banner?.Text);
        Assert.Single(Regex.Matches(h.ErrorLogText, PauseCappedLogMarker));

        await h.AdvanceInStepsAsync(Tuning.MaxRateLimitWait - TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        Assert.Equal(count, h.GitHub.Handler.Requests.Count);

        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.True(h.GitHub.Handler.Requests.Count > count);
        Assert.Null(h.Last.Banner);
    }

    [Fact]
    public async Task reset是epoch上限時不丟例外_快照照常發布()
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => PrimaryLimit(h, LastRepresentableEpoch), times: 1);
        var snapshots = h.Snapshots.Count;

        await h.AdvanceAsync(Tuning.ProbeInterval);

        Assert.True(h.Snapshots.Count > snapshots);
        Assert.Equal(BannerKind.PrimaryLimit, h.Last.Banner?.Kind);
        Assert.Equal(Start + Tuning.ProbeInterval + Tuning.MaxRateLimitWait, h.Last.Banner?.Until);
        Assert.Single(Regex.Matches(h.ErrorLogText, PauseCappedLogMarker));
        Assert.DoesNotContain(nameof(ArgumentOutOfRangeException), h.ErrorLogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 次級限流的retry_after異常大時暫停限制在上限()
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden,
            "You have exceeded a secondary rate limit. Please wait a few minutes before you try again.",
            ("retry-after", LargestRetryAfter)), times: 1);

        await h.AdvanceAsync(Tuning.ProbeInterval);

        Assert.Equal(BannerKind.SecondaryLimit, h.Last.Banner?.Kind);
        Assert.Equal(Start + Tuning.ProbeInterval + Tuning.MaxRateLimitWait, h.Last.Banner?.Until);
        Assert.Equal("被 GitHub 限流，11:10 恢復。", h.Last.Banner?.Text);
        Assert.Single(Regex.Matches(h.ErrorLogText, PauseCappedLogMarker));
    }

    /// <summary>會改寫 Date 的中間設備：reset 正常、Date 舊，算出來一樣異常大。</summary>
    [Fact]
    public async Task 回應的Date過舊讓暫停異常大時也限制在上限()
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "API rate limit exceeded",
            ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-reset", Epoch(h.GitHub.ResetEpoch(TimeSpan.FromHours(1)))),
            ("date", (h.Clock.GetUtcNow() - TimeSpan.FromDays(3)).ToString("r", CultureInfo.InvariantCulture))), times: 1);

        await h.AdvanceAsync(Tuning.ProbeInterval);

        Assert.Equal(Start + Tuning.ProbeInterval + Tuning.MaxRateLimitWait, h.Last.Banner?.Until);
        Assert.Single(Regex.Matches(h.ErrorLogText, PauseCappedLogMarker));
    }

    /// <summary>60 天超過 Task.Delay 能接受的約 49.7 天，沒有上限時複製 log 會以未預期錯誤結束。</summary>
    [Fact]
    public async Task 複製log進行中遇到異常大的暫停時等到上限再繼續()
    {
        using var jobsSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20)));
            h.Main.Jobs[9001] = [FakeData.Job(7001, "build", "failure")];
            h.Main.Logs[7001] = FakeData.LogLines("build", 5);
            h.GitHub.OnRequest = request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/jobs", StringComparison.Ordinal))
                {
                    jobsSeen.Set();
                    release.Wait(Guard);
                }
            };
        });
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => PrimaryLimit(h, Epoch(h.GitHub.ResetEpoch(TimeSpan.FromDays(60)))), times: 1);

        // 複製 log 的 E8 卡在回應裡，這時輪詢收到異常大的 reset；E8 回來之後，E9 要等暫停結束才送。
        var copy = Task.Run(() => h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None));
        Assert.True(jobsSeen.Wait(Guard));
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.Equal(BannerKind.PrimaryLimit, h.Last.Banner?.Kind);
        release.Set();

        var early = await Task.WhenAny(copy, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(copy, early);
        Assert.DoesNotContain(h.GitHub.ApiRequests, path => path.EndsWith("/logs", StringComparison.Ordinal));

        h.Clock.Advance(Tuning.MaxRateLimitWait);
        var result = await copy.WaitAsync(Guard);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(nameof(ArgumentOutOfRangeException), h.ErrorLogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 正常的reset不受上限影響也不寫error_log()
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => PrimaryLimit(h, Epoch(h.GitHub.ResetEpoch(TimeSpan.FromHours(1)))), times: 1);

        await h.AdvanceAsync(Tuning.ProbeInterval);

        // 等待 = reset − Date + RateLimitResetMargin = 3605 秒（spec §6.4 第 1 列）。
        Assert.Equal(Start + Tuning.ProbeInterval + TimeSpan.FromSeconds(3605), h.Last.Banner?.Until);
        Assert.DoesNotContain(PauseCappedLogMarker, h.ErrorLogText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task 剛好等於上限時不算超過_多一秒才限制並寫error_log(int secondsOverLimit, bool logged)
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        var resetFromNow = Tuning.MaxRateLimitWait - Tuning.RateLimitResetMargin + TimeSpan.FromSeconds(secondsOverLimit);
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => PrimaryLimit(h, Epoch(h.GitHub.ResetEpoch(resetFromNow))), times: 1);

        await h.AdvanceAsync(Tuning.ProbeInterval);

        Assert.Equal(Start + Tuning.ProbeInterval + Tuning.MaxRateLimitWait, h.Last.Banner?.Until);
        Assert.Equal(logged, h.ErrorLogText.Contains(PauseCappedLogMarker, StringComparison.Ordinal));
    }

    private static HttpResponseMessage PrimaryLimit(EngineHarness h, string resetEpoch) => h.GitHub.Error(HttpStatusCode.Forbidden, "API rate limit exceeded",
        ("x-ratelimit-remaining", "0"),
        ("x-ratelimit-reset", resetEpoch));

    private static string Epoch(long seconds) => seconds.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task _60秒內3個repo無法分類的403觸發全域暫停()
    {
        string[] names = ["octo-org/sample-app", "octo-org/sample-api", "octo-org/sample-web"];
        await using var h = EngineHarness.Create(h =>
        {
            h.GitHub.AddRepo(FakeData.Repo(2003, names[1], Start.AddDays(-2)));
            h.GitHub.AddRepo(FakeData.Repo(2004, names[2], Start.AddDays(-2)));
        });
        await h.DrainAsync();

        foreach (var name in names)
        {
            h.GitHub.Override($"/repos/{name}/actions/runs?per_page=30", _ => h.GitHub.Error(HttpStatusCode.Forbidden,
                "Although you appear to have the correct authorization credentials, the organization has enabled OAuth App access restrictions."));
        }

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        var count = h.GitHub.Handler.Requests.Count;

        Assert.Equal(3, h.GitHub.Responses.Count(r => r.Status == (int)HttpStatusCode.Forbidden));
        Assert.Equal(BannerKind.SecondaryLimit, h.Last.Banner?.Kind);
        Assert.True(h.Last.Summary.Issues.HasFlag(ConnectionIssue.RateLimited));
        Assert.Contains("403", h.ErrorLogText);
        Assert.Contains("存取被拒", h.RepoRow(FakeData.RepoId).ProblemNote);

        await h.AdvanceAsync(Tuning.MinRateLimitWait - TimeSpan.FromSeconds(1));
        Assert.Equal(count, h.GitHub.Handler.Requests.Count);
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.True(h.GitHub.Handler.Requests.Count > count);
    }

    // ── 離線 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 連續3次網路錯誤轉離線並改用rate_limit探測()
    {
        await using var h = EngineHarness.Create();
        await GoOfflineAsync(h);

        Assert.True(h.Last.Offline);
        Assert.Equal(BannerKind.Offline, h.Last.Banner?.Kind);
        Assert.Equal("離線 · 最後更新 10:00:00", h.Last.Banner?.Text);
        Assert.True(h.Last.Summary.Issues.HasFlag(ConnectionIssue.Offline));
        var count = h.GitHub.Handler.Requests.Count;

        // 離線後只用 E10 探測，從 30 秒起倍增（spec §6.4）。
        await h.AdvanceAsync(Tuning.OfflineProbeInitial - TimeSpan.FromSeconds(1));
        Assert.Equal(count, h.GitHub.Handler.Requests.Count);
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(count + 1, h.GitHub.Handler.Requests.Count);

        await h.AdvanceAsync(Tuning.OfflineProbeInitial * 2 - TimeSpan.FromSeconds(1));
        Assert.Equal(count + 1, h.GitHub.Handler.Requests.Count);
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(count + 2, h.GitHub.Handler.Requests.Count);

        Assert.All(h.GitHub.ApiRequests.Skip(count), path => Assert.Equal("/rate_limit", path));
        Assert.True(h.Last.Offline);
    }

    [Fact]
    public async Task 恢復後全量刷新()
    {
        await using var h = EngineHarness.Create();
        await GoOfflineAsync(h);
        var count = h.GitHub.Handler.Requests.Count;

        h.GitHub.NetworkDown = false;
        await h.AdvanceAsync(Tuning.OfflineProbeInitial);

        var after = h.GitHub.ApiRequests.Skip(count).ToList();
        Assert.Equal("/rate_limit", after[0]);
        Assert.Contains(EngineHarness.ProbeAPath, after);
        Assert.Contains(EngineHarness.RunsPath, after);
        Assert.Contains(EngineHarness.PullsPath, after);
        Assert.False(h.Last.Offline);
        Assert.Null(h.Last.Banner);
    }

    // ── 單一目標的錯誤與 repo 的變化 ──────────────────────────────────

    [Fact]
    public async Task E7回403只停PR追蹤E4照常()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddMinutes(-30), Start.AddMinutes(-25)));
            h.Main.Runs.Add(FakeData.Run(9010, 43, "completed", "failure", Start.AddMinutes(-15), Start.AddMinutes(-10),
                branch: "feature/login", sha: FakeData.FeatureSha, eventName: "pull_request", pullRequests: [17]));
            h.Main.Pulls.Add(FakeData.Pull(17, "feature/login", FakeData.FeatureSha));
        });
        await h.DrainAsync();

        // 開啟中 PR 的分支是追蹤鍵：它的失敗讓系統匣變紅。
        Assert.Equal(1, h.Last.Summary.FailingCount);
        Assert.Equal(1, h.GitHub.Count(EngineHarness.PullsPath));

        h.GitHub.Override(EngineHarness.PullsPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden,
            "Resource not accessible by personal access token", ("X-Accepted-GitHub-Permissions", "pull_requests=read")));
        await h.AdvanceAsync(Tuning.PullListTtlActive);
        Assert.Equal(2, h.GitHub.Count(EngineHarness.PullsPath));

        // 只停 PR 追蹤：PR 鍵的狀態變成未知（不再算失敗），預設分支的成功照常亮綠燈，repo 仍在監看中。
        Assert.Equal(0, h.Last.Summary.FailingCount);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
        var row = h.RepoRow(FakeData.RepoId);
        Assert.True(row.Monitored);
        Assert.Contains("缺少 Pull requests 權限或無法存取", row.ProblemNote);
        Assert.False(h.Last.Summary.Issues.HasFlag(ConnectionIssue.RateLimited));

        // E4 照常輪詢；E7 每 PermissionRetryInterval 才再試一次。
        var runs = h.GitHub.Count(EngineHarness.RunsPath);
        await h.AdvanceAsync(Tuning.QuietMax);
        Assert.True(h.GitHub.Count(EngineHarness.RunsPath) > runs);
        Assert.Equal(2, h.GitHub.Count(EngineHarness.PullsPath));

        await h.AdvanceAsync(Tuning.PermissionRetryInterval - Tuning.QuietMax - TimeSpan.FromSeconds(1));
        Assert.Equal(2, h.GitHub.Count(EngineHarness.PullsPath));
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(3, h.GitHub.Count(EngineHarness.PullsPath));
    }

    [Fact]
    public async Task repo回404後清單仍有則每小時重試()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.RunsPath, _ => h.GitHub.Error(HttpStatusCode.NotFound, "Not Found"));
        var before = h.GitHub.ApiRequests.Count;

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        // 404 不立刻判定刪除，先重抓 repo 清單確認（spec §5.4）。
        var after = h.GitHub.ApiRequests.Skip(before).ToList();
        Assert.True(after.IndexOf(EngineHarness.ListAPath) > after.IndexOf(EngineHarness.RunsPath));
        var row = h.RepoRow(FakeData.RepoId);
        Assert.True(row.Monitored);
        Assert.Contains("Actions 端點回 404", row.ProblemNote);
        var runs = h.GitHub.Count(EngineHarness.RunsPath);

        await h.AdvanceInStepsAsync(Tuning.NotFoundRetryInterval - TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(600));
        Assert.Equal(runs, h.GitHub.Count(EngineHarness.RunsPath));
        await h.AdvanceAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(runs + 1, h.GitHub.Count(EngineHarness.RunsPath));
    }

    [Fact]
    public async Task 連續兩輪清單不見才移除()
    {
        RepoWorld api = null!;
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            api = h.GitHub.AddRepo(FakeData.Repo(2003, "octo-org/sample-api", Start.AddDays(-2)));
            api.Runs.Add(FakeData.Run(9101, 7, "completed", "success", Start.AddDays(-1), Start.AddDays(-1), repo: "octo-org/sample-api",
                headRepoId: 2003));
        });
        await h.DrainAsync();
        h.Main.Listed = false;

        await h.AdvanceAsync(Tuning.RepoListRefresh);
        Assert.Contains(h.Last.Repos, r => r.Repo.Id == FakeData.RepoId);
        Assert.Contains(h.Last.Rows, r => r.Run.Id == 9001);
        Assert.Equal(1, h.Cache.GetRepoStates()[FakeData.RepoId].MissingRounds);

        await h.AdvanceAsync(Tuning.RepoListRefresh);
        Assert.DoesNotContain(h.Last.Repos, r => r.Repo.Id == FakeData.RepoId);
        Assert.DoesNotContain(h.Last.Rows, r => r.Run.Id == 9001);
        Assert.DoesNotContain(h.Cache.GetRepos(), r => r.Id == FakeData.RepoId);
        Assert.Contains(h.Last.Rows, r => r.Run.Id == 9101);

        var mainRequests = h.GitHub.ApiRequests.Count(p => p.StartsWith("/repos/octo-org/sample-app/", StringComparison.Ordinal));
        await h.AdvanceAsync(Tuning.QuietMax);
        Assert.Equal(mainRequests, h.GitHub.ApiRequests.Count(p => p.StartsWith("/repos/octo-org/sample-app/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task 同id改名後用新名稱重組網址()
    {
        const string newName = "octo-org/sample-app-renamed";
        const string newRunsPath = "/repos/" + newName + "/actions/runs?per_page=30";
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start)));
        await h.DrainAsync();
        Assert.NotNull(h.Cache.Get(Api + EngineHarness.RunsPath));
        var before = h.GitHub.ApiRequests.Count;

        h.GitHub.Rename(FakeData.RepoName, newName);
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);

        // 舊網址回 301：照 Location 重送這一次，再重抓清單，用 id 對照出新名稱（spec §5.4）。
        var after = h.GitHub.ApiRequests.Skip(before).ToList();
        Assert.Equal(EngineHarness.RunsPath, after[0]);
        Assert.Equal(newRunsPath, after[1]);
        Assert.Contains(EngineHarness.ListAPath, after);
        Assert.Contains(newRunsPath, after.Skip(after.IndexOf(EngineHarness.ListAPath)));

        Assert.Null(h.Cache.Get(Api + EngineHarness.RunsPath));
        Assert.NotNull(h.Cache.Get(Api + newRunsPath));
        Assert.Equal(newName, h.RepoRow(FakeData.RepoId).Repo.FullName);
        Assert.Equal("https://github.com/" + newName, h.RepoRow(FakeData.RepoId).Repo.HtmlUrl);
        Assert.Equal(newName, Assert.Single(h.Last.Rows).Repo.FullName);
        Assert.Equal(newName, Assert.Single(h.Cache.GetRepos()).FullName);

        var oldCount = h.GitHub.Count(EngineHarness.RunsPath);
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        Assert.Equal(oldCount, h.GitHub.Count(EngineHarness.RunsPath));
    }

    // ── 鎖定、睡眠 ────────────────────────────────────────────────────

    [Fact]
    public async Task 鎖定時間隔至少300秒解鎖後全量刷新並彙總通知()
    {
        var running = FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(running));
        await h.DrainAsync();

        h.Engine.Post(new EngineCommand.SetLocked(true));
        Assert.Equal(0, await h.StepAsync());
        var count = h.GitHub.Handler.Requests.Count;

        await h.AdvanceInStepsAsync(Tuning.LockedMinInterval - Tuning.ActiveRepoInterval, Tuning.ActiveRepoInterval);
        Assert.Equal(count, h.GitHub.Handler.Requests.Count);

        FakeData.Complete(running, "failure", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        Assert.True(h.GitHub.Handler.Requests.Count > count);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);
        Assert.Empty(h.Toasts);

        var beforeUnlock = h.GitHub.ApiRequests.Count;
        h.Engine.Post(new EngineCommand.SetLocked(false));
        await h.DrainAsync();

        var after = h.GitHub.ApiRequests.Skip(beforeUnlock).ToList();
        Assert.Contains(EngineHarness.ProbeAPath, after);
        Assert.Contains(EngineHarness.RunsPath, after);
        Assert.Contains(EngineHarness.PullsPath, after);
        var toast = Assert.Single(h.Toasts);
        Assert.StartsWith("鎖定期間的變化", toast.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 單調時鐘跳超過120秒視為喚醒()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        var pulls = h.GitHub.Count(EngineHarness.PullsPath);

        h.Engine.Tick();
        h.Clock.Advance(Tuning.SleepGapThreshold - TimeSpan.FromSeconds(1));
        h.Engine.Tick();
        await h.DrainAsync();
        Assert.Equal(pulls, h.GitHub.Count(EngineHarness.PullsPath));

        h.Clock.Advance(Tuning.SleepGapThreshold + TimeSpan.FromSeconds(1));
        h.Engine.Tick();
        await h.DrainAsync();

        // PR 清單平常 30 分鐘才查一次；喚醒後的全量刷新讓它立即重查（spec §6.5、§6.6）。
        Assert.Equal(pulls + 1, h.GitHub.Count(EngineHarness.PullsPath));
    }

    // ── E6、靜音、標記 ────────────────────────────────────────────────

    [Fact]
    public async Task 不在第一頁的進行中run用E6以安靜間隔追蹤()
    {
        const string detailPath = "/repos/octo-org/sample-app/actions/runs/9100";
        var stuck = FakeData.Run(9100, 77, "in_progress", null, Start.AddMinutes(-1), Start, branch: "feature/x", sha: FakeData.FeatureSha);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(stuck));
        await h.DrainAsync();

        // 這筆 run 被擠出第 1 頁，但本地仍顯示進行中。
        h.Main.Runs.Clear();
        h.Main.OlderRuns.Add(stuck);
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        Assert.Equal(0, h.GitHub.Count(detailPath));
        Assert.Contains(h.Last.Rows, r => r.Run.Id == 9100);

        await h.AdvanceInStepsAsync(Tuning.QuietMax - Tuning.ActiveRepoInterval, Tuning.ActiveRepoInterval);
        Assert.Equal(0, h.GitHub.Count(detailPath));
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        Assert.Equal(1, h.GitHub.Count(detailPath));

        await h.AdvanceInStepsAsync(Tuning.QuietMax - Tuning.ActiveRepoInterval, Tuning.ActiveRepoInterval);
        Assert.Equal(1, h.GitHub.Count(detailPath));
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        Assert.Equal(2, h.GitHub.Count(detailPath));

        // 完成後就不再用 E6 追蹤。
        FakeData.Complete(stuck, "success", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.QuietMax);
        Assert.Equal(3, h.GitHub.Count(detailPath));
        await h.AdvanceAsync(Tuning.QuietMax * 2);
        Assert.Equal(3, h.GitHub.Count(detailPath));
        Assert.DoesNotContain(h.Last.Rows, r => r.Run.Id == 9100);
    }

    [Fact]
    public async Task 靜音後從清單與計數移除()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddHours(-2), Start.AddHours(-1))));
        await h.DrainAsync();
        Assert.Equal(1, h.Last.Summary.FailingCount);
        Assert.Contains(h.Last.Rows, r => r.Run.Id == 9001);

        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();

        Assert.Empty(h.Last.Rows);
        Assert.Equal(0, h.Last.Summary.FailingCount);
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);
        var row = h.RepoRow(FakeData.RepoId);
        Assert.False(row.Monitored);
        Assert.Equal(SkipReason.Muted, row.Reason);
        Assert.True(row.Override.Muted);
        Assert.True(h.Cache.GetOverrides()[FakeData.RepoId].Muted);

        var repoRequests = h.GitHub.ApiRequests.Count(p => p.StartsWith("/repos/", StringComparison.Ordinal));
        await h.AdvanceAsync(Tuning.QuietMax * 2);
        Assert.Equal(repoRequests, h.GitHub.ApiRequests.Count(p => p.StartsWith("/repos/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task 取消靜音後以操作時間為界線()
    {
        await using var h = EngineHarness.Create(h => h.Cache.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.DrainAsync();
        Assert.DoesNotContain(h.GitHub.ApiRequests, p => p.StartsWith("/repos/", StringComparison.Ordinal));

        // 靜音期間（啟動之後）完成的失敗。
        h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(3), Start.AddMinutes(5)));
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));

        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, RepoOverride.None));
        await h.DrainAsync();
        Assert.Contains(EngineHarness.RunsPath, h.GitHub.ApiRequests);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);
        Assert.Empty(h.Toasts);
        Assert.True(h.Cache.Contains(9001, 1, "failure"));

        // 取消靜音之後才完成的失敗照常通知。
        var unmutedAt = Start.AddMinutes(10);
        h.Main.Runs.Insert(0, FakeData.Run(9002, 43, "completed", "failure", unmutedAt.AddSeconds(30), unmutedAt.AddMinutes(1)));
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        var toast = Assert.Single(h.Toasts);
        Assert.Equal("失敗：octo-org/sample-app · CI #43", toast.Title);
    }

    // ── 使用者操作 ────────────────────────────────────────────────────

    [Fact]
    public async Task 複製失敗log只取failure與timed_out的job並截尾()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20)));
            h.Main.Jobs[9001] =
            [
                FakeData.Job(7001, "build", "failure"),
                FakeData.Job(7002, "lint", "success"),
                FakeData.Job(7003, "test", "timed_out"),
                FakeData.Job(7004, "deploy", "cancelled"),
            ];
            h.Main.Logs[7001] = FakeData.LogLines("build", 300);
            h.Main.Logs[7002] = FakeData.LogLines("lint", 10);
            h.Main.Logs[7003] = FakeData.LogLines("test", 300);
            h.Main.Logs[7004] = FakeData.LogLines("deploy", 10);
        });
        await h.DrainAsync();
        var before = h.GitHub.ApiRequests.Count;

        var result = await h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None);

        Assert.True(result.Success, result.Message);
        var lines = result.Text!.Split('\n');
        Assert.Equal("=== build（失敗）===", lines[0]);
        Assert.Equal(h.Settings.LogTailLines, lines.Count(l => l.StartsWith("build line ", StringComparison.Ordinal)));
        Assert.Contains("build line 300", lines);
        Assert.Contains("build line 101", lines);
        Assert.DoesNotContain("build line 100", lines);
        Assert.Contains("=== test（失敗（逾時））===", lines);
        Assert.Equal(h.Settings.LogTailLines, lines.Count(l => l.StartsWith("test line ", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, l => l.StartsWith("lint", StringComparison.Ordinal) || l.StartsWith("deploy", StringComparison.Ordinal));
        Assert.DoesNotContain(FakeGitHub.LogSignature, result.Text, StringComparison.Ordinal);

        Assert.Equal(
            [
                "/repos/octo-org/sample-app/actions/runs/9001/jobs?filter=latest&per_page=100",
                "/repos/octo-org/sample-app/actions/jobs/7001/logs",
                "/repos/octo-org/sample-app/actions/jobs/7003/logs",
            ],
            h.GitHub.ApiRequests.Skip(before));
    }

    [Fact]
    public async Task E9的版本錯誤只顯示訊息不停止輪詢()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20)));
            h.Main.Jobs[9001] = [FakeData.Job(7001, "build", "failure")];
            h.Main.Logs[7001] = FakeData.LogLines("build", 5);
            h.GitHub.Override("/repos/octo-org/sample-app/actions/jobs/7001/logs",
                _ => h.GitHub.Error(HttpStatusCode.Gone, "This API version is no longer supported."));
        });
        await h.DrainAsync();

        var result = await h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None);
        Assert.False(result.Success);
        Assert.Contains("拿不到這個 job 的 log（狀態碼 410）", result.Message, StringComparison.Ordinal);

        // 使用者操作的 E9 錯誤不進全域流程：沒有版本錯誤橫幅，輪詢照常。
        await h.StepAsync();
        Assert.NotEqual(BannerKind.VersionError, h.Last.Banner?.Kind);
        Assert.False(h.Last.Summary.Issues.HasFlag(ConnectionIssue.VersionError));
        var count = h.GitHub.ApiRequests.Count;
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(count));
    }

    [Fact]
    public async Task 使用者操作與輪詢共用節流器()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20))));

        // 同一刻把每分鐘上限用完。
        for (var i = 0; i < Tuning.MaxRequestsPerMinute; i++)
        {
            h.Engine.Post(new EngineCommand.RefreshAll());
            if (await h.DrainAsync() == 0)
            {
                break;
            }
        }

        Assert.Equal(Tuning.MaxRequestsPerMinute, h.GitHub.Handler.Requests.Count);

        var copy = h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None);
        Assert.False(copy.IsCompleted);
        Assert.Equal(0, await h.StepAsync());
        Assert.Equal(Tuning.MaxRequestsPerMinute, h.GitHub.Handler.Requests.Count);

        // 視窗空出來、但使用者操作的計時器還沒觸發：輪詢讓路給排隊中的使用者操作（spec §6.3 第 1 級）。
        h.Clock.Manual.Advance(Tuning.RequestWindow);
        Assert.Equal(0, await h.StepAsync());
        Assert.Equal(Tuning.MaxRequestsPerMinute, h.GitHub.Handler.Requests.Count);

        h.Clock.Advance(TimeSpan.Zero);
        var result = await copy.WaitAsync(Guard);

        Assert.Equal("這次 run 沒有失敗的 job log 可複製", result.Message);
        Assert.Equal("/repos/octo-org/sample-app/actions/runs/9001/jobs?filter=latest&per_page=100",
            h.GitHub.ApiRequests[Tuning.MaxRequestsPerMinute]);

        // 使用者操作送完，輪詢接著送。
        Assert.Equal(1, await h.StepAsync());
    }

    [Fact]
    public async Task 檢查連線顯示看得到的repo數與私有數()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.GitHub.AddRepo(FakeData.Repo(2005, "alice/private-notes", Start.AddDays(-200), ownerId: FakeData.MyId, ownerType: "User",
                isPrivate: true));
            h.GitHub.AddRepo(FakeData.Repo(2006, "carol/shared-tool", Start.AddDays(-3), ownerId: 4001, ownerType: "User"), collaborator: true);
        });
        await h.DrainAsync();

        var result = await h.Engine.CheckConnectionAsync(None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(FakeData.MyLogin, result.Login);
        Assert.Equal(3, result.VisibleRepos);
        Assert.Equal(1, result.PrivateRepos);
        Assert.Equal("看得到 3 個 repo（其中私有 1 個）", result.Message);
    }

    // ── 迴圈的健壯性 ──────────────────────────────────────────────────

    [Fact]
    public async Task 迴圈內的例外寫入error_log且迴圈繼續()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.PullsPath, _ => throw new InvalidOperationException("測試注入的例外"), times: 1);

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Contains("測試注入的例外", h.ErrorLogText);
        Assert.Equal(BannerKind.UnexpectedError, h.Last.Banner?.Kind);
        Assert.Equal("發生未預期的錯誤，已記錄到 error.log。", h.Last.Banner?.Text);

        // 迴圈沒有停：之後的輪詢照常送出。
        var count = h.GitHub.ApiRequests.Count;
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(count));
    }

    [Fact]
    public async Task 快照事件處理丟例外不影響迴圈()
    {
        var received = 0;
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        h.Engine.SnapshotPublished += _ => throw new InvalidOperationException("快照事件處理失敗");
        h.Engine.SnapshotPublished += _ => Interlocked.Increment(ref received);
        h.Engine.ToastReady += _ => throw new InvalidOperationException("提示事件處理失敗");

        var sent = await h.DrainAsync();

        Assert.Equal(6, sent);
        Assert.True(Volatile.Read(ref received) >= sent);
        Assert.Contains("快照事件處理失敗", h.ErrorLogText);
        var count = h.GitHub.ApiRequests.Count;
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.True(h.GitHub.ApiRequests.Count > count);
    }

    [Fact]
    public async Task DisposeAsync停止迴圈()
    {
        var pullsSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            h.GitHub.OnRequest = request =>
            {
                if (request.RequestUri!.PathAndQuery == EngineHarness.PullsPath)
                {
                    pullsSeen.TrySetResult();
                }
                else if (request.RequestUri.PathAndQuery == EngineHarness.ProbeAPath)
                {
                    probeSeen.TrySetResult();
                }
            };
        });

        await h.Engine.StartAsync(None);
        await pullsSeen.Task.WaitAsync(Guard);

        // 迴圈閒置時，指令會把它喚醒。
        h.Engine.Post(new EngineCommand.RefreshAll());
        await probeSeen.Task.WaitAsync(Guard);

        await h.Engine.DisposeAsync();
        var count = h.GitHub.Handler.Requests.Count;

        h.Engine.Post(new EngineCommand.RefreshAll());
        h.Engine.Tick();
        h.Clock.Advance(TimeSpan.FromHours(1));
        h.Engine.Tick();

        Assert.Equal(count, h.GitHub.Handler.Requests.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Engine.RunOnceAsync(None));
    }

    [Fact]
    public async Task api_log與快照都不含token()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Tokens.Returns(EngineHarness.OtherFakeToken);
            h.GitHub.Override("/user", _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"), times: 1);
            h.GitHub.Override(EngineHarness.PullsPath,
                _ => throw new InvalidOperationException("Authorization: Bearer " + EngineHarness.OtherFakeToken), times: 1);
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20)));
            h.Main.Jobs[9001] = [FakeData.Job(7001, "build", "failure")];
            h.Main.Logs[7001] = FakeData.LogLines("build", 5);
        });

        await h.DrainAsync();
        var copy = await h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None);
        Assert.True(copy.Success, copy.Message);
        await h.StepAsync();

        var apiLog = h.ApiLogText;
        var errorLog = h.ErrorLogText;
        Assert.Contains("401", apiLog);
        Assert.Contains(FakeGitHub.LogHost + "/jobs/7001?***", apiLog);
        Assert.Contains("InvalidOperationException", errorLog);

        string[] secrets = [Fixture.FakeToken, EngineHarness.OtherFakeToken, FakeGitHub.LogSignature];
        var snapshots = JsonSerializer.Serialize(h.Snapshots);
        var toasts = JsonSerializer.Serialize(h.Toasts);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, apiLog, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, errorLog, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, snapshots, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, toasts, StringComparison.Ordinal);
        }
    }

    /// <summary>暖機後網路中斷：全量刷新的 3 個請求都是連線層錯誤，轉成離線。</summary>
    private static async Task GoOfflineAsync(EngineHarness h)
    {
        await h.DrainAsync();
        var before = h.GitHub.Handler.Requests.Count;
        h.GitHub.NetworkDown = true;
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal(Tuning.OfflineThreshold, h.GitHub.Handler.Requests.Count - before);
    }
}
