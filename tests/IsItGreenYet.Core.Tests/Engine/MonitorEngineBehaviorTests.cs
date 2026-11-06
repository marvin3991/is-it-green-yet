using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>計畫列出的整合測試以外，其他 spec 行為的整合測試（重啟、標記、暫停、5xx、版本、stale repo、設定變更）。</summary>
public class MonitorEngineBehaviorTests
{
    private const string Api = FakeGitHub.ApiBase;

    private static readonly DateTimeOffset Start = EngineHarness.Start;

    [Fact]
    public async Task 重啟後先發布快取快照且不重發通知()
    {
        var running = FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(running));
        await h.DrainAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(10));
        FakeData.Complete(running, "failure", h.Clock.GetUtcNow());
        await h.AdvanceAsync(TimeSpan.FromSeconds(5));
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);
        Assert.Single(h.Toasts);

        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        await h.RestartAsync();
        var requests = h.GitHub.Handler.Requests.Count;
        var snapshots = h.Snapshots.Count;

        // 第一步先讀 DB 發布快取快照（spec §3.1），之後才送 E1。
        await h.StepAsync();
        var cached = h.Snapshots[snapshots];
        Assert.Equal(9001, Assert.Single(cached.Rows).Run.Id);
        Assert.Equal(1, cached.Summary.FailingCount);
        Assert.Equal(requests, h.GitHub.Handler.Requests.Count - 1);

        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);
        Assert.Single(h.Toasts);
        Assert.Equal((int)HttpStatusCode.NotModified, h.GitHub.Responses.Last(r => r.PathAndQuery == EngineHarness.RunsPath).Status);
    }

    [Fact]
    public async Task 抓完一輪repo清單後快照標示清單已抓過即使一個repo都沒有()
    {
        // 看得到 0 個 repo 的帳號：主畫面要靠這個旗標分辨「還在載入」與「真的沒有資料」（spec §10.11）。
        await using var h = EngineHarness.Create(h => h.GitHub.Owned.Clear());

        await h.StepAsync();
        Assert.False(h.Last.RepoListFetched);

        await h.DrainAsync();
        Assert.True(h.Last.RepoListFetched);
        Assert.Empty(h.Last.Repos);
        Assert.Equal(new LoadingProgress(0, 0), h.Last.Loading);
    }

    [Fact]
    public async Task 結束時還在佇列裡的靜音與完成時通知我照樣寫進資料庫()
    {
        // 剛按了靜音或「完成時通知我」就結束程式：迴圈來不及處理，使用者的選擇也不能遺失。
        var running = FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start);
        var done = FakeData.Run(9002, 41, "completed", "success", Start.AddMinutes(-30), Start.AddMinutes(-20));
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(running);
            h.Main.Runs.Add(done);
        });
        await h.DrainAsync();

        var muted = new RepoOverride(Muted: true, ForceIncluded: false);
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, muted));
        h.Engine.Post(new EngineCommand.SetWatched(9001, Watched: true));
        h.Engine.Post(new EngineCommand.SetWatched(9002, Watched: true));
        await h.Engine.DisposeAsync();

        Assert.Equal(muted, h.Cache.GetOverrides()[FakeData.RepoId]);
        Assert.Contains(9001L, h.Cache.GetWatched());

        // 已經完成的 run 照平常的規則不接受標記，結束時也不寫（不會留下永遠清不掉的標記）。
        Assert.DoesNotContain(9002L, h.Cache.GetWatched());
    }

    [Fact]
    public async Task 開啟repo管理頁時抓一次清單B並列出協作repo()
    {
        // spec §5.1：沒開「包含協作 repo」時，開啟 repo 管理頁仍要抓一次清單 B，才能列出所有看得到的 repo（§11.1）。
        const string ListBPath = "/user/repos?affiliation=collaborator&per_page=100";
        await using var h = EngineHarness.Create(h =>
            h.GitHub.AddRepo(FakeData.Repo(2006, "carol/shared-tool", Start.AddDays(-1), ownerId: 4001, ownerType: "User"),
                collaborator: true));
        await h.DrainAsync();
        Assert.DoesNotContain(ListBPath, h.GitHub.ApiRequests);
        Assert.DoesNotContain(h.Last.Repos, row => row.Repo.Id == 2006);

        h.Engine.Post(new EngineCommand.RepoManagerOpened());
        await h.DrainAsync();

        Assert.Single(h.GitHub.ApiRequests, path => path == ListBPath);
        var shared = h.RepoRow(2006);
        Assert.Equal(RepoCategory.Collaborator, shared.Repo.Category);
        Assert.False(shared.Monitored);
        Assert.Equal(SkipReason.Collaborator, shared.Reason);
        Assert.True(shared.CanForceInclude);

        // 只抓一次：之後的定期完整重抓不再帶清單 B，而且協作 repo 不會因此被當成消失。
        await h.AdvanceAsync(Tuning.RepoListRefresh + TimeSpan.FromMinutes(1));
        await h.AdvanceAsync(Tuning.RepoListRefresh + TimeSpan.FromMinutes(1));
        Assert.Single(h.GitHub.ApiRequests, path => path == ListBPath);
        Assert.Contains(h.Last.Repos, row => row.Repo.Id == 2006);
    }

    [Fact]
    public async Task 完整重抓進行中開啟repo管理頁時同一輪接著抓清單B()
    {
        const string ListBPath = "/user/repos?affiliation=collaborator&per_page=100";
        await using var h = EngineHarness.Create(h =>
            h.GitHub.AddRepo(FakeData.Repo(2006, "carol/shared-tool", Start.AddDays(-1), ownerId: 4001, ownerType: "User"),
                collaborator: true));

        // 送出第一個請求（E1）之前就開啟 repo 管理頁：啟動時的完整重抓還沒開始，指令要在它開始時帶上清單 B。
        h.Engine.Post(new EngineCommand.RepoManagerOpened());
        await h.DrainAsync();

        Assert.Single(h.GitHub.ApiRequests, path => path == ListBPath);
        Assert.Contains(h.Last.Repos, row => row.Repo.Id == 2006);
    }

    [Fact]
    public async Task 重啟後要重新抓完一輪repo清單才標示清單已抓過()
    {
        await using var h = EngineHarness.Create(h => h.GitHub.Owned.Clear());
        await h.DrainAsync();
        Assert.True(h.Last.RepoListFetched);

        await h.RestartAsync();
        await h.StepAsync();
        Assert.False(h.Last.RepoListFetched);

        await h.DrainAsync();
        Assert.True(h.Last.RepoListFetched);
    }

    [Fact]
    public async Task 多頁repo清單的每一頁權限都寫進資料庫()
    {
        // 清單 A 有兩頁：第 1 頁的 sample-app 能寫、第 2 頁的 sample-api 不能寫（第二階段 spec §4.1）。
        // 假 GitHub 的探測看不到 sample-api、也不帶 permissions，所以兩個值都只能來自完整重抓的分頁。
        const string secondPage = EngineHarness.ListAPath + "&page=2";
        await using var h = EngineHarness.Create(h =>
        {
            h.GitHub.AddRepo(FakeData.Repo(2003, "octo-org/sample-api", Start.AddDays(-3))).Listed = false;
            h.GitHub.Override(EngineHarness.ListAPath, _ => RepoListPage(Api + secondPage,
                FakeData.Repo(FakeData.RepoId, FakeData.RepoName, Start.AddDays(-2), push: true)));
            h.GitHub.Override(secondPage, _ => RepoListPage(null,
                FakeData.Repo(2003, "octo-org/sample-api", Start.AddDays(-3), push: false)));
        });

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(secondPage));
        Assert.Contains(h.Last.Repos, row => row.Repo.Id == 2003);
        Assert.Equal([(FakeData.RepoId, true), (2003L, false)], SortedPermissions(h, FakeData.MyId));
    }

    [Fact]
    public async Task 同一輪重複出現的repo沒有permissions時不蓋掉已讀到的值()
    {
        // 分頁途中排序變動，同一個 repo 在第 2 頁又出現一次、這次沒有 permissions：不確定，不蓋掉第 1 頁讀到的值。
        const string secondPage = EngineHarness.ListAPath + "&page=2";
        await using var h = EngineHarness.Create(h =>
        {
            h.GitHub.Override(EngineHarness.ListAPath, _ => RepoListPage(Api + secondPage,
                FakeData.Repo(FakeData.RepoId, FakeData.RepoName, Start.AddDays(-2), push: true)));
            h.GitHub.Override(secondPage, _ => RepoListPage(null, FakeData.Repo(FakeData.RepoId, FakeData.RepoName, Start.AddDays(-2))));
        });

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(secondPage));
        Assert.Equal([(FakeData.RepoId, true)], SortedPermissions(h, FakeData.MyId));
    }

    [Fact]
    public async Task push探測回應的權限也寫進資料庫()
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        Assert.Empty(h.Cache.GetRepoPermissions(FakeData.MyId));

        // 之後的回應才帶 permissions：完整重抓要一小時後，這個值只能來自探測（第二階段 spec §4.1）。
        h.Main.Repo["permissions"] = new JsonObject { ["push"] = false };
        await h.AdvanceAsync(Tuning.ProbeInterval);

        Assert.Equal(1, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.Contains(h.GitHub.Responses, response => response.PathAndQuery == EngineHarness.ProbeAPath && response.Status == (int)HttpStatusCode.OK);
        Assert.Equal([(FakeData.RepoId, false)], SortedPermissions(h, FakeData.MyId));
    }

    [Fact]
    public async Task 標記完成時通知我的run完成後通知並取消標記()
    {
        // 別人在功能分支上的 run：不在追蹤範圍，也不是我觸發的，只有 N3 會通知。
        var running = FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start, branch: "feature/x",
            sha: FakeData.FeatureSha, actorId: 4001, actorLogin: "carol");
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(running));
        await h.DrainAsync();

        h.Engine.Post(new EngineCommand.SetWatched(9001, Watched: true));
        await h.StepAsync();
        Assert.Contains(9001L, h.Last.WatchedRunIds);
        Assert.Contains(9001L, h.Cache.GetWatched());

        FakeData.Complete(running, "cancelled", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        var toast = Assert.Single(h.Toasts);
        Assert.Equal("取消：octo-org/sample-app · CI #42", toast.Title);
        Assert.Empty(h.Last.WatchedRunIds);
        Assert.Empty(h.Cache.GetWatched());
    }

    [Fact]
    public async Task 暫停自動更新時不排週期性輪詢但F5仍刷新一次()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Settings.RefreshMode = RefreshMode.Paused;
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddHours(-1), Start.AddHours(-1)));
        });
        await h.DrainAsync();
        Assert.Equal(["/user", "/rate_limit"], h.GitHub.ApiRequests);

        await h.AdvanceAsync(TimeSpan.FromHours(2));
        Assert.Equal(2, h.GitHub.ApiRequests.Count);
        Assert.Equal(RefreshMode.Paused, h.Last.RefreshMode);

        // F5：探測找到 repo、查 workflow 清單，接著送 runs 與 PR，按一次就刷新完（spec §6.2、§6.6）。
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.RunsPath));
        Assert.Equal(1, h.GitHub.Count(EngineHarness.PullsPath));
        Assert.Equal(new LoadingProgress(1, 1), h.Last.Loading);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);

        var count = h.GitHub.ApiRequests.Count;
        await h.AdvanceAsync(TimeSpan.FromHours(2));
        Assert.Equal(count, h.GitHub.ApiRequests.Count);

        // 恢復自動更新：立即全量刷新並排回週期性輪詢。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();
        Assert.Equal(2, h.GitHub.Count(EngineHarness.RunsPath));
        await h.AdvanceAsync(Tuning.QuietMax);
        Assert.Equal(3, h.GitHub.Count(EngineHarness.RunsPath));
    }

    [Fact]
    public async Task 固定刷新秒數取代安靜間隔()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Seconds30));
        await h.StepAsync();
        var runs = h.GitHub.Count(EngineHarness.RunsPath);

        // 安靜公式是 300 秒；固定 30 秒時 30 秒後就再輪詢（spec §6.2）。
        await h.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(runs + 1, h.GitHub.Count(EngineHarness.RunsPath));
    }

    [Fact]
    public async Task 回應帶x_poll_interval時下次間隔不短於它()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start)));
        await h.DrainAsync();
        h.GitHub.AddHeader(EngineHarness.RunsPath, "x-poll-interval", "60");

        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        var runs = h.GitHub.Count(EngineHarness.RunsPath);

        // 進行中本來 15 秒一次；回應要求 60 秒，就取 max(15, 60)（spec §4.1）。
        await h.AdvanceInStepsAsync(TimeSpan.FromSeconds(59), Tuning.ActiveRepoInterval);
        Assert.Equal(runs, h.GitHub.Count(EngineHarness.RunsPath));
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(runs + 1, h.GitHub.Count(EngineHarness.RunsPath));
    }

    [Fact]
    public async Task 連續3個5xx加角標與橫幅且該目標退避()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        foreach (var path in new[] { EngineHarness.ProbeAPath, EngineHarness.RunsPath, EngineHarness.PullsPath })
        {
            h.GitHub.Override(path, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"));
        }

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Equal(BannerKind.ServerErrors, h.Last.Banner?.Kind);
        Assert.Equal("GitHub 回應異常 · 最後更新 10:00", h.Last.Banner?.Text);
        Assert.True(h.Last.Summary.Issues.HasFlag(ConnectionIssue.ServerErrors));
        Assert.Contains("ServerError", h.ErrorLogText);

        // runs 的正常間隔是 300 秒；5xx 後退避成 min(300 × 2, RepoErrorBackoffMax) = 600 秒（spec §6.4）。
        var runs = h.GitHub.Count(EngineHarness.RunsPath);
        await h.AdvanceAsync(Tuning.QuietMax);
        Assert.Equal(runs, h.GitHub.Count(EngineHarness.RunsPath));
        await h.AdvanceAsync(Tuning.RepoErrorBackoffMax - Tuning.QuietMax);
        Assert.Equal(runs + 1, h.GitHub.Count(EngineHarness.RunsPath));

        foreach (var path in new[] { EngineHarness.ProbeAPath, EngineHarness.RunsPath, EngineHarness.PullsPath })
        {
            h.GitHub.RemoveOverrides(path);
        }

        // 探測已退避到 RepoErrorBackoffMax 以內，下一個回應不是 5xx 就清掉角標。
        await h.AdvanceAsync(Tuning.RepoErrorBackoffMax);
        Assert.Null(h.Last.Banner);
        Assert.False(h.Last.Summary.Issues.HasFlag(ConnectionIssue.ServerErrors));
    }

    [Fact]
    public async Task 版本錯誤停止所有輪詢()
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Gone, "API version is no longer supported."));

        await h.AdvanceAsync(Tuning.ProbeInterval);
        var count = h.GitHub.Handler.Requests.Count;
        Assert.Equal(BannerKind.VersionError, h.Last.Banner?.Kind);
        Assert.True(h.Last.Summary.Issues.HasFlag(ConnectionIssue.VersionError));
        Assert.Equal("需要更新程式", h.Last.TrayIssueLine);

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.AdvanceAsync(TimeSpan.FromHours(2));
        Assert.Equal(count, h.GitHub.Handler.Requests.Count);
    }

    [Fact]
    public async Task stale_repo第一次看到時檢查一次有90天內的run就監看()
    {
        await using var h = EngineHarness.Create(h =>
        {
            var recent = h.GitHub.AddRepo(FakeData.Repo(2007, "octo-org/sample-old", Start.AddDays(-120)));
            recent.Runs.Add(FakeData.Run(9201, 3, "completed", "success", Start.AddDays(-10), Start.AddDays(-10),
                repo: "octo-org/sample-old", headRepoId: 2007));
            var dormant = h.GitHub.AddRepo(FakeData.Repo(2008, "octo-org/sample-dormant", Start.AddDays(-200)));
            dormant.Runs.Add(FakeData.Run(9301, 1, "completed", "success", Start.AddDays(-200), Start.AddDays(-200),
                repo: "octo-org/sample-dormant", headRepoId: 2008));
        });
        await h.DrainAsync();

        // Q7：E3 有 workflow 再用 E4（同主要輪詢的網址），本地有 90 天內的 run 就當成有紀錄並監看。
        Assert.Contains("/repos/octo-org/sample-old/actions/workflows?per_page=100", h.GitHub.ApiRequests);
        Assert.Contains("/repos/octo-org/sample-old/actions/runs?per_page=30", h.GitHub.ApiRequests);
        Assert.True(h.RepoRow(2007).Monitored);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9201);

        Assert.False(h.RepoRow(2008).Monitored);
        Assert.Equal(SkipReason.Stale, h.RepoRow(2008).Reason);
        Assert.DoesNotContain(h.Last.Rows, row => row.Run.Id == 9301);
        var dormant = h.GitHub.ApiRequests.Count(p => p.StartsWith("/repos/octo-org/sample-dormant/", StringComparison.Ordinal));
        Assert.Equal(2, dormant);

        // 只做一次：之後不再輪詢被第 6 條略過的 repo。
        await h.AdvanceAsync(TimeSpan.FromHours(2));
        Assert.Equal(dormant, h.GitHub.ApiRequests.Count(p => p.StartsWith("/repos/octo-org/sample-dormant/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task 超過1小時沒有成功回應的repo列入無法更新()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start)));
        await h.DrainAsync();
        foreach (var path in new[] { EngineHarness.RunsPath, EngineHarness.PullsPath, EngineHarness.WorkflowsPath, EngineHarness.SeedPath })
        {
            h.GitHub.Override(path, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
                ("X-Accepted-GitHub-Permissions", "actions=read")));
        }

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Contains("權限不足，需要 actions=read", h.RepoRow(FakeData.RepoId).ProblemNote);
        Assert.Equal(1, h.Last.Summary.PendingCount);

        await h.AdvanceAsync(Tuning.StaleDataThreshold + TimeSpan.FromSeconds(1));

        // 不再計入進行中（已經不知道它有沒有結束），tooltip 多一行原因；不加角標（Q24）。
        Assert.Equal(1, h.Last.Summary.StaleRepoCount);
        Assert.Equal(0, h.Last.Summary.PendingCount);
        Assert.Equal("1 個 repo 無法更新", h.Last.TrayIssueLine);
        Assert.Equal(ConnectionIssue.None, h.Last.Summary.Issues);
    }

    [Fact]
    public async Task 開啟包含協作repo的設定後抓清單B並監看()
    {
        await using var h = EngineHarness.Create(h =>
        {
            var shared = h.GitHub.AddRepo(FakeData.Repo(2006, "carol/shared-tool", Start.AddDays(-1), ownerId: 4001, ownerType: "User"),
                collaborator: true);
            shared.Runs.Add(FakeData.Run(9401, 5, "completed", "success", Start.AddHours(-3), Start.AddHours(-3),
                repo: "carol/shared-tool", headRepoId: 2006));
        });
        await h.DrainAsync();
        Assert.DoesNotContain("/user/repos?affiliation=collaborator&per_page=100", h.GitHub.ApiRequests);

        // App 改的是同一個 AppSettings 物件：Engine 仍要分辨出哪些設定變了。
        h.Settings.IncludeCollaboratorRepos = true;
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.DrainAsync();

        Assert.Contains("/user/repos?affiliation=collaborator&per_page=100", h.GitHub.ApiRequests);
        Assert.Contains("/repos/carol/shared-tool/actions/runs?per_page=30", h.GitHub.ApiRequests);
        var row = h.RepoRow(2006);
        Assert.True(row.Monitored);
        Assert.Equal(RepoCategory.Collaborator, row.Repo.Category);
        Assert.Contains(h.Last.Rows, r => r.Run.Id == 9401);

        // 之後探測 B 也照常送。
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.Contains("/user/repos?affiliation=collaborator&sort=pushed&per_page=100", h.GitHub.ApiRequests);
    }

    [Fact]
    public async Task 睡眠指令期間不送請求喚醒後全量刷新()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();

        h.Engine.Post(new EngineCommand.Suspend());
        var count = h.GitHub.ApiRequests.Count;
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(count, h.GitHub.ApiRequests.Count);

        h.Engine.Post(new EngineCommand.Resume());
        await h.DrainAsync();
        var after = h.GitHub.ApiRequests.Skip(count).ToList();
        Assert.Contains(EngineHarness.ProbeAPath, after);
        Assert.Contains(EngineHarness.RunsPath, after);
        Assert.Contains(EngineHarness.PullsPath, after);
    }

    [Fact]
    public async Task PR分支補抓每個head_sha只抓一次且不通知()
    {
        const string seedPath = "/repos/octo-org/sample-app/actions/runs?head_sha=" + FakeData.FeatureSha + "&per_page=30";
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddHours(-1), Start.AddHours(-1))));
        await h.DrainAsync();

        // 功能分支上 push 的 run 在啟動之後失敗，早已被擠出 E4 第 1 頁；之後才開 PR，它因此變成追蹤中。
        // （pull_request 事件的 run 一定晚於 PR，不是這種情況：MonitorEngineReviewTests。）
        h.Main.OlderRuns.Add(FakeData.Run(9010, 43, "completed", "failure", Start.AddMinutes(3), Start.AddMinutes(5),
            branch: "feature/login", sha: FakeData.FeatureSha, pullRequests: [17]));
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        h.Main.Pulls.Add(FakeData.Pull(17, "feature/login", FakeData.FeatureSha));
        await h.AdvanceAsync(Tuning.PullListTtl);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        Assert.Equal(1, h.GitHub.Count(seedPath));
        Assert.Equal(1, h.Last.Summary.FailingCount);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9010 && row.IsTrackedFailure);
        Assert.Equal([17], h.Last.PullRequestNumbers[9010]);
        // 追蹤範圍的變動（開了 PR）不發通知（spec §9.1）。
        Assert.Empty(h.Toasts);
        Assert.True(h.Cache.PrSeedChecked(FakeData.RepoId, FakeData.FeatureSha));

        // 有 PR 鍵失敗之後，PR 清單下一次重查起改成每 PullListTtlActive 一次（Q25），同一個 head sha 不再補抓。
        await h.AdvanceAsync(Tuning.PullListTtl);
        var pulls = h.GitHub.Count(EngineHarness.PullsPath);
        await h.AdvanceAsync(Tuning.PullListTtlActive);
        Assert.Equal(pulls + 1, h.GitHub.Count(EngineHarness.PullsPath));
        Assert.Equal(1, h.GitHub.Count(seedPath));
    }

    [Fact]
    public async Task Deprecation回應只記一次error_log並顯示一次性橫幅()
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        h.GitHub.AddHeader(EngineHarness.ProbeAPath, "Deprecation", "@1798761600");

        await h.AdvanceAsync(Tuning.ProbeInterval);
        var banner = h.Last.Banner;
        Assert.Equal(BannerKind.Deprecated, banner?.Kind);
        Assert.Equal("GitHub API 版本即將停止支援，請留意程式更新。", banner?.Text);

        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.Same(banner, h.Last.Banner);
        Assert.Single(Regex.Matches(h.ErrorLogText, "Deprecation"));
    }

    [Fact]
    public async Task 輪詢中收到401換到新token後重送成功()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            h.Tokens.Returns(EngineHarness.OtherFakeToken);
        });
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"), times: 1);

        Assert.Equal(0, await h.AdvanceAsync(TimeSpan.Zero));
        h.Clock.Advance(Tuning.ProbeInterval);
        Assert.Equal(2, await h.StepAsync());

        var probes = h.GitHub.Handler.Requests.Where(r => r.Uri.PathAndQuery == EngineHarness.ProbeAPath).ToList();
        Assert.Equal("Bearer " + EngineHarness.OtherFakeToken, probes[^1].Header("Authorization"));
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
        Assert.Null(h.Last.Banner);
        Assert.NotNull(h.Cache.Get(Api + EngineHarness.ProbeAPath));
    }

    [Fact]
    public async Task 每發布一份快照Sequence加1()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start)));

        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);

        // 第一份是 1，之後每一份加 1，不跳號也不重複：App 用它判斷收到的快照是不是寫入回傳之後的（第二階段 spec §5.3）。
        var expected = Enumerable.Range(1, h.Snapshots.Count).Select(n => (long)n);
        Assert.Equal(expected, h.Snapshots.Select(snapshot => snapshot.Sequence));
    }

    [Fact]
    public async Task 命令列刷新和F5一樣做一次全量刷新()
    {
        // 自動更新暫停時照樣刷新（第二階段 spec §10.5）；刷新完成的判斷與狀態表的序號由之後的任務補上。
        await using var h = EngineHarness.Create(h =>
        {
            h.Settings.RefreshMode = RefreshMode.Paused;
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddHours(-1), Start.AddHours(-1)));
        });
        await h.DrainAsync();
        Assert.Equal(0, h.GitHub.Count(EngineHarness.RunsPath));

        h.Engine.Post(new EngineCommand.CliRefresh());
        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(EngineHarness.RunsPath));
        Assert.Equal(1, h.GitHub.Count(EngineHarness.PullsPath));
        Assert.Equal(new LoadingProgress(1, 1), h.Last.Loading);
        Assert.Empty(h.ErrorLogText);
    }

    /// <summary>repo 清單的一頁；<paramref name="next"/> 不為 null 時帶 Link next。</summary>
    private static HttpResponseMessage RepoListPage(string? next, params JsonObject[] repos)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (next is not null)
        {
            headers["Link"] = $"<{next}>; rel=\"next\"";
        }

        return FakeHttpHandler.Json(new JsonArray([.. repos]).ToJsonString(), HttpStatusCode.OK, headers);
    }

    /// <summary>資料庫裡這個帳號的 push 權限，依 repo id 排序。</summary>
    private static List<(long RepoId, bool CanPush)> SortedPermissions(EngineHarness h, long userId) =>
        h.Cache.GetRepoPermissions(userId).OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)).ToList();
}
