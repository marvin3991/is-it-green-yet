using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Polling;
using IsItGreenYet.Core.Tests.Auth;
using IsItGreenYet.Core.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>Task 13 整合審查回報的問題：每個測試重現一個情境（修正前失敗），之後當成回歸測試。</summary>
public class MonitorEngineReviewTests
{
    private const string Collaborator = "carol/shared-tool";
    private const string CollaboratorRunsPath = "/repos/carol/shared-tool/actions/runs?per_page=30";
    private const string JobsPath = "/repos/octo-org/sample-app/actions/runs/9001/jobs?filter=latest&per_page=100";
    private const string RunDetailPath = "/repos/octo-org/sample-app/actions/runs/9100";
    private const string PrSeedPath = "/repos/octo-org/sample-app/actions/runs?head_sha=" + FakeData.FeatureSha + "&per_page=30";

    /// <summary>同一個功能分支上另一個 commit 的假 sha。</summary>
    private const string OtherFeatureSha = "9f8e7d6c5b4a39281706f5e4d3c2b1a098765432";

    /// <summary>超過 90 天沒有 push 的假 repo（第 6 條）。</summary>
    private const long StaleRepoId = 2007;
    private const string StaleRepoName = "octo-org/sample-old";
    private const string StaleWorkflowsPath = "/repos/octo-org/sample-old/actions/workflows?per_page=100";
    private const string StaleRunsPath = "/repos/octo-org/sample-old/actions/runs?per_page=30";
    private const string StalePrSeedPath = "/repos/octo-org/sample-old/actions/runs?head_sha=" + FakeData.FeatureSha + "&per_page=30";
    private const string StaleLintSeedPath = "/repos/octo-org/sample-old/actions/workflows/161336/runs?branch=main&per_page=5";
    private const string StalePullsPath = "/repos/octo-org/sample-old/pulls?state=open&per_page=100";

    /// <summary>防止 StepUntilAsync 因為實作錯誤無限送下去。</summary>
    private const int MaxSteps = 100;

    /// <summary>第三把假 token：由 <see cref="Fixture.FakeToken"/> 改幾個字元而來，格式相同。</summary>
    private static readonly string ThirdFakeToken = Fixture.FakeToken.Replace('0', '2');

    private static readonly DateTimeOffset Start = EngineHarness.Start;

    /// <summary>背景工作的保險上限；正常情況下等待的條件都會立刻成立，不靠時間流逝。</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    /// <summary>觀察背景迴圈有沒有空轉的真實時間；修正後這段期間只會有一兩份快照。</summary>
    private static readonly TimeSpan SpinObservation = TimeSpan.FromMilliseconds(200);

    /// <summary>空轉檢查的上限：迴圈每醒來一次發布一份快照，正常情況下這段期間最多醒來幾次。</summary>
    private const int MaxIdleSnapshots = 5;

    /// <summary>等重試的測試每次推進的時間：遠短於一小時的重試間隔，又不必一秒一秒推。</summary>
    private static readonly TimeSpan RetryStep = TimeSpan.FromMinutes(5);

    /// <summary>重建資料庫退避期間連跑幾步（時間不前進），確認不會每一步都重試、都寫 error.log。</summary>
    private const int IdleSteps = 20;

    /// <summary>§5.4 的權限提示開頭（EngineText.ActionsNotFound）。</summary>
    private const string ActionsNotFoundNote = "Actions 端點回 404";

    /// <summary>另一個假帳號（換帳號、換 token 的情境）。</summary>
    private const long BobId = 1002;
    private const string BobLogin = "bob";

    /// <summary>預設 repo 的第二個假 workflow。</summary>
    private const long LintWorkflowId = 161336;
    private const string LintWorkflowName = "Lint";
    private const string LintWorkflowPath = ".github/workflows/lint.yml";

    /// <summary>模擬 account 表持續寫不進去（例如磁碟滿或唯讀）的 trigger 與它的錯誤訊息。</summary>
    private const string InjectedWriteFailure = "測試注入的帳號寫入失敗";
    private const string BlockAccountWrites =
        "CREATE TRIGGER block_account BEFORE INSERT ON account BEGIN SELECT RAISE(ABORT, '" + InjectedWriteFailure + "'); END;";

    /// <summary>同一個組織的第二個假 repo。</summary>
    private const long OtherRepoId = 2002;
    private const string OtherRepoName = "octo-org/other-app";

    /// <summary>bob 自己的假 repo（換帳號之前是協作 repo）。</summary>
    private const long BobRepoId = 2050;
    private const string BobRepoName = "bob/tool";

    /// <summary>無法分類的 403（不是限流、也不是「Resource not accessible by」，spec §6.4 第 4 列）。</summary>
    private const string ForbiddenByPolicy = "Forbidden by organization policy";

    /// <summary>ETA 樣本的索引（DatabaseInitializer 建立）：查 ETA 樣本時才會讀它。</summary>
    private const string DurationSamplesIndex = "idx_duration_samples_workflow";

    /// <summary>workflow 都已停用的假 repo（第 8 條）與它的 workflow。</summary>
    private const long DormantRepoId = 2009;
    private const string DormantRepoName = "octo-org/dormant";
    private const long DormantWorkflowId = 161337;

    /// <summary>
    /// 重建後在新的資料庫弄壞的表：重讀 DB（LoadFromDatabase）會讀它，監看的其他步驟不讀不寫（只有靜音與強制納入會寫），
    /// 弄壞之後監看照常，只有重建時的重讀會讀到損毀。
    /// </summary>
    private const string RebuiltCorruptTable = "repo_overrides";

    /// <summary>連續改名重建幾次之後，又讀到損毀就改用退避（MonitorEngine 的同名常數）。</summary>
    private const int RebuildsBeforeBackoff = 2;

    /// <summary>達到重建門檻之後要看到第幾個請求：第一個在達到門檻的那一步，第二個一定在下一步。</summary>
    private const int RequestsThroughNextStep = 2;

    /// <summary>持續損毀時觀察重建次數上限的時間。</summary>
    private static readonly TimeSpan RebuildObservation = TimeSpan.FromMinutes(10);

    /// <summary>持續損毀只留第一份備份：之後的重試都是這個程序自己重建的檔，沒有設過靜音或強制納入就丟棄。</summary>
    private const int FirstBackupOnly = 1;

    /// <summary>觀察持續損毀不多留備份的時間：遠長於退避上限，重試已經到上限好幾次。</summary>
    private static readonly TimeSpan PersistentObservation = TimeSpan.FromHours(2);

    /// <summary>暫停中按幾次 F5（每次間隔超過 <see cref="Tuning.RebuildStreakWindow"/>）。</summary>
    private const int RefreshPresses = 3;

    /// <summary>模擬睡眠喚醒的時間：遠長於 <see cref="Tuning.RebuildStreakWindow"/>。</summary>
    private static readonly TimeSpan SleepGap = TimeSpan.FromDays(1);

    /// <summary>比一頁清單多幾個 repo 的世界：探測的第 1 頁（<see cref="Tuning.ListPageSize"/>）放不下，完整清單要 2 頁。</summary>
    private const int ReposBeyondOnePage = Tuning.ListPageSize + 5;
    private const int ListPagesBeyondOnePage = 2;

    /// <summary>那些假 repo 與假 run 的 id 起點，不和其他假資料重複。</summary>
    private const long ManyReposBaseId = 50000;
    private const long ManyCollaboratorReposBaseId = 51000;
    private const long ManyRunsIdOffset = 10000;

    /// <summary>清單 A、B 的第 2 頁與清單 B 的第 1 頁（FakeGitHub 的 Link next）。</summary>
    private const string ListASecondPage = EngineHarness.ListAPath + "&page=2";
    private const string ListBPath = "/user/repos?affiliation=collaborator&per_page=100";
    private const string ListBSecondPage = ListBPath + "&page=2";

    /// <summary><see cref="Collaborator"/> 的假 repo id。</summary>
    private const long CollaboratorRepoId = 2006;

    /// <summary>節流器的視窗（<see cref="Tuning.MaxRequestsPerMinute"/> 的「分鐘」）。</summary>
    private static readonly TimeSpan RequestWindow = TimeSpan.FromMinutes(1);

    /// <summary>送完 <see cref="ReposBeyondOnePage"/> 個 repo 的 E3、E4、E7 最多要幾分鐘的保險上限。</summary>
    private const int MaxDrainMinutes = 30;

    /// <summary>重新啟動的次數比編號備份上限多幾次。</summary>
    private const int RestartsBeyondLimit = 3;

    /// <summary>編號備份滿額之後的固定檔名（DatabaseInitializer）。</summary>
    private const string LatestBackupName = "cache.db.corrupt-latest.bak";

    /// <summary>丟棄損毀的資料庫之前 error.log 的標題（MonitorEngine）。</summary>
    private const string DiscardedLogContext = "未保留損毀的快取資料庫";

    /// <summary>
    /// 中間隔著乾淨步驟的情境跑幾輪，每輪 <see cref="Tuning.RequestWindow"/>：要多於這段時間內重建次數的上限（門檻 + 退避重試），
    /// 不算連續、每輪都重建時才會超過上限。
    /// </summary>
    private const int CleanStepRounds = 15;

    /// <summary>退避到上限之後再觀察幾次重試。</summary>
    private const int RetriesObservedAtBackoffMax = 3;

    /// <summary>SQLITE_CORRUPT（SQLite 的結果碼）。</summary>
    private const int SqliteCorruptCode = 11;

    /// <summary>
    /// 給 <see cref="CorruptAfterEachRebuild"/>：請求進來時才弄壞，這個請求的回應寫入時讀到損毀。
    /// 不用 <see cref="EngineClock.CalledFrom"/> 判斷 SendNextAsync、Observe 這些方法：Release 組態（CI）裡 JIT 重新編譯後它們可能被內嵌，呼叫堆疊上就看不到了。
    /// </summary>
    private const string DuringRequest = "請求進來時";

    private static readonly CancellationToken None = CancellationToken.None;

    // ── 等錯誤重試的目標 ──────────────────────────────────────────────

