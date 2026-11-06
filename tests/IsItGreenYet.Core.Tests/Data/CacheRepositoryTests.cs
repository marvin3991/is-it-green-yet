using System.Globalization;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Core.Tests.Data;

public class CacheRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);
    private const long RepoId = 1001;
    private const long OtherRepoId = 2002;
    private const long WorkflowId = 501;
    private const string RepoFullName = "octo-org/sample-app";
    private const string OtherRepoFullName = "octo-org/other-app";
    private const string RunsUrl = "https://api.github.com/repos/octo-org/sample-app/actions/runs?per_page=30";
    private const long MyUserId = 42;
    private const long OtherUserId = 4001;

    // ── 共用假資料 ────────────────────────────────────────────────────

    private static CacheRepository CreateRepository(TempWorkspace workspace, TimeProvider? time = null)
    {
        DatabaseInitializer.Ensure(workspace.Paths.CacheDbPath);
        return new CacheRepository(workspace.Paths.CacheDbPath, time ?? new ManualTimeProvider(Now));
    }

    private static RepoInfo Repo(long id = RepoId, string fullName = RepoFullName) => new(
        id,
        fullName,
        fullName.Split('/')[1],
        OwnerId: 9001,
        OwnerLogin: "octo-org",
        OwnerType: "Organization",
        IsPrivate: true,
        IsFork: false,
        IsArchived: false,
        IsDisabled: false,
        DefaultBranch: "main",
        HtmlUrl: $"https://github.com/{fullName}",
        PushedAt: Now.AddHours(-1),
        RepoCategory.Organization);

    private static RunInfo Run(
        long id,
        int attempt = 1,
        string? status = "completed",
        string? conclusion = "success",
        DateTimeOffset? createdAt = null,
        IReadOnlyList<int>? pullRequests = null,
        long repoId = RepoId) => new(
        id,
        repoId,
        WorkflowId,
        WorkflowName: "CI",
        Path: ".github/workflows/ci.yml",
        RunNumber: (int)(id % 100000),
        RunAttempt: attempt,
        DisplayTitle: $"修正 #{id}",
        Event: "push",
        Status: status,
        Conclusion: conclusion,
        HeadBranch: "main",
        HeadSha: "dcf27ad0000000000000000000000000000000aa",
        HeadRepoId: repoId,
        HeadRepoFullName: RepoFullName,
        HeadRepoHtmlUrl: $"https://github.com/{RepoFullName}",
        ActorId: 42,
        ActorLogin: "octocat",
        TriggeringActorLogin: "octocat",
        PullRequestNumbers: pullRequests ?? [],
        RunStartedAt: (createdAt ?? Now.AddMinutes(-10)).AddSeconds(5),
        CreatedAt: createdAt ?? Now.AddMinutes(-10),
        UpdatedAt: (createdAt ?? Now.AddMinutes(-10)).AddMinutes(3),
        HtmlUrl: $"https://github.com/{RepoFullName}/actions/runs/{id}");

    private static HttpCacheEntry Cache(string url, string? etag, string? next = null, IReadOnlyList<long>? ids = null) =>
        new(url, etag, next, ids);

    private static RepoPermissionsUpdate Permissions(long userId, params (long RepoId, bool? CanPush)[] values) =>
        new(userId, values.ToDictionary(value => value.RepoId, value => value.CanPush));

    /// <summary>字典沒有順序，換成依 repo id 排序的清單再比。</summary>
    private static List<(long RepoId, bool CanPush)> Sorted(IReadOnlyDictionary<long, bool> permissions) =>
        permissions.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)).ToList();

    /// <summary>record 的集合欄位是參考比較，先換成實際值再比整筆，集合另外比內容。</summary>
    private static void AssertRunEqual(RunInfo expected, RunInfo actual)
    {
        Assert.Equal(expected with { PullRequestNumbers = actual.PullRequestNumbers }, actual);
        Assert.Equal(expected.PullRequestNumbers, actual.PullRequestNumbers);
    }

    private static void AssertCacheEqual(HttpCacheEntry expected, HttpCacheEntry? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected with { ItemIds = actual.ItemIds }, actual);
        Assert.Equal(expected.ItemIds, actual.ItemIds);
    }

    private static SqliteConnection Open(TempWorkspace workspace)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = workspace.Paths.CacheDbPath }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalar(TempWorkspace workspace, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open(workspace);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static long Count(TempWorkspace workspace, string table, string where = "1 = 1", params (string Name, object Value)[] parameters) =>
        Convert.ToInt64(Scalar(workspace, $"SELECT COUNT(*) FROM {table} WHERE {where};", parameters), CultureInfo.InvariantCulture);

    // ── 空資料庫 ──────────────────────────────────────────────────────

    [Fact]
    public void 空資料庫的查詢回傳空結果而不是丟例外()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        Assert.Null(repository.GetAccount());
        Assert.Empty(repository.GetRepos());
        Assert.Empty(repository.GetRepoStates());
        Assert.Empty(repository.GetOverrides());
        Assert.Empty(repository.GetWorkflows(RepoId));
        Assert.Empty(repository.GetRuns(RepoId));
        Assert.Empty(repository.GetAttempts(RepoId));
        Assert.Empty(repository.GetDurationSamples(RepoId, WorkflowId));
        Assert.Empty(repository.GetPullRequests(RepoId));
        Assert.False(repository.PrSeedChecked(RepoId, "abc"));
        Assert.Empty(repository.GetWatched());
        Assert.Null(repository.Get(RunsUrl));
        Assert.False(repository.Contains(1, 1, "failure"));
        Assert.Empty(repository.GetRepoPermissions(MyUserId));
        Assert.Empty(repository.GetPendingReruns());
        Assert.Null(repository.GetStatusSnapshot());

        // 沒有任何資料時清理也不能出錯
        repository.Cleanup(Now, new HashSet<long>());
        repository.DeleteRepo(RepoId);
        repository.MarkRunGone(1);
        repository.DeletePendingRerun(1);
    }

    [Fact]
    public void 路徑為空白時丟出參數例外()
    {
        Assert.Throws<ArgumentException>(() => new CacheRepository("  "));
        Assert.Throws<ArgumentException>(() => new CacheRepository("  ", new ManualTimeProvider(Now)));
    }

    // ── 每組 Get/Save 往返 ─────────────────────────────────────────────

    [Fact]
    public void 帳號Get與Save往返且只保留一筆()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        repository.SaveAccount(new AccountInfo(42, "octocat"));
        repository.SaveAccount(new AccountInfo(43, "octocat-renamed"));

        Assert.Equal(new AccountInfo(43, "octocat-renamed"), repository.GetAccount());
        Assert.Equal(1, Count(workspace, "account"));
    }

    [Fact]
    public void repo清單Get與Upsert往返()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        var collaborator = Repo(OtherRepoId, OtherRepoFullName) with
        {
            Category = RepoCategory.Collaborator,
            IsFork = true,
            IsArchived = true,
            IsDisabled = true,
            IsPrivate = false,
            PushedAt = null,
        };

        repository.UpsertRepos([Repo(), collaborator], []);

        var repos = repository.GetRepos().ToDictionary(r => r.Id);
        Assert.Equal(2, repos.Count);
        Assert.Equal(Repo(), repos[RepoId]);
        Assert.Equal(collaborator, repos[OtherRepoId]);
    }

    [Fact]
    public void Upsert同一個id會更新名稱且不動監看狀態()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        repository.UpsertRepos([Repo()], []);
        var state = new RepoState(RepoId, 3, 2, Now, "Stale", "Actions 端點回 404", Now.AddMinutes(-1), 1, Now.AddHours(-2));
        repository.SaveRepoState(state);

        var renamed = Repo(RepoId, "octo-org/renamed-app");
        repository.UpsertRepos([renamed], []);

        Assert.Equal(renamed, Assert.Single(repository.GetRepos()));
        Assert.Equal(state, repository.GetRepoStates()[RepoId]);
    }

    [Fact]
    public void UpsertRepos在同一交易寫入快取項()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        const string listUrl = "/user/repos?affiliation=owner,organization_member&per_page=100";
        var entry = Cache(listUrl, "W/\"page1\"", "https://api.github.com/user/repos?page=2", [RepoId, OtherRepoId]);

        repository.UpsertRepos([Repo(), Repo(OtherRepoId, OtherRepoFullName)], [entry]);

        AssertCacheEqual(entry, repository.Get(listUrl));

        // 寫到一半失敗（full_name 違反 NOT NULL）時，repo 與快取項都不能留下
        var broken = Repo(3003, "octo-org/broken") with { FullName = null! };
        var newer = Cache(listUrl, "W/\"page1-new\"", null, [3003]);
        Assert.Throws<SqliteException>(() => repository.UpsertRepos([Repo(4004, "octo-org/fourth"), broken], [newer]));
        Assert.Equal(2, repository.GetRepos().Count);
        Assert.Equal("W/\"page1\"", repository.Get(listUrl)!.ETag);
    }

    [Fact]
    public void repo狀態Get與Save往返()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        repository.UpsertRepos([Repo(), Repo(OtherRepoId, OtherRepoFullName)], []);
        var taipei = TimeSpan.FromHours(8);
        var state = new RepoState(
            RepoId,
            WorkflowsTotal: 4,
            WorkflowsActive: 3,
            WorkflowsCheckedAt: new DateTimeOffset(2026, 10, 24, 10, 0, 0, taipei),
            SkipReason: "NoWorkflows",
            ProblemNote: "Actions 端點回 404：可能是 token 對這個 repo 沒有 Actions 權限",
            LastSuccessAt: Now.AddSeconds(-30),
            MissingRounds: 1,
            BaselineT0: Now.AddHours(-3));

        repository.SaveRepoState(state);

        var states = repository.GetRepoStates();
        Assert.Equal(state, states[RepoId]);
        Assert.Equal(TimeSpan.Zero, states[RepoId].WorkflowsCheckedAt!.Value.Offset);
        // 沒存過狀態的 repo 回預設值
        Assert.Equal(new RepoState(OtherRepoId, null, null, null, null, null, null, 0, null), states[OtherRepoId]);

        // 存回 null 會清掉
        var cleared = new RepoState(RepoId, null, null, null, null, null, null, 0, null);
        repository.SaveRepoState(cleared);
        Assert.Equal(cleared, repository.GetRepoStates()[RepoId]);
    }

    [Fact]
    public void SaveRepoState對不存在的repo不做事()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        repository.SaveRepoState(new RepoState(RepoId, 1, 1, Now, null, null, Now, 0, Now));

        Assert.Empty(repository.GetRepoStates());
        Assert.Empty(repository.GetRepos());
    }

    [Fact]
    public void 靜音與強制納入Get與Set往返()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        repository.SetOverride(RepoId, new RepoOverride(Muted: true, ForceIncluded: false));
        repository.SetOverride(OtherRepoId, new RepoOverride(Muted: false, ForceIncluded: true));
        repository.SetOverride(RepoId, new RepoOverride(Muted: true, ForceIncluded: true));

        var overrides = repository.GetOverrides();
        Assert.Equal(2, overrides.Count);
        Assert.Equal(new RepoOverride(true, true), overrides[RepoId]);
        Assert.Equal(new RepoOverride(false, true), overrides[OtherRepoId]);
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture),
            Scalar(workspace, "SELECT updated_at FROM repo_overrides WHERE repo_id = $id;", ("$id", RepoId)));
    }

    [Fact]
    public void workflow清單Get與Replace往返並移除不在清單的workflow()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        const string url = "https://api.github.com/repos/octo-org/sample-app/actions/workflows?per_page=100";
        var ci = new WorkflowInfo(RepoId, WorkflowId, "CI", ".github/workflows/ci.yml", "active", "https://github.com/octo-org/sample-app/actions/workflows/ci.yml");
        var nightly = new WorkflowInfo(RepoId, 502, "Nightly", ".github/workflows/nightly.yml", "disabled_manually", "https://github.com/octo-org/sample-app/actions/workflows/nightly.yml");
        var other = new WorkflowInfo(OtherRepoId, 777, "Build", ".github/workflows/build.yml", "active", "https://github.com/octo-org/other-app/actions/workflows/build.yml");
        repository.ReplaceWorkflows(OtherRepoId, [other], null);

        repository.ReplaceWorkflows(RepoId, [ci, nightly], Cache(url, "\"wf1\"", null, [WorkflowId, 502]));
        Assert.Equal([ci, nightly], repository.GetWorkflows(RepoId));

        var renamedCi = ci with { Name = "CI 主流程", State = "disabled_inactivity" };
        repository.ReplaceWorkflows(RepoId, [renamedCi], Cache(url, "\"wf2\"", null, [WorkflowId]));

        Assert.Equal([renamedCi], repository.GetWorkflows(RepoId));
        Assert.Equal("\"wf2\"", repository.Get(url)!.ETag);
        Assert.Equal([other], repository.GetWorkflows(OtherRepoId));

        // 空清單 = 這個 repo 已經沒有 workflow
        repository.ReplaceWorkflows(RepoId, [], null);
        Assert.Empty(repository.GetWorkflows(RepoId));
    }

    [Fact]
    public void run與attempt的Get與Save往返()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        var taipei = TimeSpan.FromHours(8);
        // 測試各種 null、分支特殊字元、多行標題、跨時區時間與很大的 id
        var tricky = Run(9_000_000_000_000_000_001) with
        {
            WorkflowName = null,
            DisplayTitle = "第一行\n第二行 🚀 " + new string('長', 500),
            Event = "dynamic",
            Status = "in_progress",
            Conclusion = null,
            HeadBranch = "feature/中文 #1 %20",
            HeadRepoId = 31337,
            HeadRepoFullName = null,
            HeadRepoHtmlUrl = null,
            ActorId = null,
            ActorLogin = null,
            TriggeringActorLogin = null,
            PullRequestNumbers = [12, 7],
            RunStartedAt = null,
            CreatedAt = new DateTimeOffset(2026, 10, 24, 9, 30, 0, taipei),
            UpdatedAt = new DateTimeOffset(2026, 10, 24, 9, 31, 0, 123, taipei),
        };
        var normal = Run(11, attempt: 2, conclusion: "failure");

        repository.SaveRunsPage(RepoId, [tricky, normal], isLatestPage: true, cache: null);

        var runs = repository.GetRuns(RepoId).ToDictionary(r => r.Id);
        Assert.Equal(2, runs.Count);
        AssertRunEqual(tricky with { PullRequestNumbers = [7, 12] }, runs[tricky.Id]);
        AssertRunEqual(normal, runs[normal.Id]);
        Assert.Equal(TimeSpan.Zero, runs[tricky.Id].CreatedAt.Offset);

        var attempts = repository.GetAttempts(RepoId);
        Assert.Equal(
            [new RunAttemptRecord(tricky.Id, 1, "in_progress", null, null, tricky.UpdatedAt)],
            attempts[tricky.Id]);
        Assert.Equal(
            [new RunAttemptRecord(normal.Id, 2, "completed", "failure", normal.RunStartedAt, normal.UpdatedAt)],
            attempts[normal.Id]);
        Assert.Empty(repository.GetRuns(OtherRepoId));
        Assert.Empty(repository.GetAttempts(OtherRepoId));
    }

    [Fact]
    public void 耗時樣本Get與Add往返且由新到舊()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        repository.AddDurationSample(1, 1, RepoId, WorkflowId, TimeSpan.FromSeconds(100), Now.AddMinutes(-30));
        repository.AddDurationSample(2, 1, RepoId, WorkflowId, TimeSpan.FromSeconds(300), Now.AddMinutes(-10));
        repository.AddDurationSample(3, 1, RepoId, WorkflowId, TimeSpan.FromSeconds(200), Now.AddMinutes(-20));
        repository.AddDurationSample(4, 1, RepoId, 999, TimeSpan.FromSeconds(999), Now);

        Assert.Equal(
            [TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(100)],
            repository.GetDurationSamples(RepoId, WorkflowId));
        Assert.Equal([TimeSpan.FromSeconds(999)], repository.GetDurationSamples(RepoId, 999));
        Assert.Empty(repository.GetDurationSamples(OtherRepoId, WorkflowId));
    }

    [Fact]
    public void 同一個attempt的耗時樣本重複加入只留一筆()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        repository.AddDurationSample(1, 1, RepoId, WorkflowId, TimeSpan.FromSeconds(100), Now);
        repository.AddDurationSample(1, 1, RepoId, WorkflowId, TimeSpan.FromSeconds(120), Now);
        repository.AddDurationSample(1, 2, RepoId, WorkflowId, TimeSpan.FromSeconds(90), Now.AddMinutes(5));

        Assert.Equal([TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(120)], repository.GetDurationSamples(RepoId, WorkflowId));
    }

    [Fact]
    public void PR清單Get與Replace往返()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        const string url = "https://api.github.com/repos/octo-org/sample-app/pulls?state=open&per_page=100";
        var fromFork = new PullRequestInfo(RepoId, 8, null, null, "patch-1", "aaa111", "https://github.com/octo-org/sample-app/pull/8");
        var sameRepo = new PullRequestInfo(RepoId, 3, RepoId, RepoFullName, "feature/中文", "bbb222", "https://github.com/octo-org/sample-app/pull/3");
        var otherRepo = new PullRequestInfo(OtherRepoId, 1, OtherRepoId, OtherRepoFullName, "fix", "ccc333", "https://github.com/octo-org/other-app/pull/1");
        repository.ReplacePullRequests(OtherRepoId, [otherRepo], null);

        repository.ReplacePullRequests(RepoId, [fromFork, sameRepo], Cache(url, "\"pr1\"", null, [80, 30]));
        Assert.Equal([sameRepo, fromFork], repository.GetPullRequests(RepoId));

        // 每次 E7 回 200 時整批替換：關掉的 PR 會消失
        repository.ReplacePullRequests(RepoId, [fromFork], Cache(url, "\"pr2\"", null, [80]));
        Assert.Equal([fromFork], repository.GetPullRequests(RepoId));
        Assert.Equal("\"pr2\"", repository.Get(url)!.ETag);
        Assert.Equal([otherRepo], repository.GetPullRequests(OtherRepoId));
    }

    [Fact]
    public void PR分支補抓紀錄往返()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        repository.MarkPrSeedChecked(RepoId, "aaa111", Now);
        repository.MarkPrSeedChecked(RepoId, "aaa111", Now.AddMinutes(1));

        Assert.True(repository.PrSeedChecked(RepoId, "aaa111"));
        Assert.False(repository.PrSeedChecked(RepoId, "bbb222"));
        Assert.False(repository.PrSeedChecked(OtherRepoId, "aaa111"));
        Assert.Equal(1, Count(workspace, "pr_seed"));
    }

    [Fact]
    public void 完成時通知我的標記Get與Set往返()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        repository.SetWatched(10, true);
        repository.SetWatched(11, true);
        repository.SetWatched(10, true);
        Assert.Equal([10L, 11L], repository.GetWatched().Order());

        repository.SetWatched(10, false);
        repository.SetWatched(999, false);
        Assert.Equal([11L], repository.GetWatched());
    }

    [Fact]
    public void HTTP快取項Get與Put往返()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        var withAll = Cache(RunsUrl, "W/\"abc\"", "https://api.github.com/repositories/1001/actions/runs?page=2", [3, 1, long.MaxValue]);
        var noEtag = Cache("/user", null, null, null);
        var emptyPage = Cache("/repos/octo-org/sample-app/pulls?state=open&per_page=100", "\"empty\"", null, []);

        repository.PutCache(withAll);
        repository.PutCache(noEtag);
        repository.PutCache(emptyPage);

        AssertCacheEqual(withAll, repository.Get(RunsUrl));
        AssertCacheEqual(noEtag, repository.Get("/user"));
        Assert.Null(repository.Get("/user")!.ItemIds);
        AssertCacheEqual(emptyPage, repository.Get(emptyPage.Url));
        Assert.Empty(repository.Get(emptyPage.Url)!.ItemIds!);

        var updated = Cache(RunsUrl, "W/\"def\"", null, [5]);
        repository.PutCache(updated);
        AssertCacheEqual(updated, repository.Get(RunsUrl));
        Assert.Null(repository.Get(RunsUrl + "&x=1"));
    }

    [Fact]
    public void 快取項的item_ids格式錯誤時當成沒有快取()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        repository.PutCache(Cache(RunsUrl, "\"abc\"", null, [1]));

        Scalar(workspace, "UPDATE http_cache SET item_ids = 'not json' WHERE url = $url;", ("$url", RunsUrl));

        // 沒有可信的項目清單就不能送 ETag，否則 304 時會沒有對應的資料（spec §14.3）
        Assert.Null(repository.Get(RunsUrl));
    }

    [Fact]
    public void 通知去重紀錄Contains與Add往返()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        repository.Add(10, 1, "failure", NotificationKind.TrackedFailure, suppressed: false, Now);
        repository.Add(11, 1, "success", null, suppressed: true, Now);

        Assert.True(repository.Contains(10, 1, "failure"));
        Assert.True(repository.Contains(11, 1, "success"));
        // 重跑讓 run_attempt 遞增，就是新的一組（spec §9.2）
        Assert.False(repository.Contains(10, 2, "failure"));
        Assert.False(repository.Contains(10, 1, "success"));
        Assert.Equal("TrackedFailure", Scalar(workspace, "SELECT kind FROM notified WHERE run_id = 10;"));
        Assert.Null(Scalar(workspace, "SELECT kind FROM notified WHERE run_id = 11;"));
        Assert.Equal(1L, Scalar(workspace, "SELECT suppressed FROM notified WHERE run_id = 11;"));
    }

    [Fact]
    public void 同一組通知重複Add時保留第一筆()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        repository.Add(10, 1, "failure", null, suppressed: true, Now);
        repository.Add(10, 1, "failure", NotificationKind.TrackedFailure, suppressed: false, Now.AddMinutes(1));

        Assert.Equal(1, Count(workspace, "notified"));
        Assert.Equal(1L, Scalar(workspace, "SELECT suppressed FROM notified;"));
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), Scalar(workspace, "SELECT notified_at FROM notified;"));
    }

    // ── SaveRunsPage ──────────────────────────────────────────────────

    [Fact]
    public void SaveRunsPage在同一交易寫入etag()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        repository.SaveRunsPage(RepoId, [Run(1)], isLatestPage: true, Cache(RunsUrl, "\"old\"", null, [1]));

        // 第二筆 head_sha 違反 NOT NULL，模擬處理到一半發生例外
        var broken = Run(3) with { HeadSha = null! };
        Assert.Throws<SqliteException>(() =>
            repository.SaveRunsPage(RepoId, [Run(2), broken], isLatestPage: true, Cache(RunsUrl, "\"new\"", null, [2, 3])));

        Assert.Equal([1L], repository.GetRuns(RepoId).Select(r => r.Id));
        Assert.Equal([1L], repository.GetAttempts(RepoId).Keys);
        Assert.Equal("\"old\"", repository.Get(RunsUrl)!.ETag);
        Assert.Equal([1L], repository.Get(RunsUrl)!.ItemIds);
        // in_latest_page 的重設也一起回復
        Assert.Equal(1L, Scalar(workspace, "SELECT in_latest_page FROM runs WHERE run_id = 1;"));
    }

    [Fact]
    public void SaveRunsPage的repo_id不符時丟例外且不寫入()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        Assert.Throws<ArgumentException>(() =>
            repository.SaveRunsPage(RepoId, [Run(1), Run(2, repoId: OtherRepoId)], isLatestPage: true, Cache(RunsUrl, "\"x\"")));

        Assert.Empty(repository.GetRuns(RepoId));
        Assert.Empty(repository.GetRuns(OtherRepoId));
        Assert.Null(repository.Get(RunsUrl));
    }

    [Fact]
    public void 第一頁換新時只有新一頁的run標成in_latest_page()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        repository.SaveRunsPage(OtherRepoId, [Run(90, repoId: OtherRepoId)], isLatestPage: true, null);
        repository.SaveRunsPage(RepoId, [Run(1), Run(2)], isLatestPage: true, null);

        repository.SaveRunsPage(RepoId, [Run(2), Run(3)], isLatestPage: true, null);
        // 不是第一頁（例如 E5、E6、PR 分支補抓）不動既有的標記
        repository.SaveRunsPage(RepoId, [Run(2), Run(4)], isLatestPage: false, null);

        Assert.Equal(0L, Scalar(workspace, "SELECT in_latest_page FROM runs WHERE run_id = 1;"));
        Assert.Equal(1L, Scalar(workspace, "SELECT in_latest_page FROM runs WHERE run_id = 2;"));
        Assert.Equal(1L, Scalar(workspace, "SELECT in_latest_page FROM runs WHERE run_id = 3;"));
        Assert.Equal(0L, Scalar(workspace, "SELECT in_latest_page FROM runs WHERE run_id = 4;"));
        Assert.Equal(1L, Scalar(workspace, "SELECT in_latest_page FROM runs WHERE run_id = 90;"));
        Assert.Equal(4, repository.GetRuns(RepoId).Count);
    }

    [Fact]
    public void 第一頁回0筆時清掉該repo的in_latest_page()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        repository.SaveRunsPage(RepoId, [Run(1)], isLatestPage: true, null);

        repository.SaveRunsPage(RepoId, [], isLatestPage: true, Cache(RunsUrl, "\"empty\"", null, []));

        Assert.Equal(0L, Scalar(workspace, "SELECT in_latest_page FROM runs WHERE run_id = 1;"));
        Assert.Empty(repository.Get(RunsUrl)!.ItemIds!);
    }

    [Fact]
    public void pr_numbers只增不減()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        repository.SaveRunsPage(RepoId, [Run(1, pullRequests: [5])], isLatestPage: true, null);
        repository.SaveRunsPage(RepoId, [Run(1, pullRequests: [3, 5])], isLatestPage: true, null);
        // PR 關閉後 API 不再回傳，本地仍要留著
        repository.SaveRunsPage(RepoId, [Run(1, pullRequests: [])], isLatestPage: true, null);

        Assert.Equal([3, 5], Assert.Single(repository.GetRuns(RepoId)).PullRequestNumbers);
    }

    [Fact]
    public void 重跑時保留舊attempt()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        var first = Run(1, attempt: 1, conclusion: "failure");
        repository.SaveRunsPage(RepoId, [first], isLatestPage: true, null);

        var rerun = Run(1, attempt: 2, status: "in_progress", conclusion: null) with { UpdatedAt = Now };
        repository.SaveRunsPage(RepoId, [rerun], isLatestPage: true, null);

        var run = Assert.Single(repository.GetRuns(RepoId));
        Assert.Equal(2, run.RunAttempt);
        Assert.Equal("in_progress", run.Status);
        Assert.Equal(
            [
                new RunAttemptRecord(1, 1, "completed", "failure", first.RunStartedAt, first.UpdatedAt),
                new RunAttemptRecord(1, 2, "in_progress", null, rerun.RunStartedAt, rerun.UpdatedAt),
            ],
            repository.GetAttempts(RepoId)[1]);
    }

    [Fact]
    public void attempt第一次被存成完成時記下觀察時間之後不改()
    {
        using var workspace = new TempWorkspace();
        var time = new ManualTimeProvider(Now);
        var repository = CreateRepository(workspace, time);
        const string sql = "SELECT completed_observed_at FROM run_attempts WHERE run_id = 1 AND run_attempt = 1;";

        repository.SaveRunsPage(RepoId, [Run(1, status: "in_progress", conclusion: null)], isLatestPage: true, null);
        Assert.Null(Scalar(workspace, sql));

        time.Advance(TimeSpan.FromMinutes(1));
        repository.SaveRunsPage(RepoId, [Run(1)], isLatestPage: true, null);
        var observed = Now.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture);
        Assert.Equal(observed, Scalar(workspace, sql));

        time.Advance(TimeSpan.FromMinutes(1));
        repository.SaveRunsPage(RepoId, [Run(1)], isLatestPage: true, null);
        Assert.Equal(observed, Scalar(workspace, sql));
    }

    [Fact]
    public void 等待核准的attempt不算完成觀察()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        repository.SaveRunsPage(RepoId, [Run(1, status: "completed", conclusion: "action_required")], isLatestPage: true, null);

        Assert.Null(Scalar(workspace, "SELECT completed_observed_at FROM run_attempts WHERE run_id = 1;"));
    }

    // ── MarkRunGone ───────────────────────────────────────────────────

    [Fact]
    public void MarkRunGone後移出清單但保留本地紀錄()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        repository.SaveRunsPage(RepoId, [Run(1), Run(2)], isLatestPage: true, null);

        repository.MarkRunGone(1);

        Assert.Equal([2L], repository.GetRuns(RepoId).Select(r => r.Id));
        Assert.Equal([2L], repository.GetAttempts(RepoId).Keys);
        Assert.Equal(1L, Scalar(workspace, "SELECT gone FROM runs WHERE run_id = 1;"));
        Assert.Equal(1, Count(workspace, "run_attempts", "run_id = 1"));
    }

    [Fact]
    public void 已不存在的run又出現在回應裡時恢復()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        repository.SaveRunsPage(RepoId, [Run(1)], isLatestPage: true, null);
        repository.MarkRunGone(1);

        repository.SaveRunsPage(RepoId, [Run(1)], isLatestPage: false, null);

        Assert.Equal([1L], repository.GetRuns(RepoId).Select(r => r.Id));
    }

    // ── 耗時樣本 ──────────────────────────────────────────────────────

    [Fact]
    public void 耗時樣本只留20筆()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        const int added = 25;
        for (var i = 1; i <= added; i++)
        {
            repository.AddDurationSample(i, 1, RepoId, WorkflowId, TimeSpan.FromSeconds(i), Now.AddMinutes(i));
        }

        repository.AddDurationSample(999, 1, RepoId, 777, TimeSpan.FromSeconds(1), Now);

        var samples = repository.GetDurationSamples(RepoId, WorkflowId);
        Assert.Equal(Tuning.DurationSamplesKept, samples.Count);
        Assert.Equal(
            Enumerable.Range(added - Tuning.DurationSamplesKept + 1, Tuning.DurationSamplesKept).Reverse().Select(s => TimeSpan.FromSeconds(s)),
            samples);
        Assert.Equal(Tuning.DurationSamplesKept + 1, Count(workspace, "duration_samples"));
        Assert.Single(repository.GetDurationSamples(RepoId, 777));
    }

    [Fact]
    public void 比現有20筆都舊的樣本不會擠掉新的()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        for (var i = 1; i <= Tuning.DurationSamplesKept; i++)
        {
            repository.AddDurationSample(i, 1, RepoId, WorkflowId, TimeSpan.FromSeconds(i), Now.AddMinutes(i));
        }

        repository.AddDurationSample(500, 1, RepoId, WorkflowId, TimeSpan.FromSeconds(500), Now.AddDays(-1));

        var samples = repository.GetDurationSamples(RepoId, WorkflowId);
        Assert.Equal(Tuning.DurationSamplesKept, samples.Count);
        Assert.DoesNotContain(TimeSpan.FromSeconds(500), samples);
    }

    // ── 清理 ──────────────────────────────────────────────────────────

    [Fact]
    public void 清理不刪第一頁裡的run()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        var old = Now.AddDays(-(Tuning.RunRetentionDays + 10));
        repository.SaveRunsPage(RepoId, [Run(1, createdAt: old)], isLatestPage: true, Cache(RunsUrl, "\"p1\"", null, [1]));

        repository.Cleanup(Now, new HashSet<long>());
        Assert.Equal([1L], repository.GetRuns(RepoId).Select(r => r.Id));

        // 第一頁換新後，舊 run 不再受保護
        repository.SaveRunsPage(RepoId, [Run(2)], isLatestPage: true, Cache(RunsUrl, "\"p2\"", null, [2]));
        repository.Cleanup(Now, new HashSet<long>());

        Assert.Equal([2L], repository.GetRuns(RepoId).Select(r => r.Id));
        Assert.Equal(0, Count(workspace, "run_attempts", "run_id = 1"));
    }

    [Fact]
    public void 清理不刪受保護的run()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        var old = Now.AddDays(-(Tuning.RunRetentionDays + 1));
        repository.SaveRunsPage(RepoId, [Run(1, createdAt: old), Run(2, createdAt: old), Run(3, createdAt: old)], isLatestPage: false, null);
        repository.SaveRunsPage(RepoId, [Run(1, attempt: 2, createdAt: old)], isLatestPage: false, null);
        repository.SetWatched(3, true);

        repository.Cleanup(Now, new HashSet<long> { 1 });

        // 1：追蹤鍵的 latest／lastDecisive；3：被標記「完成時通知我」
        Assert.Equal([1L, 3L], repository.GetRuns(RepoId).Select(r => r.Id).Order());
        Assert.Equal(2, repository.GetAttempts(RepoId)[1].Count);
        Assert.Equal(0, Count(workspace, "run_attempts", "run_id = 2"));
    }

    [Fact]
    public void 清理只刪超過保留期的run()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        var boundary = Now.AddDays(-Tuning.RunRetentionDays);
        repository.SaveRunsPage(
            RepoId,
            [Run(1, createdAt: boundary), Run(2, createdAt: boundary.AddTicks(-1)), Run(3, createdAt: Now.AddDays(1))],
            isLatestPage: false,
            null);

        // now 用台北時間傳入，結果必須和 UTC 相同
        repository.Cleanup(Now.ToOffset(TimeSpan.FromHours(8)), new HashSet<long>());

        // 剛好 90 天不算「超過」；時鐘誤差造成的未來時間也不刪
        Assert.Equal([1L, 3L], repository.GetRuns(RepoId).Select(r => r.Id).Order());
    }

    [Fact]
    public void notified只刪已不存在的run()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        var old = Now.AddDays(-(Tuning.RunRetentionDays + 1));
        repository.SaveRunsPage(RepoId, [Run(1, createdAt: old), Run(2)], isLatestPage: false, null);
        repository.Add(1, 1, "success", null, suppressed: true, old);
        repository.Add(2, 1, "success", NotificationKind.MyRunSucceeded, suppressed: false, Now);
        repository.Add(3, 1, "failure", NotificationKind.TrackedFailure, suppressed: false, old);
        repository.Add(4, 1, "failure", NotificationKind.TrackedFailure, suppressed: false, old);
        repository.SaveRunsPage(RepoId, [Run(4, createdAt: old)], isLatestPage: false, null);

        repository.Cleanup(Now, new HashSet<long> { 4 });

        Assert.False(repository.Contains(1, 1, "success"));
        Assert.True(repository.Contains(2, 1, "success"));
        Assert.False(repository.Contains(3, 1, "failure"));
        // run 還在（受保護），通知紀錄就算很舊也留著，否則下次看到又會通知
        Assert.True(repository.Contains(4, 1, "failure"));
    }

    // ── 寫入與狀態表 ──────────────────────────────────────────────────

    [Fact]
    public void UpsertRepos同一交易寫入權限()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        repository.UpsertRepos([Repo(), Repo(OtherRepoId, OtherRepoFullName)], [],
            Permissions(MyUserId, (RepoId, true), (OtherRepoId, false)));

        Assert.Equal([(RepoId, true), (OtherRepoId, false)], Sorted(repository.GetRepoPermissions(MyUserId)));
        Assert.Equal(CacheRepository.FormatTime(Now),
            Scalar(workspace, "SELECT updated_at FROM repo_permissions WHERE repo_id = $id;", ("$id", RepoId)));

        // 寫到一半失敗（full_name 違反 NOT NULL）時，權限和 repo 一起回復
        var broken = Repo(3003, "octo-org/broken") with { FullName = null! };
        Assert.Throws<SqliteException>(() => repository.UpsertRepos([Repo(4004, "octo-org/fourth"), broken], [],
            Permissions(MyUserId, (RepoId, false), (4004, true), (3003, true))));
        Assert.Equal([(RepoId, true), (OtherRepoId, false)], Sorted(repository.GetRepoPermissions(MyUserId)));
        Assert.Equal(2, repository.GetRepos().Count);
    }

    [Fact]
    public void 權限為null的repo不寫()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);

        repository.UpsertRepos([Repo(), Repo(OtherRepoId, OtherRepoFullName)], [],
            Permissions(MyUserId, (RepoId, true), (OtherRepoId, null)));

        // 回應沒有 permissions 物件是不確定，不寫成 false（第二階段 spec §4.1）
        Assert.Equal([(RepoId, true)], Sorted(repository.GetRepoPermissions(MyUserId)));
        Assert.Equal(1, Count(workspace, "repo_permissions"));

        // 之後的回應又沒有 permissions 也一樣不確定，不蓋掉之前讀到的值；不帶權限的寫入也不動它
        repository.UpsertRepos([Repo()], [], Permissions(MyUserId, (RepoId, null)));
        repository.UpsertRepos([Repo()], []);
        Assert.Equal([(RepoId, true)], Sorted(repository.GetRepoPermissions(MyUserId)));
    }

    [Fact]
    public void 只讀得到同一個帳號的權限()
    {
        using var workspace = new TempWorkspace();
        var time = new ManualTimeProvider(Now);
        var repository = CreateRepository(workspace, time);
        const long neverSeenUserId = 5005;

        repository.UpsertRepos([Repo(), Repo(OtherRepoId, OtherRepoFullName)], [], Permissions(MyUserId, (RepoId, true)));
        repository.UpsertRepos([Repo(), Repo(OtherRepoId, OtherRepoFullName)], [],
            Permissions(OtherUserId, (RepoId, false), (OtherRepoId, true)));

        // 鍵是 (repo_id, user_id)：換了帳號寫入不會蓋掉原帳號的值，user_id 不同的一律不回（第二階段 spec §4.1）
        Assert.Equal([(RepoId, true)], Sorted(repository.GetRepoPermissions(MyUserId)));
        Assert.Equal([(RepoId, false), (OtherRepoId, true)], Sorted(repository.GetRepoPermissions(OtherUserId)));
        Assert.Empty(repository.GetRepoPermissions(neverSeenUserId));

        // 同一個帳號再讀到一次：以新的值與時間為準
        time.Advance(TimeSpan.FromMinutes(5));
        repository.UpsertRepos([Repo()], [], Permissions(MyUserId, (RepoId, false)));
        Assert.Equal([(RepoId, false)], Sorted(repository.GetRepoPermissions(MyUserId)));
        Assert.Equal(CacheRepository.FormatTime(Now.AddMinutes(5)), Scalar(workspace,
            "SELECT updated_at FROM repo_permissions WHERE repo_id = $id AND user_id = $user;", ("$id", RepoId), ("$user", MyUserId)));
    }

    [Fact]
    public void 待確認重跑的寫入讀回與刪除()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        var taipei = TimeSpan.FromHours(8);
        var first = new PendingRerun(9001, RepoId, PreviousAttempt: 1, RequestedAt: new DateTimeOffset(2026, 10, 24, 10, 0, 0, taipei), Notify: true);
        var second = new PendingRerun(9002, OtherRepoId, PreviousAttempt: 3, RequestedAt: Now, Notify: false);

        repository.UpsertPendingRerun(second);
        repository.UpsertPendingRerun(first);

        // 依 run_id 排序；時間存成 UTC
        Assert.Equal([first, second], repository.GetPendingReruns());
        Assert.All(repository.GetPendingReruns(), row => Assert.Equal(TimeSpan.Zero, row.RequestedAt.Offset));

        // 同一個 run 再送一次重跑：整列覆蓋
        var again = first with { PreviousAttempt = 2, RequestedAt = Now.AddMinutes(1), Notify = false };
        repository.UpsertPendingRerun(again);
        Assert.Equal([again, second], repository.GetPendingReruns());

        repository.DeletePendingRerun(9001);
        // 不存在的 run 不做事
        repository.DeletePendingRerun(9999);
        Assert.Equal([second], repository.GetPendingReruns());
    }

    [Fact]
    public void 狀態表整列寫入後讀回相同()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        var taipei = TimeSpan.FromHours(8);
        var running = new StatusSnapshotRow(
            WrittenAt: new DateTimeOffset(2026, 10, 24, 10, 0, 0, taipei),
            HeartbeatAt: new DateTimeOffset(2026, 10, 24, 10, 0, 1, taipei),
            StoppedAt: null,
            AppVersion: "0.2.0+test",
            StatusJson: """{"schemaVersion":1,"state":"success"}""",
            RefreshStartedSeq: 3,
            RefreshDoneSeq: 2,
            RefreshPartial: true);

        repository.SaveStatusSnapshot(running);
        Assert.Equal(running, repository.GetStatusSnapshot());
        Assert.Equal(TimeSpan.Zero, repository.GetStatusSnapshot()!.WrittenAt.Offset);

        // 正常結束時寫 stopped_at；每次都整列覆蓋，表裡永遠只有 id = 1 那一列（第二階段 spec §10.2）
        var stopped = new StatusSnapshotRow(Now.AddMinutes(1), Now.AddMinutes(1), Now.AddMinutes(1).AddSeconds(2), "0.2.1+test",
            """{"title":"修正登入按鈕"}""", RefreshStartedSeq: 4, RefreshDoneSeq: 4, RefreshPartial: false);
        repository.SaveStatusSnapshot(stopped);
        Assert.Equal(stopped, repository.GetStatusSnapshot());
        Assert.Equal(1, Count(workspace, "status_snapshot"));
        Assert.Equal(1L, Scalar(workspace, "SELECT id FROM status_snapshot;"));

        // 重新啟動後第一次寫入：stopped_at 清成 null
        repository.SaveStatusSnapshot(running);
        Assert.Equal(running, repository.GetStatusSnapshot());
    }

    // ── 刪除 ──────────────────────────────────────────────────────────

    [Fact]
    public void DeleteRepo連帶刪除權限與待確認重跑()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        repository.UpsertRepos([Repo(), Repo(OtherRepoId, OtherRepoFullName)], [],
            Permissions(MyUserId, (RepoId, true), (OtherRepoId, true)));
        repository.UpsertRepos([Repo(), Repo(OtherRepoId, OtherRepoFullName)], [], Permissions(OtherUserId, (RepoId, false)));
        repository.UpsertPendingRerun(new PendingRerun(9001, RepoId, 1, Now, Notify: true));
        repository.UpsertPendingRerun(new PendingRerun(9002, RepoId, 2, Now, Notify: false));
        var kept = new PendingRerun(9003, OtherRepoId, 1, Now, Notify: true);
        repository.UpsertPendingRerun(kept);

        repository.DeleteRepo(RepoId);

        // 每個帳號的權限都刪；其他 repo 的權限與待確認重跑不受影響
        Assert.Equal([(OtherRepoId, true)], Sorted(repository.GetRepoPermissions(MyUserId)));
        Assert.Empty(repository.GetRepoPermissions(OtherUserId));
        Assert.Equal([kept], repository.GetPendingReruns());
    }

    [Fact]
    public void DeleteRepo連帶刪除()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        repository.UpsertRepos([Repo(), Repo(OtherRepoId, OtherRepoFullName)], []);
        foreach (var (repoId, fullName, runId) in new[] { (RepoId, RepoFullName, 1L), (OtherRepoId, OtherRepoFullName, 2L) })
        {
            repository.SaveRunsPage(repoId, [Run(runId, repoId: repoId)], isLatestPage: true,
                Cache($"https://api.github.com/repos/{fullName}/actions/runs?per_page=30", "\"r\""));
            repository.ReplaceWorkflows(repoId,
                [new WorkflowInfo(repoId, WorkflowId, "CI", ".github/workflows/ci.yml", "active", $"https://github.com/{fullName}/actions")],
                Cache($"/repos/{fullName}/actions/workflows?per_page=100", "\"w\""));
            repository.ReplacePullRequests(repoId,
                [new PullRequestInfo(repoId, 1, repoId, fullName, "feature", "abc", $"https://github.com/{fullName}/pull/1")], null);
            // 第 2 頁以後的網址是 Link header 給的，可能是 /repositories/{id}/ 形式（spec §4.1 分頁）
            repository.PutCache(Cache($"https://api.github.com/repositories/{repoId}/pulls?state=open&per_page=100&page=2", "\"p2\"", null, [5, 6]));
            repository.AddDurationSample(runId, 1, repoId, WorkflowId, TimeSpan.FromMinutes(1), Now);
            repository.MarkPrSeedChecked(repoId, "abc", Now);
            repository.SaveRepoState(new RepoState(repoId, 1, 1, Now, null, null, Now, 0, Now));
        }

        repository.PutCache(Cache("/user/repos?affiliation=owner,organization_member&per_page=100", "\"list\""));
        repository.SetOverride(RepoId, new RepoOverride(Muted: true, ForceIncluded: false));

        repository.DeleteRepo(RepoId);

        Assert.Equal([OtherRepoId], repository.GetRepos().Select(r => r.Id));
        Assert.Equal([OtherRepoId], repository.GetRepoStates().Keys);
        Assert.Empty(repository.GetRuns(RepoId));
        Assert.Equal(0, Count(workspace, "runs", "repo_id = $id", ("$id", RepoId)));
        Assert.Equal(0, Count(workspace, "run_attempts", "run_id = 1"));
        Assert.Empty(repository.GetWorkflows(RepoId));
        Assert.Empty(repository.GetPullRequests(RepoId));
        Assert.Empty(repository.GetDurationSamples(RepoId, WorkflowId));
        Assert.False(repository.PrSeedChecked(RepoId, "abc"));
        Assert.Null(repository.Get($"https://api.github.com/repos/{RepoFullName}/actions/runs?per_page=30"));
        Assert.Null(repository.Get($"/repos/{RepoFullName}/actions/workflows?per_page=100"));
        Assert.Null(repository.Get($"https://api.github.com/repositories/{RepoId}/pulls?state=open&per_page=100&page=2"));

        // 其他 repo 的資料、repo 清單的快取都不受影響
        Assert.Single(repository.GetRuns(OtherRepoId));
        Assert.Single(repository.GetAttempts(OtherRepoId));
        Assert.Single(repository.GetWorkflows(OtherRepoId));
        Assert.Single(repository.GetPullRequests(OtherRepoId));
        Assert.Single(repository.GetDurationSamples(OtherRepoId, WorkflowId));
        Assert.True(repository.PrSeedChecked(OtherRepoId, "abc"));
        Assert.NotNull(repository.Get($"https://api.github.com/repos/{OtherRepoFullName}/actions/runs?per_page=30"));
        Assert.NotNull(repository.Get($"/repos/{OtherRepoFullName}/actions/workflows?per_page=100"));
        Assert.NotNull(repository.Get($"https://api.github.com/repositories/{OtherRepoId}/pulls?state=open&per_page=100&page=2"));
        Assert.NotNull(repository.Get("/user/repos?affiliation=owner,organization_member&per_page=100"));
        // 靜音與強制納入以 repo id 為鍵、不在契約的刪除清單裡，repo 回來時仍然有效
        Assert.True(repository.GetOverrides()[RepoId].Muted);
    }

    [Fact]
    public void DeleteRepo不會刪到名稱相同開頭的其他repo快取()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        repository.UpsertRepos([Repo(), Repo(OtherRepoId, RepoFullName + "-v2")], []);
        repository.PutCache(Cache($"/repos/{RepoFullName}/actions/runs?per_page=30", "\"a\""));
        repository.PutCache(Cache($"/repos/{RepoFullName}-v2/actions/runs?per_page=30", "\"b\""));

        repository.DeleteRepo(RepoId);

        Assert.Null(repository.Get($"/repos/{RepoFullName}/actions/runs?per_page=30"));
        Assert.NotNull(repository.Get($"/repos/{RepoFullName}-v2/actions/runs?per_page=30"));
    }

    [Fact]
    public void DeleteRepo會刪掉以repo_id為路徑的快取且不刪到id開頭相同的其他repo()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        // 10010 的十進位開頭和 1001 相同
        const long prefixSharingRepoId = RepoId * 10;
        repository.UpsertRepos([Repo(), Repo(prefixSharingRepoId, OtherRepoFullName)], []);
        var deleted = new[]
        {
            $"https://api.github.com/repositories/{RepoId}/actions/runs?per_page=30&page=2",
            $"/repositories/{RepoId}/actions/workflows?per_page=100&page=2",
        };
        var kept = new[]
        {
            $"https://api.github.com/repositories/{prefixSharingRepoId}/actions/runs?per_page=30&page=2",
            $"/repositories/{prefixSharingRepoId}/actions/workflows?per_page=100&page=2",
        };
        foreach (var url in deleted.Concat(kept))
        {
            repository.PutCache(Cache(url, "\"page2\"", null, [7]));
        }

        repository.DeleteRepo(RepoId);

        Assert.All(deleted, url => Assert.Null(repository.Get(url)));
        Assert.All(kept, url => Assert.NotNull(repository.Get(url)));
    }

    [Fact]
    public void repo不在清單裡時DeleteRepo仍刪掉以repo_id為路徑的快取()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        var pageTwoUrl = $"https://api.github.com/repositories/{RepoId}/pulls?state=open&per_page=100&page=2";
        repository.PutCache(Cache(pageTwoUrl, "\"p2\"", null, [5, 6]));

        repository.DeleteRepo(RepoId);

        // 這種網址只靠 repo id 就能對出來，不需要 repos 表裡的名稱
        Assert.Null(repository.Get(pageTwoUrl));
    }

    [Fact]
    public void DeleteCacheForPrefix()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        const string prefix = "https://api.github.com/repos/octo-org/sample_app/";
        repository.PutCache(Cache(prefix + "actions/runs?per_page=30", "\"1\""));
        repository.PutCache(Cache(prefix + "pulls?state=open&per_page=100", "\"2\""));
        // 底線與百分比在 LIKE 裡是萬用字元，這裡必須當成一般字元
        repository.PutCache(Cache("https://api.github.com/repos/octo-org/sampleXapp/actions/runs?per_page=30", "\"3\""));
        repository.PutCache(Cache("https://api.github.com/repos/octo-org/other-app/actions/runs?per_page=30", "\"4\""));
        // 大小寫不同視為不同網址（存下什麼就比對什麼）
        repository.PutCache(Cache("https://api.github.com/repos/OCTO-ORG/sample_app/actions/runs?per_page=30", "\"5\""));

        repository.DeleteCacheForPrefix(prefix);

        Assert.Null(repository.Get(prefix + "actions/runs?per_page=30"));
        Assert.Null(repository.Get(prefix + "pulls?state=open&per_page=100"));
        Assert.NotNull(repository.Get("https://api.github.com/repos/octo-org/sampleXapp/actions/runs?per_page=30"));
        Assert.NotNull(repository.Get("https://api.github.com/repos/octo-org/other-app/actions/runs?per_page=30"));
        Assert.NotNull(repository.Get("https://api.github.com/repos/OCTO-ORG/sample_app/actions/runs?per_page=30"));

        repository.PutCache(Cache("/repos/octo-org/100%/x", "\"6\""));
        repository.PutCache(Cache("/repos/octo-org/100abc/x", "\"7\""));
        repository.DeleteCacheForPrefix("/repos/octo-org/100%/");
        Assert.Null(repository.Get("/repos/octo-org/100%/x"));
        Assert.NotNull(repository.Get("/repos/octo-org/100abc/x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DeleteCacheForPrefix空白前綴丟出例外且不刪任何快取(string prefix)
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        repository.PutCache(Cache(RunsUrl, "\"1\""));

        Assert.Throws<ArgumentException>(() => repository.DeleteCacheForPrefix(prefix));

        Assert.NotNull(repository.Get(RunsUrl));
    }

    // ── 其他寫入的參數檢查 ─────────────────────────────────────────────

    [Fact]
    public void Replace的repo_id不符時丟例外且不寫入()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        var workflow = new WorkflowInfo(OtherRepoId, WorkflowId, "CI", ".github/workflows/ci.yml", "active", "https://github.com/x");
        var pullRequest = new PullRequestInfo(OtherRepoId, 1, null, null, "feature", "abc", "https://github.com/x/pull/1");

        Assert.Throws<ArgumentException>(() => repository.ReplaceWorkflows(RepoId, [workflow], null));
        Assert.Throws<ArgumentException>(() => repository.ReplacePullRequests(RepoId, [pullRequest], null));

        Assert.Empty(repository.GetWorkflows(RepoId));
        Assert.Empty(repository.GetWorkflows(OtherRepoId));
        Assert.Empty(repository.GetPullRequests(RepoId));
        Assert.Empty(repository.GetPullRequests(OtherRepoId));
    }

    [Fact]
    public void 時間以UTC存放跨時區讀回同一時刻()
    {
        using var workspace = new TempWorkspace();
        var repository = CreateRepository(workspace);
        var taipeiTime = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(8));

        repository.MarkPrSeedChecked(RepoId, "abc", taipeiTime);
        repository.Add(1, 1, "failure", null, suppressed: false, taipeiTime);
        repository.AddDurationSample(1, 1, RepoId, WorkflowId, TimeSpan.FromSeconds(30), taipeiTime);

        const string utcText = "2026-10-24T00:00:00.0000000+00:00";
        Assert.Equal(utcText, Scalar(workspace, "SELECT checked_at FROM pr_seed;"));
        Assert.Equal(utcText, Scalar(workspace, "SELECT notified_at FROM notified;"));
        Assert.Equal(utcText, Scalar(workspace, "SELECT completed_at FROM duration_samples;"));
    }
}