    [Fact]
    public async Task E4回404標示問題後暫停再恢復仍每小時重試()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.RunsPath, _ => h.GitHub.Error(HttpStatusCode.NotFound, "Not Found"));
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Contains("Actions 端點回 404", h.RepoRow(FakeData.RepoId).ProblemNote);
        var runs = h.GitHub.Count(EngineHarness.RunsPath);

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();

        // 恢復時的全量刷新不提前送等重試的目標；從上一次送出起算一小時後照常重試（spec §5.4）。
        Assert.Equal(runs, h.GitHub.Count(EngineHarness.RunsPath));
        await h.AdvanceInStepsAsync(Tuning.NotFoundRetryInterval, TimeSpan.FromMinutes(5));
        Assert.True(h.GitHub.Count(EngineHarness.RunsPath) > runs);
    }

    [Fact]
    public async Task E7回403後靜音再取消靜音仍每小時重試()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            h.GitHub.Override(EngineHarness.PullsPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden,
                "Resource not accessible by personal access token", ("X-Accepted-GitHub-Permissions", "pull_requests=read")));
        });
        await h.DrainAsync();
        Assert.Contains("缺少 Pull requests 權限或無法存取", h.RepoRow(FakeData.RepoId).ProblemNote);
        var pulls = h.GitHub.Count(EngineHarness.PullsPath);

        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, RepoOverride.None));
        await h.DrainAsync();

        // 取消靜音不提前重試；每 PermissionRetryInterval 照常再試一次（spec §4.2 E7、§6.4 第 3 列）。
        Assert.Equal(pulls, h.GitHub.Count(EngineHarness.PullsPath));
        await h.AdvanceInStepsAsync(Tuning.PermissionRetryInterval, TimeSpan.FromMinutes(5));
        Assert.True(h.GitHub.Count(EngineHarness.PullsPath) > pulls);
    }

    // ── 問題標示 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 重啟後同一個目標成功就清掉上一次執行的問題標示()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            h.GitHub.Override(EngineHarness.PullsPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden,
                "Resource not accessible by personal access token", ("X-Accepted-GitHub-Permissions", "pull_requests=read")), times: 1);
        });
        await h.DrainAsync();
        Assert.Contains("缺少 Pull requests 權限或無法存取", h.RepoRow(FakeData.RepoId).ProblemNote);

        // 你修好權限後重新啟動：這次執行的回應都成功，repo 管理頁不能再顯示舊的問題（spec §5.2 第 9 條）。
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));
        await h.RestartAsync();
        await h.DrainAsync();

        Assert.Null(h.RepoRow(FakeData.RepoId).ProblemNote);
        Assert.Null(h.Cache.GetRepoStates()[FakeData.RepoId].ProblemNote);
    }

    [Fact]
    public async Task E6回403標示問題後再次成功就清掉()
    {
        var stuck = FakeData.Run(9100, 77, "in_progress", null, Start.AddMinutes(-1), Start, branch: "feature/x", sha: FakeData.FeatureSha);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(stuck));
        await h.DrainAsync();

        // 這筆 run 被擠出第 1 頁，之後由 E6 追蹤；第一次 E6 回權限不足。
        h.Main.Runs.Clear();
        h.Main.OlderRuns.Add(stuck);
        h.GitHub.Override(RunDetailPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
            ("X-Accepted-GitHub-Permissions", "actions=read")), times: 1);
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        await h.AdvanceInStepsAsync(Tuning.QuietMax, Tuning.ActiveRepoInterval);
        Assert.Equal(1, h.GitHub.Count(RunDetailPath));
        Assert.Contains("權限不足，需要 actions=read", h.RepoRow(FakeData.RepoId).ProblemNote);

        await h.AdvanceInStepsAsync(Tuning.PermissionRetryInterval, TimeSpan.FromMinutes(5));

        Assert.True(h.GitHub.Count(RunDetailPath) >= 2);
        Assert.Null(h.RepoRow(FakeData.RepoId).ProblemNote);
    }

    [Fact]
    public async Task E6的run完成後不再追蹤時清掉E6的問題標示()
    {
        var stuck = FakeData.Run(9100, 77, "in_progress", null, Start.AddMinutes(-1), Start, branch: "feature/x", sha: FakeData.FeatureSha);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(stuck));
        await h.DrainAsync();
        h.Main.Runs.Clear();
        h.Main.OlderRuns.Add(stuck);
        h.GitHub.Override(RunDetailPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
            ("X-Accepted-GitHub-Permissions", "actions=read")));
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        await h.AdvanceInStepsAsync(Tuning.QuietMax, Tuning.ActiveRepoInterval);
        Assert.Contains("權限不足，需要 actions=read", h.RepoRow(FakeData.RepoId).ProblemNote);

        // 這筆 run 完成後回到第 1 頁：不再需要 E6，E6 的問題也就不是目前的問題了。
        FakeData.Complete(stuck, "success", h.Clock.GetUtcNow());
        h.Main.Runs.Add(stuck);
        await h.AdvanceAsync(Tuning.QuietMax);

        Assert.Null(h.RepoRow(FakeData.RepoId).ProblemNote);
    }

    // ── 排程用單調時鐘 ────────────────────────────────────────────────

    [Fact]
    public async Task 系統時間往回調之後workflow清單仍在一小時內重查()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.WorkflowsPath));

        // 只把牆鐘往回調一天；暫停再恢復時依「上一次查的時間」重排 workflow 清單。
        h.Clock.Manual.SetUtcNow(h.Clock.GetUtcNow().AddDays(-1));
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();

        // 排程用單調時鐘，不受系統時間調整影響（spec §15）：最晚 WorkflowsRefresh 後就重查。
        await h.AdvanceInStepsAsync(Tuning.WorkflowsRefresh, TimeSpan.FromMinutes(5));
        Assert.True(h.GitHub.Count(EngineHarness.WorkflowsPath) > 1);
    }

    // ── 401 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task 重送仍401時即使又能取到不同token也進入未登入()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Tokens.Returns(EngineHarness.OtherFakeToken).Returns(ThirdFakeToken);
            h.GitHub.Override("/user", _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"));
        });

        Assert.Equal(2, await h.StepAsync());

        // spec §13.2 第 2 點：重送仍 401 就進入未登入，不再送第三個無效憑證。
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);
        Assert.Equal(BannerKind.Unauthorized, h.Last.Banner?.Kind);
        Assert.Equal(0, await h.DrainAsync());
        Assert.Equal(2, h.GitHub.Handler.Requests.Count);
    }

    // ── 首輪靜默的時間界線 ────────────────────────────────────────────

    [Fact]
    public async Task 補抓排入之後才完成的預設分支失敗要通知()
    {
        // 預設分支只有一筆進行中的 run：這個鍵沒有 lastDecisive，冷啟動時排 E5。
        // 這筆 run 在 E4 之後、E5 之前失敗，第一次看到它完成的是 E5。
        var running = FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start);
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(running);
            h.GitHub.OnRequest = request =>
            {
                if (request.RequestUri!.PathAndQuery == EngineHarness.SeedPath)
                {
                    FakeData.Complete(running, "failure", h.Clock.GetUtcNow());
                }
            };
        });
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // 補抓結果受 §9.3 的時間界線約束：完成時間晚於啟動時間，照樣是 N1（spec §7.2、§9.1）。
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));
        var toast = Assert.Single(h.Toasts);
        Assert.Equal("失敗：octo-org/sample-app · CI #42", toast.Title);
    }

    [Fact]
    public async Task PR分支補抓排入之後才完成的失敗要通知()
    {
        var pending = FakeData.Run(9010, 43, "in_progress", null, Start.AddMinutes(3), Start.AddMinutes(3),
            branch: "feature/login", sha: FakeData.FeatureSha, eventName: "pull_request", pullRequests: [17]);
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddHours(-1), Start.AddHours(-1))));
        await h.DrainAsync();

        // 執行中開了 PR；它的 run 不在 E4 第 1 頁，補抓送出的那一刻才失敗。
        h.Main.Pulls.Add(FakeData.Pull(17, "feature/login", FakeData.FeatureSha));
        h.Main.OlderRuns.Add(pending);
        h.GitHub.OnRequest = request =>
        {
            if (request.RequestUri!.PathAndQuery == PrSeedPath)
            {
                FakeData.Complete(pending, "failure", h.Clock.GetUtcNow());
            }
        };
        await h.AdvanceAsync(Tuning.PullListTtl);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // 補抓排入之前就完成的才算「追蹤範圍變動」（spec §9.1）；排入之後才完成的是新的失敗。
        Assert.Equal(1, h.GitHub.Count(PrSeedPath));
        var toast = Assert.Single(h.Toasts);
        Assert.Equal("失敗：octo-org/sample-app · CI #43", toast.Title);
    }

    [Fact]
    public async Task PR分支補抓看到本地記過的進行中run完成時照一般的完成觀察()
    {
        // 同一個分支先後兩個 commit 的 run 都在跑；之後都被擠出第 1 頁。PR 的 head 是較舊的那個 commit（例如 force-push 回去）。
        var older = FakeData.Run(9010, 10, "in_progress", null, Start.AddMinutes(-2), Start.AddMinutes(-2),
            branch: "feature/login", sha: FakeData.FeatureSha);
        var newer = FakeData.Run(9011, 11, "in_progress", null, Start.AddMinutes(-1), Start.AddMinutes(-1),
            branch: "feature/login", sha: OtherFeatureSha);
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddHours(-1), Start.AddHours(-1)));
            h.Main.Runs.Add(older);
            h.Main.Runs.Add(newer);
        });
        await h.DrainAsync();
        h.Main.Runs.Remove(older);
        h.Main.Runs.Remove(newer);
        h.Main.OlderRuns.Add(older);
        h.Main.OlderRuns.Add(newer);
        h.Main.Pulls.Add(FakeData.Pull(17, "feature/login", FakeData.FeatureSha));

        // 本地記過這筆 run 還在跑；它在啟動之後、補抓排入之前完成，第一次看到它完成的是補抓。
        h.GitHub.OnRequest = request =>
        {
            if (request.RequestUri!.PathAndQuery == PrSeedPath)
            {
                FakeData.Complete(older, "failure", Start.AddMinutes(25));
            }
        };
        await h.AdvanceAsync(Tuning.PullListTtl);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // 這是一般的「從未完成轉成完成」（spec §9.1）：照 §9.3 的界線判定，不因為是補抓看到的就壓掉。
        Assert.Equal(1, h.GitHub.Count(PrSeedPath));
        var toast = Assert.Single(h.Toasts);
        Assert.Equal("失敗：octo-org/sample-app · CI #10", toast.Title);
    }

    [Fact]
    public async Task 執行中打開包含協作repo前已完成的協作repo失敗不通知()
    {
        RepoWorld shared = null!;
        await using var h = EngineHarness.Create(h =>
        {
            shared = h.GitHub.AddRepo(FakeData.Repo(2006, Collaborator, Start.AddDays(-1), ownerId: 4001, ownerType: "User"),
                collaborator: true);
            shared.Runs.Add(FakeData.Run(9401, 5, "completed", "failure", Start.AddMinutes(3), Start.AddMinutes(5),
                repo: Collaborator, headRepoId: 2006));
        });
        await h.DrainAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));

        h.Settings.IncludeCollaboratorRepos = true;
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // 因為你改了設定才開始監看：時間界線是改設定的時間（spec §9.3），之前完成的只記 suppressed。
        Assert.Contains(CollaboratorRunsPath, h.GitHub.ApiRequests);
        Assert.Empty(h.Toasts);
        Assert.True(h.Cache.Contains(9401, 1, "failure"));
        Assert.Equal(Start.AddMinutes(10), h.Cache.GetRepoStates()[2006].BaselineT0);

        // 改設定之後才完成的失敗照常通知。
        var changedAt = Start.AddMinutes(10);
        shared.Runs.Insert(0, FakeData.Run(9402, 6, "completed", "failure", changedAt.AddSeconds(30), changedAt.AddMinutes(1),
            repo: Collaborator, headRepoId: 2006));
        await h.AdvanceAsync(TimeSpan.FromMinutes(5));
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        var toast = Assert.Single(h.Toasts);
        Assert.Equal("失敗：carol/shared-tool · CI #6", toast.Title);
    }

    [Fact]
    public async Task 沒有workflow的repo被push後立即重查workflow清單並開始監看()
    {
        await using var h = EngineHarness.Create(h => h.Main.Workflows.Clear());
        await h.DrainAsync();
        Assert.Equal(SkipReason.NoWorkflows, h.RepoRow(FakeData.RepoId).Reason);
        Assert.Equal(1, h.GitHub.Count(EngineHarness.WorkflowsPath));

        // 推上第一個 workflow：被第 7 條略過的 repo 在 pushed_at 改變時立即重查 E3（spec §5.3）。
        var pushedAt = Start.AddSeconds(20);
        h.Main.Workflows.Add(FakeData.Workflow());
        h.Main.SetPushedAt(pushedAt);
        h.Main.Runs.Add(FakeData.Run(9001, 1, "in_progress", null, pushedAt.AddSeconds(5), pushedAt.AddSeconds(5)));
        await h.AdvanceAsync(Tuning.ProbeInterval);

        Assert.Equal(2, h.GitHub.Count(EngineHarness.WorkflowsPath));
        Assert.True(h.RepoRow(FakeData.RepoId).Monitored);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
        // 因為新的 push 才從略過轉成監看：時間界線是那次的 pushed_at（spec §9.3）。
        Assert.Equal(pushedAt, h.Cache.GetRepoStates()[FakeData.RepoId].BaselineT0);
    }

    // ── 橫幅 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 不同的未預期例外在10分鐘後再發生時是新的一則橫幅()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.PullsPath, _ => throw new InvalidOperationException("第一個測試例外"), times: 1);
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        var first = h.Last.Banner;
        Assert.Equal(BannerKind.UnexpectedError, first?.Kind);

        // 同一則提示在後續快照裡是同一個實例（App 用它記住你關掉的是哪一則）。
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.Same(first, h.Last.Banner);

        await h.AdvanceAsync(Tuning.ErrorBannerDedupWindow);
        h.GitHub.Override(EngineHarness.PullsPath, _ => throw new NotSupportedException("第二個測試例外"), times: 1);
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        // 10 分鐘後的另一個例外要再提示一次（spec §10.2、§15）：不能沿用你已經關掉的那一則。
        Assert.Equal(BannerKind.UnexpectedError, h.Last.Banner?.Kind);
        Assert.NotSame(first, h.Last.Banner);
    }

    [Fact]
    public async Task Deprecation提示之後發生的未預期例外仍會顯示()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        h.GitHub.AddHeader(EngineHarness.ProbeAPath, "Deprecation", "@1798761600");
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.Equal(BannerKind.Deprecated, h.Last.Banner?.Kind);

        h.GitHub.Override(EngineHarness.PullsPath, _ => throw new InvalidOperationException("測試注入的例外"), times: 1);
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Equal(BannerKind.UnexpectedError, h.Last.Banner?.Kind);
    }

    // ── 完成時通知我 ──────────────────────────────────────────────────

    [Fact]
    public async Task 已完成的run不接受完成時通知我的標記()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddHours(-1), Start.AddHours(-1))));
        await h.DrainAsync();

        h.Engine.Post(new EngineCommand.SetWatched(9001, Watched: true));
        await h.StepAsync();

        // 已經完成的 run 不會再有完成觀察，標記永遠不會自動取消（spec §9.1 N3）。
        Assert.Empty(h.Last.WatchedRunIds);
        Assert.Empty(h.Cache.GetWatched());
    }

    // ── 資料庫損毀 ────────────────────────────────────────────────────

    [Fact]
    public async Task 啟動時讀不到損毀的cache_db就改名重建並提示()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        await h.RestartAsync();
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        await h.StepAsync();

        // spec §10.2、§15：改名保留後重建，提示靜音與強制納入已遺失。
        var backup = $"cache.db.corrupt-{h.Clock.GetLocalNow():yyyyMMddHHmmss}.bak";
        Assert.Equal(BannerKind.DatabaseRecovered, h.Last.Banner?.Kind);
        Assert.Equal($"快取資料庫無法讀取，已改名保留為 {backup} 並重建；repo 的靜音與強制納入設定已遺失，請到 repo 管理重新設定。",
            h.Last.Banner?.Text);
        Assert.True(File.Exists(Path.Combine(h.Workspace.Root, backup)));

        // 快取丟了只會重抓（spec §14.3）。
        await h.DrainAsync();
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
        Assert.Equal(FakeData.RepoName, Assert.Single(h.Cache.GetRepos()).FullName);
    }

    [Fact]
    public async Task 啟動時讀不到損毀的cache_db時StartAsync不丟例外並照常開始()
    {
        var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        await h.RestartAsync();
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        var requests = h.GitHub.Handler.Requests.Count;
        h.GitHub.OnRequest = request =>
        {
            if (request.RequestUri!.PathAndQuery == "/user")
            {
                verified.TrySetResult();
            }
        };

        await h.Engine.StartAsync(None);
        await verified.Task.WaitAsync(Guard);

        Assert.Contains(h.Snapshots, snapshot => snapshot.Banner?.Kind == BannerKind.DatabaseRecovered);
        Assert.True(h.GitHub.Handler.Requests.Count > requests);
    }

    [Fact]
    public async Task 啟動時讀DB失敗而不是損毀時退避後重試()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        await h.RestartAsync();
        var before = h.GitHub.ApiRequests.Count;
        Execute(h.Workspace.Paths.CacheDbPath, "ALTER TABLE repos RENAME TO repos_hidden;");

        // 不是損毀：不改名，照未預期錯誤提示；迴圈照常，不往外丟。
        Assert.Equal(0, await h.StepAsync());
        Assert.Equal(BannerKind.UnexpectedError, h.Last.Banner?.Kind);
        Assert.Empty(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*"));
        Assert.Single(Regex.Matches(h.ErrorLogText, "讀取快取資料庫"));

        // 退避期間不重試，不會每一步都寫一筆 error.log。
        Assert.Equal(0, await h.StepAsync());
        Assert.Single(Regex.Matches(h.ErrorLogText, "讀取快取資料庫"));

        Execute(h.Workspace.Paths.CacheDbPath, "ALTER TABLE repos_hidden RENAME TO repos;");
        await h.AdvanceAsync(Tuning.EngineTick);

        Assert.Single(Regex.Matches(h.ErrorLogText, "讀取快取資料庫"));
        Assert.Contains("/user", h.GitHub.ApiRequests.Skip(before));
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
    }

    [Fact]
    public async Task 執行中讀寫cache_db遇到損毀就改名重建並重新抓取()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        // 下一次寫入 repos 表（記下成功時間）時才讀到損毀。
        await h.AdvanceAsync(Tuning.QuietMax);

        Assert.Equal(BannerKind.DatabaseRecovered, h.Last.Banner?.Kind);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
        Assert.Equal(FakeData.RepoName, Assert.Single(h.Cache.GetRepos()).FullName);
        Assert.True(h.RepoRow(FakeData.RepoId).Monitored);
    }

    // ── 背景迴圈與釋放 ────────────────────────────────────────────────

    [Fact]
    public async Task 使用者操作等節流器時背景迴圈不空轉()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20))));
        await FillRequestWindowAsync(h);

        var copy = h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None);
        Assert.False(copy.IsCompleted);

        // 視窗空出來、但使用者操作的計時器還沒觸發：輪詢讓路，迴圈應該停下來等，不是一直空轉發布快照。
        h.Clock.Manual.Advance(Tuning.RequestWindow);
        await h.Engine.StartAsync(None);
        var before = h.Snapshots.Count;
        await Task.Delay(SpinObservation);

        Assert.True(h.Snapshots.Count - before <= MaxIdleSnapshots, $"背景迴圈空轉：發布了 {h.Snapshots.Count - before} 份快照");

        h.Clock.Advance(TimeSpan.Zero);
        await copy.WaitAsync(Guard);
    }

    [Fact]
    public async Task 釋放時等進行中的使用者操作結束而且不寫error_log()
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

        var copy = Task.Run(() => h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None));
        Assert.True(jobsSeen.Wait(Guard));
        var dispose = h.Engine.DisposeAsync().AsTask();
        release.Set();
        await dispose.WaitAsync(Guard);

        // 程式結束時進行中的操作當成取消：不用已釋放的 HTTP 用戶端繼續送，也不當成未預期錯誤。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy.WaitAsync(Guard));
        Assert.DoesNotContain("ObjectDisposedException", h.ErrorLogText, StringComparison.Ordinal);
        Assert.DoesNotContain("複製失敗 log", h.ErrorLogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取token期間釋放時StartAsync以取消結束()
    {
        var pending = new TaskCompletionSource<TokenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = EngineHarness.Create(h => h.Tokens.Waits(pending.Task), defaultToken: false);

        var start = h.Engine.StartAsync(None);
        Assert.False(start.IsCompleted);
        var dispose = h.Engine.DisposeAsync().AsTask();
        pending.SetResult(new TokenResult(Fixture.FakeToken, TokenSource.GhStored, null));

        // 開機後立刻從系統匣結束：StartAsync 以取消結束，不丟 ObjectDisposedException，也不啟動迴圈。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Guard));
        await dispose.WaitAsync(Guard);
        Assert.Empty(h.GitHub.Handler.Requests);
    }

    // ── 每分鐘上限 ────────────────────────────────────────────────────

    [Fact]
    public async Task 複製失敗log的E8分頁每一頁都計入每分鐘上限()
    {
        const string secondPage = JobsPath + "&page=2";
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20)));
            h.Main.Logs[7001] = FakeData.LogLines("build", 5);
            h.Main.Logs[7002] = FakeData.LogLines("test", 5);
            h.GitHub.Override(JobsPath, _ => JobsPage(FakeData.Job(7001, "build", "failure"),
                next: FakeGitHub.ApiBase + secondPage));
            h.GitHub.Override(secondPage, _ => JobsPage(FakeData.Job(7002, "test", "failure"), next: null));
        });
        await h.DrainAsync();

        var copy = await h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None);
        Assert.True(copy.Success, copy.Message);
        Assert.Equal(1, h.GitHub.Count(secondPage));

        // 同一刻反覆全量刷新到節流器擋住：任何 60 秒視窗內最多 60 個請求（spec §6.3，含分頁的每一頁）。
        await FillRequestWindowAsync(h);
        Assert.Equal(Tuning.MaxRequestsPerMinute, h.GitHub.ApiRequests.Count);
    }

    [Fact]
    public async Task 跟隨301轉址的請求也計入每分鐘上限()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start)));
        await h.DrainAsync();
        h.GitHub.Rename(FakeData.RepoName, "octo-org/sample-app-renamed");

        // 有 run 在跑的 repo 先送：E4 用舊網址收到 301，GitHubClient 照 Location 再送一次。
        await FillRequestWindowAsync(h);

        Assert.Contains(h.GitHub.Responses, response => response.Status == (int)HttpStatusCode.MovedPermanently);
        Assert.Equal(Tuning.MaxRequestsPerMinute, h.GitHub.ApiRequests.Count);
    }

    // ── 寫入與通知 ────────────────────────────────────────────────────

    [Fact]
    public async Task 寫入notified失敗時之後再補上通知()
    {
        var running = FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(running));
        await h.DrainAsync();

        Execute(h.Workspace.Paths.CacheDbPath,
            "CREATE TRIGGER block_notified BEFORE INSERT ON notified BEGIN SELECT RAISE(ABORT, '測試注入的寫入失敗'); END;");
        FakeData.Complete(running, "failure", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        Assert.Contains("測試注入的寫入失敗", h.ErrorLogText, StringComparison.Ordinal);

        // run 與 ETag 已經寫進 DB：之後的回應是 304，也看不出「新的完成」；這筆通知要留著重試。
        Execute(h.Workspace.Paths.CacheDbPath, "DROP TRIGGER block_notified;");
        await h.AdvanceInStepsAsync(Tuning.RepoErrorBackoffMax, Tuning.ActiveRepoInterval);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        var toast = Assert.Single(h.Toasts);
        Assert.Equal("失敗：octo-org/sample-app · CI #42", toast.Title);
        Assert.True(h.Cache.Contains(9001, 1, "failure"));
    }

    // ── 第 2 輪：一次性目標被移出排程之後 ─────────────────────────────

    [Fact]
    public async Task PR分支補抓排入後暫停再恢復仍會補抓()
    {
        await using var h = EngineHarness.Create(ArrangeOpenPullRequestWithOlderFailure);
        await StepUntilAsync(h, () => h.GitHub.Count(EngineHarness.PullsPath) == 1);
        Assert.Equal(0, h.GitHub.Count(PrSeedPath));

        // 補抓已經排入、還沒送出時暫停：暫停移出所有目標，恢復後 E7 帶 ETag 只會回 304。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.AdvanceInStepsAsync(TimeSpan.FromHours(2), TimeSpan.FromMinutes(5));

        // 每個 head sha 仍要補抓一次（spec §7.2），PR 分支的失敗才會被追蹤。
        Assert.Equal(1, h.GitHub.Count(PrSeedPath));
        Assert.Equal(1, h.Last.Summary.FailingCount);
    }

    [Fact]
    public async Task PR分支補抓排入後重新啟動仍會補抓()
    {
        await using var h = EngineHarness.Create(ArrangeOpenPullRequestWithOlderFailure);
        await StepUntilAsync(h, () => h.GitHub.Count(EngineHarness.PullsPath) == 1);
        Assert.Equal(0, h.GitHub.Count(PrSeedPath));

        await h.RestartAsync();
        await h.AdvanceInStepsAsync(TimeSpan.FromHours(2), TimeSpan.FromMinutes(5));

        Assert.Equal(1, h.GitHub.Count(PrSeedPath));
        Assert.Equal(1, h.Last.Summary.FailingCount);
    }

    [Fact]
    public async Task stale首次檢查的E3之後暫停再恢復仍會送E4()
    {
        await using var h = EngineHarness.Create(ArrangeStaleRepoWithRecentRun);
        await StepUntilAsync(h, () => h.GitHub.Count(StaleWorkflowsPath) == 1);
        Assert.Equal(0, h.GitHub.Count(StaleRunsPath));

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();

        // 有 workflow 才 E4、只做一次（spec §5.3、Q7）：被暫停打斷也要做完，10 天前的 run 讓它轉成監看。
        Assert.Equal(1, h.GitHub.Count(StaleRunsPath));
        Assert.True(h.RepoRow(StaleRepoId).Monitored);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9201);
    }

    [Fact]
    public async Task stale首次檢查的E3之後重新啟動仍會送E4()
    {
        await using var h = EngineHarness.Create(ArrangeStaleRepoWithRecentRun);
        await StepUntilAsync(h, () => h.GitHub.Count(StaleWorkflowsPath) == 1);
        Assert.Equal(0, h.GitHub.Count(StaleRunsPath));

        await h.RestartAsync();
        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(StaleRunsPath));
        Assert.True(h.RepoRow(StaleRepoId).Monitored);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9201);
    }

    [Fact]
    public async Task 冷啟動時E3網路錯誤一次之後F5立即送E3與E4()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20)));
            h.GitHub.Override(EngineHarness.WorkflowsPath, _ => throw new HttpRequestException("模擬的連線失敗"), times: 1);
        });
        await h.DrainAsync();
        Assert.Equal(0, h.GitHub.Count(EngineHarness.RunsPath));

        // F5 要刷新所有監看中 repo 的 runs（spec §6.6）；workflow 清單還沒查過時先補查。
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Equal(2, h.GitHub.Count(EngineHarness.WorkflowsPath));
        Assert.Equal(1, h.GitHub.Count(EngineHarness.RunsPath));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.Equal(new LoadingProgress(1, 1), h.Last.Loading);
    }

    [Fact]
    public async Task 冷啟動時E3遇到5xx之後以安靜間隔重試而不是等一小時()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20)));
            h.GitHub.Override(EngineHarness.WorkflowsPath, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"), times: 1);
        });
        await h.DrainAsync();
        Assert.Equal(0, h.GitHub.Count(EngineHarness.RunsPath));

        // 還沒查過的 workflow 清單是一次性的前置檢查：重試沿用一次性目標的安靜間隔上限，不是 WorkflowsRefresh。
        await h.AdvanceAsync(Tuning.QuietMax);

        Assert.Equal(2, h.GitHub.Count(EngineHarness.WorkflowsPath));
        Assert.Equal(1, h.GitHub.Count(EngineHarness.RunsPath));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
    }

    // ── 第 2 輪：每分鐘上限、5xx 角標 ────────────────────────────────

    [Fact]
    public async Task 視窗已滿時401換到新token的重送等節流器放行才送()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            h.Tokens.Returns(EngineHarness.OtherFakeToken);
        });
        await h.DrainAsync();

        // 視窗裡第 60 個請求收到 401，重新取得 token 換到另一把。
        var injected = false;
        h.GitHub.OnRequest = request =>
        {
            if (!injected && request.RequestUri!.Host == FakeGitHub.ApiHost && h.GitHub.ApiRequests.Count == Tuning.MaxRequestsPerMinute)
            {
                injected = true;
                h.GitHub.Override(request.RequestUri.PathAndQuery, _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"), times: 1);
            }
        };
        await FillRequestWindowAsync(h);

        // 任何 60 秒視窗內最多 60 個請求（spec §6.3），401 的重送也一樣。
        Assert.True(injected);
        Assert.Equal(Tuning.MaxRequestsPerMinute, h.GitHub.ApiRequests.Count);

        // 視窗空出來之後才用新 token 重送；之後照常輪詢，不再重取 token。
        await h.AdvanceAsync(Tuning.RequestWindow);
        Assert.True(h.GitHub.ApiRequests.Count > Tuning.MaxRequestsPerMinute);
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
        Assert.Equal("Bearer " + EngineHarness.OtherFakeToken, h.GitHub.Handler.Requests[^1].Header("Authorization"));
        Assert.Equal(2, h.Tokens.Calls);
    }

    [Fact]
    public async Task 延後的401重送仍收到401時直接進入未登入()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            h.Tokens.Returns(EngineHarness.OtherFakeToken).Returns(ThirdFakeToken);
        });
        await h.DrainAsync();

        // 從視窗裡第 60 個請求起全部回 401：第 60 個換到新 token 但視窗已滿，之後用新 token 送出的也被拒。
        h.GitHub.OnRequest = request =>
        {
            if (request.RequestUri!.Host == FakeGitHub.ApiHost && h.GitHub.ApiRequests.Count >= Tuning.MaxRequestsPerMinute)
            {
                h.GitHub.Override(request.RequestUri.PathAndQuery, _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"), times: 1);
            }
        };
        await FillRequestWindowAsync(h);
        Assert.Equal(Tuning.MaxRequestsPerMinute, h.GitHub.ApiRequests.Count);

        await h.AdvanceAsync(Tuning.RequestWindow);

        // 新 token 的那一次重送仍 401：就是「重送仍 401」（spec §13.2 第 2 點），不再重取第三把、不再送。
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);
        Assert.Equal(BannerKind.Unauthorized, h.Last.Banner?.Kind);
        Assert.Equal(2, h.Tokens.Calls);
        Assert.Equal(Tuning.MaxRequestsPerMinute + 1, h.GitHub.ApiRequests.Count);
        Assert.Equal("Bearer " + EngineHarness.OtherFakeToken, h.GitHub.Handler.Requests[^1].Header("Authorization"));
    }

    [Fact]
    public async Task 連續的5xx中間夾著4xx仍算連續並加角標()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"));
        h.GitHub.Override(EngineHarness.RunsPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Organization access restricted"));
        h.GitHub.Override(EngineHarness.PullsPath, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"));

        // 探測、runs、PR 清單依優先序各送一次，再全量刷新一次：探測又是 5xx（runs 在等權限重試，不提前）。
        h.Engine.Post(new EngineCommand.RefreshAll());
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(1, await h.StepAsync());
        }

        h.Engine.Post(new EngineCommand.RefreshAll());
        Assert.Equal(1, await h.StepAsync());

        // 5xx、403、5xx、5xx：中間沒有 2xx 或 304，已經連續 3 個 5xx（spec §6.4）。
        Assert.Equal([502, 403, 502, 502], h.GitHub.Responses.TakeLast(4).Select(response => response.Status));
        Assert.True(h.Last.Summary.Issues.HasFlag(ConnectionIssue.ServerErrors));
        Assert.Equal(BannerKind.ServerErrors, h.Last.Banner?.Kind);
    }

    // ── 第 2 輪：補抓的問題標示 ───────────────────────────────────────

    [Fact]
    public async Task E5回403標示問題後再次成功就清掉()
    {
        await using var h = EngineHarness.Create(h =>
            h.GitHub.Override(EngineHarness.SeedPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
                ("X-Accepted-GitHub-Permissions", "actions=read")), times: 1));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));
        Assert.Contains("權限不足，需要 actions=read", h.RepoRow(FakeData.RepoId).ProblemNote);

        await h.AdvanceInStepsAsync(Tuning.PermissionRetryInterval, TimeSpan.FromMinutes(5));

        Assert.Equal(2, h.GitHub.Count(EngineHarness.SeedPath));
        Assert.Null(h.RepoRow(FakeData.RepoId).ProblemNote);
    }

    [Fact]
    public async Task PR分支補抓回403標示問題後再次成功就清掉()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddHours(-1), Start.AddHours(-1))));
        await h.DrainAsync();
        h.Main.Pulls.Add(FakeData.Pull(17, "feature/login", FakeData.FeatureSha));
        h.Main.OlderRuns.Add(FakeData.Run(9010, 43, "completed", "failure", Start.AddHours(-2), Start.AddHours(-2),
            branch: "feature/login", sha: FakeData.FeatureSha, eventName: "pull_request", pullRequests: [17]));
        h.GitHub.Override(PrSeedPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
            ("X-Accepted-GitHub-Permissions", "actions=read")), times: 1);
        await h.AdvanceAsync(Tuning.PullListTtl);
        Assert.Equal(1, h.GitHub.Count(PrSeedPath));
        Assert.Contains("權限不足，需要 actions=read", h.RepoRow(FakeData.RepoId).ProblemNote);

        await h.AdvanceInStepsAsync(Tuning.PermissionRetryInterval, TimeSpan.FromMinutes(5));

        Assert.Equal(2, h.GitHub.Count(PrSeedPath));
        Assert.Null(h.RepoRow(FakeData.RepoId).ProblemNote);
        Assert.Equal(1, h.Last.Summary.FailingCount);
    }

    // ── 第 2 輪：完成觀察與時間界線 ───────────────────────────────────

    [Fact]
    public async Task 開PR後第一次看到就已完成的失敗要通知一次()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddHours(-1), Start.AddHours(-1))));
        await h.DrainAsync();

        // 安靜的 repo 上開了 PR，它的 CI 在下一次 E4 之前就跑完並失敗；本地的 PR 清單還沒有這個 PR。
        h.Main.Pulls.Add(FakeData.Pull(17, "feature/login", FakeData.FeatureSha));
        h.Main.Runs.Insert(0, FakeData.Run(9010, 43, "completed", "failure", Start.AddSeconds(30), Start.AddMinutes(1),
            branch: "feature/login", sha: FakeData.FeatureSha, eventName: "pull_request", pullRequests: [17]));
        await h.AdvanceAsync(Tuning.QuietMax);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // pull_request 事件的 run 是因為 PR 才產生的：完成時它已經屬於 PR 分支的鍵（spec §9.1 N1），不是追蹤範圍的變動。
        Assert.Equal(1, h.Last.Summary.FailingCount);
        var toast = Assert.Single(h.Toasts);
        Assert.Equal("失敗：octo-org/sample-app · CI #43", toast.Title);
        Assert.True(h.Cache.Contains(9010, 1, "failure"));
    }

    [Fact]
    public async Task PR分支補抓回來的pull_request事件失敗在啟動之後就要通知()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddHours(-1), Start.AddHours(-1))));
        await h.DrainAsync();

        // 執行中開了 PR；它的 pull_request run 在啟動之後失敗，而且早已被擠出 E4 第 1 頁，只有補抓看得到。
        h.Main.Pulls.Add(FakeData.Pull(17, "feature/login", FakeData.FeatureSha));
        h.Main.OlderRuns.Add(FakeData.Run(9010, 43, "completed", "failure", Start.AddMinutes(3), Start.AddMinutes(5),
            branch: "feature/login", sha: FakeData.FeatureSha, eventName: "pull_request", pullRequests: [17]));
        await h.AdvanceAsync(Tuning.PullListTtl);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // 補抓結果照 §9.3 的界線（spec §7.2）：PR 早於這筆 run 存在，不是追蹤範圍的變動。
        Assert.Equal(1, h.GitHub.Count(PrSeedPath));
        var toast = Assert.Single(h.Toasts);
        Assert.Equal("失敗：octo-org/sample-app · CI #43", toast.Title);
    }

    [Fact]
    public async Task PR分支補抓回來的我觸發的成功照時間界線通知()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddHours(-1), Start.AddHours(-1))));
        await h.DrainAsync();

        // 我在功能分支上 push 的 run 在啟動之後成功，早已被擠出 E4 第 1 頁；之後才開 PR，補抓才看到它。
        h.Main.OlderRuns.Add(FakeData.Run(9010, 43, "completed", "success", Start.AddMinutes(3), Start.AddMinutes(5),
            branch: "feature/login", sha: FakeData.FeatureSha));
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        h.Main.Pulls.Add(FakeData.Pull(17, "feature/login", FakeData.FeatureSha));
        await h.AdvanceAsync(Tuning.PullListTtl);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // N2 不限追蹤範圍（spec §9.1），只受 §9.3 的時間界線約束：補抓排入的時間不是它的界線。
        Assert.Equal(1, h.GitHub.Count(PrSeedPath));
        var toast = Assert.Single(h.Toasts);
        Assert.Equal("成功：octo-org/sample-app · CI #43", toast.Title);
    }

    [Fact]
    public async Task 已靜音的stale_repo取消靜音後靜音期間完成的失敗只記suppressed()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.SetPushedAt(Start.AddDays(-200));
            h.Cache.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false));
        });
        await h.DrainAsync();
        Assert.Equal(SkipReason.Muted, h.RepoRow(FakeData.RepoId).Reason);
        Assert.DoesNotContain(h.GitHub.ApiRequests, path => path.StartsWith("/repos/", StringComparison.Ordinal));

        // 靜音期間排程 workflow 跑出一筆失敗；取消靜音後 repo 落在第 6 條，經由 stale 首次檢查才轉成監看。
        h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(3), Start.AddMinutes(5), eventName: "schedule"));
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        var unmutedAt = h.Clock.GetUtcNow();
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, RepoOverride.None));
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // 因為你的操作才開始監看：界線是操作的時間（spec §9.3），靜音期間完成的只記 suppressed。
        Assert.True(h.RepoRow(FakeData.RepoId).Monitored);
        Assert.Empty(h.Toasts);
        Assert.True(h.Cache.Contains(9001, 1, "failure"));
        Assert.Equal(unmutedAt, h.Cache.GetRepoStates()[FakeData.RepoId].BaselineT0);
        Assert.Equal(1, h.Last.Summary.FailingCount);
    }

    // ── 第 2 輪：清單內容 ─────────────────────────────────────────────

    [Fact]
    public async Task 暫停自動更新前後主清單內容相同()
    {
        var stuck = FakeData.Run(9100, 77, "in_progress", null, Start.AddMinutes(-1), Start, branch: "feature/x", sha: FakeData.FeatureSha);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(stuck));
        await h.DrainAsync();

        // 這筆 run 被擠出第 1 頁，之後靠 E6 追蹤（spec §10.10 第 4 類）。
        h.Main.Runs.Clear();
        h.Main.OlderRuns.Add(stuck);
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        var before = h.Last.Rows.Select(row => row.Run.Id).ToList();
        Assert.Contains(9100L, before);

        // 暫停只停止週期性輪詢（spec §6.2、§10.3），不改變清單的內容與「N 筆執行」。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();

        Assert.Equal(before, h.Last.Rows.Select(row => row.Run.Id).ToList());
    }

    // ── 第 2 輪：使用者操作與執行緒 ───────────────────────────────────

    [Fact]
    public async Task 使用者操作進行中輪詢進入未登入之後不再送API請求()
    {
        const string firstLogPath = "/repos/octo-org/sample-app/actions/jobs/7001/logs";
        const string secondLogPath = "/repos/octo-org/sample-app/actions/jobs/7002/logs";
        using var firstSeen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20)));
            h.Main.Jobs[9001] = [FakeData.Job(7001, "build", "failure"), FakeData.Job(7002, "test", "failure")];
            h.Main.Logs[7001] = FakeData.LogLines("build", 5);
            h.Main.Logs[7002] = FakeData.LogLines("test", 5);
        });
        await h.DrainAsync();
        h.GitHub.OnRequest = request =>
        {
            if (request.RequestUri!.PathAndQuery == firstLogPath)
            {
                firstSeen.Set();
                release.Wait(Guard);
            }
        };

        var copy = Task.Run(() => h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None));
        Assert.True(firstSeen.Wait(Guard));

        // 同一時間輪詢收到 401，重新取得 token 拿回同一個值：進入未登入（spec §13.2 第 2 點）。
        h.Tokens.Returns(Fixture.FakeToken);
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"), times: 1);
        h.Clock.Advance(Tuning.ProbeInterval);
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);

        release.Set();
        var result = await copy.WaitAsync(Guard);

        // 未登入之後不再用被拒的 token 送任何請求；已經拿到的 log 照常回給你。
        Assert.DoesNotContain(secondLogPath, h.GitHub.ApiRequests);
        Assert.True(result.Success, result.Message);
        Assert.Equal("已複製 1 個失敗 job 的 log；1 個拿不到", result.Message);
        Assert.Contains("尚未登入 GitHub。", result.Text);
    }

    [Fact]
    public async Task StartAsync在背景執行緒上讀DB並發布第一份快照()
    {
        var pending = new TaskCompletionSource<TokenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = EngineHarness.Create(h => h.Tokens.Waits(pending.Task), defaultToken: false);
        var published = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Engine.SnapshotPublished += _ => published.TrySetResult(Environment.CurrentManagedThreadId);

        // 用獨立的執行緒（不是執行緒集區）代表 App 的 UI 執行緒。
        var callerThread = 0;
        Task? start = null;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            start = h.Engine.StartAsync(None);
        });
        int snapshotThread;
        try
        {
            caller.Start();
            caller.Join();
            snapshotThread = await published.Task.WaitAsync(Guard);
        }
        finally
        {
            // 斷言失敗時也要讓取 token 結束，否則釋放引擎會一直等初始化。
            pending.TrySetResult(new TokenResult(Fixture.FakeToken, TokenSource.GhStored, null));
        }

        // 讀 DB、組第一份快照都不能卡住呼叫端（spec §3.1）；事件一律在背景執行緒觸發。
        Assert.NotEqual(callerThread, snapshotThread);
        await start!.WaitAsync(Guard);
    }

    // ── 第 3 輪：等錯誤重試的目標不提前送 ─────────────────────────────

    [Fact]
    public async Task E4回403等重試時切換固定刷新秒數不提前重送()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.RunsPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
            ("X-Accepted-GitHub-Permissions", "actions=read")));
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Contains("權限不足，需要 actions=read", h.RepoRow(FakeData.RepoId).ProblemNote);
        var runs = h.GitHub.Count(EngineHarness.RunsPath);

        // 固定秒數只換掉正常的間隔；等權限重試的 E4 仍是每 PermissionRetryInterval 一次（spec §6.1、§6.4 第 3 列）。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Minute1));
        await h.DrainAsync();
        await h.AdvanceInStepsAsync(Tuning.PermissionRetryInterval - RetryStep, RetryStep);
        Assert.Equal(runs, h.GitHub.Count(EngineHarness.RunsPath));

        await h.AdvanceInStepsAsync(RetryStep * 2, RetryStep);
        Assert.Equal(runs + 1, h.GitHub.Count(EngineHarness.RunsPath));
    }

    [Fact]
    public async Task E4回404等重試時切換固定刷新秒數不提前重送()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.RunsPath, _ => h.GitHub.Error(HttpStatusCode.NotFound, "Not Found"));
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Contains(ActionsNotFoundNote, h.RepoRow(FakeData.RepoId).ProblemNote);
        var runs = h.GitHub.Count(EngineHarness.RunsPath);

        // 完整重抓確認之後改成每 NotFoundRetryInterval 重試一次（spec §5.4），不跟著固定秒數提前。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Seconds30));
        await h.DrainAsync();
        await h.AdvanceInStepsAsync(Tuning.NotFoundRetryInterval - RetryStep, RetryStep);
        Assert.Equal(runs, h.GitHub.Count(EngineHarness.RunsPath));

        await h.AdvanceInStepsAsync(RetryStep * 2, RetryStep);
        Assert.Equal(runs + 1, h.GitHub.Count(EngineHarness.RunsPath));
    }

    [Fact]
    public async Task E5回403等重試時暫停再恢復或靜音再取消靜音都不提前重送()
    {
        await using var h = EngineHarness.Create(h =>
            h.GitHub.Override(EngineHarness.SeedPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
                ("X-Accepted-GitHub-Permissions", "actions=read"))));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));
        Assert.Contains("權限不足，需要 actions=read", h.RepoRow(FakeData.RepoId).ProblemNote);

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));

        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, RepoOverride.None));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));

        // 被移出排程之後照重試間隔排回去：從上一次送出起算 PermissionRetryInterval 才再試（spec §6.1 錯誤狀態的目標）。
        await h.AdvanceInStepsAsync(Tuning.PermissionRetryInterval - RetryStep, RetryStep);
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));
        await h.AdvanceInStepsAsync(RetryStep * 2, RetryStep);
        Assert.Equal(2, h.GitHub.Count(EngineHarness.SeedPath));
    }

    [Fact]
    public async Task E6回403等重試時暫停再恢復不提前重送()
    {
        var stuck = StuckFeatureRun();
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(stuck));
        await ForbidRunDetailAsync(h, stuck);

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();

        // 恢復時排回去的 E6 從上一次送出起算重試間隔，不是一般的 E6 間隔（spec §6.1）。
        await h.AdvanceInStepsAsync(Tuning.PermissionRetryInterval - RetryStep * 2, RetryStep);
        Assert.Equal(1, h.GitHub.Count(RunDetailPath));
        await h.AdvanceInStepsAsync(RetryStep * 3, RetryStep);
        Assert.Equal(2, h.GitHub.Count(RunDetailPath));
    }

    [Fact]
    public async Task E6回403等重試時標記完成時通知我不提前重送()
    {
        var stuck = StuckFeatureRun();
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(stuck));
        await ForbidRunDetailAsync(h, stuck);

        h.Engine.Post(new EngineCommand.SetWatched(9100, Watched: true));
        await h.DrainAsync();
        Assert.Contains(9100L, h.Last.WatchedRunIds);

        // 被標記的 run 改用所屬 repo 的間隔，但等權限重試的 E6 仍照重試間隔（spec §6.1）。
        await h.AdvanceInStepsAsync(Tuning.PermissionRetryInterval - RetryStep * 2, RetryStep);
        Assert.Equal(1, h.GitHub.Count(RunDetailPath));
        await h.AdvanceInStepsAsync(RetryStep * 3, RetryStep);
        Assert.Equal(2, h.GitHub.Count(RunDetailPath));
    }

    // ── 第 3 輪：重新檢查 ─────────────────────────────────────────────

    [Fact]
    public async Task 已登入時重新檢查遇到user回5xx只送一次且照常輪詢()
    {
        var running = FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(running));
        await h.DrainAsync();
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
        var users = h.GitHub.Count("/user");
        var runs = h.GitHub.Count(EngineHarness.RunsPath);

        // 「重新檢查」拿回同一把 token，這次 E1 遇到 5xx。
        h.Tokens.Returns(Fixture.FakeToken);
        h.GitHub.Override("/user", _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"));
        h.Engine.Post(new EngineCommand.RecheckAuth());
        await h.AdvanceInStepsAsync(Tuning.RepoErrorBackoffMax, Tuning.ActiveRepoInterval);

        // 手動按才送的單一請求，不會變成連續重試（spec §13.2 第 3 點）；其他目標照常輪詢（spec §6.4 單一目標的錯誤）。
        Assert.Equal(users + 1, h.GitHub.Count("/user"));
        Assert.True(h.GitHub.Count(EngineHarness.RunsPath) > runs + 1, $"E4 只送了 {h.GitHub.Count(EngineHarness.RunsPath) - runs} 次");
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
    }

    // ── 第 3 輪：404 的權限提示 ───────────────────────────────────────

    [Fact]
    public async Task 完整重抓時已經不在清單的repo回404不標示Actions權限問題()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();

        // repo 被刪除：清單看不到它，E4 回 404。
        h.Main.Listed = false;
        h.GitHub.Override(EngineHarness.RunsPath, _ => h.GitHub.Error(HttpStatusCode.NotFound, "Not Found"));
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        // 第一輪不見只是還沒確認（spec §5.4 連續兩輪才算）；權限提示只屬於「id 還在、名稱沒變」的情況。
        Assert.Equal(1, h.Cache.GetRepoStates()[FakeData.RepoId].MissingRounds);
        Assert.DoesNotContain(ActionsNotFoundNote, h.RepoRow(FakeData.RepoId).ProblemNote ?? string.Empty, StringComparison.Ordinal);

        // 也不會立刻重送 E4 或再重抓清單：照 NotFoundRetryInterval 等下一次。
        var runs = h.GitHub.Count(EngineHarness.RunsPath);
        var lists = h.GitHub.Count(EngineHarness.ListAPath);
        await h.AdvanceInStepsAsync(Tuning.QuietMax, Tuning.ActiveRepoInterval);
        Assert.Equal(runs, h.GitHub.Count(EngineHarness.RunsPath));
        Assert.Equal(lists, h.GitHub.Count(EngineHarness.ListAPath));

        // 第二輪仍不見就移除；過程中從來沒有顯示權限提示。
        await h.AdvanceInStepsAsync(Tuning.RepoListRefresh, RetryStep);
        Assert.DoesNotContain(h.Last.Repos, row => row.Repo.Id == FakeData.RepoId);
        Assert.DoesNotContain(h.Snapshots, snapshot =>
            snapshot.Repos.Any(row => row.ProblemNote?.Contains(ActionsNotFoundNote, StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task 清單分頁中途失敗的那一輪不把E4的404標成Actions權限問題()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.RunsPath, _ => h.GitHub.Error(HttpStatusCode.NotFound, "Not Found"));
        h.GitHub.Override(EngineHarness.ListAPath, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"), times: 1);

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        // 這一輪的分頁失敗、沒看到這個 id：還不知道它在不在，不算消失，也不能說是權限問題（spec §5.4）。
        Assert.Contains(h.GitHub.Responses, response => response.PathAndQuery == EngineHarness.ListAPath && response.Status == (int)HttpStatusCode.BadGateway);
        Assert.Equal(0, h.Cache.GetRepoStates()[FakeData.RepoId].MissingRounds);
        Assert.DoesNotContain(ActionsNotFoundNote, h.RepoRow(FakeData.RepoId).ProblemNote ?? string.Empty, StringComparison.Ordinal);
    }

    // ── 第 3 輪：無法更新的起算點 ─────────────────────────────────────

    [Fact]
    public async Task 執行中才取消靜音的repo從開始監看時起算無法更新()
    {
        await using var h = EngineHarness.Create(h => h.Cache.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.StaleDataThreshold * 2);

        // 程式已經執行 2 小時才取消靜音；之後 workflow 清單一直回 5xx。
        h.GitHub.Override(EngineHarness.WorkflowsPath, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"));
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, RepoOverride.None));
        await h.DrainAsync();
        Assert.True(h.RepoRow(FakeData.RepoId).Monitored);

        // 剛開始監看，還沒有機會更新，不是「超過 1 小時沒有任何成功回應」（spec §7.3）。
        Assert.Equal(0, h.Last.Summary.StaleRepoCount);
        Assert.Null(h.Last.TrayIssueLine);
        await h.AdvanceInStepsAsync(Tuning.StaleDataThreshold - RetryStep, RetryStep);
        Assert.Equal(0, h.Last.Summary.StaleRepoCount);

        // 開始監看之後超過 1 小時仍沒有任何成功回應，才列入無法更新。
        await h.AdvanceInStepsAsync(RetryStep * 2, RetryStep);
        Assert.Equal(1, h.Last.Summary.StaleRepoCount);
        Assert.Equal("1 個 repo 無法更新", h.Last.TrayIssueLine);
    }

    // ── 第 3 輪：執行中損毀而改名失敗 ─────────────────────────────────

    /// <remarks>改名失敗靠 Windows 的檔案共用規則（開著的檔案沒有允許刪除就不能改名），所以只在 Windows 上執行。</remarks>
    [WindowsFact]
    public async Task 執行中cache_db損毀而改名失敗時退避重試且照常發布快照()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        CorruptTable(h.Workspace.Paths.CacheDbPath, "workflows");

        // 另一個程式開著 cache.db（沒有允許刪除），改名一定失敗。
        using (new FileStream(h.Workspace.Paths.CacheDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            // E4 第 1 頁多了一筆 run：寫入後重新讀這個 repo 的資料（含 workflows 表）時遇到損毀。
            h.Main.Runs.Insert(0, FakeData.Run(9002, 43, "completed", "success", Start.AddMinutes(1), Start.AddMinutes(2)));
            await h.AdvanceAsync(Tuning.QuietMax);
            Assert.Single(Regex.Matches(h.ErrorLogText, "重建快取資料庫"));
            Assert.Equal(BannerKind.UnexpectedError, h.Last.Banner?.Kind);

            // 退避期間不重試改名、不重讀 DB：每一步照樣發布快照（沿用上一份的內容），error.log 不再增加。
            var logged = h.ErrorLogText.Length;
            var published = h.Snapshots.Count;
            for (var step = 0; step < IdleSteps; step++)
            {
                Assert.Equal(0, await h.StepAsync());
            }

            Assert.Equal(logged, h.ErrorLogText.Length);
            Assert.Equal(published + IdleSteps, h.Snapshots.Count);
            Assert.Equal(BannerKind.UnexpectedError, h.Last.Banner?.Kind);
        }

        // 檔案放開之後，下一次重試就改名重建並重新抓取（spec §15）。
        await h.AdvanceAsync(Tuning.RepoErrorBackoffMax);

        Assert.Equal(BannerKind.DatabaseRecovered, h.Last.Banner?.Kind);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9002);
    }

    // ── 第 4 輪：404 的補抓也照重試間隔 ───────────────────────────────

    [Fact]
    public async Task E5回404後暫停再恢復或靜音再取消靜音不提前重送()
    {
        await using var h = EngineHarness.Create(h =>
            h.GitHub.Override(EngineHarness.SeedPath, _ => h.GitHub.Error(HttpStatusCode.NotFound, "Not Found")));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));

        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, RepoOverride.None));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));

        // 錯誤狀態的目標從上一次送出起算 NotFoundRetryInterval 才再試（spec §6.1），被移出排程再排回去也一樣。
        await h.AdvanceInStepsAsync(Tuning.NotFoundRetryInterval - RetryStep, RetryStep);
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));
        await h.AdvanceInStepsAsync(RetryStep * 2, RetryStep);
        Assert.Equal(2, h.GitHub.Count(EngineHarness.SeedPath));
    }

    [Fact]
    public async Task PR分支補抓回404後暫停再恢復不提前重送()
    {
        await using var h = EngineHarness.Create(h =>
        {
            ArrangeOpenPullRequestWithOlderFailure(h);
            h.GitHub.Override(PrSeedPath, _ => h.GitHub.Error(HttpStatusCode.NotFound, "Not Found"));
        });
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(PrSeedPath));

        // 恢復時 E7 回 304 會重新挑補抓對象：等 404 重試的補抓不能因此立即重送。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(PrSeedPath));

        await h.AdvanceInStepsAsync(Tuning.NotFoundRetryInterval - RetryStep, RetryStep);
        Assert.Equal(1, h.GitHub.Count(PrSeedPath));
        await h.AdvanceInStepsAsync(RetryStep * 2, RetryStep);
        Assert.Equal(2, h.GitHub.Count(PrSeedPath));
    }

    // ── 第 4 輪：未登入時的重新檢查 ───────────────────────────────────

    [Fact]
    public async Task 未登入時重新檢查遇到user回5xx只送一次()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Tokens.Returns(EngineHarness.OtherFakeToken);
            h.GitHub.Override("/user", _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"));
        });
        Assert.Equal(2, await h.StepAsync());
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);

        // 「重新檢查」拿回的仍是被拒的那一把（之後每 AuthRecheckInterval 的重新檢查也一樣），這次 E1 遇到 5xx。
        var observed = TimeSpan.FromMinutes(30);
        for (var recheck = 0; recheck <= observed / Tuning.AuthRecheckInterval; recheck++)
        {
            h.Tokens.Returns(EngineHarness.OtherFakeToken);
        }

        h.GitHub.RemoveOverrides("/user");
        h.GitHub.Override("/user", _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"));
        h.Engine.Post(new EngineCommand.RecheckAuth());
        await h.AdvanceInStepsAsync(observed, Tuning.OfflineProbeInitial);

        // 手動按才送的單一請求，不會變成用被拒的 token 連續重試（spec §13.2 第 3 點）；仍是未登入。
        Assert.Equal(3, h.GitHub.Count("/user"));
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);
    }

    [Fact]
    public async Task 未登入時換到的新token驗證遇到5xx後再按重新檢查仍會重試到驗證成功()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Tokens.Returns(EngineHarness.OtherFakeToken);
            h.GitHub.Override("/user", _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"));
        });
        Assert.Equal(2, await h.StepAsync());
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);

        // 重新檢查換到新的 token，它的 E1 遇到 5xx：新 token 要記住「還沒驗證」、退避後再送（handoff Task 10）。
        h.GitHub.RemoveOverrides("/user");
        h.GitHub.Override("/user", _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"));
        h.Tokens.Returns(ThirdFakeToken).Returns(ThirdFakeToken);
        h.Engine.Post(new EngineCommand.RecheckAuth());
        Assert.Equal(1, await h.StepAsync());

        // 驗證還沒成功時又按一次「重新檢查」，拿回同一把新 token：它不是被拒的那一把，仍要驗證到成功為止。
        h.Engine.Post(new EngineCommand.RecheckAuth());
        Assert.Equal(1, await h.StepAsync());
        h.GitHub.RemoveOverrides("/user");
        await h.AdvanceInStepsAsync(Tuning.RepoErrorBackoffMax, Tuning.OfflineProbeInitial);

        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
        Assert.Equal("Bearer " + ThirdFakeToken, h.GitHub.Handler.Requests.Last(r => r.Uri.AbsolutePath == "/user").Header("Authorization"));
    }

    [Fact]
    public async Task 未登入時重新檢查拿回同一把token而E1成功就恢復輪詢()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            h.Tokens.Returns(EngineHarness.OtherFakeToken);
            h.GitHub.Override("/user", _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"), times: 2);
        });
        Assert.Equal(2, await h.StepAsync());
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);

        // 例如你在 GitHub 上重新授權了同一把 token：重新檢查的那一次 E1 成功就恢復輪詢（spec §13.2 第 3 點）。
        h.Tokens.Returns(EngineHarness.OtherFakeToken);
        h.Engine.Post(new EngineCommand.RecheckAuth());
        await h.DrainAsync();

        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
        Assert.Equal(3, h.GitHub.Count("/user"));
        Assert.Contains(EngineHarness.RunsPath, h.GitHub.ApiRequests);
    }

    // ── 第 4 輪：錯誤退避與刷新模式 ───────────────────────────────────

    [Fact]
    public async Task E4持續5xx退避中切換固定刷新秒數不重設退避()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.RunsPath, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"));

        // 安靜的 repo 每 QuietMax 一次；這一次 5xx 之後退避到 RepoErrorBackoffMax（spec §6.4）。
        await h.AdvanceAsync(Tuning.QuietMax);
        var runs = h.GitHub.Count(EngineHarness.RunsPath);
        Assert.Equal((int)HttpStatusCode.BadGateway, h.GitHub.Responses.Last(response => response.PathAndQuery == EngineHarness.RunsPath).Status);

        // 剛送出就切到固定 1 分：只換掉正常的間隔，退避等級不變；成功一次才恢復正常間隔（spec §6.4）。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Minute1));
        await h.DrainAsync();
        await h.AdvanceInStepsAsync(Tuning.RepoErrorBackoffMax - Tuning.ActiveRepoInterval, Tuning.ActiveRepoInterval);
        Assert.Equal(runs, h.GitHub.Count(EngineHarness.RunsPath));

        await h.AdvanceInStepsAsync(Tuning.ActiveRepoInterval * 2, Tuning.ActiveRepoInterval);
        Assert.Equal(runs + 1, h.GitHub.Count(EngineHarness.RunsPath));
    }

    [Fact]
    public async Task E6持續5xx退避中標記完成時通知我不重設退避()
    {
        var stuck = StuckFeatureRun();
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(stuck));
        await h.DrainAsync();

        // 這筆 run 被擠出第 1 頁、改由 E6 以 QuietMax 追蹤；E6 一律回 5xx，第一次送出之後退避到 RepoErrorBackoffMax。
        h.Main.Runs.Clear();
        h.Main.OlderRuns.Add(stuck);
        h.GitHub.Override(RunDetailPath, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"));
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        await h.AdvanceInStepsAsync(Tuning.QuietMax, Tuning.ActiveRepoInterval);
        Assert.Equal(1, h.GitHub.Count(RunDetailPath));

        // 剛送出就標記「完成時通知我」：改用所屬 repo 的間隔，但退避中的不短於退避後的間隔（spec §6.4）。
        h.Engine.Post(new EngineCommand.SetWatched(9100, Watched: true));
        await h.DrainAsync();
        await h.AdvanceInStepsAsync(Tuning.RepoErrorBackoffMax - Tuning.ActiveRepoInterval, Tuning.ActiveRepoInterval);
        Assert.Equal(1, h.GitHub.Count(RunDetailPath));

        await h.AdvanceInStepsAsync(Tuning.ActiveRepoInterval * 2, Tuning.ActiveRepoInterval);
        Assert.Equal(2, h.GitHub.Count(RunDetailPath));
    }

    // ── 第 4 輪：無法更新用單調時鐘 ───────────────────────────────────

    [Fact]
    public async Task 系統時間往回調之後一直失敗的repo仍在1小時後列入無法更新()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        foreach (var path in new[] { EngineHarness.RunsPath, EngineHarness.PullsPath, EngineHarness.WorkflowsPath, EngineHarness.SeedPath })
        {
            h.GitHub.Override(path, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"));
        }

        // 只把牆鐘往回調一天；之後這個 repo 的每個請求都失敗。
        h.Clock.Manual.SetUtcNow(h.Clock.GetUtcNow().AddDays(-1));
        await h.AdvanceInStepsAsync(Tuning.StaleDataThreshold * 2, RetryStep);

        // 「超過 1 小時沒有任何成功回應」看的是本機經過的時間（spec §7.3），不受系統時間調整影響（spec §15）。
        Assert.Equal(1, h.Last.Summary.StaleRepoCount);
        Assert.Contains("1 個 repo 無法更新", h.Last.TrayIssueLine);
    }

    [Fact]
    public async Task 系統時間往前調時正常更新的repo不算無法更新()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start)));
        await h.DrainAsync();
        await h.AdvanceInStepsAsync(Tuning.StaleDataThreshold + RetryStep, Tuning.ActiveRepoInterval);
        Assert.Equal(0, h.Last.Summary.StaleRepoCount);
        Assert.Equal(1, h.Last.Summary.PendingCount);

        // 只把牆鐘往前調 2 小時：這一步沒有送出任何請求，判定結果不能因此改變（spec §15）。
        h.Clock.Manual.SetUtcNow(h.Clock.GetUtcNow() + Tuning.StaleDataThreshold * 2);
        Assert.Equal(0, await h.StepAsync());

        Assert.Equal(0, h.Last.Summary.StaleRepoCount);
        Assert.Equal(1, h.Last.Summary.PendingCount);
        Assert.Null(h.Last.TrayIssueLine);
    }

    // ── 第 4 輪：暫停模式 ─────────────────────────────────────────────

    [Fact]
    public async Task 以暫停模式啟動後恢復時立即完整重抓()
    {
        await using var h = EngineHarness.Create(h => h.Settings.RefreshMode = RefreshMode.Paused);
        await h.DrainAsync();
        Assert.Equal(0, h.GitHub.Count(EngineHarness.ListAPath));

        await h.AdvanceAsync(RetryStep);
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();

        // 這次執行還沒做過啟動時的完整重抓：恢復時補做（spec §5.3），之後每 RepoListRefresh 一次。
        Assert.Equal(1, h.GitHub.Count(EngineHarness.ListAPath));
        await h.AdvanceInStepsAsync(Tuning.RepoListRefresh - RetryStep, RetryStep);
        Assert.Equal(1, h.GitHub.Count(EngineHarness.ListAPath));
        await h.AdvanceInStepsAsync(RetryStep * 2, RetryStep);
        Assert.Equal(2, h.GitHub.Count(EngineHarness.ListAPath));
    }

    [Fact]
    public async Task 暫停模式下E4和E7回403超過重試間隔後F5各送一次()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.RunsPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
            ("X-Accepted-GitHub-Permissions", "actions=read")), times: 1);
        h.GitHub.Override(EngineHarness.PullsPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden,
            "Resource not accessible by personal access token", ("X-Accepted-GitHub-Permissions", "pull_requests=read")), times: 1);
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Contains("權限不足，需要 actions=read", h.RepoRow(FakeData.RepoId).ProblemNote);
        Assert.Contains("缺少 Pull requests 權限或無法存取", h.RepoRow(FakeData.RepoId).ProblemNote);
        var runs = h.GitHub.Count(EngineHarness.RunsPath);
        var pulls = h.GitHub.Count(EngineHarness.PullsPath);

        // 你修好權限之後切成暫停：重試間隔內按 F5 仍不提前送（spec §6.1）。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal(runs, h.GitHub.Count(EngineHarness.RunsPath));
        Assert.Equal(pulls, h.GitHub.Count(EngineHarness.PullsPath));

        // 超過重試間隔之後：暫停時 F5 是唯一的刷新方式（spec §6.2、§6.6），E4 與 E7 各送一次，問題標示跟著清掉。
        await h.AdvanceAsync(Tuning.PermissionRetryInterval + RetryStep);
        Assert.Equal(runs, h.GitHub.Count(EngineHarness.RunsPath));
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Equal(runs + 1, h.GitHub.Count(EngineHarness.RunsPath));
        Assert.Equal(pulls + 1, h.GitHub.Count(EngineHarness.PullsPath));
        Assert.Null(h.RepoRow(FakeData.RepoId).ProblemNote);
    }

    [Fact]
    public async Task 暫停模式下第一次E3回403超過重試間隔後F5再送E3並接著送E4()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Settings.RefreshMode = RefreshMode.Paused;
            h.GitHub.Override(EngineHarness.WorkflowsPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
                ("X-Accepted-GitHub-Permissions", "actions=read")), times: 1);
        });
        await h.DrainAsync();

        // 暫停模式下按 F5 才發現這個 repo；第一次 E3 回權限不足，有 workflow 才 E4，所以 E4 也還沒送。
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.WorkflowsPath));
        Assert.Contains("權限不足，需要 actions=read", h.RepoRow(FakeData.RepoId).ProblemNote);

        // 重試間隔內按 F5 不提前送（spec §6.1）。
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.WorkflowsPath));

        // 超過重試間隔之後的 F5 再送 E3，查到 workflow 就接著送 E4（spec §6.2、§6.6、§5.3）。
        await h.AdvanceAsync(Tuning.PermissionRetryInterval + RetryStep);
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Equal(2, h.GitHub.Count(EngineHarness.WorkflowsPath));
        Assert.Equal(1, h.GitHub.Count(EngineHarness.RunsPath));
        Assert.Null(h.RepoRow(FakeData.RepoId).ProblemNote);
    }

    // ── 第 4 輪：時間界線 ─────────────────────────────────────────────

    [Fact]
    public async Task 執行中重建資料庫後損毀那一步完成的追蹤失敗通知一次且重建前通知過的不重發()
    {
        var running = FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start);
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Workflows.Add(FakeData.Workflow(id: LintWorkflowId, name: LintWorkflowName, path: LintWorkflowPath));
            h.Main.Runs.Add(running);
        });
        await h.DrainAsync();

        // 啟動之後 Lint 失敗：通知一次（spec §9.1 N1）。
        h.Main.Runs.Insert(0, FakeData.Run(9002, 7, "completed", "failure", Start.AddSeconds(5), Start.AddSeconds(10),
            workflowId: LintWorkflowId, name: LintWorkflowName));
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);
        Assert.Equal("失敗：octo-org/sample-app · Lint #7", Assert.Single(h.Toasts).Title);
        h.Engine.Post(new EngineCommand.ToastDismissed());

        // run_attempts 表損毀；同時 CI 失敗：寫入這次完成時才讀到損毀，改名重建後重新抓取。
        CorruptTable(h.Workspace.Paths.CacheDbPath, "run_attempts");
        FakeData.Complete(running, "failure", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));

        // 界線仍是程式啟動時間（spec §9.3、§14.3）：損毀那一步完成的 CI 失敗照常通知一次；
        // 重建前已經通知過的 Lint 失敗不再通知（notified 跟著清空了，靠這次執行記下的去重鍵）。
        Assert.Equal(2, h.Toasts.Count);
        Assert.Equal("失敗：octo-org/sample-app · CI #42", h.Toasts[^1].Title);
        Assert.True(h.Cache.Contains(9001, 1, "failure"));
    }

    [Fact]
    public async Task 改用另一個帳號之前完成的失敗不通知改了之後才完成的照常通知()
    {
        const long bobRepoId = 2050;
        const string bobRepo = "bob/tool";
        const string bobRunsPath = "/repos/bob/tool/actions/runs?per_page=30";
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        await h.AdvanceAsync(TimeSpan.FromHours(3));

        // 設定改成 PAT，貼上的是另一個帳號 bob 的 token；bob 的 repo 在改設定之前 2 小時就失敗了。
        var bob = h.GitHub.AddRepo(FakeData.Repo(bobRepoId, bobRepo, Start.AddHours(1), ownerId: BobId, ownerType: "User"));
        bob.Runs.Add(FakeData.Run(9501, 3, "completed", "failure", Start.AddHours(1), Start.AddHours(1).AddMinutes(2),
            repo: bobRepo, headRepoId: bobRepoId, actorId: BobId, actorLogin: BobLogin));
        h.GitHub.User = FakeData.User(BobId, BobLogin);
        h.Tokens.Returns(EngineHarness.OtherFakeToken, TokenSource.Pat);
        h.Settings.AuthMode = AuthMode.Pat;
        var changedAt = h.Clock.GetUtcNow();
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // 換了帳號是因為你改了設定才開始監看（spec §9.3）：新帳號的 repo 以那個時間為界線，之前完成的只記 suppressed。
        Assert.Equal(BobId, h.Cache.GetAccount()?.UserId);
        Assert.Contains(bobRunsPath, h.GitHub.ApiRequests);
        Assert.True(h.RepoRow(bobRepoId).Monitored);
        Assert.Empty(h.Toasts);
        Assert.True(h.Cache.Contains(9501, 1, "failure"));
        Assert.Equal(changedAt, h.Cache.GetRepoStates()[bobRepoId].BaselineT0);

        // 改了之後才完成的失敗照常通知。
        bob.Runs.Insert(0, FakeData.Run(9502, 4, "completed", "failure", changedAt.AddSeconds(30), changedAt.AddMinutes(1),
            repo: bobRepo, headRepoId: bobRepoId, actorId: BobId, actorLogin: BobLogin));
        await h.AdvanceAsync(Tuning.QuietMax);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        Assert.Equal("失敗：bob/tool · CI #4", Assert.Single(h.Toasts).Title);
    }

    // ── 第 4 輪：E1 處理丟出例外 ──────────────────────────────────────

    [Fact]
    public async Task account表寫入失敗時時間不前進連跑多步user只送1次()
    {
        await using var h = EngineHarness.Create(h => Execute(h.Workspace.Paths.CacheDbPath, BlockAccountWrites));

        for (var step = 0; step < IdleSteps; step++)
        {
            await h.StepAsync();
        }

        // 帳號寫不進 DB 時先留在記憶體、照常開始輪詢（spec §15 單輪失敗不讓迴圈停掉）：不會每一步都重送 E1、都寫一筆 error.log。
        Assert.Equal(1, h.GitHub.Count("/user"));
        Assert.Single(Regex.Matches(h.ErrorLogText, InjectedWriteFailure));
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
        Assert.Contains(EngineHarness.RunsPath, h.GitHub.ApiRequests);
    }

    [Fact]
    public async Task account表寫入失敗時背景迴圈照常輪詢而不重送user()
    {
        var runsSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = EngineHarness.Create(h =>
        {
            Execute(h.Workspace.Paths.CacheDbPath, BlockAccountWrites);
            h.GitHub.OnRequest = request =>
            {
                if (request.RequestUri!.PathAndQuery == EngineHarness.RunsPath)
                {
                    runsSeen.TrySetResult();
                }
            };
        });

        // 時鐘不前進：修正前背景迴圈每一步都重送 E1，直到節流器的每分鐘上限，之後一直等，E4 永遠送不出去。
        await h.Engine.StartAsync(None);
        await runsSeen.Task.WaitAsync(Guard);

        Assert.Equal(1, h.GitHub.Count("/user"));
    }

    [Fact]
    public async Task 送出E1前讀http_cache失敗時退避而不是每一步都重試()
    {
        await using var h = EngineHarness.Create(h =>
            Execute(h.Workspace.Paths.CacheDbPath, "ALTER TABLE http_cache RENAME TO http_cache_hidden;"));

        for (var step = 0; step < IdleSteps; step++)
        {
            await h.StepAsync();
        }

        // E1 送出前讀 ETag 就失敗：和連線失敗一樣退避後再送（spec §6.4、§15），不會每一步都重試、都寫一筆 error.log。
        Assert.Single(Regex.Matches(h.ErrorLogText, "no such table: http_cache"));
        Assert.Equal(0, h.GitHub.Count("/user"));

        Execute(h.Workspace.Paths.CacheDbPath, "ALTER TABLE http_cache_hidden RENAME TO http_cache;");
        await h.AdvanceAsync(Tuning.OfflineProbeInitial);

        Assert.Equal(1, h.GitHub.Count("/user"));
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
    }

    // ── 第 4 輪：檢查連線用的是哪一把 token ───────────────────────────

    [Fact]
    public async Task 檢查連線的E1回來時token已經換掉就不套用帳號()
    {
        using var seen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        var blockOnce = new StrongBox<bool>(true);
        h.GitHub.Override("/user", request => UserByToken(request, blockOnce, seen, release, otherTokenStatus: HttpStatusCode.OK));

        var check = Task.Run(() => h.Engine.CheckConnectionAsync(None));
        Assert.True(seen.Wait(Guard));

        // 檢查連線的 E1（舊 token）還在路上時按了「重新檢查」，換到另一個帳號的 token，輪詢用它驗證成功。
        h.Tokens.Returns(EngineHarness.OtherFakeToken);
        h.Engine.Post(new EngineCommand.RecheckAuth());
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(BobId, h.Cache.GetAccount()?.UserId);

        release.Set();
        await check.WaitAsync(Guard);
        await h.DrainAsync();

        // 舊 token 的結果只回給「檢查連線」，不能蓋掉輪詢已經用新 token 驗證過的帳號。
        Assert.Equal(BobId, h.Cache.GetAccount()?.UserId);
    }

    [Fact]
    public async Task 檢查連線的E1回來時新token還沒驗證就不開始輪詢()
    {
        using var seen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        var blockOnce = new StrongBox<bool>(true);
        h.GitHub.Override("/user", request => UserByToken(request, blockOnce, seen, release, otherTokenStatus: HttpStatusCode.BadGateway));

        var check = Task.Run(() => h.Engine.CheckConnectionAsync(None));
        Assert.True(seen.Wait(Guard));

        // 重新檢查換到新 token，它的 E1 遇到 5xx、還在退避：新 token 還沒驗證（handoff Task 10）。
        h.Tokens.Returns(EngineHarness.OtherFakeToken);
        h.Engine.Post(new EngineCommand.RecheckAuth());
        Assert.Equal(1, await h.StepAsync());
        var runs = h.GitHub.Count(EngineHarness.RunsPath);

        release.Set();
        await check.WaitAsync(Guard);
        await h.DrainAsync();

        // 舊 token 的檢查結果不能讓新 token 跳過 E1 驗證就開始輪詢（spec §13.2）。
        Assert.Equal(runs, h.GitHub.Count(EngineHarness.RunsPath));
    }

    // ── 第 5 輪：5xx 退避中的目標被移出排程再排回去 ───────────────────

    [Fact]
    public async Task E5回5xx退避中暫停再恢復或靜音再取消靜音不提前重送()
    {
        await using var h = EngineHarness.Create(h =>
            h.GitHub.Override(EngineHarness.SeedPath, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error")));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));

        // 5xx 讓 E5 退避（不短於正常間隔 SeedRecheck，spec §6.4）；暫停、靜音把它移出排程，排回去時仍照退避，不立即補抓。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));

        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, RepoOverride.None));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));

        // 從上一次送出起算退避後的間隔才再試；成功一次才恢復正常間隔（spec §6.4）。
        await h.AdvanceInStepsAsync(Tuning.SeedRecheck - RetryStep, RetryStep);
        Assert.Equal(1, h.GitHub.Count(EngineHarness.SeedPath));
        await h.AdvanceInStepsAsync(RetryStep * 2, RetryStep);
        Assert.Equal(2, h.GitHub.Count(EngineHarness.SeedPath));
    }

    [Fact]
    public async Task PR分支補抓回5xx退避中暫停再恢復不提前重送()
    {
        await using var h = EngineHarness.Create(h =>
        {
            ArrangeOpenPullRequestWithOlderFailure(h);
            h.GitHub.Override(PrSeedPath, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"));
        });
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(PrSeedPath));

        // 恢復時 E7 回 304 會重新挑補抓對象：退避中的補抓照退避後的間隔（spec §6.4），不立即重送。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(PrSeedPath));

        await h.AdvanceInStepsAsync(Tuning.QuietMax - Tuning.ActiveRepoInterval, Tuning.ActiveRepoInterval);
        Assert.Equal(1, h.GitHub.Count(PrSeedPath));
        await h.AdvanceInStepsAsync(Tuning.ActiveRepoInterval * 2, Tuning.ActiveRepoInterval);
        Assert.Equal(2, h.GitHub.Count(PrSeedPath));
    }

    [Fact]
    public async Task E6回5xx退避中暫停再恢復不重設退避()
    {
        var stuck = StuckFeatureRun();
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(stuck));
        await h.DrainAsync();

        // 這筆 run 被擠出第 1 頁、改由 E6 以 QuietMax 追蹤；E6 一律回 5xx，第一次送出之後退避到 RepoErrorBackoffMax。
        h.Main.Runs.Clear();
        h.Main.OlderRuns.Add(stuck);
        h.GitHub.Override(RunDetailPath, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"));
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        await h.AdvanceInStepsAsync(Tuning.QuietMax, Tuning.ActiveRepoInterval);
        Assert.Equal(1, h.GitHub.Count(RunDetailPath));

        // 剛送出就暫停再恢復：排回去的 E6 從上一次送出起算退避後的間隔，退避等級不重設（spec §6.4）。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();
        await h.AdvanceInStepsAsync(Tuning.RepoErrorBackoffMax - Tuning.ActiveRepoInterval, Tuning.ActiveRepoInterval);
        Assert.Equal(1, h.GitHub.Count(RunDetailPath));

        await h.AdvanceInStepsAsync(Tuning.ActiveRepoInterval * 2, Tuning.ActiveRepoInterval);
        Assert.Equal(2, h.GitHub.Count(RunDetailPath));
    }

    [Fact]
    public async Task stale首次檢查回5xx退避中暫停再恢復不提前重送()
    {
        await using var h = EngineHarness.Create(h =>
        {
            ArrangeStaleRepoWithRecentRun(h);
            h.GitHub.Override(StaleWorkflowsPath, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"));
        });
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(StaleWorkflowsPath));

        // stale 首次檢查（Q7）的 5xx 也只讓它自己退避（spec §6.4）：暫停再恢復時不立即重送。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(StaleWorkflowsPath));

        await h.AdvanceInStepsAsync(Tuning.QuietMax - Tuning.ActiveRepoInterval, Tuning.ActiveRepoInterval);
        Assert.Equal(1, h.GitHub.Count(StaleWorkflowsPath));
        await h.AdvanceInStepsAsync(Tuning.ActiveRepoInterval * 2, Tuning.ActiveRepoInterval);
        Assert.Equal(2, h.GitHub.Count(StaleWorkflowsPath));
    }

    // ── 第 5 輪：不屬於 repo 的目標等錯誤重試 ─────────────────────────

    [Fact]
    public async Task 探測回403等重試時切換固定刷新秒數或暫停再恢復不提前重送()
    {
        await using var h = EngineHarness.Create(h =>
            h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, ForbiddenByPolicy)));
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.Equal(1, h.GitHub.Count(EngineHarness.ProbeAPath));

        // 非限流的 403：探測改成每 PermissionRetryInterval 重試一次（spec §6.1 最後一列、§6.4 第 4 列），
        // 切換固定秒數不提前重送。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Minute1));
        await h.DrainAsync();
        await h.AdvanceInStepsAsync(RetryStep, Tuning.ProbeInterval);
        Assert.Equal(1, h.GitHub.Count(EngineHarness.ProbeAPath));

        // 暫停再恢復（全量刷新加上排回週期性目標）也一樣。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(EngineHarness.ProbeAPath));

        await h.AdvanceInStepsAsync(Tuning.PermissionRetryInterval - RetryStep * 2, RetryStep);
        Assert.Equal(1, h.GitHub.Count(EngineHarness.ProbeAPath));
        await h.AdvanceInStepsAsync(RetryStep * 2, RetryStep);
        Assert.Equal(2, h.GitHub.Count(EngineHarness.ProbeAPath));
    }

    [Fact]
    public async Task 探測回403後再試成功就恢復隨全量刷新立即送出()
    {
        await using var h = EngineHarness.Create(h =>
            h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, ForbiddenByPolicy), times: 1));
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.Equal(1, h.GitHub.Count(EngineHarness.ProbeAPath));

        // 照重試間隔再試一次就成功：不再是錯誤狀態的目標，F5 照常立即送探測（spec §6.6）。
        await h.AdvanceInStepsAsync(Tuning.PermissionRetryInterval, RetryStep);
        Assert.Equal(2, h.GitHub.Count(EngineHarness.ProbeAPath));
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal(3, h.GitHub.Count(EngineHarness.ProbeAPath));
    }

    // ── 第 5 輪：等驗證期間拿不到 token ───────────────────────────────

    [Fact]
    public async Task E1退避中重新檢查拿不到token之後不送不帶憑證的請求()
    {
        await using var h = EngineHarness.Create(h => h.GitHub.Override("/user", UserFailsWithToken(h)));
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(AuthStatus.Unknown, h.Last.Auth);

        // 啟動時的 E1 遇到 5xx、還在退避；這時 gh 已經登出，按「重新檢查」拿不到任何 token：進入未登入（spec §13.2 第 2 點）。
        h.Tokens.Returns(token: null);
        h.Engine.Post(new EngineCommand.RecheckAuth());
        await h.StepAsync();
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);

        // 未登入時停止所有輪詢：原本等退避的 E1 不能不帶憑證送出（spec §13.2、§17「未登入狀態不送 API」）。
        var requests = h.GitHub.ApiRequests.Count;
        await h.AdvanceInStepsAsync(Tuning.AuthRecheckInterval - Tuning.OfflineProbeInitial, Tuning.OfflineProbeInitial);
        Assert.Equal(requests, h.GitHub.ApiRequests.Count);
        Assert.DoesNotContain(h.GitHub.Handler.Requests, request => request.Uri.Host == FakeGitHub.ApiHost && request.Header("Authorization") is null);
    }

    [Fact]
    public async Task E1退避中使用者操作收到401而重取不到token之後不送不帶憑證的請求()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20))));
        await h.DrainAsync();
        await h.RestartAsync();
        h.GitHub.Override("/user", UserFailsWithToken(h));
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(AuthStatus.Unknown, h.Last.Auth);

        // E1 還在退避時複製失敗 log：E8 收到 401，重新取得 token 卻拿不到（例如 gh 已登出），進入未登入（spec §13.2 第 2 點）。
        h.GitHub.Override(JobsPath, _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"));
        h.Tokens.Returns(token: null);
        var copy = await h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None);
        Assert.False(copy.Success);
        await h.StepAsync();
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);

        var requests = h.GitHub.ApiRequests.Count;
        await h.AdvanceInStepsAsync(Tuning.AuthRecheckInterval - Tuning.OfflineProbeInitial, Tuning.OfflineProbeInitial);
        Assert.Equal(requests, h.GitHub.ApiRequests.Count);
        Assert.DoesNotContain(h.GitHub.Handler.Requests, request => request.Uri.Host == FakeGitHub.ApiHost && request.Header("Authorization") is null);
    }

    // ── 第 5 輪：執行中重建資料庫 ─────────────────────────────────────

    [Fact]
    public async Task 執行中重建資料庫後原本靜音的repo在靜音期間完成的失敗只記suppressed()
    {
        var otherRunning = FakeData.Run(9301, 5, "in_progress", null, Start.AddMinutes(-1), Start,
            repo: OtherRepoName, headRepoId: OtherRepoId, actorId: BobId, actorLogin: BobLogin);
        await using var h = EngineHarness.Create(h =>
        {
            h.Cache.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false));
            h.GitHub.AddRepo(FakeData.Repo(OtherRepoId, OtherRepoName, Start.AddDays(-2))).Runs.Add(otherRunning);
        });
        await h.DrainAsync();
        Assert.False(h.RepoRow(FakeData.RepoId).Monitored);

        // 靜音期間（程式啟動之後）預設分支失敗；靜音的 repo 不輪詢，這筆失敗從沒被觀察過。
        h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(10), Start.AddMinutes(12)));
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(15), Tuning.ActiveRepoInterval);

        // run_attempts 表損毀；other-app 的 run 完成時寫入才讀到損毀，改名重建後重新抓取。靜音設定跟著遺失（spec §14.3、§15）。
        CorruptTable(h.Workspace.Paths.CacheDbPath, "run_attempts");
        FakeData.Complete(otherRunning, "success", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        var rebuiltAt = h.Clock.GetUtcNow();
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(10), Tuning.ActiveRepoInterval);

        // 重建後不會一次跳出一堆舊通知（spec §9.3 最後一點、§14.3）：原本靜音的 repo 是因為重建才開始監看，
        // 界線是重建的時間（同「取消靜音」以操作時間為界線，spec §9.3），靜音期間完成的只記 suppressed。
        Assert.True(h.RepoRow(FakeData.RepoId).Monitored);
        Assert.Empty(h.Toasts);
        Assert.True(h.Cache.Contains(9001, 1, "failure"));
        Assert.Equal(rebuiltAt, h.Cache.GetRepoStates()[FakeData.RepoId].BaselineT0);
    }

    [Fact]
    public async Task 執行中重建資料庫後剛取消靜音的repo仍以取消靜音的時間為界線()
    {
        await using var h = EngineHarness.Create(h => h.Cache.SetOverride(FakeData.RepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.DrainAsync();

        // 靜音期間預設分支失敗；之後取消靜音，第一次 E4 寫入這筆 run 時才讀到 run_attempts 表損毀，改名重建。
        h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(3), Start.AddMinutes(5)));
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        CorruptTable(h.Workspace.Paths.CacheDbPath, "run_attempts");
        var unmutedAt = h.Clock.GetUtcNow();
        h.Engine.Post(new EngineCommand.SetOverride(FakeData.RepoId, RepoOverride.None));
        await h.DrainAsync();
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(10), Tuning.ActiveRepoInterval);

        // 重建前的界線（取消靜音的時間，spec §9.3）不因重建而提早成程式啟動時間：靜音期間完成的失敗只記 suppressed。
        Assert.True(h.RepoRow(FakeData.RepoId).Monitored);
        Assert.Empty(h.Toasts);
        Assert.True(h.Cache.Contains(9001, 1, "failure"));
        Assert.Equal(unmutedAt, h.Cache.GetRepoStates()[FakeData.RepoId].BaselineT0);
    }

    [Fact]
    public async Task 執行中重建資料庫後被標記的run完成時仍發完成時通知我的提示()
    {
        var running = FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start, branch: "feature/x", sha: FakeData.FeatureSha,
            actorId: BobId, actorLogin: BobLogin);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(running));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetWatched(9001, Watched: true));
        await h.StepAsync();
        Assert.Contains(9001L, h.Last.WatchedRunIds);

        // run_attempts 表損毀；同時這筆 run 成功：寫入時才讀到損毀，改名重建後重新抓取。
        CorruptTable(h.Workspace.Paths.CacheDbPath, "run_attempts");
        FakeData.Complete(running, "success", h.Clock.GetUtcNow());
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(10), Tuning.ActiveRepoInterval);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));

        // spec §14.3 只有靜音與強制納入會遺失：「完成時通知我」的標記保留，重新抓回來的完成照常通知一次，之後取消標記（spec §9.1 N3）。
        Assert.Equal("成功：octo-org/sample-app · CI #42", Assert.Single(h.Toasts).Title);
        Assert.Empty(h.Last.WatchedRunIds);
        Assert.Empty(h.Cache.GetWatched());
    }

    // ── 第 5 輪：換帳號讓認得的 repo 轉成監看 ─────────────────────────

    [Fact]
    public async Task 換帳號讓略過的協作repo變成自己的repo時換帳號之前完成的失敗只記suppressed()
    {
        RepoWorld bob = null!;
        await using var h = EngineHarness.Create(h =>
        {
            h.Settings.IncludeCollaboratorRepos = true;
            bob = h.GitHub.AddRepo(FakeData.Repo(BobRepoId, BobRepoName, Start.AddDays(-2), ownerId: BobId, ownerType: "User"), collaborator: true);
        });
        await h.DrainAsync();
        Assert.True(h.RepoRow(BobRepoId).Monitored);

        // 關掉「包含協作 repo」：bob/tool 變成略過（第 5 條）；略過期間它的預設分支失敗。
        h.Settings.IncludeCollaboratorRepos = false;
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.DrainAsync();
        Assert.Equal(SkipReason.Collaborator, h.RepoRow(BobRepoId).Reason);
        bob.Runs.Add(FakeData.Run(9501, 3, "completed", "failure", Start.AddHours(1), Start.AddHours(1).AddMinutes(2),
            repo: BobRepoName, headRepoId: BobRepoId, actorId: BobId, actorLogin: BobLogin));
        await h.AdvanceAsync(TimeSpan.FromHours(3));

        // 設定改成 bob 的 PAT：bob/tool 對新帳號是自己的 repo（清單 A），因為換帳號才開始監看。
        h.GitHub.Collaborated.Remove(bob);
        h.GitHub.Owned.Add(bob);
        h.GitHub.User = FakeData.User(BobId, BobLogin);
        h.Tokens.Returns(EngineHarness.OtherFakeToken, TokenSource.Pat);
        h.Settings.AuthMode = AuthMode.Pat;
        var changedAt = h.Clock.GetUtcNow();
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // 因為你的操作才開始監看，界線是換帳號的時間（spec §9.3「改設定」）：換帳號之前完成的只記 suppressed。
        Assert.Equal(RepoCategory.Own, h.RepoRow(BobRepoId).Repo.Category);
        Assert.True(h.RepoRow(BobRepoId).Monitored);
        Assert.Empty(h.Toasts);
        Assert.True(h.Cache.Contains(9501, 1, "failure"));
        Assert.Equal(changedAt, h.Cache.GetRepoStates()[BobRepoId].BaselineT0);
    }

    // ── 第 5 輪：最後更新時間 ─────────────────────────────────────────

    [Fact]
    public async Task 以離線狀態啟動時橫幅寫上次的最後更新時間()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.QuietMax);
        var lastSuccess = h.Cache.GetRepoStates()[FakeData.RepoId].LastSuccessAt;
        Assert.NotNull(lastSuccess);

        // 重新啟動時網路不通：畫面列出快取的 run，連續連線失敗後轉成離線。
        h.GitHub.NetworkDown = true;
        await h.RestartAsync();
        await h.AdvanceInStepsAsync(Tuning.OfflineProbeMax, Tuning.OfflineProbeInitial);
        Assert.True(h.Last.Offline);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);

        // 保留最後資料，標示「離線 · 最後更新時間」（spec §15、§10.2）：最後更新是 DB 記下的最近一次成功回應（spec §7.4）。
        Assert.Equal(lastSuccess, h.Last.LastUpdatedUtc);
        var local = TimeZoneInfo.ConvertTime(lastSuccess.Value, h.Clock.LocalTimeZone).ToString(EngineText.LongTimeFormat, CultureInfo.InvariantCulture);
        Assert.Equal("離線 · 最後更新 " + local, h.Last.Banner?.Text);
    }

    // ── 第 5 輪：啟動時損毀而改名失敗 ─────────────────────────────────

    /// <remarks>改名失敗靠 Windows 的檔案共用規則（開著的檔案沒有允許刪除就不能改名），所以只在 Windows 上執行。</remarks>
    [WindowsFact]
    public async Task 啟動時第一份快照讀到損毀而改名失敗時不每一步都寫error_log並照常發布快照()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        await h.RestartAsync();

        // ETA 樣本的索引損毀：啟動時讀 DB 用不到它（初始化照樣成功），組第一份快照讀 ETA 樣本時才讀到損毀。
        CorruptTable(h.Workspace.Paths.CacheDbPath, DurationSamplesIndex);
        var published = h.Snapshots.Count;
        using (new FileStream(h.Workspace.Paths.CacheDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            // 另一個程式開著 cache.db，改名重建失敗而退避：還沒有上一份快照可以沿用，也不能每一步都重讀 DB、都寫 error.log（spec §15）。
            for (var step = 0; step < IdleSteps; step++)
            {
                await h.StepAsync();
            }

            Assert.Single(Regex.Matches(h.ErrorLogText, "建立快照"));
            Assert.Single(Regex.Matches(h.ErrorLogText, "重建快取資料庫"));
            Assert.Equal(published + IdleSteps, h.Snapshots.Count);
            Assert.Equal(BannerKind.UnexpectedError, h.Last.Banner?.Kind);
            Assert.Contains(h.Last.Repos, row => row.Repo.Id == FakeData.RepoId);
        }

        // 檔案放開之後，下一次重試就改名重建並重新抓取（spec §15）。
        await h.AdvanceAsync(Tuning.RepoErrorBackoffMax);

        Assert.Equal(BannerKind.DatabaseRecovered, h.Last.Banner?.Kind);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
    }

    // ── 第 5 輪：E1 送出之後才收到的 401 ──────────────────────────────

    [Fact]
    public async Task 檢查連線的E1回來時輪詢已經用同一把token進入未登入就不恢復Ok()
    {
        using var seen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        var blockOnce = new StrongBox<bool>(true);
        h.GitHub.Override("/user", request => UserByToken(request, blockOnce, seen, release, otherTokenStatus: HttpStatusCode.OK));

        var check = Task.Run(() => h.Engine.CheckConnectionAsync(None));
        Assert.True(seen.Wait(Guard));

        // 檢查連線的 E1 還在路上時 token 被撤銷：輪詢收到 401，重新取得 token 拿回同一把，進入未登入（spec §13.2 第 2 點）。
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"));
        h.Tokens.Returns(Fixture.FakeToken);
        h.Engine.Post(new EngineCommand.RefreshAll());
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);
        var probes = h.GitHub.Count(EngineHarness.ProbeAPath);

        release.Set();
        await check.WaitAsync(Guard);
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        // 那次 E1 在 401 之前送出，它的 200 不能把狀態改回 Ok：之後不再用被拒的 token 送出請求（spec §13.2）。
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);
        Assert.Equal(probes, h.GitHub.Count(EngineHarness.ProbeAPath));
    }

    [Fact]
    public async Task 啟動驗證的E1回來時使用者操作已經用同一把token進入未登入就不開始輪詢()
    {
        using var seen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20))));
        await h.DrainAsync();
        await h.RestartAsync();
        var blockOnce = new StrongBox<bool>(true);
        h.GitHub.Override("/user", request => UserByToken(request, blockOnce, seen, release, otherTokenStatus: HttpStatusCode.OK));
        var requests = h.GitHub.ApiRequests.Count;

        // 啟動時的 E1 還在路上：同一時間複製失敗 log 的 E8 收到 401，重新取得 token 拿回同一把，進入未登入（spec §13.2 第 2 點）。
        var verify = Task.Run(() => h.StepAsync());
        Assert.True(seen.Wait(Guard));
        h.GitHub.Override(JobsPath, _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"));
        h.Tokens.Returns(Fixture.FakeToken);
        var copy = await h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None);
        Assert.False(copy.Success);

        release.Set();
        await verify.WaitAsync(Guard);
        await h.DrainAsync();

        // E1 的 200 不能把狀態改成 Ok：不開始輪詢，不再用被拒的 token 送出請求（spec §13.2）。
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);
        Assert.Equal(requests + 2, h.GitHub.ApiRequests.Count);
    }

    [Fact]
    public async Task 已登入時重新檢查的E1回來時使用者操作已經用同一把token進入未登入就不恢復Ok()
    {
        using var seen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20))));
        await h.DrainAsync();
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
        var blockOnce = new StrongBox<bool>(true);
        h.GitHub.Override("/user", request => UserByToken(request, blockOnce, seen, release, otherTokenStatus: HttpStatusCode.OK));

        // 已登入時按「重新檢查」拿回同一把 token，這次 E1 還在路上時，複製失敗 log 的 E8 收到 401、重新取得仍是同一把，進入未登入。
        h.Tokens.Returns(Fixture.FakeToken);
        h.Engine.Post(new EngineCommand.RecheckAuth());
        var recheck = Task.Run(() => h.StepAsync());
        Assert.True(seen.Wait(Guard));
        h.GitHub.Override(JobsPath, _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"));
        h.Tokens.Returns(Fixture.FakeToken);
        var copy = await h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None);
        Assert.False(copy.Success);
        var requests = h.GitHub.ApiRequests.Count;

        release.Set();
        await recheck.WaitAsync(Guard);
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        // 那次 E1 在 401 之前送出，它的 200 不能把狀態改回 Ok（spec §13.2）。
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);
        Assert.Equal(requests, h.GitHub.ApiRequests.Count);
    }

    // ── 第 6 輪：舊 token 遲到的 401 ──────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 未登入時換到的新token驗證中收到舊token遲到的401仍恢復輪詢(bool viaPatSetting)
    {
        using var jobsSeen = new ManualResetEventSlim();
        using var jobsRelease = new ManualResetEventSlim();
        using var userSeen = new ManualResetEventSlim();
        using var userRelease = new ManualResetEventSlim();
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddMinutes(-30), Start.AddMinutes(-20))));
        await h.DrainAsync();
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);

        // 複製失敗 log 的 E8 用 token A 送出，卡在路上。
        h.GitHub.Override(JobsPath, _ =>
        {
            jobsSeen.Set();
            jobsRelease.Wait(Guard);
            return h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials");
        }, times: 1);
        var copy = Task.Run(() => h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, None));
        Assert.True(jobsSeen.Wait(Guard));

        // 這段期間 token A 被撤銷：輪詢收到 401，重新取得 token 拿回同一把，進入未登入（spec §13.2 第 2 點）。
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"), times: 1);
        h.Tokens.Returns(Fixture.FakeToken);
        h.Engine.Post(new EngineCommand.RefreshAll());
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal(AuthStatus.Unauthorized, h.Last.Auth);

        // 換到有效的新 token B（按「重新檢查」，或設定改成 B 的 PAT），用 E1 驗證；這個 E1 還在路上時，A 的 E8 才帶著 401 回來。
        h.GitHub.Override("/user", _ =>
        {
            userSeen.Set();
            userRelease.Wait(Guard);
            return FakeHttpHandler.Json(FakeData.User().ToJsonString());
        }, times: 1);
        if (viaPatSetting)
        {
            h.Tokens.Returns(EngineHarness.OtherFakeToken, TokenSource.Pat);
            h.Settings.AuthMode = AuthMode.Pat;
            h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        }
        else
        {
            h.Tokens.Returns(EngineHarness.OtherFakeToken);
            h.Engine.Post(new EngineCommand.RecheckAuth());
        }

        var verify = Task.Run(() => h.StepAsync());
        Assert.True(userSeen.Wait(Guard));
        jobsRelease.Set();
        Assert.False((await copy.WaitAsync(Guard)).Success);
        userRelease.Set();
        await verify.WaitAsync(Guard);
        var probes = h.GitHub.Count(EngineHarness.ProbeAPath);
        await h.DrainAsync();

        // 那個 401 是舊 token A 收到的，沒有否定 B：E1 的 200 驗證了 B，恢復輪詢（spec §13.2 第 3 點「成功就恢復輪詢」）。
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
        Assert.True(h.GitHub.Count(EngineHarness.ProbeAPath) > probes);
    }

    // ── 第 6 輪：換帳號而且略過期間有 push ────────────────────────────

    [Fact]
    public async Task 換帳號讓略過的協作repo變成自己的repo且略過期間有push時換帳號之前完成的失敗只記suppressed()
    {
        RepoWorld bob = null!;
        await using var h = EngineHarness.Create(h =>
        {
            h.Settings.IncludeCollaboratorRepos = true;
            bob = h.GitHub.AddRepo(FakeData.Repo(BobRepoId, BobRepoName, Start.AddDays(-2), ownerId: BobId, ownerType: "User"), collaborator: true);
        });
        await h.DrainAsync();
        Assert.True(h.RepoRow(BobRepoId).Monitored);

        // 關掉「包含協作 repo」：bob/tool 變成略過（第 5 條）；略過期間有一次 push，那次 push 觸發的 run 失敗。
        h.Settings.IncludeCollaboratorRepos = false;
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.DrainAsync();
        Assert.Equal(SkipReason.Collaborator, h.RepoRow(BobRepoId).Reason);
        bob.SetPushedAt(Start.AddHours(1));
        bob.Runs.Add(FakeData.Run(9501, 3, "completed", "failure", Start.AddHours(1), Start.AddHours(1).AddMinutes(2),
            repo: BobRepoName, headRepoId: BobRepoId, actorId: BobId, actorLogin: BobLogin));
        await h.AdvanceAsync(TimeSpan.FromHours(3));

        // 設定改成 bob 的 PAT：bob/tool 對新帳號是自己的 repo（清單 A），pushed_at 也和記憶體裡的不同。
        h.GitHub.Collaborated.Remove(bob);
        h.GitHub.Owned.Add(bob);
        h.GitHub.User = FakeData.User(BobId, BobLogin);
        h.Tokens.Returns(EngineHarness.OtherFakeToken, TokenSource.Pat);
        h.Settings.AuthMode = AuthMode.Pat;
        var changedAt = h.Clock.GetUtcNow();
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // 協作 repo 的略過不會因為 push 解除，轉成監看是因為換帳號（spec §9.3「改設定」）：界線是換帳號的時間，不是 pushed_at。
        Assert.Equal(RepoCategory.Own, h.RepoRow(BobRepoId).Repo.Category);
        Assert.True(h.RepoRow(BobRepoId).Monitored);
        Assert.Empty(h.Toasts);
        Assert.True(h.Cache.Contains(9501, 1, "failure"));
        Assert.Equal(changedAt, h.Cache.GetRepoStates()[BobRepoId].BaselineT0);
    }

    // ── 第 6 輪：重建資料庫時沒在監看的 repo 的界線 ────────────────────

    [Fact]
    public async Task 執行中重建資料庫前workflow都已停用的repo在啟動後啟用並失敗時重建後照常通知()
    {
        var running = FakeData.Run(9301, 5, "in_progress", null, Start.AddMinutes(-1), Start, actorId: BobId, actorLogin: BobLogin);
        RepoWorld dormant = null!;
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(running);
            dormant = h.GitHub.AddRepo(FakeData.Repo(DormantRepoId, DormantRepoName, Start.AddDays(-2)));
            dormant.Workflows.Clear();
            dormant.Workflows.Add(FakeData.Workflow(id: DormantWorkflowId, state: "disabled_manually", repo: DormantRepoName));
        });
        await h.DrainAsync();
        Assert.Equal(SkipReason.AllWorkflowsDisabled, h.RepoRow(DormantRepoId).Reason);

        // 程式啟動之後在 GitHub 上啟用 workflow（不是 push，pushed_at 不變），手動觸發的 run 失敗。
        // 每小時一次的 workflow 清單重查還沒到，Engine 還沒看到這筆失敗。
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        dormant.Workflows[0]["state"] = "active";
        dormant.Runs.Add(FakeData.Run(9601, 7, "completed", "failure", Start.AddMinutes(18), Start.AddMinutes(20), eventName: "workflow_dispatch",
            workflowId: DormantWorkflowId, repo: DormantRepoName, headRepoId: DormantRepoId));
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(20), Tuning.ActiveRepoInterval);
        Assert.Empty(h.Toasts);

        // run_attempts 表損毀；sample-app 的 run 完成時寫入才讀到損毀，改名重建後重新抓取。
        CorruptTable(h.Workspace.Paths.CacheDbPath, "run_attempts");
        FakeData.Complete(running, "success", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(10), Tuning.ActiveRepoInterval);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // 第 8 條的略過不會因重建遺失（spec §14.3、§15 只有靜音與強制納入會遺失）：界線仍是程式啟動時間（spec §9.3 最後一點），
        // 程式啟動之後才完成的失敗照常通知一次（spec §9.1 N1）。
        Assert.True(h.RepoRow(DormantRepoId).Monitored);
        Assert.Equal("失敗：octo-org/dormant · CI #7", Assert.Single(h.Toasts).Title);
        Assert.Null(h.Cache.GetRepoStates()[DormantRepoId].BaselineT0);
    }

    [Fact]
    public async Task 執行中重建資料庫前取消靜音而落在stale的repo重建後仍以取消靜音的時間為界線()
    {
        var running = FakeData.Run(9301, 5, "in_progress", null, Start.AddMinutes(-1), Start, actorId: BobId, actorLogin: BobLogin);
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(running);
            ArrangeStaleRepoWithRecentRun(h);
            h.Cache.SetOverride(StaleRepoId, new RepoOverride(Muted: true, ForceIncluded: false));
        });
        await h.DrainAsync();
        Assert.Equal(SkipReason.Muted, h.RepoRow(StaleRepoId).Reason);

        // 靜音期間排程 workflow 失敗。取消靜音後落在第 6 條，stale 首次檢查的 E3 遇到 5xx 而退避，重建時仍沒在監看。
        h.GitHub.World(StaleRepoName).Runs.Insert(0, FakeData.Run(9202, 4, "completed", "failure", Start.AddMinutes(3), Start.AddMinutes(5),
            eventName: "schedule", repo: StaleRepoName, headRepoId: StaleRepoId));
        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        h.GitHub.Override(StaleWorkflowsPath, _ => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"), times: 1);
        var unmutedAt = h.Clock.GetUtcNow();
        h.Engine.Post(new EngineCommand.SetOverride(StaleRepoId, RepoOverride.None));
        await h.DrainAsync();
        Assert.Equal(SkipReason.Stale, h.RepoRow(StaleRepoId).Reason);

        // run_attempts 表損毀；sample-app 的 run 完成時寫入才讀到損毀，改名重建後重新抓取。
        CorruptTable(h.Workspace.Paths.CacheDbPath, "run_attempts");
        FakeData.Complete(running, "success", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        Assert.Single(Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak"));
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(10), Tuning.ActiveRepoInterval);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        // 取消靜音的界線（操作的時間，spec §9.3）在重建後沿用，不改成重建的時間（會漏發取消靜音之後完成的）或程式啟動時間（會多發）：
        // 靜音期間完成的只記 suppressed。
        Assert.True(h.RepoRow(StaleRepoId).Monitored);
        Assert.Empty(h.Toasts);
        Assert.True(h.Cache.Contains(9202, 1, "failure"));
        Assert.Equal(unmutedAt, h.Cache.GetRepoStates()[StaleRepoId].BaselineT0);
    }

    // ── 第 7 輪：重建後新的資料庫仍損毀 ───────────────────────────────

    [Fact]
    public async Task 重建後新的資料庫仍損毀時改用退避而且指令照常套用()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, storageBroken: () => true);

        // 退避期間步驟照常跑：使用者的指令下一步就套用，時間不前進就不再改名重建。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();
        Assert.Equal(RefreshMode.Paused, h.Last.RefreshMode);
        AssertRebuilds(h, RebuildsBeforeBackoff);
    }

    [Fact]
    public async Task 重建後新的資料庫仍損毀而退避時等到重試的時間才再改名重建()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, storageBroken: () => true);

        // 和改名重建失敗一樣從 EngineTick 起倍增（spec §15）：到了重試的時間只重建一次，新的資料庫仍損毀就再等兩倍。
        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        AssertRebuilds(h, RebuildsBeforeBackoff + 1);
        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        AssertRebuilds(h, RebuildsBeforeBackoff + 1);
        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        AssertRebuilds(h, RebuildsBeforeBackoff + 2);
    }

    [Fact]
    public async Task 重建後新的資料庫仍損毀而退避之後儲存恢復就不再退避而且監看照常()
    {
        var storageBroken = true;
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, () => storageBroken);

        // 退避期間 GitHub 上多了一筆 run：退避開始時沿用的快照沒有它，看得到就代表快照又從 DB 組出。
        h.Main.Runs.Insert(0, FakeData.Run(9002, 43, "completed", "success", Start.AddMinutes(1), Start.AddMinutes(2)));

        // 儲存層恢復：到了重試的時間改名重建，新的資料庫讀得到；之後不再退避，快照照常從 DB 組出，監看照常。
        storageBroken = false;
        var before = h.GitHub.ApiRequests.Count;
        await h.AdvanceAsync(Tuning.EngineTick);
        await h.AdvanceInStepsAsync(Tuning.QuietMax, Tuning.ActiveRepoInterval);
        AssertRebuilds(h, RebuildsBeforeBackoff + 1);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9002);
        Assert.Contains(EngineHarness.ProbeAPath, h.GitHub.ApiRequests.Skip(before));

        // 超過視窗沒有再改名重建：連續的次數與退避間隔都歸零，之後再遇到同樣的情況，第一次仍立即再重建一次，退避也從 EngineTick 重新起算。
        await h.AdvanceInStepsAsync(Tuning.RebuildStreakWindow, RetryStep);
        storageBroken = true;
        var rebuilds = h.Engine.DatabaseRebuildCount;
        await EnterRebuildBackoffAsync(h, () => storageBroken);
        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        AssertRebuilds(h, rebuilds + RebuildsBeforeBackoff + 1);
    }

    [Fact]
    public async Task 重建後新的資料庫仍損毀而退避之後儲存恢復但視窗內又讀到損毀就直接退避不再立即重建()
    {
        var storageBroken = true;
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, () => storageBroken);

        // 儲存層恢復：到了重試的時間改名重建，新的資料庫讀得到，清單重新抓回來。
        storageBroken = false;
        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        long rebuilds = RebuildsBeforeBackoff + 1;
        AssertRebuilds(h, rebuilds);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);

        // 還在視窗內又讀到損毀：這一次不重建，也不留下備份，快照沿用損毀前的最後一份；間隔接著倍增（上一次等 EngineTick，這次等兩倍）。
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        await AdvanceAndStepAsync(h, Tuning.QuietMax);
        AssertRebuilds(h, rebuilds);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        AssertRebuilds(h, rebuilds);
        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        AssertRebuilds(h, rebuilds + 1);
    }

    /// <remarks>改名失敗靠 Windows 的檔案共用規則（開著的檔案沒有允許刪除就不能改名），所以只在 Windows 上執行。</remarks>
    [WindowsFact]
    public async Task 重建的退避結束超過視窗之後改名失敗時間隔從EngineTick重新起算()
    {
        var storageBroken = true;
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, () => storageBroken);

        // 再重試一次仍損毀，間隔累積到兩倍；下一次重試時儲存層恢復，之後超過視窗沒有再改名重建。
        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        storageBroken = false;
        await AdvanceAndStepAsync(h, Tuning.EngineTick * 2);
        await h.AdvanceInStepsAsync(Tuning.RebuildStreakWindow, RetryStep);
        var rebuilds = h.Engine.DatabaseRebuildCount;
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        // 另一個程式開著 cache.db（沒有允許刪除，改名與丟棄都會失敗）：這是新的一段，失敗的退避不沿用上一段留下的間隔。
        using (new FileStream(h.Workspace.Paths.CacheDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await AdvanceAndStepAsync(h, Tuning.QuietMax);
            AssertRebuilds(h, rebuilds);
        }

        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        AssertRebuilds(h, rebuilds + 1);
    }

    /// <remarks>改名失敗靠 Windows 的檔案共用規則（開著的檔案沒有允許刪除就不能改名），所以只在 Windows 上執行。</remarks>
    [WindowsFact]
    public async Task 改名重建一再失敗時間隔接著倍增()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var backups = BackupCount(h);
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        // 另一個程式開著 cache.db（沒有允許刪除）：第一次失敗之後等 EngineTick，到時間再失敗一次，接著等兩倍。
        using (new FileStream(h.Workspace.Paths.CacheDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await AdvanceAndStepAsync(h, Tuning.QuietMax);
            Assert.Single(Regex.Matches(h.ErrorLogText, "重建快取資料庫"));
            await AdvanceAndStepAsync(h, Tuning.EngineTick);
            Assert.Equal(2, Regex.Matches(h.ErrorLogText, "重建快取資料庫").Count);
        }

        // 檔案放開之後：還沒到兩倍的時間不重試，到了才改名重建。
        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        Assert.Equal(backups, BackupCount(h));
        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        Assert.Equal(backups + 1, BackupCount(h));
    }

    /// <summary>退避期間使用者看得到的狀態（docs/known-limitations.zh-TW.md 已知限制第 7 項）：這裡的損毀在每次重建的 Reset 裡就讀到。</summary>
    [Fact]
    public async Task 持續損毀的退避期間快照沿用狀態表不寫輪詢照常而且每次重試換一則新的橫幅()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, storageBroken: () => true);

        // 沿用的是第一次重建後從空白的新資料庫組出的那一份，不是損毀前的 9001。
        var carried = h.Last;
        Assert.Empty(carried.Rows);
        Assert.Equal(BannerKind.DatabaseRecovered, carried.Banner?.Kind);

        h.Main.Runs.Insert(0, FakeData.Run(9002, 43, "completed", "success", Start.AddMinutes(1), Start.AddMinutes(2)));
        var requests = h.GitHub.ApiRequests.Count;
        await h.AdvanceInStepsAsync(RebuildObservation, Tuning.EngineTick);

        Assert.Empty(h.Last.Rows);
        Assert.Equal(carried.LastUpdatedUtc, h.Last.LastUpdatedUtc);
        Assert.True(h.GitHub.ApiRequests.Count > requests);
        Assert.Equal(BannerKind.DatabaseRecovered, h.Last.Banner?.Kind);
        Assert.NotEqual(carried.Banner!.NoticeId, h.Last.Banner!.NoticeId);
        Assert.Equal(StatusReadKind.NoRow, StatusSnapshotReader.Read(h.Workspace.Paths.CacheDbPath, Tuning.CliDbBusyTimeout).Kind);
    }

    /// <summary>
    /// 退避期間畫面停在沿用的快照（docs/known-limitations.zh-TW.md 已知限制第 7 項）：清單其實抓回來了，但沿用的快照裡 repo 清單還是空的。
    /// 「抓完了沒有」要跟著快照，主畫面才不會當成抓回 0 筆，顯示「目前沒有 workflow 執行紀錄」（MainViewModel.IsLoading）。
    /// </summary>
    [Fact]
    public async Task 持續損毀的退避期間沿用的快照還沒有清單時不算清單已抓回來()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var requests = h.GitHub.Count(EngineHarness.ListAPath);
        await EnterRebuildBackoffAsync(h, storageBroken: () => true);
        var published = h.Snapshots.Count;
        await h.AdvanceInStepsAsync(RebuildObservation, Tuning.EngineTick);

        // 每次重建之後都重新抓了完整清單，只是沿用的快照看不到。
        Assert.True(h.GitHub.Count(EngineHarness.ListAPath) > requests);
        Assert.All(h.Snapshots.Skip(published), snapshot =>
        {
            Assert.Empty(snapshot.Repos);
            Assert.False(snapshot.RepoListFetched);
        });
    }

    /// <summary>
    /// 暫停中開啟 repo 管理會抓回完整清單，但退避期間 repo 管理看的也是沿用的快照：
    /// 不能當成抓回 0 筆，顯示「沒有看得到的 repo」（RepoManagerViewModel.SyncVisible）。
    /// </summary>
    [Fact]
    public async Task 持續損毀的退避期間暫停中開啟repo管理時沿用的空清單不算清單已抓回來()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, storageBroken: () => true);
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();
        await StepUntilNextRetryAsync(h);

        var requests = h.GitHub.Count(EngineHarness.ListAPath);
        h.Engine.Post(new EngineCommand.RepoManagerOpened());
        await h.DrainAsync();

        Assert.True(h.GitHub.Count(EngineHarness.ListAPath) > requests);
        Assert.Empty(h.Last.Repos);
        Assert.False(h.Last.RepoListFetched);
    }

    /// <summary>退避期間沿用的是損毀前有資料的快照時，重試重建把記憶體裡的清單歸零，畫面仍看這一份：清單與「抓完了」都留著。</summary>
    [Fact]
    public async Task 持續損毀的退避期間沿用損毀前有資料的快照時清單與抓完的標示都不變()
    {
        var storageBroken = true;
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, () => storageBroken);
        Assert.False(h.Last.RepoListFetched);

        // 儲存層恢復：到了重試的時間改名重建，不再沿用，新組的快照有清單，也算抓完了。
        storageBroken = false;
        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
        Assert.True(h.Last.RepoListFetched);

        // 視窗內又壞掉：直接退避，快照沿用這一份；到了重試的時間（間隔接著倍增）重建一次，新的資料庫重讀時又損毀。
        storageBroken = true;
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        await AdvanceAndStepAsync(h, Tuning.QuietMax);
        var rebuilds = h.Engine.DatabaseRebuildCount;
        var published = h.Snapshots.Count;
        await AdvanceAndStepAsync(h, Tuning.EngineTick * 2);

        Assert.Equal(rebuilds + 1, h.Engine.DatabaseRebuildCount);
        Assert.All(h.Snapshots.Skip(published), snapshot =>
        {
            Assert.Contains(snapshot.Rows, row => row.Run.Id == 9001);
            Assert.Contains(snapshot.Repos, row => row.Repo.Id == FakeData.RepoId);
            Assert.True(snapshot.RepoListFetched);
        });
    }

    /// <summary>
    /// 看得到 0 個 repo 的帳號（docs/known-limitations.zh-TW.md 已知限制第 7 項）：沿用的快照是抓完之前組的，退避期間和其他帳號一樣是「正在載入」；
    /// 儲存層恢復後新組的快照才算抓回 0 筆，顯示「沒有資料」。
    /// </summary>
    [Fact]
    public async Task 持續損毀的退避期間看得到0個repo的帳號要等儲存恢復才算抓回0筆()
    {
        var storageBroken = true;
        await using var h = EngineHarness.Create(h => h.GitHub.Owned.Clear());
        await h.DrainAsync();
        Assert.True(h.Last.RepoListFetched);

        // 沒有 repo 要寫進 repos 表：改弄壞每個請求都會讀的 http_cache。
        await EnterRebuildBackoffAsync(h, () => storageBroken, firstCorrupted: "http_cache");
        await h.AdvanceInStepsAsync(RebuildObservation, Tuning.EngineTick);
        Assert.Empty(h.Last.Repos);
        Assert.False(h.Last.RepoListFetched);

        storageBroken = false;
        await AdvanceAndStepAsync(h, Tuning.RepoErrorBackoffMax);
        Assert.Empty(h.Last.Repos);
        Assert.True(h.Last.RepoListFetched);
    }

    /// <remarks>次數在 Reset 之前記好：Reset 丟出例外（由步驟的 try/catch 接住）時，這一次重建也算連續，不會每一步都改名重建。</remarks>
    [Fact]
    public async Task 重建時Reset丟出損毀的例外也算進連續的次數()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        h.Clock.BeforeGetUtcNow = () =>
        {
            if (EngineClock.CalledFrom("ResetAfterDatabaseRecovery"))
            {
                throw new SqliteException("測試注入的損毀", SqliteCorruptCode);
            }
        };
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        await AdvanceAndStepAsync(h, Tuning.QuietMax);

        AssertRebuilds(h, RebuildsBeforeBackoff);
    }

    // 損毀不一定在 Reset 裡讀到：新的資料庫要到寫狀態表（WriteStatus）或步驟主體（回應寫 repos、http_cache）才讀到時，一樣算連續。
    [Theory]
    [InlineData("WriteStatus", "status_snapshot")]
    [InlineData(DuringRequest, "http_cache")]
    [InlineData(DuringRequest, "repos")]
    public async Task 重建後要到Reset之外才讀到損毀時也改用退避而且指令照常套用(string injectIn, string table)
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        CorruptAfterEachRebuild(h, injectIn, table);
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        // 時間不前進：只有門檻次數的重建，之後不再重建，不會每一步或每個請求都重建一次。
        await AdvanceAndStepAsync(h, Tuning.QuietMax);
        AssertRebuilds(h, RebuildsBeforeBackoff);

        // 退避期間步驟照常跑：使用者的指令下一步就套用。
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();
        Assert.Equal(RefreshMode.Paused, h.Last.RefreshMode);
        AssertRebuilds(h, RebuildsBeforeBackoff);
    }

    [Theory]
    [InlineData(DuringRequest, "http_cache")]
    [InlineData(DuringRequest, "repos")]
    public async Task 重建後要到步驟主體才讀到損毀時推進十分鐘重建次數有上限而且不多送請求(string injectIn, string table)
    {
        var normal = await RequestsWithoutCorruptionAsync(RebuildObservation);
        var (rebuilds, requests) = await ObserveCorruptionAfterEachRebuildAsync(injectIn, table, RebuildObservation);

        Assert.InRange(rebuilds, RebuildsBeforeBackoff + 1, RebuildsBeforeBackoff + BackoffRetriesWithin(RebuildObservation));
        Assert.InRange(requests, 1, normal.Periodic);
    }

    /// <remarks>
    /// 步驟主體讀寫都正常、只有狀態表壞掉時，退避期間輪詢照常，每次重建又像冷啟動一樣重抓（spec §14.3「快取丟了只會重抓」，新的資料庫沒有 ETag），
    /// 所以請求比沒有損毀時多；多出來的不超過每次重建一次冷啟動，上限跟著重建次數走。
    /// </remarks>
    [Fact]
    public async Task 重建後要到寫狀態表才讀到損毀時推進十分鐘重建次數有上限而且每次重建最多多送一次冷啟動的請求()
    {
        var normal = await RequestsWithoutCorruptionAsync(RebuildObservation);
        var (rebuilds, requests) = await ObserveCorruptionAfterEachRebuildAsync("WriteStatus", "status_snapshot", RebuildObservation);

        Assert.InRange(rebuilds, RebuildsBeforeBackoff + 1, RebuildsBeforeBackoff + BackoffRetriesWithin(RebuildObservation));
        Assert.InRange(requests, 1, normal.Periodic + (rebuilds * normal.ColdStart));
    }

    /// <summary>
    /// 真的背景迴圈（時間不前進）：回報損毀時會喚醒迴圈，如果不改用退避，就會一直改名重建，空轉到時間前進為止。
    /// 達到門檻的那一步送一個請求、下一步再送一個：第二個請求進來時，沒有改用退避的話已經又重建過，所以重建次數不靠等多久；沒有重建到門檻時會等到 <see cref="Guard"/> 而失敗。
    /// </summary>
    [Fact]
    public async Task 重建後要到寫狀態表才讀到損毀時背景迴圈不空轉也不再改名重建()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        CorruptAfterEachRebuild(h, "WriteStatus", "status_snapshot");
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        h.Clock.Advance(Tuning.QuietMax);
        var requestsAtThreshold = 0;
        var nextStepRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.GitHub.OnRequest = _ =>
        {
            if (h.Engine.DatabaseRebuildCount >= RebuildsBeforeBackoff && Interlocked.Increment(ref requestsAtThreshold) == RequestsThroughNextStep)
            {
                nextStepRequest.TrySetResult();
            }
        };

        await h.Engine.StartAsync(None);
        await nextStepRequest.Task.WaitAsync(Guard);
        var snapshots = h.Snapshots.Count;
        await Task.Delay(SpinObservation);

        AssertRebuilds(h, RebuildsBeforeBackoff);
        Assert.True(h.Snapshots.Count - snapshots <= MaxIdleSnapshots, $"背景迴圈空轉：發布了 {h.Snapshots.Count - snapshots} 份快照");
    }

    /// <remarks>
    /// 「證明新的資料庫正常才歸零」的做法在這裡會一直歸零：沒有重建、結尾也沒有損毀的步驟不代表新的資料庫正常，
    /// 額度用滿時那一步只是什麼都沒有碰到（送出請求的步驟也可能碰巧只寫到沒壞的表）。
    /// </remarks>
    [Fact]
    public async Task 重建後隔著沒送請求的乾淨步驟才又讀到損毀時仍算連續()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var elapsed = TimeSpan.Zero;

        for (var round = 0; round < CleanStepRounds; round++)
        {
            // 額度用滿：這一步不送請求、資料庫也還沒壞，是乾淨的一步。
            await FillRequestWindowAsync(h);
            Assert.Equal(0, await h.StepAsync());

            // 額度恢復之後，下一次寫入 repos 表才讀到損毀。
            CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
            await h.AdvanceAsync(Tuning.RequestWindow);
            elapsed += Tuning.RequestWindow;
        }

        Assert.InRange(h.Engine.DatabaseRebuildCount, RebuildsBeforeBackoff + 1, RebuildsBeforeBackoff + BackoffRetriesWithin(elapsed));
        Assert.Equal(FirstBackupOnly, BackupCount(h));
    }

    /// <summary>退避到上限之後的重試仍算連續（<see cref="Tuning.RebuildStreakWindow"/> 比上限長），不會把間隔歸零。</summary>
    [Theory]
    [InlineData("ResetAfterDatabaseRecovery", RebuiltCorruptTable)]
    [InlineData("WriteStatus", "status_snapshot")]
    [InlineData(DuringRequest, "http_cache")]
    [InlineData(DuringRequest, "repos")]
    public async Task 持續損毀時重試間隔從EngineTick起倍增到上限而且到上限之後不歸零(string injectIn, string table)
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        CorruptAfterEachRebuild(h, injectIn, table);
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        await AdvanceAndStepAsync(h, Tuning.QuietMax);
        long rebuilds = RebuildsBeforeBackoff;
        AssertRebuilds(h, rebuilds);

        var retriesAtMax = 0;
        var delay = TimeSpan.Zero;
        while (retriesAtMax < RetriesObservedAtBackoffMax)
        {
            delay = IntervalPolicy.ErrorBackoff(Tuning.EngineTick, delay, IntervalPolicy.StretchFactor(null));
            retriesAtMax += delay == Tuning.RepoErrorBackoffMax ? 1 : 0;

            // 重試的前一刻還不重建，到了重試的時間只重建一次。
            await AdvanceAndStepAsync(h, delay - Tuning.EngineTick);
            AssertRebuilds(h, rebuilds);
            await AdvanceAndStepAsync(h, Tuning.EngineTick);
            AssertRebuilds(h, ++rebuilds);
        }
    }

    /// <remarks>改名失敗靠 Windows 的檔案共用規則（開著的檔案沒有允許刪除就不能改名），所以只在 Windows 上執行。</remarks>
    [WindowsFact]
    public async Task 持續損毀時到上限之後重試改名失敗一次也不把次數與間隔歸零()
    {
        await AssertRetriesFailingKeepStreakAsync(failedRetries: 1);
    }

    /// <remarks>改名失敗靠 Windows 的檔案共用規則，所以只在 Windows 上執行。兩次失敗讓成功的重建相隔 3 × 上限，把視窗拉長也擋不住。</remarks>
    [WindowsFact]
    public async Task 持續損毀時到上限之後重試連續改名失敗兩次也不把次數與間隔歸零()
    {
        await AssertRetriesFailingKeepStreakAsync(failedRetries: 2);
    }

    private static async Task AssertRetriesFailingKeepStreakAsync(int failedRetries)
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        CorruptAfterEachRebuild(h, "WriteStatus", "status_snapshot");
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        await AdvanceAndStepAsync(h, Tuning.QuietMax);

        // 退避到上限。
        var delay = TimeSpan.Zero;
        do
        {
            delay = IntervalPolicy.ErrorBackoff(Tuning.EngineTick, delay, IntervalPolicy.StretchFactor(null));
            await AdvanceAndStepAsync(h, delay);
        }
        while (delay != Tuning.RepoErrorBackoffMax);

        var rebuilds = h.Engine.DatabaseRebuildCount;
        using (new FileStream(h.Workspace.Paths.CacheDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            for (var retry = 0; retry < failedRetries; retry++)
            {
                await AdvanceAndStepAsync(h, Tuning.RepoErrorBackoffMax);
                AssertRebuilds(h, rebuilds);
            }
        }

        // 放開之後下一次重試只重建一次，而且仍是上限的間隔：不會立即再重建，也不會從 EngineTick 重新倍增。
        await AdvanceAndStepAsync(h, Tuning.RepoErrorBackoffMax);
        AssertRebuilds(h, rebuilds + 1);
        await AdvanceAndStepAsync(h, Tuning.RepoErrorBackoffMax - Tuning.EngineTick);
        AssertRebuilds(h, rebuilds + 1);
        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        AssertRebuilds(h, rebuilds + 2);
    }

    // ── 第 7 輪：持續損毀時的備份上限（spec §14.3、§15；docs/known-limitations.zh-TW.md 已知限制第 7 項）──

    [Theory]
    [InlineData("ResetAfterDatabaseRecovery", RebuiltCorruptTable)]
    [InlineData("WriteStatus", "status_snapshot")]
    [InlineData(DuringRequest, "http_cache")]
    [InlineData(DuringRequest, "repos")]
    public async Task 持續損毀推進兩小時只留第一份備份而且重試照常(string injectIn, string table)
    {
        await using var h = await EnterPersistentCorruptionAsync(injectIn, table);

        await h.AdvanceInStepsAsync(PersistentObservation, RetryStep);

        // 間隔到上限之後也是每 10 分鐘重試一次，重試不留檔。
        Assert.Equal(FirstBackupOnly, BackupCount(h));
        Assert.True(h.Engine.DatabaseRebuildCount >= RebuildsBeforeBackoff + (long)(PersistentObservation / Tuning.RepoErrorBackoffMax),
            $"兩小時只重建了 {h.Engine.DatabaseRebuildCount} 次");
    }

    [Theory]
    [InlineData("ResetAfterDatabaseRecovery", RebuiltCorruptTable)]
    [InlineData("WriteStatus", "status_snapshot")]
    [InlineData(DuringRequest, "http_cache")]
    [InlineData(DuringRequest, "repos")]
    public async Task 持續損毀時暫停也不多留備份而且暫停中按F5也不多留(string injectIn, string table)
    {
        await using var h = await EnterPersistentCorruptionAsync(injectIn, table);
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();
        Assert.Equal(RefreshMode.Paused, h.Last.RefreshMode);

        await h.AdvanceInStepsAsync(PersistentObservation, RetryStep);
        Assert.Equal(FirstBackupOnly, BackupCount(h));

        // 暫停中不送請求：DuringRequest 的兩組要有請求才注入，第一次重試之後資料庫就是好的，這裡只驗到那一次重試與下面的 F5；另外兩組才驗到持續重試。
        if (injectIn != DuringRequest)
        {
            Assert.True(h.Engine.DatabaseRebuildCount >= (long)(PersistentObservation / Tuning.RepoErrorBackoffMax),
                $"暫停兩小時只重建了 {h.Engine.DatabaseRebuildCount} 次");
        }

        // 每次間隔超過視窗：連續次數重新起算，第一次又立即重建；這些重建一樣不留檔。
        var rebuilds = h.Engine.DatabaseRebuildCount;
        for (var press = 0; press < RefreshPresses; press++)
        {
            h.Clock.Advance(Tuning.RebuildStreakWindow + Tuning.EngineTick);
            h.Engine.Post(new EngineCommand.RefreshAll());
            await h.DrainAsync();
            Assert.Equal(FirstBackupOnly, BackupCount(h));
        }

        Assert.True(h.Engine.DatabaseRebuildCount > rebuilds);
    }

    /// <summary>暫停不加專用分支：重試照常，所以儲存層恢復後不必解除暫停或重開 App；暫停期間不輪詢，清單要按 F5 才重新抓。</summary>
    [Fact]
    public async Task 暫停中儲存恢復後最晚一個上限的間隔就恢復不必解除暫停而且按F5抓得回清單()
    {
        var storageBroken = true;
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, () => storageBroken);
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();

        // 退避到上限之後對齊到剛重試完：接下來要等滿一個上限的間隔，是最久的情況。
        await h.AdvanceInStepsAsync(PersistentObservation, RetryStep);
        await StepUntilNextRetryAsync(h);
        var rebuilds = h.Engine.DatabaseRebuildCount;

        storageBroken = false;
        await AdvanceAndStepAsync(h, Tuning.RepoErrorBackoffMax);

        // 不再沿用舊的快照：狀態表又寫得進去（退避期間不寫），這一次重建之後也不會再重建。
        // 暫停不排完整重抓，新的資料庫是空的：清單區是「正在載入」（docs/known-limitations.zh-TW.md 已知限制第 7 項），要等 F5。
        AssertRebuilds(h, rebuilds + 1);
        Assert.Equal(RefreshMode.Paused, h.Last.RefreshMode);
        Assert.Equal(StatusReadKind.Ok, StatusSnapshotReader.Read(h.Workspace.Paths.CacheDbPath, Tuning.CliDbBusyTimeout).Kind);
        Assert.Empty(h.Last.Repos);
        Assert.False(h.Last.RepoListFetched);

        // F5 用完整重抓代替探測（spec §6.6）：看到的 repo 接著查 workflow 清單與 runs，清單回來。
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        AssertRebuilds(h, rebuilds + 1);
        Assert.Equal(RefreshMode.Paused, h.Last.RefreshMode);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
    }

    /// <summary>
    /// docs/known-limitations.zh-TW.md 已知限制第 7 項：暫停中要等重試重建之後才按。更早按的，重建會清掉排程與回應，暫停中不會重排，所以不會自己回來，要再按一次。
    /// 退避期間畫面沿用快照，按了也看不出效果。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 暫停中儲存恢復後重試之前重新抓的結果被重建清掉要再做一次(bool openRepoManager)
    {
        var storageBroken = true;
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, () => storageBroken);
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();
        await h.AdvanceInStepsAsync(PersistentObservation, RetryStep);
        await StepUntilNextRetryAsync(h);
        var rebuilds = h.Engine.DatabaseRebuildCount;
        EngineCommand Fetch() => openRepoManager ? new EngineCommand.RepoManagerOpened() : new EngineCommand.RefreshAll();

        storageBroken = false;
        h.Engine.Post(Fetch());
        await h.DrainAsync();
        Assert.Empty(h.Last.Repos);

        await AdvanceAndStepAsync(h, Tuning.RepoErrorBackoffMax);
        AssertRebuilds(h, rebuilds + 1);
        Assert.Empty(h.Last.Repos);
        Assert.False(h.Last.RepoListFetched);

        var requests = h.GitHub.ApiRequests.Count;
        await h.AdvanceInStepsAsync(RetryStep, Tuning.EngineTick);
        Assert.Equal(requests, h.GitHub.ApiRequests.Count);
        Assert.Empty(h.Last.Repos);

        h.Engine.Post(Fetch());
        await h.DrainAsync();
        AssertRebuilds(h, rebuilds + 1);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
    }

    /// <summary>docs/known-limitations.zh-TW.md 已知限制第 7 項：解除暫停不必等，恢復後下一次重試重建完自動重抓完整清單。</summary>
    [Fact]
    public async Task 暫停中儲存還壞時解除暫停恢復後下一次重試自動抓回完整清單()
    {
        var storageBroken = true;
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, () => storageBroken);
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();
        await h.AdvanceInStepsAsync(PersistentObservation, RetryStep);

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.StepAsync();
        await StepUntilNextRetryAsync(h);
        var rebuilds = h.Engine.DatabaseRebuildCount;

        storageBroken = false;
        await h.AdvanceInStepsAsync(Tuning.RepoErrorBackoffMax + Tuning.QuietMax, Tuning.ActiveRepoInterval);

        AssertRebuilds(h, rebuilds + 1);
        Assert.True(h.Last.RepoListFetched);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
    }

    /// <summary>docs/known-limitations.zh-TW.md 已知限制第 7 項：暫停中重建完才解除暫停時沒有下一次重建，清單靠解除暫停時補排。</summary>
    [Fact]
    public async Task 暫停中重建完才解除暫停也抓回完整清單()
    {
        var storageBroken = true;
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, () => storageBroken);
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();
        await h.AdvanceInStepsAsync(PersistentObservation, RetryStep);
        await StepUntilNextRetryAsync(h);
        var rebuilds = h.Engine.DatabaseRebuildCount;

        storageBroken = false;
        await AdvanceAndStepAsync(h, Tuning.RepoErrorBackoffMax);
        AssertRebuilds(h, rebuilds + 1);
        Assert.False(h.Last.RepoListFetched);

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();

        AssertRebuilds(h, rebuilds + 1);
        Assert.True(h.Last.RepoListFetched);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
    }

    /// <summary>
    /// 暫停中重建之後新的資料庫是空的：F5 只抓探測的第 1 頁（最近 push 的 100 個）的話，最舊的 repo 不在清單裡，
    /// 它的失敗算不進總狀態，全部成功就回 Success（spec §6.6）。
    /// </summary>
    [Fact]
    public async Task 暫停中重建後按F5抓回完整清單最舊的repo失敗時總狀態不是成功()
    {
        await using var h = await RebuildWhilePausedAsync();
        AddReposBeyondFirstPage(h);
        var before = h.Snapshots.Count;

        h.Engine.Post(new EngineCommand.RefreshAll());
        await DrainAcrossRequestLimitAsync(h);

        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.Equal(1, h.Last.Summary.FailingCount);
        Assert.Equal(ReposBeyondOnePage, h.Last.Repos.Count);
        Assert.True(h.Last.RepoListFetched);

        // 清單抓完之後各 repo 還要查過一次：這段期間最舊的還沒查到，一樣不能先變綠燈。
        Assert.DoesNotContain(h.Snapshots.Skip(before), snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>repo 查到一半回 5xx：暫停中不重試，這個 repo 的狀態不知道，總狀態不是 Success；再按一次 F5 補查。</summary>
    [Fact]
    public async Task 暫停中重建後有repo查不到時總狀態不是成功再按一次補查()
    {
        await using var h = await RebuildWhilePausedAsync();
        h.GitHub.Override(EngineHarness.RunsPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        var otherRepoRuns = "/repos/" + OtherRepoName + "/actions/runs?per_page=30";
        var other = h.GitHub.AddRepo(FakeData.Repo(OtherRepoId, OtherRepoName, Start.AddDays(-3)));
        other.Runs.Add(FakeData.Run(9002, 1, "completed", "success", Start.AddDays(-1), Start.AddDays(-1), repo: OtherRepoName, headRepoId: OtherRepoId));

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(otherRepoRuns));
        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>repo 回權限錯誤是等重試（spec §6.1），有問題標示：不讓總狀態一直是無資料。</summary>
    [Theory]
    [InlineData(EngineHarness.RunsPath)]
    [InlineData(EngineHarness.WorkflowsPath)]
    public async Task 暫停中重建後等權限重試的repo不擋其他repo的成功(string path)
    {
        await using var h = await RebuildWhilePausedAsync();
        h.GitHub.Override(path, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
            ("X-Accepted-GitHub-Permissions", "actions=read")));
        var other = h.GitHub.AddRepo(FakeData.Repo(OtherRepoId, OtherRepoName, Start.AddDays(-3)));
        other.Runs.Add(FakeData.Run(9002, 1, "completed", "success", Start.AddDays(-1), Start.AddDays(-1), repo: OtherRepoName, headRepoId: OtherRepoId));

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Contains("權限不足", h.RepoRow(FakeData.RepoId).ProblemNote);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>額度（spec §6.6）：這次執行第一次按 F5 用完整重抓代替探測，多的只有第 2 頁以後的分頁；抓完之後 F5 回到只送探測。</summary>
    [Fact]
    public async Task 暫停中重建後第一次F5用完整重抓代替探測抓完之後回到只送探測()
    {
        await using var h = await RebuildWhilePausedAsync();
        AddReposBeyondFirstPage(h);
        var (list, probes) = (h.GitHub.Count(EngineHarness.ListAPath), h.GitHub.Count(EngineHarness.ProbeAPath));

        h.Engine.Post(new EngineCommand.RefreshAll());
        await DrainAcrossRequestLimitAsync(h);

        Assert.Equal(list + 1, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.Equal(1, h.GitHub.Count(ListASecondPage));
        Assert.Equal(probes, h.GitHub.Count(EngineHarness.ProbeAPath));

        h.Engine.Post(new EngineCommand.RefreshAll());
        await DrainAcrossRequestLimitAsync(h);

        Assert.Equal(list + 1, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.Equal(1, h.GitHub.Count(ListASecondPage));
        Assert.Equal(probes + 1, h.GitHub.Count(EngineHarness.ProbeAPath));
    }

    /// <summary>額度：repo 不超過一頁時，完整清單就是 1 個請求，和原本的 1 個探測一樣多。</summary>
    [Fact]
    public async Task 暫停中重建後repo不超過一頁時F5的清單請求和只送探測一樣多()
    {
        await using var h = await RebuildWhilePausedAsync();
        var (list, probes) = (h.GitHub.Count(EngineHarness.ListAPath), h.GitHub.Count(EngineHarness.ProbeAPath));

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Equal(list + 1, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.Equal(probes, h.GitHub.Count(EngineHarness.ProbeAPath));
        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>
    /// 分頁中途失敗：已抓到的分頁照常寫入（spec §5.4），但這份清單已知不完整，全部成功也不是 Success；暫停中不自動重試，再按一次 F5 才補齊。
    /// </summary>
    [Fact]
    public async Task 暫停中F5抓清單第2頁失敗時總狀態不是成功再按一次抓回完整清單()
    {
        await using var h = await RebuildWhilePausedAsync();
        AddReposBeyondFirstPage(h);
        h.GitHub.Override(ListASecondPage, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);

        h.Engine.Post(new EngineCommand.RefreshAll());
        await DrainAcrossRequestLimitAsync(h);

        Assert.Equal(Tuning.ListPageSize, h.Last.Repos.Count);
        Assert.False(h.Last.RepoListFetched);
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);
        Assert.Equal("no_data", StatusOverall(h));
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);

        var requests = h.GitHub.ApiRequests.Count;
        await h.AdvanceInStepsAsync(RetryStep, RequestWindow);
        Assert.Equal(requests, h.GitHub.ApiRequests.Count);

        h.Engine.Post(new EngineCommand.RefreshAll());
        await DrainAcrossRequestLimitAsync(h);

        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(ReposBeyondOnePage, h.Last.Repos.Count);
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
    }

    /// <summary>EnsurePeriodicTargets 從上一次送出起算一小時：那一輪沒抓完時不能等，解除暫停就要補做。</summary>
    [Fact]
    public async Task 暫停中F5抓清單失敗後解除暫停立即補做完整重抓()
    {
        await using var h = await RebuildWhilePausedAsync();
        AddReposBeyondFirstPage(h);
        h.GitHub.Override(ListASecondPage, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        h.Engine.Post(new EngineCommand.RefreshAll());
        await DrainAcrossRequestLimitAsync(h);
        Assert.False(h.Last.RepoListFetched);

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.AdvanceInStepsAsync(RetryStep, RequestWindow);

        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(ReposBeyondOnePage, h.Last.Repos.Count);
    }

    /// <summary>
    /// 持續損毀、但按的時候不在等重試（間隔超過 <see cref="Tuning.RebuildStreakWindow"/>）：這一下會送完整清單，失敗不重試，
    /// 所以每按一次最多一輪（這個世界是 2 頁），也不多留備份。
    /// </summary>
    [Theory]
    [InlineData("WriteStatus", "status_snapshot")]
    [InlineData(DuringRequest, "http_cache")]
    [InlineData(DuringRequest, "repos")]
    public async Task 持續損毀時暫停中隔一段時間按F5每次最多送一輪完整清單(string injectIn, string table)
    {
        await using var h = await EnterPersistentCorruptionAsync(injectIn, table);
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();
        AddReposBeyondFirstPage(h);
        int ListRequests() => h.GitHub.Count(EngineHarness.ListAPath) + h.GitHub.Count(ListASecondPage);

        for (var press = 0; press < RefreshPresses; press++)
        {
            h.Clock.Advance(Tuning.RebuildStreakWindow + Tuning.EngineTick);
            var before = ListRequests();
            h.Engine.Post(new EngineCommand.RefreshAll());
            await h.DrainAsync();

            Assert.InRange(ListRequests() - before, 0, ListPagesBeyondOnePage);
            Assert.Equal(FirstBackupOnly, BackupCount(h));
        }
    }

    /// <summary>補做不能蓋掉錯誤的等待：清單正在 5xx 退避（spec §6.4）或等權限重試（spec §6.1）時，暫停再恢復不提前重送。</summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task 啟動時清單回錯誤後暫停再恢復不提前重送(HttpStatusCode status)
    {
        await using var h = EngineHarness.Create(h => h.GitHub.Override(EngineHarness.ListAPath, _ => h.GitHub.Error(status, ForbiddenByPolicy), times: 1));
        await h.DrainAsync();
        Assert.False(h.Last.RepoListFetched);
        var list = h.GitHub.Count(EngineHarness.ListAPath);

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();
        Assert.Equal(list, h.GitHub.Count(EngineHarness.ListAPath));

        await h.AdvanceInStepsAsync(Tuning.RepoListRefresh + RetryStep, RetryStep);
        Assert.True(h.GitHub.Count(EngineHarness.ListAPath) > list);
        Assert.True(h.Last.RepoListFetched);
    }

    /// <summary>
    /// 已知的限制（docs/known-limitations.zh-TW.md 已知限制第 7 項）：是不是已知不完整只記在記憶體，部分清單寫進資料庫之後重新啟動，就和上一次的完整清單分不出來，
    /// 全部成功時總狀態是 Success；第一次 F5 一樣會補做，補齊之後就對了。
    /// </summary>
    [Fact]
    public async Task 暫停中F5抓清單失敗後重新啟動總狀態沿用部分清單第一次F5補齊()
    {
        await using var h = await RebuildWhilePausedAsync();
        AddReposBeyondFirstPage(h);
        h.GitHub.Override(ListASecondPage, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        h.Engine.Post(new EngineCommand.RefreshAll());
        await DrainAcrossRequestLimitAsync(h);
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        h.Settings.RefreshMode = RefreshMode.Paused;
        await h.RestartAsync();
        await h.DrainAsync();
        Assert.Equal(Tuning.ListPageSize, h.Last.Repos.Count);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);

        h.Engine.Post(new EngineCommand.RefreshAll());
        await DrainAcrossRequestLimitAsync(h);

        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(ReposBeyondOnePage, h.Last.Repos.Count);
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
    }

    /// <summary>
    /// 清單回權限錯誤或 404：重試間隔內再按 F5 不重送（spec §6.1），這時改送探測；探測的第 1 頁一樣是已知不完整的清單，全部成功也不是 Success。
    /// 成功之後不再算在等重試：開啟 repo 管理頁立即抓清單 B。
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task 暫停中清單回錯誤時重試間隔內按F5不重送探測看到的部分清單也不是成功(HttpStatusCode status)
    {
        await using var h = await RebuildWhilePausedAsync();
        AddReposBeyondFirstPage(h);
        h.GitHub.Override(EngineHarness.ListAPath, _ => h.GitHub.Error(status, ForbiddenByPolicy), times: 1);
        var list = h.GitHub.Count(EngineHarness.ListAPath);

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal(list + 1, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.Empty(h.Last.Repos);

        h.Engine.Post(new EngineCommand.RefreshAll());
        await DrainAcrossRequestLimitAsync(h);
        Assert.Equal(list + 1, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.Equal(Tuning.ListPageSize, h.Last.Repos.Count);
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        h.Clock.Advance(status == HttpStatusCode.NotFound ? Tuning.NotFoundRetryInterval : Tuning.PermissionRetryInterval);
        h.Engine.Post(new EngineCommand.RefreshAll());
        await DrainAcrossRequestLimitAsync(h);
        Assert.Equal(list + 2, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);

        h.Engine.Post(new EngineCommand.RepoManagerOpened());
        await DrainAcrossRequestLimitAsync(h);
        Assert.Equal(1, h.GitHub.Count(ListBPath));
    }

    /// <summary>開啟 repo 管理頁也照同一個等待（spec §6.1）：清單在等權限重試時，重試間隔內不提前重送。</summary>
    [Fact]
    public async Task 暫停中清單等權限重試時開啟repo管理也不提前重送()
    {
        await using var h = await RebuildWhilePausedAsync();
        h.GitHub.Override(EngineHarness.ListAPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, ForbiddenByPolicy), times: 1);
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        var list = h.GitHub.Count(EngineHarness.ListAPath);

        h.Engine.Post(new EngineCommand.RepoManagerOpened());
        await h.DrainAsync();
        Assert.Equal(list, h.GitHub.Count(EngineHarness.ListAPath));

        h.Clock.Advance(Tuning.PermissionRetryInterval);
        h.Engine.Post(new EngineCommand.RepoManagerOpened());
        await h.DrainAsync();
        Assert.Equal(list + 1, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.True(h.Last.RepoListFetched);
    }

    /// <summary>命令列 <c>--refresh</c>（第二階段 spec §10.5）：完整清單是這一批的目標，抓完最後一頁才算刷新完成，不是第 1 頁回來就算。</summary>
    [Fact]
    public async Task 暫停中命令列刷新要等完整清單的最後一頁才算完成()
    {
        await using var h = await RebuildWhilePausedAsync();
        AddReposBeyondFirstPage(h);
        var started = h.Cache.GetStatusSnapshot()!.RefreshStartedSeq;

        h.Engine.Post(new EngineCommand.CliRefresh());
        await StepUntilAsync(h, () => h.GitHub.Count(EngineHarness.ListAPath) == 2);
        Assert.Equal(started, h.Cache.GetStatusSnapshot()!.RefreshDoneSeq);

        await StepUntilAsync(h, () => h.GitHub.Count(ListASecondPage) == 1);
        var row = h.Cache.GetStatusSnapshot()!;
        Assert.Equal((started + 1, started + 1, false), (row.RefreshStartedSeq, row.RefreshDoneSeq, row.RefreshPartial));
        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(ReposBeyondOnePage, h.Last.Repos.Count);
    }

    /// <summary>以暫停模式啟動時啟動的完整重抓沒有排：資料庫是空的就和重建後一樣，第一次 F5 補做。</summary>
    [Fact]
    public async Task 以暫停模式啟動資料庫是空的時按F5也抓回完整清單()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Settings.RefreshMode = RefreshMode.Paused;
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            AddReposBeyondFirstPage(h);
        });
        await h.DrainAsync();
        Assert.Equal(0, h.GitHub.Count(EngineHarness.ListAPath));

        h.Engine.Post(new EngineCommand.RefreshAll());
        await DrainAcrossRequestLimitAsync(h);

        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(ReposBeyondOnePage, h.Last.Repos.Count);
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
    }

    /// <summary>資料庫裡有上一次的清單：不算已知不完整，暫停中重新啟動的總狀態照舊（spec §6.2）；第一次 F5 補做啟動時沒排的那一輪，帶 ETag。</summary>
    [Fact]
    public async Task 以暫停模式啟動資料庫有清單時總狀態照舊第一次F5補做完整重抓()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
        var (list, responses) = (h.GitHub.Count(EngineHarness.ListAPath), h.GitHub.Responses.Count);

        h.Settings.RefreshMode = RefreshMode.Paused;
        await h.RestartAsync();
        await h.DrainAsync();
        Assert.False(h.Last.RepoListFetched);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
        Assert.Equal(list, h.GitHub.Count(EngineHarness.ListAPath));

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Equal(list + 1, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.Contains((EngineHarness.ListAPath, (int)HttpStatusCode.NotModified), h.GitHub.Responses.Skip(responses));
        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>只有暫停模式沒有週期性的完整重抓：其他模式的 F5 照舊依週期（spec §5.3、§6.6）。</summary>
    [Fact]
    public async Task 自動更新時F5不補做完整重抓()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var (list, probes) = (h.GitHub.Count(EngineHarness.ListAPath), h.GitHub.Count(EngineHarness.ProbeAPath));

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Equal(list, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.Equal(probes + 1, h.GitHub.Count(EngineHarness.ProbeAPath));
    }

    /// <summary>其他模式清單還沒抓完時有自己的退避（spec §6.4）：F5 不提前重抓，照舊送探測。</summary>
    [Fact]
    public async Task 自動更新時清單還沒抓完按F5也不提前重抓()
    {
        await using var h = EngineHarness.Create(h =>
            h.GitHub.Override(EngineHarness.ListAPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1));
        await h.DrainAsync();
        Assert.False(h.Last.RepoListFetched);
        var (list, probes) = (h.GitHub.Count(EngineHarness.ListAPath), h.GitHub.Count(EngineHarness.ProbeAPath));

        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Equal(list, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.Equal(probes + 1, h.GitHub.Count(EngineHarness.ProbeAPath));
    }

    /// <summary>開著「包含協作 repo」：這一輪連清單 B 一起抓完才算完整，第 2 頁之後的協作 repo 也算進總狀態。</summary>
    [Fact]
    public async Task 暫停中開著包含協作repo時F5抓回兩份完整清單()
    {
        await using var h = await RebuildWhilePausedAsync(includeCollaborators: true);
        AddReposBeyondFirstPage(h, collaborator: true);

        h.Engine.Post(new EngineCommand.RefreshAll());
        await DrainAcrossRequestLimitAsync(h);

        Assert.Equal(1, h.GitHub.Count(ListBSecondPage));
        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(ReposBeyondOnePage + 1, h.Last.Repos.Count);
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
    }

    /// <summary>
    /// F5 的那一輪進行中才打開「包含協作 repo」：同一輪接著抓清單 B（同開啟 repo 管理頁）。否則這一輪抓完之後算已抓過，暫停中不會再抓，
    /// 協作 repo 只剩探測的第 1 頁。
    /// </summary>
    [Fact]
    public async Task 暫停中F5的一輪進行中打開包含協作repo時同一輪接著抓清單B()
    {
        await using var h = await RebuildWhilePausedAsync();
        AddReposBeyondFirstPage(h, oldestFails: false);
        AddReposBeyondFirstPage(h, collaborator: true);
        var list = h.GitHub.Count(EngineHarness.ListAPath);

        h.Engine.Post(new EngineCommand.RefreshAll());
        await StepUntilAsync(h, () => h.GitHub.Count(EngineHarness.ListAPath) == list + 1);
        Assert.False(h.Last.RepoListFetched);
        h.Engine.Post(new EngineCommand.SettingsChanged(new AppSettings { RefreshMode = RefreshMode.Paused, IncludeCollaboratorRepos = true }));
        await DrainAcrossRequestLimitAsync(h);

        Assert.Equal(1, h.GitHub.Count(ListBSecondPage));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
    }

    /// <summary>repo 管理與 F5 共用同一輪：不管誰先，清單 A 都只抓一輪，清單 B 也抓到。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 暫停中開啟repo管理與按F5只抓一輪(bool repoManagerFirst)
    {
        await using var h = await RebuildWhilePausedAsync();
        AddReposBeyondFirstPage(h);
        h.GitHub.AddRepo(FakeData.Repo(CollaboratorRepoId, Collaborator, Start.AddDays(-1), ownerId: 4001, ownerType: "User"), collaborator: true);
        var list = h.GitHub.Count(EngineHarness.ListAPath);
        EngineCommand[] commands = repoManagerFirst
            ? [new EngineCommand.RepoManagerOpened(), new EngineCommand.RefreshAll()]
            : [new EngineCommand.RefreshAll(), new EngineCommand.RepoManagerOpened()];

        foreach (var command in commands)
        {
            h.Engine.Post(command);
        }

        await DrainAcrossRequestLimitAsync(h);

        Assert.Equal(list + 1, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.Equal(1, h.GitHub.Count(ListASecondPage));
        Assert.Equal(1, h.GitHub.Count(ListBPath));
        Assert.Contains(h.Last.Repos, row => row.Repo.Id == CollaboratorRepoId);
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
    }

    /// <summary>開啟 repo 管理頁排的第 1 頁是使用者操作的優先序（spec §5.1）：接著按 F5 不把它降回維護的優先序。</summary>
    [Fact]
    public async Task 暫停中開啟repo管理之後按F5不降低清單的優先序()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        h.Settings.RefreshMode = RefreshMode.Paused;
        await h.RestartAsync();
        await h.DrainAsync();
        var requests = h.GitHub.ApiRequests.Count;

        h.Engine.Post(new EngineCommand.RepoManagerOpened());
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.StepAsync();

        Assert.Equal(EngineHarness.ListAPath, h.GitHub.ApiRequests[requests]);
    }

    [Fact]
    public async Task 暫停中F5的一輪進行中開啟repo管理時同一輪接著抓清單B()
    {
        await using var h = await RebuildWhilePausedAsync();
        AddReposBeyondFirstPage(h);
        h.GitHub.AddRepo(FakeData.Repo(CollaboratorRepoId, Collaborator, Start.AddDays(-1), ownerId: 4001, ownerType: "User"), collaborator: true);
        var list = h.GitHub.Count(EngineHarness.ListAPath);

        h.Engine.Post(new EngineCommand.RefreshAll());
        await StepUntilAsync(h, () => h.GitHub.Count(EngineHarness.ListAPath) == list + 1);
        h.Engine.Post(new EngineCommand.RepoManagerOpened());
        await DrainAcrossRequestLimitAsync(h);

        Assert.Equal(list + 1, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.Equal(1, h.GitHub.Count(ListASecondPage));
        Assert.Equal(1, h.GitHub.Count(ListBPath));
        Assert.Contains(h.Last.Repos, row => row.Repo.Id == CollaboratorRepoId);
    }

    /// <summary>
    /// 儲存層還壞、在等下一次重試重建時：完整重抓的結果會被那次重建清掉，所以 F5 不抓完整清單，每次只有探測（同沒有這個修正時），也不多留備份。
    /// 等重試重建完才按的第一次才抓。
    /// </summary>
    [Fact]
    public async Task 暫停中儲存還壞時重複按F5不抓完整清單恢復重建之後才抓()
    {
        var storageBroken = true;
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, () => storageBroken);
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();

        // 暫停之前自動更新已經抓回清單：要在暫停中重試重建過一次，才是「還沒抓完過清單」又在等重試。
        await StepUntilNextRetryAsync(h);
        AddReposBeyondFirstPage(h);
        var rebuilds = h.Engine.DatabaseRebuildCount;
        var list = h.GitHub.Count(EngineHarness.ListAPath);

        // 時間不前進，一直在等下一次重試。
        for (var press = 0; press < RefreshPresses; press++)
        {
            var probes = h.GitHub.Count(EngineHarness.ProbeAPath);
            h.Engine.Post(new EngineCommand.RefreshAll());
            await h.StepAsync();
            await h.StepAsync();
            Assert.Equal(list, h.GitHub.Count(EngineHarness.ListAPath));
            Assert.Equal(probes + 1, h.GitHub.Count(EngineHarness.ProbeAPath));
            Assert.Equal(FirstBackupOnly, BackupCount(h));
        }

        storageBroken = false;
        await StepUntilNextRetryAsync(h);
        Assert.True(h.Engine.DatabaseRebuildCount > rebuilds);
        Assert.Equal(FirstBackupOnly, BackupCount(h));

        h.Engine.Post(new EngineCommand.RefreshAll());
        await DrainAcrossRequestLimitAsync(h);

        Assert.Equal(list + 1, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
    }

    /// <remarks>改名失敗靠 Windows 的檔案共用規則，只在 Windows 上執行。暫停沒有專用分支：改名失敗的重試間隔和沒暫停一樣。</remarks>
    [WindowsFact]
    public async Task 暫停中改名失敗的重試間隔和沒暫停一樣()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var backups = BackupCount(h);
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        // 暫停時只有 F5 會送請求：回應寫入 repos 表時才讀到損毀。
        using (new FileStream(h.Workspace.Paths.CacheDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            h.Engine.Post(new EngineCommand.RefreshAll());
            await h.DrainAsync();
            Assert.Single(Regex.Matches(h.ErrorLogText, "重建快取資料庫"));
        }

        await AdvanceAndStepAsync(h, Tuning.EngineTick);

        Assert.Equal(backups + 1, BackupCount(h));
    }

    [Fact]
    public async Task 暫停中才第一次發現損毀時可能有設定的檔照樣改名保留()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        // 暫停時只有 F5 會送請求：回應寫入 repos 表時才讀到損毀。
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.True(h.Engine.DatabaseRebuildCount >= 1);
        Assert.Equal([OtherRepoId], OverrideRepoIds(h, Assert.Single(BackupPaths(h))));
    }

    [Fact]
    public async Task 有靜音設定的資料庫損毀時第一份留下之後重試這份的位元組不變而且讀得到設定()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();

        await EnterRebuildBackoffAsync(h, storageBroken: () => true);
        var backup = Assert.Single(BackupPaths(h));
        var bytes = File.ReadAllBytes(backup);
        Assert.Equal([OtherRepoId], OverrideRepoIds(h, backup));

        var rebuilds = h.Engine.DatabaseRebuildCount;
        await h.AdvanceInStepsAsync(Tuning.RepoErrorBackoffMax, RetryStep);

        Assert.True(h.Engine.DatabaseRebuildCount > rebuilds);
        Assert.Equal(backup, Assert.Single(BackupPaths(h)));
        Assert.Equal(bytes, File.ReadAllBytes(backup));
        Assert.Equal([OtherRepoId], OverrideRepoIds(h, backup));
    }

    [Fact]
    public async Task 退避期間又設定靜音時下一次重試多留一份讀得到那一筆再下一次不留()
    {
        await using var h = await EnterPersistentCorruptionAsync("WriteStatus", "status_snapshot");
        var first = Assert.Single(BackupPaths(h));

        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();
        AssertRebuilds(h, RebuildsBeforeBackoff);

        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        AssertRebuilds(h, RebuildsBeforeBackoff + 1, backups: FirstBackupOnly + 1);
        var second = Assert.Single(BackupPaths(h), path => path != first);
        Assert.Equal([OtherRepoId], OverrideRepoIds(h, second));

        await AdvanceAndStepAsync(h, Tuning.EngineTick * 2);
        AssertRebuilds(h, RebuildsBeforeBackoff + 2, backups: FirstBackupOnly + 1);
    }

    /// <summary>設定寫進去之後 repo_overrides 才壞掉：讀不到資料列，只剩旗標保護這份設定。之後再設一次寫入失敗，也不會把旗標清掉。</summary>
    [Fact]
    public async Task 退避期間設靜音成功之後repo_overrides壞掉下一次重試仍然留檔()
    {
        await using var h = await EnterPersistentCorruptionAsync("WriteStatus", "status_snapshot");
        var discards = DiscardLogCount(h);

        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();
        Assert.DoesNotContain("處理指令 SetOverride", h.ErrorLogText, StringComparison.Ordinal);

        CorruptTable(h.Workspace.Paths.CacheDbPath, RebuiltCorruptTable);
        Assert.False(DatabaseInitializer.HasOverrideRows(h.Workspace.Paths.CacheDbPath));
        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: false, ForceIncluded: true)));
        await h.StepAsync();
        Assert.Contains("處理指令 SetOverride", h.ErrorLogText, StringComparison.Ordinal);
        AssertRebuilds(h, RebuildsBeforeBackoff);

        await AdvanceAndStepAsync(h, Tuning.EngineTick);

        AssertRebuilds(h, RebuildsBeforeBackoff + 1, backups: FirstBackupOnly + 1);
        Assert.Equal(discards, DiscardLogCount(h));
    }

    [Fact]
    public async Task 寫入repo_overrides失敗時不算設過下一次重試不多留檔而且橫幅指向原本那份()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        await EnterRebuildBackoffAsync(h, storageBroken: () => true);
        var kept = Path.GetFileName(Assert.Single(BackupPaths(h)));

        // 這裡的損毀就在 repo_overrides：寫不進去，這份檔裡沒有這一筆，留著只會佔名額。
        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();
        Assert.Contains("處理指令 SetOverride", h.ErrorLogText, StringComparison.Ordinal);

        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        AssertRebuilds(h, RebuildsBeforeBackoff + 1);
        Assert.Contains(kept, h.Last.Banner!.Text, StringComparison.Ordinal);

        await AdvanceAndStepAsync(h, Tuning.EngineTick * 2);
        AssertRebuilds(h, RebuildsBeforeBackoff + 2);
    }

    /// <summary>latest 裡是真正的設定：寫入失敗的那份檔沒有這一筆，不能拿它蓋掉。</summary>
    [Fact]
    public async Task 編號備份已滿時寫入repo_overrides失敗也不會蓋掉latest裡的設定()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();
        AddNumberedBackups(h);

        await EnterRebuildBackoffAsync(h, storageBroken: () => true);
        var latest = h.Workspace.PathTo(LatestBackupName);
        var latestBytes = File.ReadAllBytes(latest);
        Assert.Equal([OtherRepoId], OverrideRepoIds(h, latest));

        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();
        Assert.Contains("處理指令 SetOverride", h.ErrorLogText, StringComparison.Ordinal);

        await AdvanceAndStepAsync(h, Tuning.EngineTick);

        Assert.Equal(RebuildsBeforeBackoff + 1, h.Engine.DatabaseRebuildCount);
        Assert.Equal(latestBytes, File.ReadAllBytes(latest));
    }

    /// <summary>丟棄是唯一不可逆的路，只看旗標不夠：磁碟上一份編號備份都沒有時不能丟（例如持續損毀期間使用者手動清掉了）。</summary>
    [Fact]
    public async Task 持續損毀期間編號備份被刪光時下一次重試改名保留()
    {
        await using var h = await EnterPersistentCorruptionAsync("WriteStatus", "status_snapshot");
        File.Delete(Assert.Single(BackupPaths(h)));

        await AdvanceAndStepAsync(h, Tuning.EngineTick);

        AssertRebuilds(h, RebuildsBeforeBackoff + 1);
    }

    /// <summary>同上：這份檔讀得到 repo_overrides 的資料列時不能丟，不管記憶體旗標怎麼說（例如別的程式寫進去的）。</summary>
    [Fact]
    public async Task 持續損毀期間讀得到repo_overrides資料列時下一次重試改名保留()
    {
        await using var h = await EnterPersistentCorruptionAsync("WriteStatus", "status_snapshot");
        var first = Assert.Single(BackupPaths(h));
        Execute(h.Workspace.Paths.CacheDbPath, string.Create(CultureInfo.InvariantCulture,
            $"INSERT INTO repo_overrides (repo_id, muted, force_included, updated_at) VALUES ({OtherRepoId}, 1, 0, '2026-10-24T00:00:00Z');"));

        await AdvanceAndStepAsync(h, Tuning.EngineTick);

        AssertRebuilds(h, RebuildsBeforeBackoff + 1, backups: FirstBackupOnly + 1);
        Assert.Equal([OtherRepoId], OverrideRepoIds(h, Assert.Single(BackupPaths(h), path => path != first)));
    }

    [Fact]
    public async Task 編號備份已滿時有靜音設定的資料庫損毀存到latest而且儲存恢復後監看照常()
    {
        var storageBroken = true;
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();
        var numbered = AddNumberedBackups(h);

        await EnterRebuildBackoffAsync(h, () => storageBroken);

        var latest = h.Workspace.PathTo(LatestBackupName);
        Assert.Equal([OtherRepoId], OverrideRepoIds(h, latest));
        Assert.Contains(LatestBackupName, h.Last.Banner!.Text, StringComparison.Ordinal);
        Assert.Equal(numbered.Count + 1, BackupCount(h));
        Assert.All(numbered, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));

        // 儲存層恢復：下一次重試就恢復，清單抓回來，不會停在載入中；丟棄不動 latest。
        var latestBytes = File.ReadAllBytes(latest);
        storageBroken = false;
        await h.AdvanceAsync(Tuning.EngineTick);
        await h.AdvanceInStepsAsync(Tuning.QuietMax, Tuning.ActiveRepoInterval);

        Assert.True(h.Last.RepoListFetched);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
        Assert.Equal(numbered.Count + 1, BackupCount(h));
        Assert.Equal(latestBytes, File.ReadAllBytes(latest));
    }

    [Fact]
    public async Task 持續損毀時反覆重新啟動總份數不超過上限最早那份不變而且都能完成初始化()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        string? earliest = null;
        byte[]? earliestBytes = null;

        for (var round = 0; round < Tuning.MaxCorruptBackups + RestartsBeyondLimit; round++)
        {
            await h.RestartAsync();
            CorruptAfterEachRebuild(h, "WriteStatus", "status_snapshot");
            CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
            var requests = h.GitHub.ApiRequests.Count;

            await AdvanceAndStepAsync(h, Tuning.QuietMax);

            // 送得出 E1 代表初始化完成，不是卡在載入資料的退避裡。
            Assert.Contains("/user", h.GitHub.ApiRequests.Skip(requests));
            Assert.True(h.Engine.DatabaseRebuildCount >= 1);
            Assert.InRange(BackupCount(h), 1, Tuning.MaxCorruptBackups + 1);
            if (earliest is null)
            {
                earliest = Assert.Single(BackupPaths(h));
                earliestBytes = File.ReadAllBytes(earliest);
            }
        }

        Assert.Equal(Tuning.MaxCorruptBackups + 1, BackupCount(h));
        Assert.Equal(Tuning.MaxCorruptBackups, DatabaseInitializer.CountBackups(h.Workspace.Paths.CacheDbPath));
        Assert.True(File.Exists(h.Workspace.PathTo(LatestBackupName)));
        Assert.Equal(earliestBytes, File.ReadAllBytes(earliest!));
    }

    [Fact]
    public async Task 持續損毀時推進一天超過視窗也不多留備份()
    {
        await using var h = await EnterPersistentCorruptionAsync("WriteStatus", "status_snapshot");
        var rebuilds = h.Engine.DatabaseRebuildCount;

        // 模擬睡眠喚醒：連續次數與間隔都重新起算，第一次立即重建；這個程序自己建的檔照樣不留。
        h.Clock.Advance(SleepGap);
        h.Engine.Post(new EngineCommand.Resume());
        await h.DrainAsync();

        Assert.True(h.Engine.DatabaseRebuildCount > rebuilds);
        Assert.Equal(FirstBackupOnly, BackupCount(h));
    }

    [Fact]
    public async Task 丟棄時橫幅仍指向留下的那份檔名而且NoticeId每次換新()
    {
        await using var h = await EnterPersistentCorruptionAsync("ResetAfterDatabaseRecovery", RebuiltCorruptTable);
        var kept = Path.GetFileName(Assert.Single(BackupPaths(h)));
        var banner = h.Last.Banner!;
        Assert.Equal($"快取資料庫無法讀取，已改名保留為 {kept} 並重建；repo 的靜音與強制納入設定已遺失，請到 repo 管理重新設定。", banner.Text);

        await AdvanceAndStepAsync(h, Tuning.EngineTick);

        AssertRebuilds(h, RebuildsBeforeBackoff + 1);
        Assert.Equal(banner.Text, h.Last.Banner!.Text);
        Assert.NotEqual(banner.NoticeId, h.Last.Banner!.NoticeId);
        Assert.Contains(DiscardedLogContext, h.ErrorLogText, StringComparison.Ordinal);
    }

    /// <summary>
    /// 舊的回退會顯示「已改名保留為 cache.db」。損毀回報之後、改名之前主檔被別人移走（error.log 寫完時注入）：
    /// 沒有東西可以保留，這個程序也還沒留過，橫幅要照實說。
    /// </summary>
    [Fact]
    public async Task 主檔已經不在時重建後橫幅說沒有留下備份而且監看照常()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var databasePath = h.Workspace.Paths.CacheDbPath;
        var removed = 0;
        h.Clock.BeforeGetUtcNow = () =>
        {
            if (Volatile.Read(ref removed) == 0 && EngineClock.CalledFrom("ReportUnexpected") && Interlocked.Exchange(ref removed, 1) == 0)
            {
                SqliteConnection.ClearAllPools();
                File.Delete(databasePath);
            }
        };
        CorruptTable(databasePath, "repos");

        await h.AdvanceAsync(Tuning.QuietMax);

        Assert.Equal(1, Volatile.Read(ref removed));
        AssertRebuilds(h, 1, backups: 0);
        Assert.Equal("快取資料庫無法讀取，已重建，這次沒有留下備份；repo 的靜音與強制納入設定可能已遺失，請到 repo 管理確認。", h.Last.Banner?.Text);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
    }

    /// <summary>
    /// 不需要使用者清備份也會走到旗標是 false、磁碟上 0 份編號備份：主檔被別人移走，SetAside 沒有東西可留，旗標照樣清掉。
    /// 這時再損毀不能丟棄，這個程序還一份都沒留過。
    /// </summary>
    [Fact]
    public async Task 主檔被移走沒有留到備份之後再損毀時改名保留()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var databasePath = h.Workspace.Paths.CacheDbPath;
        var removed = 0;
        h.Clock.BeforeGetUtcNow = () =>
        {
            if (Volatile.Read(ref removed) == 0 && EngineClock.CalledFrom("ReportUnexpected") && Interlocked.Exchange(ref removed, 1) == 0)
            {
                SqliteConnection.ClearAllPools();
                File.Delete(databasePath);
            }
        };
        CorruptTable(databasePath, "repos");
        await h.AdvanceAsync(Tuning.QuietMax);
        AssertRebuilds(h, 1, backups: 0);

        CorruptTable(databasePath, "repos");
        await AdvanceAndStepAsync(h, Tuning.QuietMax);

        AssertRebuilds(h, 2);
        Assert.DoesNotContain(DiscardedLogContext, h.ErrorLogText, StringComparison.Ordinal);
    }

    /// <remarks>
    /// 開著的檔案不能改名，這是 Windows 的檔案共用規則，所以只在 Windows 上執行。
    /// 磁碟上已有編號備份時 IsDiscardable 是 true（這裡讀不到資料列），只剩旗標擋著：旗標清早了，第一次損毀的檔會在重試時被丟掉。
    /// </remarks>
    [WindowsFact]
    public async Task 已有編號備份時第一次損毀改名失敗重試成功仍然留檔()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var databasePath = h.Workspace.Paths.CacheDbPath;
        File.WriteAllText(h.Workspace.PathTo("cache.db.corrupt-20261023000000.bak"), "上次執行留下的", System.Text.Encoding.UTF8);
        CorruptTable(databasePath, "repos");

        using (new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await AdvanceAndStepAsync(h, Tuning.QuietMax);
            AssertRebuilds(h, 0);
            Assert.Contains("重建快取資料庫", h.ErrorLogText, StringComparison.Ordinal);
        }

        await AdvanceAndStepAsync(h, Tuning.RepoErrorBackoffMax);

        AssertRebuilds(h, 1, backups: FirstBackupOnly + 1);
        Assert.DoesNotContain(DiscardedLogContext, h.ErrorLogText, StringComparison.Ordinal);
    }

    /// <remarks>同上，只在 Windows 上執行。repo_overrides 也壞掉時 HasOverrideRows 讀不到，真的只剩旗標保護使用者的設定。</remarks>
    [WindowsFact]
    public async Task 已有編號備份時有設定的檔改名失敗而且repo_overrides也壞掉重試仍然留檔()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();
        var databasePath = h.Workspace.Paths.CacheDbPath;
        File.WriteAllText(h.Workspace.PathTo("cache.db.corrupt-20261023000000.bak"), "上次執行留下的", System.Text.Encoding.UTF8);
        CorruptTable(databasePath, RebuiltCorruptTable);
        CorruptTable(databasePath, "repos");

        using (new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await AdvanceAndStepAsync(h, Tuning.QuietMax);
            AssertRebuilds(h, 0);
        }

        await AdvanceAndStepAsync(h, Tuning.RepoErrorBackoffMax);

        Assert.True(h.Engine.DatabaseRebuildCount >= 1);
        Assert.Equal(FirstBackupOnly + 1, BackupCount(h));
    }

    /// <remarks>
    /// 同上，只在 Windows 上執行。別的程式在檢查之後才開著 WAL（這裡用備份檔名讀時間的時機模擬）：
    /// 主檔先搬走的話，下一次重試留的是路徑上新建的空檔，橫幅指向它，真正的備份也少了 WAL。
    /// </remarks>
    [WindowsFact]
    public async Task 檢查之後別的程式才開著WAL時主檔留在原地放開後重試留下真正的資料()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();
        var databasePath = h.Workspace.Paths.CacheDbPath;
        var wal = databasePath + "-wal";
        var placed = 0;
        FileStream? holder = null;
        h.Clock.BeforeGetUtcNow = () =>
        {
            if (Volatile.Read(ref placed) == 0 && EngineClock.CalledFrom("SetAside") && Interlocked.Exchange(ref placed, 1) == 0)
            {
                File.WriteAllText(wal, "別的程式開著", System.Text.Encoding.UTF8);
                holder = new FileStream(wal, FileMode.Open, FileAccess.Read, FileShare.None);
            }
        };
        CorruptTable(databasePath, "repos");

        try
        {
            await AdvanceAndStepAsync(h, Tuning.QuietMax);
            Assert.Equal(1, Volatile.Read(ref placed));
            AssertRebuilds(h, 0, backups: 0);
            Assert.Contains("重建快取資料庫", h.ErrorLogText, StringComparison.Ordinal);
            Assert.True(new FileInfo(databasePath).Length > 0);
        }
        finally
        {
            holder?.Dispose();
        }

        await AdvanceAndStepAsync(h, Tuning.RepoErrorBackoffMax);

        AssertRebuilds(h, 1);
        var backup = Assert.Single(BackupPaths(h));
        Assert.Equal([OtherRepoId], OverrideRepoIds(h, backup));
        Assert.Contains(Path.GetFileName(backup), h.Last.Banner!.Text, StringComparison.Ordinal);
    }

    /// <remarks>同上，只在 Windows 上執行。主檔先搬走的話，下一次重試會拿新建的空檔蓋掉 latest 裡真正的資料，橫幅還說已保留。</remarks>
    [WindowsFact]
    public async Task 編號備份已滿時別的程式開著latest的附屬檔就退避放開後才存到latest()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();
        AddNumberedBackups(h);
        var databasePath = h.Workspace.Paths.CacheDbPath;
        var latest = h.Workspace.PathTo(LatestBackupName);
        File.WriteAllText(latest, "舊的 latest", System.Text.Encoding.UTF8);
        File.WriteAllText(latest + "-shm", "舊的 shm", System.Text.Encoding.UTF8);
        CorruptTable(databasePath, "repos");

        using (new FileStream(latest + "-shm", FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await AdvanceAndStepAsync(h, Tuning.QuietMax);

            Assert.Equal(0, h.Engine.DatabaseRebuildCount);
            Assert.True(File.Exists(databasePath) && new FileInfo(databasePath).Length > 0);
            Assert.Equal("舊的 latest", File.ReadAllText(latest, System.Text.Encoding.UTF8));
        }

        await AdvanceAndStepAsync(h, Tuning.RepoErrorBackoffMax);

        Assert.Equal(1, h.Engine.DatabaseRebuildCount);
        Assert.Equal([OtherRepoId], OverrideRepoIds(h, latest));
        Assert.False(File.Exists(latest + "-shm"));
        Assert.Contains(LatestBackupName, h.Last.Banner!.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 改名成功之後 CreateSchema 持續失敗（SetAside 讀時間時在 -wal 的位置放資料夾，SQLite 開不了 WAL）：
    /// 旗標在 Rebuild 之前就設好，下一次重試不會把半成品再佔一個名額；橫幅的檔名也在 Rebuild 之前記好。
    /// </summary>
    [Fact]
    public async Task CreateSchema持續失敗時備份不超過一份修好之後自動恢復()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var databasePath = h.Workspace.Paths.CacheDbPath;
        var walBlocker = databasePath + "-wal";
        var placed = 0;
        h.Clock.BeforeGetUtcNow = () =>
        {
            if (Volatile.Read(ref placed) == 0 && EngineClock.CalledFrom("SetAside") && Interlocked.Exchange(ref placed, 1) == 0)
            {
                File.Delete(walBlocker);
                Directory.CreateDirectory(walBlocker);
            }
        };
        CorruptTable(databasePath, "repos");

        await AdvanceAndStepAsync(h, Tuning.QuietMax);
        Assert.Equal(1, Volatile.Read(ref placed));
        AssertRebuilds(h, 0);
        Assert.Contains("重建快取資料庫", h.ErrorLogText, StringComparison.Ordinal);
        var kept = Path.GetFileName(Assert.Single(BackupPaths(h)));

        // 重試一直失敗：不多留備份，重建次數也不會增加。
        await h.AdvanceInStepsAsync(Tuning.RepoErrorBackoffMax * 2, RetryStep);
        AssertRebuilds(h, 0);

        // 修好之後：下一次重試（最晚一個上限的間隔）自動恢復，監看照常。
        Directory.Delete(walBlocker);
        await h.AdvanceInStepsAsync(Tuning.RepoErrorBackoffMax + Tuning.QuietMax, Tuning.ActiveRepoInterval);
        AssertRebuilds(h, 1);
        Assert.Equal(kept, Path.GetFileName(Assert.Single(BackupPaths(h))));
        Assert.Contains(kept, h.Last.Banner!.Text, StringComparison.Ordinal);
        Assert.True(h.Last.RepoListFetched);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
    }

    /// <summary>半成品的檔裡沒有設定（寫入也失敗）：修好之後丟棄，不佔名額，橫幅仍指向真正留下的那份。</summary>
    [Fact]
    public async Task CreateSchema失敗期間設靜音寫入失敗時半成品不留檔()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var databasePath = h.Workspace.Paths.CacheDbPath;
        var walBlocker = databasePath + "-wal";
        var placed = 0;
        h.Clock.BeforeGetUtcNow = () =>
        {
            if (Volatile.Read(ref placed) == 0 && EngineClock.CalledFrom("SetAside") && Interlocked.Exchange(ref placed, 1) == 0)
            {
                File.Delete(walBlocker);
                Directory.CreateDirectory(walBlocker);
            }
        };
        CorruptTable(databasePath, "repos");
        await AdvanceAndStepAsync(h, Tuning.QuietMax);
        AssertRebuilds(h, 0);
        var kept = Path.GetFileName(Assert.Single(BackupPaths(h)));

        h.Engine.Post(new EngineCommand.SetOverride(OtherRepoId, new RepoOverride(Muted: true, ForceIncluded: false)));
        await h.StepAsync();
        Assert.Contains("處理指令 SetOverride", h.ErrorLogText, StringComparison.Ordinal);

        Directory.Delete(walBlocker);
        await AdvanceAndStepAsync(h, Tuning.RepoErrorBackoffMax);

        AssertRebuilds(h, 1);
        Assert.Contains(kept, h.Last.Banner!.Text, StringComparison.Ordinal);
    }

    /// <remarks>開著的檔案沒有允許刪除就不能刪，這是 Windows 的檔案共用規則，所以只在 Windows 上執行。</remarks>
    [WindowsFact]
    public async Task 可丟棄時遇到別的程式開著cache_db就退避不刪放開後恢復()
    {
        await using var h = await EnterPersistentCorruptionAsync("WriteStatus", "status_snapshot");
        var databasePath = h.Workspace.Paths.CacheDbPath;
        var discardLogs = DiscardLogCount(h);

        using (new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await AdvanceAndStepAsync(h, Tuning.EngineTick);

            // 刪之前先寫 error.log（進到這裡之前第 2 次重建已經丟棄過，所以比筆數），有檔案大小與編號備份份數；刪不掉走既有的退避。
            AssertRebuilds(h, RebuildsBeforeBackoff);
            Assert.True(File.Exists(databasePath));
            Assert.Equal(discardLogs + 1, DiscardLogCount(h));
            Assert.Matches("大小 [1-9][0-9]* bytes，磁碟上有 1 份編號備份", h.ErrorLogText);
            Assert.Contains("重建快取資料庫", h.ErrorLogText, StringComparison.Ordinal);

            // 間隔接著倍增：上一次等 1 個 EngineTick，這次等 2 個。
            await AdvanceAndStepAsync(h, Tuning.EngineTick);
            AssertRebuilds(h, RebuildsBeforeBackoff);
        }

        await AdvanceAndStepAsync(h, Tuning.EngineTick);
        AssertRebuilds(h, RebuildsBeforeBackoff + 1);
    }

    // ── 第 8 輪：第一次載入（spec §6.6、§7.3）────────────────────────────

    /// <summary>資料庫是空的：最舊的 repo 排在最後才查到，它的失敗回來之前沒有任何一份快照是 Success。</summary>
    [Fact]
    public async Task 自動更新冷啟動時全部查完之前不報綠燈最舊的repo失敗時總狀態是失敗()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            AddReposBeyondFirstPage(h);
        });

        await AdvanceMinutesUntilAsync(h, () => h.Last.Summary.State == OverallState.Failure);

        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
        Assert.Equal(ReposBeyondOnePage, h.Last.Repos.Count);
    }

    /// <summary>全部成功時，第一份 Success 是所有 repo 都查過之後；這之前狀態表是 no_data，命令列也不會先回成功。</summary>
    [Fact]
    public async Task 自動更新冷啟動全部成功時查完才是成功這之前命令列是無資料()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            AddReposBeyondFirstPage(h, oldestFails: false);
        });

        await StepUntilAsync(h, () => h.Snapshots.Count > 0 && h.Last.Loading.Completed > 0);
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);
        Assert.Equal("no_data", StatusOverall(h));

        await AdvanceMinutesUntilAsync(h, () => h.Last.Summary.State == OverallState.Success);

        var firstGreen = h.Snapshots.First(snapshot => snapshot.Summary.State == OverallState.Success);
        Assert.True(firstGreen.RepoListFetched);
        Assert.Equal(ReposBeyondOnePage, firstGreen.Loading.Total);
        Assert.Equal(firstGreen.Loading.Total, firstGreen.Loading.Completed);
    }

    /// <summary>解除暫停時探測先回來、完整清單的第 2 頁排在各 repo 的查詢後面：清單還沒抓完時，看得到的全部成功也不算。</summary>
    [Fact]
    public async Task 暫停中重建後不按F5直接解除暫停時全部查完之前不報綠燈()
    {
        await using var h = await RebuildWhilePausedAsync();
        AddReposBeyondFirstPage(h);
        var before = h.Snapshots.Count;

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await AdvanceMinutesUntilAsync(h, () => h.Last.Summary.State == OverallState.Failure);

        Assert.DoesNotContain(h.Snapshots.Skip(before), snapshot => snapshot.Summary.State == OverallState.Success);
        Assert.Equal(ReposBeyondOnePage, h.Last.Repos.Count);
    }

    /// <summary>
    /// 還沒抓完過的清單失敗時用安靜間隔的上限重試（同還沒查過的 workflow 清單），不必等一小時；
    /// 第 2 頁回 5xx 一次，這段期間清單不完整，全部成功也不是 Success。
    /// </summary>
    [Fact]
    public async Task 冷啟動時清單第2頁失敗不報綠燈而且不必等一小時就補齊()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            AddReposBeyondFirstPage(h);
            h.GitHub.Override(ListASecondPage, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        });

        await AdvanceMinutesUntilAsync(h, () => h.Last.Summary.State == OverallState.Failure);

        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
        Assert.Equal(ReposBeyondOnePage, h.Last.Repos.Count);
        Assert.InRange(h.Clock.GetUtcNow() - Start, TimeSpan.Zero, Tuning.RepoListRefresh);
    }

    /// <summary>repo 不超過一頁、清單回 5xx 一次：探測已經看到全部，總狀態是無資料只到下一次重試，不是一小時。</summary>
    [Fact]
    public async Task 冷啟動時repo不超過一頁清單回5xx一次也只灰到下一次重試()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            h.GitHub.Override(EngineHarness.ListAPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        });
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.Contains(h.Last.Rows, row => row.Run.Id == 9001);
        Assert.False(h.Last.RepoListFetched);
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        await h.AdvanceInStepsAsync(Tuning.QuietMax, Tuning.ProbeInterval);

        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>
    /// 清單一直失敗：一直是無資料（清單確實不完整），重試間隔從安靜間隔的上限倍增到錯誤退避的上限，不會每一步都送。恢復之後下一次重試就補齊。
    /// </summary>
    [Fact]
    public async Task 冷啟動時清單一直失敗時一直是無資料重試有上限恢復後補齊()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            h.GitHub.Override(EngineHarness.ListAPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"));
        });
        await h.DrainAsync();

        await h.AdvanceInStepsAsync(PersistentObservation, Tuning.ProbeInterval);

        // 第一次 + 第一個間隔（QuietMax）之後一次 + 之後每個上限的間隔一次。
        var mostSends = 2 + (int)((PersistentObservation - Tuning.QuietMax) / Tuning.RepoErrorBackoffMax);
        Assert.InRange(h.GitHub.Count(EngineHarness.ListAPath), 2, mostSends);
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        h.GitHub.RemoveOverrides(EngineHarness.ListAPath);
        await h.AdvanceInStepsAsync(Tuning.RepoErrorBackoffMax, Tuning.ProbeInterval);

        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>資料庫有清單時失敗的重試也一樣縮短；上一次的結果照舊，總狀態不變灰。</summary>
    [Fact]
    public async Task 重新啟動時清單回5xx不必等一小時就重試總狀態照舊()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ListAPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        var list = h.GitHub.Count(EngineHarness.ListAPath);
        var before = h.Snapshots.Count;

        await h.RestartAsync();
        await h.DrainAsync();
        Assert.Equal(list + 1, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.False(h.Last.RepoListFetched);

        await h.AdvanceInStepsAsync(Tuning.QuietMax, Tuning.ProbeInterval);

        Assert.Equal(list + 2, h.GitHub.Count(EngineHarness.ListAPath));
        Assert.True(h.Last.RepoListFetched);
        Assert.All(h.Snapshots.Skip(before), snapshot => Assert.Equal(OverallState.Success, snapshot.Summary.State));
    }

    /// <summary>失敗只藏在 PR 分支補抓裡（E4 第 1 頁看不到）：E4 都回來了，補抓還沒送也不算查完。</summary>
    [Fact]
    public async Task 冷啟動時PR分支補抓還沒送也不報綠燈()
    {
        await using var h = EngineHarness.Create(ArrangeOpenPullRequestWithOlderFailure);

        await h.DrainAsync();

        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>失敗只藏在預設分支補抓（E5）裡：另一個 repo 的 E4 先成功也不報綠燈。</summary>
    [Fact]
    public async Task 冷啟動時預設分支補抓還沒送也不報綠燈()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.OlderRuns.Add(FakeData.Run(9002, 40, "completed", "failure", Start.AddDays(-1), Start.AddDays(-1)));
            AddOtherRepoWithSuccess(h);
        });

        await h.DrainAsync();

        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>失敗只藏在 stale 首次檢查（Q7）之後的 E4 裡：檢查還沒做完也不報綠燈。</summary>
    [Fact]
    public async Task 冷啟動時stale首次檢查還沒做完也不報綠燈()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            var stale = h.GitHub.AddRepo(FakeData.Repo(StaleRepoId, StaleRepoName, Start.AddDays(-120)));
            stale.Runs.Add(FakeData.Run(9201, 3, "completed", "failure", Start.AddDays(-10), Start.AddDays(-10),
                eventName: "schedule", repo: StaleRepoName, headRepoId: StaleRepoId));
        });

        await h.DrainAsync();

        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>等權限重試的 repo 不等：有問題標示，否則權限不足的 repo 會讓總狀態一直是無資料。</summary>
    [Theory]
    [InlineData(EngineHarness.RunsPath)]
    [InlineData(EngineHarness.WorkflowsPath)]
    public async Task 自動更新冷啟動時等權限重試的repo不擋其他repo的成功(string path)
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.GitHub.Override(path, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
                ("X-Accepted-GitHub-Permissions", "actions=read")));
            AddOtherRepoWithSuccess(h);
        });

        await h.DrainAsync();

        Assert.Contains("權限不足", h.RepoRow(FakeData.RepoId).ProblemNote);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>
    /// 一個 repo 的 E4 一直回 5xx（E3、E7 正常）：狀態不知道，但不能讓總狀態永遠是無資料。
    /// 最多等 <see cref="Tuning.StaleDataThreshold"/>，從開始監看這個 repo 起算。
    /// </summary>
    [Fact]
    public async Task 自動更新冷啟動時一直查不到的repo最多擋一個無法更新的門檻()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.GitHub.Override(EngineHarness.RunsPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"));
            AddOtherRepoWithSuccess(h);
        });
        await h.DrainAsync();

        await h.AdvanceInStepsAsync(Tuning.StaleDataThreshold - RetryStep, RetryStep);
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        await h.AdvanceInStepsAsync(RetryStep * 2, RetryStep);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>第一次載入是一次性的：完成之後才出現的 repo 查回來之前，總狀態不會閃回無資料（常駐程式，第一次執行可能跑好幾天）。</summary>
    [Fact]
    public async Task 第一次載入完成之後新出現的repo不讓總狀態閃回無資料()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
        var before = h.Snapshots.Count;

        AddOtherRepoWithSuccess(h);
        await h.AdvanceAsync(Tuning.ProbeInterval);

        Assert.Contains(h.Last.Repos, row => row.Repo.Id == OtherRepoId);
        Assert.All(h.Snapshots.Skip(before), snapshot => Assert.Equal(OverallState.Success, snapshot.Summary.State));
    }

    /// <summary>資料庫有上一次的結果：重新啟動時總狀態照舊，還沒查過也不變灰（spec §6.2）。</summary>
    [Fact]
    public async Task 重新啟動時資料庫有上一次的結果總狀態不變灰()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var before = h.Snapshots.Count;

        await h.RestartAsync();
        await h.DrainAsync();

        Assert.True(h.Snapshots.Count > before);
        Assert.All(h.Snapshots.Skip(before), snapshot => Assert.Equal(OverallState.Success, snapshot.Summary.State));
    }

    /// <summary>執行中重建後也是第一次載入：自動更新的重建不例外（spec §14.3）。</summary>
    [Fact]
    public async Task 自動更新時重建後全部查完之前不報綠燈()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var other = h.GitHub.AddRepo(FakeData.Repo(OtherRepoId, OtherRepoName, Start.AddDays(-3)));
        other.Runs.Add(FakeData.Run(9012, 1, "completed", "failure", Start.AddDays(-1), Start.AddDays(-1), repo: OtherRepoName, headRepoId: OtherRepoId));
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        h.Clock.Advance(Tuning.QuietMax);
        await StepUntilAsync(h, () => h.Engine.DatabaseRebuildCount >= 1);
        var before = h.Snapshots.Count;
        await h.DrainAsync();

        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots.Skip(before), snapshot => snapshot.Summary.State == OverallState.Success);
    }

    // ── 第 9 輪：stale 首次檢查查過的 repo 不等 workflow 清單的定期重查（spec §6.6）──
    // 名稱寫「不必等」的案例修正前失敗（註明是對照的除外）；其他的修正前後都通過，用來防止放行過頭。

    /// <summary>
    /// 超過 90 天沒有 push、90 天內有 run 的 repo：stale 首次檢查已經用同一個網址查過 E3，E4 也查過。
    /// 排在 WorkflowsRefresh 之後的 workflow 清單是下一次定期重查，不是第一次都還沒送過，不等它（時間沒有前進，E3 也沒有多送）。
    /// </summary>
    [Theory]
    [InlineData("active", SkipReason.None)]
    [InlineData("disabled_manually", SkipReason.AllWorkflowsDisabled)]
    public async Task 冷啟動時stale首次檢查查過的repo全部成功時不必等workflow清單重抓(string workflowState, SkipReason reason)
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            ArrangeStaleRepoWithRecentRun(h);
            h.GitHub.World(StaleRepoName).Workflows[0]["state"] = workflowState;
        });

        await h.DrainAsync();

        Assert.Equal(reason, h.RepoRow(StaleRepoId).Reason);
        Assert.Equal(1, h.GitHub.Count(StaleWorkflowsPath));
        Assert.Equal(1, h.GitHub.Count(StaleRunsPath));
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
        Assert.Equal("success", StatusOverall(h));
    }

    /// <summary>對照：90 天內沒有 run 的 stale repo 查完之後還是略過，不排 workflow 清單，本來就不必等。</summary>
    [Fact]
    public async Task 冷啟動時90天內沒有run的stale_repo全部成功時不必等()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            var stale = h.GitHub.AddRepo(FakeData.Repo(StaleRepoId, StaleRepoName, Start.AddDays(-120)));
            stale.Runs.Add(FakeData.Run(9201, 3, "completed", "success", Start.AddDays(-100), Start.AddDays(-100),
                eventName: "schedule", repo: StaleRepoName, headRepoId: StaleRepoId));
        });

        await h.DrainAsync();

        Assert.Equal(SkipReason.Stale, h.RepoRow(StaleRepoId).Reason);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>只因為 90 天內的 run 才監看的 repo，失敗只藏在 PR 分支補抓裡：E3、E4 都查過了，E7 與補抓還沒送仍然不算查完。</summary>
    [Fact]
    public async Task 冷啟動時stale轉成監看的repo的PR分支補抓還沒送也不報綠燈()
    {
        await using var h = EngineHarness.Create(ArrangeStaleRepoWithFailureOnlyInPullRequestSeed);

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(StalePrSeedPath));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>同上，失敗只藏在第二個 workflow 的預設分支補抓（E5）裡。</summary>
    [Fact]
    public async Task 冷啟動時stale轉成監看的repo的預設分支補抓還沒送也不報綠燈()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            ArrangeStaleRepoWithRecentRun(h);
            AddStaleLintWorkflowWithOlderFailure(h);
        });

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(StaleLintSeedPath));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>workflow 都已停用的 stale repo 不擋，但不能讓別的 repo 還沒送的補抓少等。</summary>
    [Fact]
    public async Task 冷啟動時workflow都停用的stale_repo不會讓其他repo的補抓少等()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.OlderRuns.Add(FakeData.Run(9002, 40, "completed", "failure", Start.AddDays(-1), Start.AddDays(-1)));
            ArrangeStaleRepoWithRecentRun(h);
            h.GitHub.World(StaleRepoName).Workflows[0]["state"] = "disabled_manually";
            AddOtherRepoWithSuccess(h);
        });

        await h.DrainAsync();

        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>
    /// 鎖定會把還沒送過的目標往後延（ApplyLockedFloor）：到期時間在未來不代表送過。
    /// E7、補抓被延後的這段期間仍然不報綠燈，只有查過 E3 的 workflow 清單定期重查不等。
    /// </summary>
    [Fact]
    public async Task 冷啟動時鎖定把還沒送的補抓往後延也不報綠燈()
    {
        await using var h = EngineHarness.Create(ArrangeStaleRepoWithFailureOnlyInPullRequestSeed);
        await StepUntilAsync(h, () => h.GitHub.Count(StaleRunsPath) == 1);
        Assert.Equal(0, h.GitHub.Count(StalePrSeedPath));

        h.Engine.Post(new EngineCommand.SetLocked(true));
        await h.DrainAsync();
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        await h.AdvanceInStepsAsync(Tuning.LockedMinInterval * 2, Tuning.LockedMinInterval);

        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>
    /// 查過 E3 的 repo 有新的 push：馬上要送的 workflow 清單不是定期重查。新的 workflow 的失敗只有接著的 E5 看得到，
    /// 清單的重試和它排在同一刻、先回來時也不報綠燈。
    /// </summary>
    [Fact]
    public async Task 冷啟動時stale轉成監看的repo有新的push時馬上要送的workflow清單要等()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            ArrangeStaleRepoWithRecentRun(h);
            h.GitHub.Override(EngineHarness.ListAPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        });
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.True(h.RepoRow(StaleRepoId).Monitored);
        Assert.False(h.Last.RepoListFetched);

        AddStaleLintWorkflowWithOlderFailure(h);
        h.GitHub.World(StaleRepoName).SetPushedAt(h.Clock.GetUtcNow());
        var before = h.Snapshots.Count;
        await h.AdvanceAsync(Tuning.QuietMax - Tuning.ProbeInterval);

        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(1, h.GitHub.Count(StaleLintSeedPath));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots.Skip(before), snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>同上，看到 push 之後、workflow 清單還沒送就鎖定：鎖定把它往後延，它還是馬上要送的那一次，不是定期重查。</summary>
    [Fact]
    public async Task 冷啟動時stale轉成監看的repo有新的push之後鎖定也要等馬上要送的workflow清單()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            ArrangeStaleRepoWithRecentRun(h);
            h.GitHub.Override(EngineHarness.ListAPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        });
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.ProbeInterval);
        AddStaleLintWorkflowWithOlderFailure(h);
        h.GitHub.World(StaleRepoName).SetPushedAt(h.Clock.GetUtcNow());
        var before = h.Snapshots.Count;
        var probes = h.GitHub.Count(EngineHarness.ProbeAPath);
        h.Clock.Advance(Tuning.QuietMax - Tuning.ProbeInterval);
        await StepUntilAsync(h, () => h.GitHub.Count(EngineHarness.ProbeAPath) > probes);

        h.Engine.Post(new EngineCommand.SetLocked(true));
        await h.DrainAsync();
        Assert.True(h.Last.RepoListFetched);
        Assert.Equal(1, h.GitHub.Count(StaleWorkflowsPath));
        await h.AdvanceAsync(Tuning.LockedMinInterval);

        Assert.Equal(1, h.GitHub.Count(StaleLintSeedPath));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots.Skip(before), snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>執行中重建後是第一次載入，stale 首次檢查照常重做一次：同樣不必等 workflow 清單重抓（spec §14.3）。</summary>
    [Fact]
    public async Task 重建後stale首次檢查查過的repo全部成功時不必等workflow清單重抓()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        ArrangeStaleRepoWithRecentRun(h);
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        h.Clock.Advance(Tuning.QuietMax);
        await StepUntilAsync(h, () => h.Engine.DatabaseRebuildCount >= 1);
        await h.DrainAsync();

        Assert.True(h.RepoRow(StaleRepoId).Monitored);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>重建後失敗只藏在補抓裡：還沒送就不報綠燈。</summary>
    [Fact]
    public async Task 重建後stale轉成監看的repo的補抓還沒送也不報綠燈()
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        ArrangeStaleRepoWithRecentRun(h);
        AddStaleOpenPullRequestWithOlderFailure(h);
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        h.Clock.Advance(Tuning.QuietMax);
        await StepUntilAsync(h, () => h.Engine.DatabaseRebuildCount >= 1);
        var before = h.Snapshots.Count;
        await h.DrainAsync();

        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots.Skip(before), snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>以暫停模式啟動、不按 F5 直接解除暫停：解除時走和自動更新一樣的流程，不必等 workflow 清單重抓。</summary>
    [Fact]
    public async Task 暫停中啟動解除暫停後stale首次檢查查過的repo不必等workflow清單重抓()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Settings.RefreshMode = RefreshMode.Paused;
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            ArrangeStaleRepoWithRecentRun(h);
        });
        await h.DrainAsync();
        Assert.False(h.Last.RepoListFetched);
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();

        Assert.True(h.Last.RepoListFetched);
        Assert.True(h.RepoRow(StaleRepoId).Monitored);
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>暫停中按 F5 就查完：解除暫停才排 workflow 清單的定期重查，總狀態不會閃回無資料（修正前後都一樣）。</summary>
    [Fact]
    public async Task 暫停中按F5查完之後解除暫停總狀態不閃回無資料()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Settings.RefreshMode = RefreshMode.Paused;
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            ArrangeStaleRepoWithRecentRun(h);
        });
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
        var before = h.Snapshots.Count;

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Auto));
        await h.DrainAsync();

        Assert.True(h.Snapshots.Count > before);
        Assert.All(h.Snapshots.Skip(before), snapshot => Assert.Equal(OverallState.Success, snapshot.Summary.State));
    }

    /// <summary>清單第 2 頁回 5xx 一次：stale repo 在第 2 頁。清單補齊之前不報綠燈，補齊之後不必再等 workflow 清單重抓。</summary>
    [Fact]
    public async Task 冷啟動時清單第2頁失敗時stale首次檢查查過的repo補齊之後不必等workflow清單重抓()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            AddReposBeyondFirstPage(h, oldestFails: false);
            ArrangeStaleRepoWithRecentRun(h);
            h.GitHub.Override(ListASecondPage, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        });

        await AdvanceMinutesUntilAsync(h, () => h.Last.Summary.State == OverallState.Success);

        var firstGreen = h.Snapshots.First(snapshot => snapshot.Summary.State == OverallState.Success);
        Assert.True(firstGreen.RepoListFetched);
        Assert.True(h.RepoRow(StaleRepoId).Monitored);
        Assert.Equal(1, h.GitHub.Count(StaleWorkflowsPath));
    }

    /// <summary>同上，失敗只藏在第 2 頁那個 stale repo 的補抓裡：補齊、送完補抓之前都不報綠燈。</summary>
    [Fact]
    public async Task 冷啟動時清單第2頁失敗時stale_repo藏的失敗查到之前不報綠燈()
    {
        await using var h = EngineHarness.Create(h =>
        {
            AddReposBeyondFirstPage(h, oldestFails: false);
            ArrangeStaleRepoWithFailureOnlyInPullRequestSeed(h);
            h.GitHub.Override(ListASecondPage, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        });

        await AdvanceMinutesUntilAsync(h, () => h.Last.Summary.State == OverallState.Failure);

        Assert.True(h.Last.RepoListFetched);
        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>清單拖到 WorkflowsRefresh 的後半才補齊：定期重查離到期仍然超過 LockedMinInterval，不必等。stale repo 在第 1 頁，清單失敗期間就查完了。</summary>
    [Fact]
    public async Task 冷啟動時清單拖很久才補齊時定期重查離到期超過門檻就不必等()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            ArrangeStaleRepoWithRecentRun(h);
            AddReposBeyondFirstPage(h, oldestFails: false);
            h.GitHub.Override(ListASecondPage, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"));
        });
        await h.AdvanceInStepsAsync(TimeSpan.FromMinutes(40), RequestWindow);
        Assert.False(h.Last.RepoListFetched);
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        h.GitHub.RemoveOverrides(ListASecondPage);
        await AdvanceMinutesUntilAsync(h, () => h.Last.RepoListFetched);

        Assert.True(h.Clock.GetUtcNow() - Start < Tuning.WorkflowsRefresh - Tuning.LockedMinInterval);
        Assert.Equal(1, h.GitHub.Count(StaleWorkflowsPath));
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>
    /// 補抓送出了但沒有拿到答案（5xx、連線失敗）：失敗可能只有它看得到，也沒有問題標示，這段期間報綠燈就是錯的。
    /// 所以這個 repo 照舊等 workflow 清單，不因為它是定期重查就放行。
    /// </summary>
    [Fact]
    public async Task 冷啟動時stale轉成監看的repo的預設分支補抓回5xx也不報綠燈()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            ArrangeStaleRepoWithRecentRun(h);
            AddStaleLintWorkflowWithOlderFailure(h);
            h.GitHub.Override(StaleLintSeedPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        });

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(StaleLintSeedPath));
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);

        await h.AdvanceAsync(Tuning.SeedRecheck);

        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
    }

    /// <summary>同上，E7 沒有拿到答案：PR 分支補抓連排都還沒排，重試成功後才查到失敗。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 冷啟動時stale轉成監看的repo的PR清單沒有拿到答案時不報綠燈(bool connectionFailure)
    {
        await using var h = EngineHarness.Create(h =>
        {
            ArrangeStaleRepoWithFailureOnlyInPullRequestSeed(h);
            h.GitHub.Override(StalePullsPath, _ => connectionFailure
                ? throw new HttpRequestException("模擬的連線失敗")
                : h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        });

        await h.DrainAsync();

        Assert.Equal(0, h.GitHub.Count(StalePrSeedPath));
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        await h.AdvanceAsync(Tuning.PullListTtl);

        Assert.Equal(1, h.GitHub.Count(StalePrSeedPath));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>同上，PR 分支補抓回 5xx。</summary>
    [Fact]
    public async Task 冷啟動時stale轉成監看的repo的PR分支補抓回5xx也不報綠燈()
    {
        await using var h = EngineHarness.Create(h =>
        {
            ArrangeStaleRepoWithFailureOnlyInPullRequestSeed(h);
            h.GitHub.Override(StalePrSeedPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        });

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(StalePrSeedPath));
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        await h.AdvanceAsync(Tuning.QuietMax);

        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>同上，E7 被限流：全域暫停期間 PR 分支補抓都還沒排，暫停結束重送才查到失敗。</summary>
    [Fact]
    public async Task 冷啟動時stale轉成監看的repo的PR清單被限流時不報綠燈()
    {
        var pause = TimeSpan.FromMinutes(10);
        await using var h = EngineHarness.Create(h =>
        {
            ArrangeStaleRepoWithFailureOnlyInPullRequestSeed(h);
            h.GitHub.Override(StalePullsPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "API rate limit exceeded for user ID 1001.",
                ("x-ratelimit-remaining", "0"),
                ("x-ratelimit-used", "5000"),
                ("x-ratelimit-reset", h.GitHub.ResetEpoch(pause).ToString(CultureInfo.InvariantCulture))), times: 1);
        });

        await h.DrainAsync();

        Assert.True(h.Last.Summary.Issues.HasFlag(ConnectionIssue.RateLimited));
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        await h.AdvanceAsync(pause + RequestWindow);

        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>同上，處理回應時丟出未預期的例外（例如寫快取失敗）：沒有橫幅以外的提示，一樣算沒有拿到答案。</summary>
    [Fact]
    public async Task 冷啟動時stale轉成監看的repo的PR清單處理時丟例外也不報綠燈()
    {
        await using var h = EngineHarness.Create(h =>
        {
            ArrangeStaleRepoWithFailureOnlyInPullRequestSeed(h);
            h.GitHub.Override(StalePullsPath, _ => throw new InvalidOperationException("模擬的未預期例外"), times: 1);
        });

        await h.DrainAsync();

        Assert.Equal(0, h.GitHub.Count(StalePrSeedPath));
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        await h.AdvanceAsync(Tuning.PullListTtl);

        Assert.Equal(1, h.GitHub.Count(StalePrSeedPath));
        Assert.Equal(OverallState.Failure, h.Last.Summary.State);
        Assert.DoesNotContain(h.Snapshots, snapshot => snapshot.Summary.State == OverallState.Success);
    }

    /// <summary>補抓回 403、404 是有答案的錯誤：403 有問題標示，404 是查的東西不在了，都有自己的重試，不必等 workflow 清單重抓。</summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task 冷啟動時stale轉成監看的repo的預設分支補抓回403或404時不必等workflow清單重抓(HttpStatusCode status)
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            ArrangeStaleRepoWithRecentRun(h);
            AddStaleLintWorkflowWithOlderFailure(h);
            h.GitHub.Override(StaleLintSeedPath, _ => h.GitHub.Error(status, "Resource not accessible by integration",
                ("X-Accepted-GitHub-Permissions", "actions=read")));
        });

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(StaleLintSeedPath));
        Assert.Equal(1, h.GitHub.Count(StaleWorkflowsPath));
        Assert.Equal(status == HttpStatusCode.Forbidden, !string.IsNullOrEmpty(h.RepoRow(StaleRepoId).ProblemNote));
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>重試拿到答案就不再等：不必等到 workflow 清單的定期重查。</summary>
    [Fact]
    public async Task 冷啟動時stale轉成監看的repo的PR清單重試成功後不必等workflow清單重抓()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            ArrangeStaleRepoWithRecentRun(h);
            h.GitHub.Override(StalePullsPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"), times: 1);
        });
        await h.DrainAsync();
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        await h.AdvanceAsync(Tuning.PullListTtl);

        Assert.Equal(1, h.GitHub.Count(StaleWorkflowsPath));
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>一直拿不到答案也不會一直灰：最多等到 workflow 清單的定期重查送出（和等它是定期重查之前一樣）。</summary>
    [Fact]
    public async Task 冷啟動時stale轉成監看的repo的PR清單一直回5xx時最多等到workflow清單重抓()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            ArrangeStaleRepoWithRecentRun(h);
            h.GitHub.Override(StalePullsPath, _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"));
        });
        await h.DrainAsync();
        await h.AdvanceAsync(Tuning.PullListTtl);
        Assert.Equal(1, h.GitHub.Count(StaleWorkflowsPath));
        Assert.Equal(OverallState.NoData, h.Last.Summary.State);

        await h.AdvanceAsync(Tuning.WorkflowsRefresh - Tuning.PullListTtl);

        Assert.Equal(2, h.GitHub.Count(StaleWorkflowsPath));
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    /// <summary>403 有問題標示，也有自己的重試（每 PermissionRetryInterval）：同「等錯誤重試的 repo 不等」，不必等 workflow 清單重抓。</summary>
    [Fact]
    public async Task 冷啟動時stale轉成監看的repo的PR清單回403時有問題標示而且不必等workflow清單重抓()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
            ArrangeStaleRepoWithRecentRun(h);
            h.GitHub.Override(StalePullsPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden,
                "Resource not accessible by personal access token", ("X-Accepted-GitHub-Permissions", "pull_requests=read")));
        });

        await h.DrainAsync();

        Assert.Contains("缺少 Pull requests 權限或無法存取", h.RepoRow(StaleRepoId).ProblemNote);
        Assert.Equal(1, h.GitHub.Count(StaleWorkflowsPath));
        Assert.Equal(OverallState.Success, h.Last.Summary.State);
    }

    // ── 輔助 ──────────────────────────────────────────────────────────

    /// <summary>第二個 repo，有一筆昨天成功的 run（9012）。</summary>
    private static void AddOtherRepoWithSuccess(EngineHarness h)
    {
        var other = h.GitHub.AddRepo(FakeData.Repo(OtherRepoId, OtherRepoName, Start.AddDays(-3)));
        other.Runs.Add(FakeData.Run(9012, 1, "completed", "success", Start.AddDays(-1), Start.AddDays(-1), repo: OtherRepoName, headRepoId: OtherRepoId));
    }

    /// <summary>
    /// 自動更新時週期性的目標一直會到期，等不到「沒有東西可送」：每次送到節流器擋住，條件成立就停，否則推進一分鐘。
    /// </summary>
    private static async Task AdvanceMinutesUntilAsync(EngineHarness h, Func<bool> condition)
    {
        for (var minute = 0; minute < MaxDrainMinutes; minute++)
        {
            await h.DrainAsync();
            if (condition())
            {
                return;
            }

            h.Clock.Advance(RequestWindow);
        }

        Assert.Fail("推進到上限還是沒有等到");
    }

    /// <summary>預設 repo 有一筆昨天完成的 run（9001）。</summary>
    private static EngineHarness CreateWithCompletedRun() =>
        EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1))));

    /// <summary>
    /// 每一次重建之後，第一次在 <paramref name="injectIn"/> 裡讀牆鐘時（或 <see cref="DuringRequest"/>：第一個請求進來時），
    /// 把新的資料庫的 <paramref name="table"/> 蓋成垃圾：模擬儲存層持續壞掉，而且損毀要到那之後才讀到。第一次重建由呼叫端觸發。
    /// 看重建次數而不是備份數：丟棄不留檔，看備份數的話注入會靜靜停掉。
    /// </summary>
    private static void CorruptAfterEachRebuild(EngineHarness h, string injectIn, string table)
    {
        var corruptedFor = h.Engine.DatabaseRebuildCount;
        void CorruptOncePerRebuild()
        {
            if (h.Engine.DatabaseRebuildCount is var rebuilds && rebuilds > corruptedFor)
            {
                corruptedFor = rebuilds;
                CorruptTable(h.Workspace.Paths.CacheDbPath, table);
            }
        }

        if (injectIn == DuringRequest)
        {
            h.GitHub.OnRequest = _ => CorruptOncePerRebuild();
            return;
        }

        h.Clock.BeforeGetUtcNow = () =>
        {
            if (EngineClock.CalledFrom(injectIn))
            {
                CorruptOncePerRebuild();
            }
        };
    }

    /// <summary>
    /// 對照組，沒有任何損毀：冷啟動送出幾個 API 請求，之後每次推進 EngineTick、共推進 <paramref name="duration"/> 又送出幾個。
    /// </summary>
    private static async Task<(int ColdStart, int Periodic)> RequestsWithoutCorruptionAsync(TimeSpan duration)
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var coldStart = h.GitHub.ApiRequests.Count;
        await h.AdvanceInStepsAsync(duration, Tuning.EngineTick);
        return (coldStart, h.GitHub.ApiRequests.Count - coldStart);
    }

    /// <summary>
    /// 冷啟動之後弄壞 repos 觸發第一次重建，之後每次重建都依 <see cref="CorruptAfterEachRebuild"/> 注入，每次推進 EngineTick、共推進 <paramref name="duration"/>。
    /// </summary>
    /// <returns>這段期間重建了幾次、送出幾個 API 請求。</returns>
    private static async Task<(int Rebuilds, int Requests)> ObserveCorruptionAfterEachRebuildAsync(string injectIn, string table, TimeSpan duration)
    {
        await using var h = CreateWithCompletedRun();
        await h.DrainAsync();
        var requests = h.GitHub.ApiRequests.Count;
        CorruptAfterEachRebuild(h, injectIn, table);
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");

        await h.AdvanceInStepsAsync(duration, Tuning.EngineTick);
        Assert.Equal(FirstBackupOnly, BackupCount(h));
        return ((int)h.Engine.DatabaseRebuildCount, h.GitHub.ApiRequests.Count - requests);
    }

    /// <summary>持續損毀時，開始退避之後 <paramref name="duration"/> 內最多有幾次重試（間隔和 RecoverDatabaseIfNeeded 一樣用 ErrorBackoff 算）。</summary>
    private static int BackoffRetriesWithin(TimeSpan duration)
    {
        var retries = 0;
        var delay = IntervalPolicy.ErrorBackoff(Tuning.EngineTick, TimeSpan.Zero, IntervalPolicy.StretchFactor(null));
        for (var elapsed = delay; elapsed <= duration; elapsed += delay)
        {
            retries++;
            delay = IntervalPolicy.ErrorBackoff(Tuning.EngineTick, delay, IntervalPolicy.StretchFactor(null));
        }

        return retries;
    }

    /// <summary>目前留下幾份損毀的備份（cache.db.corrupt-*.bak）。</summary>
    private static int BackupCount(EngineHarness h) => Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak").Length;

    /// <summary>推進時間後跑 <see cref="IdleSteps"/> 步（時間不再前進）。</summary>
    private static async Task AdvanceAndStepAsync(EngineHarness h, TimeSpan delta)
    {
        h.Clock.Advance(delta);
        for (var step = 0; step < IdleSteps; step++)
        {
            await h.StepAsync();
        }
    }

    /// <summary>
    /// 模擬儲存層壞掉：<paramref name="storageBroken"/> 回 true 時，每一次重建之後（ResetAfterDatabaseRecovery 一開始讀時間時）
    /// 都把新的資料庫的 <see cref="RebuiltCorruptTable"/> 蓋成垃圾，重讀一定讀到損毀。接著弄壞 repos 表，
    /// 下一次寫入它時讀到損毀而重建；時間不前進時跑 <see cref="IdleSteps"/> 步，確認連續第 <see cref="RebuildsBeforeBackoff"/> 次之後改用退避。
    /// 看重建次數而不是備份數：丟棄不留檔，看備份數的話注入會靜靜停掉。
    /// </summary>
    /// <param name="firstCorrupted">觸發第一次重建時弄壞的表；沒有 repo 要寫進 repos 表的帳號要換一張。</param>
    private static async Task EnterRebuildBackoffAsync(EngineHarness h, Func<bool> storageBroken, string firstCorrupted = "repos")
    {
        var corruptedFor = h.Engine.DatabaseRebuildCount;
        h.Clock.BeforeGetUtcNow = () =>
        {
            if (storageBroken() && EngineClock.CalledFrom("ResetAfterDatabaseRecovery") && h.Engine.DatabaseRebuildCount is var rebuilds && rebuilds > corruptedFor)
            {
                corruptedFor = rebuilds;
                CorruptTable(h.Workspace.Paths.CacheDbPath, RebuiltCorruptTable);
            }
        };

        var before = h.Engine.DatabaseRebuildCount;
        var backups = BackupCount(h);
        CorruptTable(h.Workspace.Paths.CacheDbPath, firstCorrupted);

        // 下一次寫入 repos 表（記下成功時間）時讀到損毀；第一次重建後仍損毀時立即再重建一次，之後時間不前進就不再重建，不會塞滿磁碟。
        await AdvanceAndStepAsync(h, Tuning.QuietMax);
        Assert.Equal(before + RebuildsBeforeBackoff, h.Engine.DatabaseRebuildCount);
        Assert.InRange(BackupCount(h), backups, backups + FirstBackupOnly);
    }

    /// <summary>冷啟動之後依 <see cref="CorruptAfterEachRebuild"/> 進入持續損毀：時間不前進時重建到門檻次數，只留第一份備份。</summary>
    private static async Task<EngineHarness> EnterPersistentCorruptionAsync(string injectIn, string table)
    {
        var h = CreateWithCompletedRun();
        await h.DrainAsync();
        CorruptAfterEachRebuild(h, injectIn, table);
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        await AdvanceAndStepAsync(h, Tuning.QuietMax);
        AssertRebuilds(h, RebuildsBeforeBackoff);
        return h;
    }

    /// <summary>狀態表裡的總狀態（命令列讀到的 <c>overall</c>）。</summary>
    private static string StatusOverall(EngineHarness h) =>
        JsonNode.Parse(h.Cache.GetStatusSnapshot()!.StatusJson)!["overall"]!.GetValue<string>();

    /// <summary>冷啟動（只有預設 repo）之後暫停，F5 的回應寫入 repos 表時讀到損毀而重建：新的資料庫是空的，暫停不排完整重抓。</summary>
    private static async Task<EngineHarness> RebuildWhilePausedAsync(bool includeCollaborators = false)
    {
        var h = EngineHarness.Create(h =>
        {
            h.Settings.IncludeCollaboratorRepos = includeCollaborators;
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
        });
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.StepAsync();
        CorruptTable(h.Workspace.Paths.CacheDbPath, "repos");
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.True(h.Engine.DatabaseRebuildCount >= 1);
        Assert.Empty(h.Last.Repos);
        Assert.False(h.Last.RepoListFetched);
        return h;
    }

    /// <summary>
    /// 加上預設 repo 一共 <see cref="ReposBeyondOnePage"/> 個 repo，每個有一筆昨天完成的 run：
    /// 越後面的越久沒有 push，所以最舊的在探測的第 1 頁之外，也在完整清單的第 2 頁（依加入的順序）。
    /// </summary>
    /// <param name="collaborator">加到清單 B（沒有預設 repo，所以多加一個）。</param>
    /// <param name="oldestFails">最舊的那個失敗，其他都成功。</param>
    private static void AddReposBeyondFirstPage(EngineHarness h, bool collaborator = false, bool oldestFails = true)
    {
        var added = collaborator ? ReposBeyondOnePage : ReposBeyondOnePage - 1;
        var baseId = collaborator ? ManyCollaboratorReposBaseId : ManyReposBaseId;
        var owner = collaborator ? "carol" : "octo-org";
        for (var index = 1; index <= added; index++)
        {
            var id = baseId + index;
            var name = string.Create(CultureInfo.InvariantCulture, $"{owner}/many-{index}");
            var world = h.GitHub.AddRepo(FakeData.Repo(id, name, Start.AddDays(-2).AddHours(-index)), collaborator);
            world.Runs.Add(FakeData.Run(id + ManyRunsIdOffset, 1, "completed", oldestFails && index == added ? "failure" : "success",
                Start.AddDays(-1), Start.AddDays(-1), repo: name, headRepoId: id));
        }
    }

    /// <summary>repo 多的時候一分鐘送不完（<see cref="Tuning.MaxRequestsPerMinute"/>）：每次推進一分鐘送到節流器擋住，直到沒有東西可送。</summary>
    private static async Task DrainAcrossRequestLimitAsync(EngineHarness h)
    {
        for (var minute = 0; minute < MaxDrainMinutes; minute++)
        {
            if (await h.DrainAsync() == 0 && minute > 0)
            {
                return;
            }

            h.Clock.Advance(RequestWindow);
        }

        Assert.Fail("節流器放行之後還是送不完");
    }

    /// <summary>逐 <see cref="Tuning.EngineTick"/> 推進到下一次重試，再讓這個時間點的步驟跑完（下一次退避就排好了）。</summary>
    private static async Task StepUntilNextRetryAsync(EngineHarness h)
    {
        var rebuilds = h.Engine.DatabaseRebuildCount;
        for (var waited = TimeSpan.Zero; h.Engine.DatabaseRebuildCount == rebuilds; waited += Tuning.EngineTick)
        {
            Assert.True(waited <= Tuning.RepoErrorBackoffMax, "等不到下一次重試");
            h.Clock.Advance(Tuning.EngineTick);
            await h.StepAsync();
        }

        await AdvanceAndStepAsync(h, TimeSpan.Zero);
    }

    /// <summary>放滿編號備份，內容各不相同。</summary>
    /// <returns>每個檔案的路徑與位元組，之後可以比對。</returns>
    private static Dictionary<string, byte[]> AddNumberedBackups(EngineHarness h)
    {
        var numbered = Enumerable.Range(0, Tuning.MaxCorruptBackups)
            .Select(number => h.Workspace.PathTo($"cache.db.corrupt-2026102300000{number}.bak"))
            .ToDictionary(path => path, path => System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(path)));
        foreach (var (path, bytes) in numbered)
        {
            File.WriteAllBytes(path, bytes);
        }

        return numbered;
    }

    /// <summary>error.log 裡有幾筆丟棄的紀錄。</summary>
    private static int DiscardLogCount(EngineHarness h) => h.ErrorLogText.Split(DiscardedLogContext).Length - 1;

    /// <summary>重建次數與備份份數一起驗：只驗備份數的話，退避壞掉（一直重建、一直丟棄）也會通過。</summary>
    private static void AssertRebuilds(EngineHarness h, long rebuilds, int backups = FirstBackupOnly)
    {
        Assert.Equal(rebuilds, h.Engine.DatabaseRebuildCount);
        Assert.Equal(backups, BackupCount(h));
    }

    private static string[] BackupPaths(EngineHarness h) => Directory.GetFiles(h.Workspace.Root, "cache.db.corrupt-*.bak");

    /// <summary>打開這份備份讀 repo_overrides 的 repo id；讀的是複本（連同 WAL），備份本身不動。</summary>
    private static List<long> OverrideRepoIds(EngineHarness h, string backupPath)
    {
        var copy = h.Workspace.PathTo("inspect-" + Guid.NewGuid().ToString("N") + ".db");
        File.Copy(backupPath, copy);
        if (File.Exists(backupPath + "-wal"))
        {
            File.Copy(backupPath + "-wal", copy + "-wal");
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT repo_id FROM repo_overrides ORDER BY repo_id;";
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>功能分支上進行中的 run（不在追蹤範圍）：被擠出第 1 頁之後改用 E6 以 QuietMax 追蹤。</summary>
    private static JsonObject StuckFeatureRun() =>
        FakeData.Run(9100, 77, "in_progress", null, Start.AddMinutes(-1), Start, branch: "feature/x", sha: FakeData.FeatureSha);

    /// <summary>冷啟動後 <paramref name="stuck"/> 被擠出第 1 頁、改由 E6 追蹤；E6 一律回權限不足，第一次送出之後就在等權限重試。</summary>
    private static async Task ForbidRunDetailAsync(EngineHarness h, JsonObject stuck)
    {
        await h.DrainAsync();
        h.Main.Runs.Clear();
        h.Main.OlderRuns.Add(stuck);
        h.GitHub.Override(RunDetailPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
            ("X-Accepted-GitHub-Permissions", "actions=read")));
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        await h.AdvanceInStepsAsync(Tuning.QuietMax, Tuning.ActiveRepoInterval);
        Assert.Equal(1, h.GitHub.Count(RunDetailPath));
        Assert.Contains("權限不足，需要 actions=read", h.RepoRow(FakeData.RepoId).ProblemNote);
    }

    /// <summary>預設分支有成功；開啟中 PR 17 的失敗 run 只在 E4 第 1 頁之外（啟動之前就失敗），只有補抓看得到。</summary>
    private static void ArrangeOpenPullRequestWithOlderFailure(EngineHarness h)
    {
        h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddHours(-1), Start.AddHours(-1)));
        h.Main.Pulls.Add(FakeData.Pull(17, "feature/login", FakeData.FeatureSha));
        h.Main.OlderRuns.Add(FakeData.Run(9010, 43, "completed", "failure", Start.AddHours(-2), Start.AddHours(-2),
            branch: "feature/login", sha: FakeData.FeatureSha, eventName: "pull_request", pullRequests: [17]));
    }

    /// <summary>超過 90 天沒有 push、但 10 天前有排程 run 的 repo（Q7 要救回來的那種）。</summary>
    private static void ArrangeStaleRepoWithRecentRun(EngineHarness h)
    {
        var stale = h.GitHub.AddRepo(FakeData.Repo(StaleRepoId, StaleRepoName, Start.AddDays(-120)));
        stale.Runs.Add(FakeData.Run(9201, 3, "completed", "success", Start.AddDays(-10), Start.AddDays(-10),
            eventName: "schedule", repo: StaleRepoName, headRepoId: StaleRepoId));
    }

    /// <summary>預設 repo 有成功；stale repo（<see cref="ArrangeStaleRepoWithRecentRun"/>）開啟中 PR 的失敗 run 不在 E4 第 1 頁，只有補抓看得到。</summary>
    private static void ArrangeStaleRepoWithFailureOnlyInPullRequestSeed(EngineHarness h)
    {
        h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "success", Start.AddDays(-1), Start.AddDays(-1)));
        ArrangeStaleRepoWithRecentRun(h);
        AddStaleOpenPullRequestWithOlderFailure(h);
    }

    private static void AddStaleOpenPullRequestWithOlderFailure(EngineHarness h)
    {
        var stale = h.GitHub.World(StaleRepoName);
        stale.Pulls.Add(FakeData.Pull(17, "feature/login", FakeData.FeatureSha, headRepoId: StaleRepoId, repo: StaleRepoName));
        stale.OlderRuns.Add(FakeData.Run(9210, 4, "completed", "failure", Start.AddDays(-11), Start.AddDays(-11),
            branch: "feature/login", sha: FakeData.FeatureSha, eventName: "pull_request", pullRequests: [17],
            repo: StaleRepoName, headRepoId: StaleRepoId));
    }

    /// <summary>stale repo 的第二個 active workflow（Lint）：預設分支的失敗 run 不在 E4 第 1 頁，只有 E5 看得到。</summary>
    private static void AddStaleLintWorkflowWithOlderFailure(EngineHarness h)
    {
        var stale = h.GitHub.World(StaleRepoName);
        stale.Workflows.Add(FakeData.Workflow(id: LintWorkflowId, name: LintWorkflowName, path: LintWorkflowPath, repo: StaleRepoName));
        stale.OlderRuns.Add(FakeData.Run(9211, 1, "completed", "failure", Start.AddDays(-11), Start.AddDays(-11),
            workflowId: LintWorkflowId, name: LintWorkflowName, repo: StaleRepoName, headRepoId: StaleRepoId));
    }

    /// <summary>一步一步送，直到 <paramref name="condition"/> 成立（每一步最多送一個請求）。</summary>
    private static async Task StepUntilAsync(EngineHarness h, Func<bool> condition)
    {
        for (var step = 0; step < MaxSteps && !condition(); step++)
        {
            if (await h.StepAsync() == 0)
            {
                break;
            }
        }

        Assert.True(condition());
    }

    /// <summary>同一刻反覆全量刷新，直到節流器擋住為止（時間不前進，所有請求都在同一個 60 秒視窗內）。</summary>
    internal static async Task FillRequestWindowAsync(EngineHarness h)
    {
        for (var i = 0; i < Tuning.MaxRequestsPerMinute; i++)
        {
            h.Engine.Post(new EngineCommand.RefreshAll());
            if (await h.DrainAsync() == 0)
            {
                return;
            }
        }
    }

    /// <summary>E8 的一頁：一個 job，<paramref name="next"/> 不為 null 時帶 Link next。</summary>
    private static HttpResponseMessage JobsPage(JsonObject job, string? next)
    {
        var body = new JsonObject { ["total_count"] = 2, ["jobs"] = new JsonArray(job) }.ToJsonString();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (next is not null)
        {
            headers["Link"] = $"<{next}>; rel=\"next\"";
        }

        return FakeHttpHandler.Json(body, HttpStatusCode.OK, headers);
    }

    /// <summary>把某個資料表（或索引）的根頁蓋成垃圾：檔頭與其他資料表都還能讀，讀寫它時才回 SQLITE_CORRUPT。</summary>
    internal static void CorruptTable(string databasePath, string table)
    {
        long rootPage;
        long pageSize;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();

            // 先把 WAL 寫回主檔：之後蓋掉的頁面才是 SQLite 真正會讀的那一份。
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT rootpage FROM sqlite_master WHERE type IN ('table', 'index') AND name = $name;";
            command.Parameters.AddWithValue("$name", table);
            rootPage = (long)command.ExecuteScalar()!;
            command.Parameters.Clear();
            command.CommandText = "PRAGMA page_size;";
            pageSize = (long)command.ExecuteScalar()!;
        }

        SqliteConnection.ClearAllPools();
        using var file = new FileStream(databasePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        file.Position = (rootPage - 1) * pageSize;
        file.Write(Enumerable.Repeat((byte)0xFF, (int)pageSize).ToArray());
    }

    /// <summary>
    /// /user 依 Authorization 回應：<see cref="Fixture.FakeToken"/> 是 alice（第一次可以卡住，等 <paramref name="release"/>），
    /// <see cref="EngineHarness.OtherFakeToken"/> 是 bob，或回 <paramref name="otherTokenStatus"/> 指定的錯誤。
    /// </summary>
    private static HttpResponseMessage UserByToken(HttpRequestMessage request, StrongBox<bool> blockOnce,
        ManualResetEventSlim seen, ManualResetEventSlim release, HttpStatusCode otherTokenStatus)
    {
        var bearer = request.Headers.Authorization?.Parameter;
        if (bearer == EngineHarness.OtherFakeToken)
        {
            return otherTokenStatus == HttpStatusCode.OK
                ? FakeHttpHandler.Json(FakeData.User(BobId, BobLogin).ToJsonString())
                : FakeHttpHandler.Json(new JsonObject { ["message"] = "Server Error" }.ToJsonString(), otherTokenStatus);
        }

        if (blockOnce.Value)
        {
            blockOnce.Value = false;
            seen.Set();
            release.Wait(Guard);
        }

        return FakeHttpHandler.Json(FakeData.User().ToJsonString());
    }

    /// <summary>/user：帶 token 時回 5xx（E1 進入退避），沒帶 token 時回 401（GitHub 對不帶憑證的 /user 的回應）。</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> UserFailsWithToken(EngineHarness h) => request =>
        request.Headers.Authorization is null
            ? h.GitHub.Error(HttpStatusCode.Unauthorized, "Requires authentication")
            : h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error");

    internal static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
