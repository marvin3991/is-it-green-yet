using System.Net;
using System.Text;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.GitHub;

public class GitHubApiTests
{
    private const string UserAgent = "IsItGreenYet/0.1.0";

    private const string Date = "Sat, 24 Oct 2026 02:07:33 GMT";

    private const string Api = "https://api.github.com";

    private const string RunsUrl = Api + "/repos/octo-org/sample-app/actions/runs?per_page=30";

    private const string LatestReleaseUrl = Api + "/repos/marvin3991/is-it-green-yet/releases/latest";

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 2, 7, 33, TimeSpan.Zero);

    private static readonly RepoInfo Repo = new(2001, "octo-org/sample-app", "sample-app", 3001, "octo-org", "Organization",
        IsPrivate: false, IsFork: false, IsArchived: false, IsDisabled: false, "main", "https://github.com/octo-org/sample-app",
        PushedAt: null, RepoCategory.Organization);

    [Theory]
    [InlineData(false, false, "/user/repos?affiliation=owner,organization_member&per_page=100")]
    [InlineData(true, false, "/user/repos?affiliation=collaborator&per_page=100")]
    [InlineData(false, true, "/user/repos?affiliation=owner,organization_member&sort=pushed&per_page=100")]
    [InlineData(true, true, "/user/repos?affiliation=collaborator&sort=pushed&per_page=100")]
    public void 四種repo清單URL(bool collaborator, bool probe, string expected)
    {
        Assert.Equal(expected, GitHubApi.RepoListUrl(collaborator, probe));
    }

    [Fact]
    public async Task user對應到AccountInfo()
    {
        var (api, handler) = Create(Routes((Api + "/user", () => Json(Fixture.Read("user.json")))));

        var result = await api.GetUserAsync(CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(new AccountInfo(1001, "alice"), result.Value);
        Assert.Equal(Api + "/user", Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{"id":1001}""")]
    [InlineData("""{"login":"alice"}""")]
    [InlineData("""{"id":1001,"login":"  "}""")]
    public async Task user缺必要欄位回ParseError(string body)
    {
        var (api, _) = Create(Routes((Api + "/user", () => Json(body, ("ETag", "W/\"u1\"")))));

        var result = await api.GetUserAsync(CancellationToken.None);

        Assert.Equal(ApiOutcome.ParseError, result.Outcome);
        Assert.Null(result.Value);
        Assert.Null(result.CacheEntry);
    }

    [Fact]
    public async Task repo清單對應到RepoInfo各欄位()
    {
        var url = GitHubApi.RepoListUrl(collaborator: false, probe: false);
        var (api, _) = Create(Routes((Api + url, () => Json(Fixture.Read("repos-page1.json"), ("ETag", "W/\"p1\"")))));

        var result = await api.GetRepoPageAsync(url, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(
            [
                new RepoInfo(2001, "octo-org/sample-app", "sample-app", 3001, "octo-org", "Organization",
                    false, false, false, false, "main", "https://github.com/octo-org/sample-app",
                    Utc(2026, 10, 24, 1, 58, 12), RepoCategory.Own),
                new RepoInfo(2002, "alice/sample-notes", "sample-notes", 1001, "alice", "User",
                    true, false, true, false, "trunk", "https://github.com/alice/sample-notes",
                    null, RepoCategory.Own),
            ],
            result.Value!.Repos);
        Assert.Equal([2001L, 2002L], result.CacheEntry!.ItemIds!);
    }

    [Fact]
    public async Task repo清單帶出push權限()
    {
        // permissions 物件本身是選填，有出現時 push 是必填的 boolean；沒有 permissions 的 repo 是不確定（第二階段 spec §4.1）。
        var url = GitHubApi.RepoListUrl(collaborator: false, probe: false);
        const string body = """
            [
              {"id":2001,"name":"sample-app","full_name":"octo-org/sample-app","owner":{"id":3001,"login":"octo-org","type":"Organization"},
               "private":false,"fork":false,"archived":false,"disabled":false,"default_branch":"main","html_url":"https://github.com/octo-org/sample-app",
               "permissions":{"admin":false,"maintain":false,"push":true,"triage":true,"pull":true}},
              {"id":2002,"name":"sample-notes","full_name":"octo-org/sample-notes","owner":{"id":3001,"login":"octo-org","type":"Organization"},
               "private":true,"fork":false,"archived":false,"disabled":false,"default_branch":"main","html_url":"https://github.com/octo-org/sample-notes",
               "permissions":{"admin":false,"maintain":false,"push":false,"triage":false,"pull":true}},
              {"id":2003,"name":"sample-docs","full_name":"octo-org/sample-docs","owner":{"id":3001,"login":"octo-org","type":"Organization"},
               "private":false,"fork":false,"archived":false,"disabled":false,"default_branch":"main","html_url":"https://github.com/octo-org/sample-docs"}
            ]
            """;
        var (api, _) = Create(Routes((Api + url, () => Json(body, ("ETag", "W/\"p1\"")))));

        var result = await api.GetRepoPageAsync(url, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal([2001L, 2002L, 2003L], result.Value!.Repos.Select(repo => repo.Id));
        Assert.Equal(
            [(2001L, (bool?)true), (2002L, false), (2003L, null)],
            result.Value.CanPush.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)));
        Assert.Equal([2001L, 2002L, 2003L], result.CacheEntry!.ItemIds!);
    }

    [Fact]
    public async Task 多頁repo清單304時沿用存下的next()
    {
        var page1 = Api + GitHubApi.RepoListUrl(collaborator: false, probe: false);
        const string page2 = Api + "/user/repos?affiliation=owner%2Corganization_member&per_page=100&page=2";
        var cache = new FakeCacheStore();
        var round = 1;
        var handler = new FakeHttpHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (round == 2)
            {
                // 第二輪：兩頁都沒變，304 不帶 Link。
                return FakeHttpHandler.Status(HttpStatusCode.NotModified);
            }

            return url == page1
                ? Json(Fixture.Read("repos-page1.json"), ("ETag", "W/\"p1\""), ("Link", $"<{page2}>; rel=\"next\", <{page2}>; rel=\"last\""))
                : Json(Fixture.Read("repos-page2.json"), ("ETag", "W/\"p2\""));
        });
        var api = new GitHubApi(new GitHubClient(handler, cache, () => Fixture.FakeToken, null, new ManualTimeProvider(Now), UserAgent));

        // 第一輪：200 回新的快取項（含 next 與 ids），呼叫端存起來。
        var first = await api.GetRepoPageAsync(GitHubApi.RepoListUrl(false, false), CancellationToken.None);
        Assert.Equal(ApiOutcome.Ok, first.Outcome);
        Assert.Equal(page2, first.CacheEntry!.NextUrl);
        cache.Put(first.CacheEntry);
        var second = await api.GetRepoPageAsync(first.CacheEntry.NextUrl!, CancellationToken.None);
        Assert.Equal([2003L, 2004L], second.Value!.Repos.Select(repo => repo.Id));
        Assert.Null(second.CacheEntry!.NextUrl);
        cache.Put(second.CacheEntry);

        // 第二輪：第 1 頁 304 沒有 Link，用存下的 next 繼續抓第 2 頁。
        round = 2;
        var again = await api.GetRepoPageAsync(GitHubApi.RepoListUrl(false, false), CancellationToken.None);
        Assert.Equal(ApiOutcome.NotModified, again.Outcome);
        Assert.Null(again.Value);
        Assert.Same(first.CacheEntry, again.CacheEntry);
        Assert.Equal([2001L, 2002L], again.CacheEntry!.ItemIds!);
        var againSecond = await api.GetRepoPageAsync(again.CacheEntry.NextUrl!, CancellationToken.None);
        Assert.Equal(ApiOutcome.NotModified, againSecond.Outcome);
        Assert.Same(second.CacheEntry, againSecond.CacheEntry);

        var requests = handler.Requests;
        Assert.Equal([page1, page2, page1, page2], requests.Select(request => request.Uri.AbsoluteUri));
        Assert.Null(requests[0].Header("If-None-Match"));
        Assert.Equal("W/\"p1\"", requests[2].Header("If-None-Match"));
        Assert.Equal("W/\"p2\"", requests[3].Header("If-None-Match"));
    }

    [Theory]
    [InlineData("""{"id":2005,"name":"x","owner":{"id":3001,"login":"octo-org","type":"Organization"},"private":false,"fork":false,"archived":false,"disabled":false,"default_branch":"main","html_url":"https://github.com/octo-org/x"}""")]
    [InlineData("""{"id":2005,"name":"x","full_name":"octo-org/x","private":false,"fork":false,"archived":false,"disabled":false,"default_branch":"main","html_url":"https://github.com/octo-org/x"}""")]
    [InlineData("""{"id":2005,"name":"x","full_name":"octo-org/x","owner":{"id":3001,"login":"octo-org","type":"Organization"},"fork":false,"archived":false,"disabled":false,"default_branch":"main","html_url":"https://github.com/octo-org/x"}""")]
    [InlineData("""{"id":2005,"name":"x","full_name":"x","owner":{"id":3001,"login":"octo-org","type":"Organization"},"private":false,"fork":false,"archived":false,"disabled":false,"default_branch":"main","html_url":"https://github.com/octo-org/x"}""")]
    [InlineData("""{"name":"x","full_name":"octo-org/x","owner":{"id":3001,"login":"octo-org","type":"Organization"},"private":false,"fork":false,"archived":false,"disabled":false,"default_branch":"main","html_url":"https://github.com/octo-org/x"}""")]
    [InlineData("""{"id":2005,"name":"x","full_name":"octo-org/x","owner":{"id":3001,"login":"octo-org","type":"Organization"},"private":false,"fork":false,"archived":false,"disabled":false,"html_url":"https://github.com/octo-org/x"}""")]
    public async Task repo項目缺必要欄位時整頁回ParseError(string broken)
    {
        // 略過一筆會讓它被當成「從清單消失」，所以整頁不採用、也不更新 ETag（spec §5.4 不誤刪）。
        var url = GitHubApi.RepoListUrl(collaborator: false, probe: false);
        var body = Fixture.Read("repos-page1.json").TrimEnd().TrimEnd(']') + "," + broken + "]";
        var (api, _) = Create(Routes((Api + url, () => Json(body, ("ETag", "W/\"p1\"")))));

        var result = await api.GetRepoPageAsync(url, CancellationToken.None);

        Assert.Equal(ApiOutcome.ParseError, result.Outcome);
        Assert.Null(result.Value);
        Assert.Null(result.CacheEntry);
    }

    [Fact]
    public async Task workflows對應到WorkflowInfo()
    {
        const string url = Api + "/repos/octo-org/sample-app/actions/workflows?per_page=100";
        var (api, _) = Create(Routes((url, () => Json(Fixture.Read("workflows.json"), ("ETag", "W/\"wf\"")))));

        var result = await api.GetWorkflowsAsync(Repo, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(
            [
                new WorkflowInfo(2001, 161335, "CI", ".github/workflows/ci.yml", "active",
                    "https://github.com/octo-org/sample-app/blob/main/.github/workflows/ci.yml"),
                new WorkflowInfo(2001, 161336, "Release", ".github/workflows/release.yml", "disabled_manually",
                    "https://github.com/octo-org/sample-app/blob/main/.github/workflows/release.yml"),
                new WorkflowInfo(2001, 161337, "Nightly", ".github/workflows/nightly.yml", "disabled_inactivity",
                    "https://github.com/octo-org/sample-app/blob/main/.github/workflows/nightly.yml"),
            ],
            result.Value!);
        Assert.Equal(new HttpCacheEntry(url, "W/\"wf\"", null, result.CacheEntry!.ItemIds), result.CacheEntry);
        Assert.Equal([161335L, 161336L, 161337L], result.CacheEntry.ItemIds!);
    }

    [Fact]
    public async Task workflows缺必要欄位的那一筆略過()
    {
        const string url = Api + "/repos/octo-org/sample-app/actions/workflows?per_page=100";
        const string body = """
            {"total_count":3,"workflows":[
              {"id":1,"name":"CI","path":".github/workflows/ci.yml","state":"active","html_url":"https://github.com/octo-org/sample-app/blob/main/.github/workflows/ci.yml"},
              {"id":2,"name":"No state","path":".github/workflows/b.yml","html_url":"https://github.com/octo-org/sample-app/blob/main/.github/workflows/b.yml"},
              {"name":"No id","path":".github/workflows/c.yml","state":"active","html_url":"https://github.com/octo-org/sample-app/blob/main/.github/workflows/c.yml"}
            ]}
            """;
        var (api, _) = Create(Routes((url, () => Json(body))));

        var result = await api.GetWorkflowsAsync(Repo, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal([1L], result.Value!.Select(workflow => workflow.Id));
        Assert.Equal([1L], result.CacheEntry!.ItemIds!);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, "")]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.OK, """{"total_count":0}""")]
    [InlineData(HttpStatusCode.OK, """{"total_count":0,"workflows":null}""")]
    public async Task workflows回204或空body當成0筆(HttpStatusCode status, string body)
    {
        const string url = Api + "/repos/octo-org/sample-app/actions/workflows?per_page=100";
        var (api, _) = Create(Routes((url, () => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        })));

        var result = await api.GetWorkflowsAsync(Repo, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Empty(result.Value!);
        Assert.Empty(result.CacheEntry!.ItemIds!);
    }

    [Fact]
    public async Task runs對應到RunInfo各欄位()
    {
        var (api, handler) = Create(Routes((RunsUrl, () => Json(Fixture.Read("runs.json"), ("ETag", "W/\"runs\"")))));

        var result = await api.GetRunsAsync(Repo, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(RunsUrl, Assert.Single(handler.Requests).Uri.AbsoluteUri);
        var runs = result.Value!;
        Assert.Equal([9001L, 9002L, 9003L, 9004L, 9005L, 9006L], runs.Select(run => run.Id));
        Assert.Equal([9001L, 9002L, 9003L, 9004L, 9005L, 9006L], result.CacheEntry!.ItemIds!);
        Assert.Equal("W/\"runs\"", result.CacheEntry.ETag);

        AssertRun(new RunInfo(9001, 2001, 161335, "CI", ".github/workflows/ci.yml", 42, 1, "Fix login button", "push",
            "completed", "success", "main", "dcf27ad0c1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6", 2001, "octo-org/sample-app",
            "https://github.com/octo-org/sample-app", 1001, "alice", "alice", [], Utc(2026, 10, 24, 1, 58, 20),
            Utc(2026, 10, 24, 1, 58, 15), Utc(2026, 10, 24, 2, 1, 32), "https://github.com/octo-org/sample-app/actions/runs/9001"),
            runs[0]);

        // name 為 null、display_title 多行（單行化交給 DisplayFormatter）、run_attempt 缺值當 1、帶 PR 編號。
        AssertRun(new RunInfo(9002, 2001, 161335, null, ".github/workflows/ci.yml", 43, 1,
            "Add dark mode\n\nAlso tidies the settings page", "pull_request", "in_progress", null, "feature/dark-mode",
            "1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d", 2001, "octo-org/sample-app", "https://github.com/octo-org/sample-app",
            1002, "bob", "bob", [17], Utc(2026, 10, 24, 2, 5, 1), Utc(2026, 10, 24, 2, 4, 58), Utc(2026, 10, 24, 2, 6, 40),
            "https://github.com/octo-org/sample-app/actions/runs/9002"),
            runs[1]);

        // actor 與 triggering_actor 缺席、head_branch 為 null、還沒開始。
        AssertRun(new RunInfo(9003, 2001, 161337, "Nightly", ".github/workflows/nightly.yml", 7, 1, "Nightly", "schedule",
            "queued", null, null, "dcf27ad0c1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6", 2001, "octo-org/sample-app",
            "https://github.com/octo-org/sample-app", null, null, null, [], null, Utc(2026, 10, 24, 2, 0, 3),
            Utc(2026, 10, 24, 2, 0, 3), "https://github.com/octo-org/sample-app/actions/runs/9003"),
            runs[2]);

        // fork 的 PR：HeadRepoId 取 head_repository.id（不是頂層的 head_repository_id），pull_requests 為空。
        AssertRun(new RunInfo(9004, 2001, 161335, "CI", ".github/workflows/ci.yml", 40, 1, "Fix typo in README", "pull_request",
            "completed", "failure", "patch-1", "f00dbabe0123456789abcdef0123456789abcdef", 4001, "fork-user/sample-app",
            "https://github.com/fork-user/sample-app", 5001, "fork-user", "fork-user", [], Utc(2026, 10, 23, 23, 10, 0),
            Utc(2026, 10, 23, 23, 9, 55), Utc(2026, 10, 23, 23, 14, 12), "https://github.com/octo-org/sample-app/actions/runs/9004"),
            runs[3]);

        // dynamic 事件照原字串保留。
        AssertRun(new RunInfo(9005, 2001, 161338, "Dependabot Updates", "dynamic/dependabot/dependabot-updates", 12, 1,
            "Dependabot Updates", "dynamic", "completed", "success", "main", "dcf27ad0c1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6", 2001,
            "octo-org/sample-app", "https://github.com/octo-org/sample-app", 6001, "dependabot[bot]", "dependabot[bot]", [],
            Utc(2026, 10, 23, 20, 0, 10), Utc(2026, 10, 23, 20, 0, 5), Utc(2026, 10, 23, 20, 2, 0),
            "https://github.com/octo-org/sample-app/actions/runs/9005"),
            runs[4]);

        // 重跑的第 2 次 attempt：觸發者是重跑的人。
        AssertRun(new RunInfo(9006, 2001, 161335, "CI", ".github/workflows/ci.yml", 41, 2, "Bump version", "push",
            "completed", "failure", "main", "0123456789abcdef0123456789abcdef01234567", 2001, "octo-org/sample-app",
            "https://github.com/octo-org/sample-app", 1001, "alice", "bob", [], Utc(2026, 10, 24, 1, 40, 0),
            Utc(2026, 10, 24, 1, 20, 0), Utc(2026, 10, 24, 1, 45, 10), "https://github.com/octo-org/sample-app/actions/runs/9006"),
            runs[5]);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("workflow_id")]
    [InlineData("run_number")]
    [InlineData("head_sha")]
    [InlineData("event")]
    [InlineData("display_title")]
    [InlineData("path")]
    [InlineData("created_at")]
    [InlineData("updated_at")]
    [InlineData("html_url")]
    [InlineData("head_repository")]
    public async Task runs缺必要欄位的那一筆略過(string missing)
    {
        var good = RunJson(9101);
        var broken = RunJson(9102, without: missing);
        var body = $$"""{"total_count":2,"workflow_runs":[{{good}},{{broken}}]}""";
        var (api, _) = Create(Routes((RunsUrl, () => Json(body))));

        var result = await api.GetRunsAsync(Repo, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal([9101L], result.Value!.Select(run => run.Id));
        Assert.Equal([9101L], result.CacheEntry!.ItemIds!);
    }

    [Theory]
    [InlineData("run_started_at", "\"not a time\"")]
    [InlineData("run_attempt", "0")]
    [InlineData("pull_requests", "null")]
    public async Task runs的選填欄位格式不對時用預設值(string field, string value)
    {
        var body = $$"""{"total_count":1,"workflow_runs":[{{RunJson(9101, field, value)}}]}""";
        var (api, _) = Create(Routes((RunsUrl, () => Json(body))));

        var result = await api.GetRunsAsync(Repo, CancellationToken.None);

        var run = Assert.Single(result.Value!);
        switch (field)
        {
            case "run_started_at":
                Assert.Null(run.RunStartedAt);
                break;
            case "run_attempt":
                // run_attempt 從 1 開始；不合理的值和缺值一樣當 1。
                Assert.Equal(1, run.RunAttempt);
                break;
            default:
                Assert.Empty(run.PullRequestNumbers);
                break;
        }
    }

    [Fact]
    public async Task repo的pushed_at格式不對時當成null()
    {
        var url = GitHubApi.RepoListUrl(collaborator: false, probe: false);
        var body = Fixture.Read("repos-page1.json").Replace("\"2026-10-24T01:58:12Z\"", "\"yesterday\"", StringComparison.Ordinal);
        var (api, _) = Create(Routes((Api + url, () => Json(body))));

        var result = await api.GetRepoPageAsync(url, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Null(result.Value!.Repos[0].PushedAt);
    }

    [Fact]
    public async Task 時間一律轉成UTC()
    {
        const string url = Api + "/repos/octo-org/sample-app/actions/runs/9002";
        var (api, _) = Create(Routes((url, () => Json(Fixture.Read("run-single.json")))));

        var result = await api.GetRunAsync(Repo, 9002, CancellationToken.None);

        var run = result.Value!;
        // run-single.json 的 run_started_at 是 +08:00 的寫法。
        Assert.Equal(Utc(2026, 10, 24, 2, 5, 1), run.RunStartedAt);
        Assert.Equal(TimeSpan.Zero, run.RunStartedAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, run.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, run.UpdatedAt.Offset);
    }

    [Fact]
    public async Task 單筆run對應()
    {
        const string url = Api + "/repos/octo-org/sample-app/actions/runs/9002";
        var (api, _) = Create(Routes((url, () => Json(Fixture.Read("run-single.json"), ("ETag", "W/\"r\"")))));

        var result = await api.GetRunAsync(Repo, 9002, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        AssertRun(new RunInfo(9002, 2001, 161335, "CI", ".github/workflows/ci.yml", 43, 1, "Add dark mode", "pull_request",
            "completed", "success", "feature/dark-mode", "1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d", 2001, "octo-org/sample-app",
            "https://github.com/octo-org/sample-app", 1002, "bob", "bob", [17], Utc(2026, 10, 24, 2, 5, 1),
            Utc(2026, 10, 24, 2, 4, 58), Utc(2026, 10, 24, 2, 9, 30), "https://github.com/octo-org/sample-app/actions/runs/9002"),
            result.Value!);
        Assert.Equal(new HttpCacheEntry(url, "W/\"r\"", null, null), result.CacheEntry);
    }

    [Fact]
    public async Task 單筆run缺必要欄位回ParseError()
    {
        const string url = Api + "/repos/octo-org/sample-app/actions/runs/9102";
        var (api, _) = Create(Routes((url, () => Json(RunJson(9102, without: "head_sha"), ("ETag", "W/\"r\"")))));

        var result = await api.GetRunAsync(Repo, 9102, CancellationToken.None);

        Assert.Equal(ApiOutcome.ParseError, result.Outcome);
        Assert.Null(result.Value);
        Assert.Null(result.CacheEntry);
    }

    [Fact]
    public async Task head_sha補抓的URL()
    {
        const string url = Api + "/repos/octo-org/sample-app/actions/runs?head_sha=1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d&per_page=30";
        var (api, handler) = Create(Routes((url, () => Json(Fixture.Read("runs.json")))));

        var result = await api.GetRunsForShaAsync(Repo, "1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d", CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(url, Assert.Single(handler.Requests).Uri.AbsoluteUri);
        Assert.Equal(url, result.CacheEntry!.Url);
    }

    [Theory]
    [InlineData("main", "main")]
    [InlineData("feature/a#1", "feature%2Fa%231")]
    [InlineData("release/v1 50%", "release%2Fv1%2050%25")]
    [InlineData("功能/登入", "%E5%8A%9F%E8%83%BD%2F%E7%99%BB%E5%85%A5")]
    [InlineData("a+b&c=d", "a%2Bb%26c%3Dd")]
    [InlineData("fix/100%-done #2 測試", "fix%2F100%25-done%20%232%20%E6%B8%AC%E8%A9%A6")]
    public async Task 分支名稱含斜線與中文時branch參數正確編碼(string branch, string encoded)
    {
        var repo = Repo with { DefaultBranch = branch };
        var expected = $"{Api}/repos/octo-org/sample-app/actions/workflows/161335/runs?branch={encoded}&per_page=5";
        var (api, handler) = Create(Routes((expected, () => Json(Fixture.Read("runs.json")))));

        var result = await api.GetDefaultBranchRunsAsync(repo, 161335, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(expected, request.Uri.AbsoluteUri);
        Assert.Equal(expected, result.CacheEntry!.Url);
        // 伺服器解碼後拿到的是原本的分支名稱。
        var query = request.Uri.Query.TrimStart('?').Split('&');
        Assert.Equal(["branch", "per_page"], query.Select(pair => pair.Split('=')[0]));
        Assert.Equal(branch, Uri.UnescapeDataString(query[0]["branch=".Length..]));
    }

    [Fact]
    public async Task 開啟中PR對應()
    {
        const string url = Api + "/repos/octo-org/sample-app/pulls?state=open&per_page=100";
        var (api, _) = Create(Routes((url, () => Json(Fixture.Read("pulls.json"), ("ETag", "W/\"pulls\"")))));

        var result = await api.GetOpenPullRequestsAsync(Repo, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(
            [
                new PullRequestInfo(2001, 17, 2001, "octo-org/sample-app", "feature/dark-mode",
                    "1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d", "https://github.com/octo-org/sample-app/pull/17"),
                new PullRequestInfo(2001, 18, 4001, "fork-user/sample-app", "patch-1",
                    "f00dbabe0123456789abcdef0123456789abcdef", "https://github.com/octo-org/sample-app/pull/18"),
                // 來源 fork 已刪除：head.repo 為 null。
                new PullRequestInfo(2001, 19, null, null, "old-idea",
                    "abcdefabcdefabcdefabcdefabcdefabcdefabcd", "https://github.com/octo-org/sample-app/pull/19"),
            ],
            result.Value!);
        Assert.Equal([17L, 18L, 19L], result.CacheEntry!.ItemIds!);
    }

    [Fact]
    public async Task jobs跟Link抓完兩頁()
    {
        const string page1 = Api + "/repos/octo-org/sample-app/actions/runs/9006/jobs?filter=latest&per_page=100";
        const string page2 = Api + "/repositories/2001/actions/runs/9006/jobs?filter=latest&per_page=100&page=2";
        var cache = new FakeCacheStore();
        cache.Put(new HttpCacheEntry(page1, "W/\"j1\"", page2, [7001, 7002]));
        cache.Put(new HttpCacheEntry(page2, "W/\"j2\"", null, [7003]));
        var (api, handler) = Create(Routes(
            (page1, () => Json(Fixture.Read("jobs.json"), ("Link", $"<{page2}>; rel=\"next\", <{page2}>; rel=\"last\""))),
            (page2, () => Json(Fixture.Read("jobs-page2.json"), ("x-ratelimit-remaining", "4990")))), cache);

        var result = await api.GetJobsAsync(Repo, 9006, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal([page1, page2], handler.Requests.Select(request => request.Uri.AbsoluteUri));
        // 一次性的請求，不帶 If-None-Match（不然 304 時拿不到內容）。
        Assert.All(handler.Requests, request => Assert.Null(request.Header("If-None-Match")));
        Assert.Equal(
            [
                new JobInfo(7001, "build", "completed", "success", Utc(2026, 10, 24, 1, 40, 5), Utc(2026, 10, 24, 1, 42, 0),
                    "https://github.com/octo-org/sample-app/actions/runs/9006/job/7001"),
                new JobInfo(7002, "test (ubuntu-latest)", "completed", "failure", Utc(2026, 10, 24, 1, 42, 5),
                    Utc(2026, 10, 24, 1, 45, 0), "https://github.com/octo-org/sample-app/actions/runs/9006/job/7002"),
                new JobInfo(7003, "test (windows-latest)", "in_progress", null, Utc(2026, 10, 24, 1, 42, 6), null, null),
            ],
            result.Value!);
        Assert.Equal(4990, result.RateLimit?.Remaining);
    }

    [Fact]
    public async Task jobs中途失敗時回傳該頁的錯誤()
    {
        const string page1 = Api + "/repos/octo-org/sample-app/actions/runs/9006/jobs?filter=latest&per_page=100";
        const string page2 = Api + "/repositories/2001/actions/runs/9006/jobs?filter=latest&per_page=100&page=2";
        var (api, _) = Create(Routes(
            (page1, () => Json(Fixture.Read("jobs.json"), ("Link", $"<{page2}>; rel=\"next\""))),
            (page2, () => Json("""{"message":"Server Error"}""", HttpStatusCode.InternalServerError))));

        var result = await api.GetJobsAsync(Repo, 9006, CancellationToken.None);

        Assert.Equal(ApiOutcome.ServerError, result.Outcome);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task jobs的Link繞回已抓過的頁時停止()
    {
        const string page1 = Api + "/repos/octo-org/sample-app/actions/runs/9006/jobs?filter=latest&per_page=100";
        const string page2 = Api + "/repositories/2001/actions/runs/9006/jobs?filter=latest&per_page=100&page=2";
        var (api, handler) = Create(Routes(
            (page1, () => Json(Fixture.Read("jobs.json"), ("Link", $"<{page2}>; rel=\"next\""))),
            (page2, () => Json(Fixture.Read("jobs.json"), ("Link", $"<{page1}>; rel=\"next\"")))));

        var result = await api.GetJobsAsync(Repo, 9006, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(2, handler.Requests.Count);
        // 兩頁內容重複時同一個 job 只留一筆。
        Assert.Equal([7001L, 7002L], result.Value!.Select(job => job.Id));
    }

    [Fact]
    public async Task rate_limit只讀core()
    {
        const string url = Api + "/rate_limit";
        var cache = new FakeCacheStore();
        cache.Put(new HttpCacheEntry(url, "W/\"rl\"", null, null));
        var (api, handler) = Create(Routes((url, () => Json(Fixture.Read("rate-limit.json"), ("Date", Date)))), cache);

        var result = await api.GetRateLimitAsync(CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(new RateLimitSnapshot(5000, 4870, 130, 1792809000, "core", Now), result.Value);
        // 要的是當下的數字，304 沒有用。
        Assert.Null(Assert.Single(handler.Requests).Header("If-None-Match"));
    }

    [Fact]
    public async Task rate_limit沒有core回ParseError()
    {
        const string url = Api + "/rate_limit";
        var (api, _) = Create(Routes((url, () => Json("""{"resources":{"search":{"limit":30}}}"""))));

        var result = await api.GetRateLimitAsync(CancellationToken.None);

        Assert.Equal(ApiOutcome.ParseError, result.Outcome);
    }

    [Fact]
    public async Task GetLatestRelease對應到ReleaseInfo()
    {
        const string htmlUrl = "https://github.com/marvin3991/is-it-green-yet/releases/tag/v0.3.0";
        var (api, handler) = Create(Routes((LatestReleaseUrl, () =>
            Json($$"""{"tag_name":"v0.3.0","html_url":"{{htmlUrl}}","name":"不用的欄位"}""", ("ETag", "W/\"rel\"")))));

        var result = await api.GetLatestReleaseAsync(conditional: true, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(new ReleaseInfo("v0.3.0", htmlUrl), result.Value);
        Assert.Equal(new HttpCacheEntry(LatestReleaseUrl, "W/\"rel\"", null, null), result.CacheEntry);
        Assert.Equal(LatestReleaseUrl, Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task GetLatestRelease缺tag_name回Ok而TagName是null()
    {
        // 需求：200 就算檢查完成；tag 解析不了（包括缺少）當成沒有新版，由 UpdateCheckRules 判斷，這裡不能轉成 ParseError。
        var (api, _) = Create(Routes((LatestReleaseUrl, () => Json("{}", ("ETag", "W/\"rel\"")))));

        var result = await api.GetLatestReleaseAsync(conditional: true, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(new ReleaseInfo(null, null), result.Value);
        Assert.Equal("W/\"rel\"", result.CacheEntry!.ETag);
    }

    [Theory]
    [InlineData("{ 壞掉")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("""{"tag_name":123}""")]
    public async Task GetLatestRelease的JSON壞掉回ParseError不帶快取項(string body)
    {
        var (api, _) = Create(Routes((LatestReleaseUrl, () => Json(body, ("ETag", "W/\"rel\"")))));

        var result = await api.GetLatestReleaseAsync(conditional: true, CancellationToken.None);

        Assert.Equal(ApiOutcome.ParseError, result.Outcome);
        Assert.Null(result.Value);
        Assert.Null(result.CacheEntry);
    }

    [Fact]
    public async Task GetLatestRelease的404原樣傳回()
    {
        var (api, _) = Create(Routes((LatestReleaseUrl, () => Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound))));

        var result = await api.GetLatestReleaseAsync(conditional: true, CancellationToken.None);

        Assert.Equal(ApiOutcome.NotFound, result.Outcome);
        Assert.Null(result.Value);
        Assert.Null(result.CacheEntry);
    }

    [Fact]
    public async Task conditional為false時不帶IfNoneMatch()
    {
        var cache = new FakeCacheStore();
        cache.Put(new HttpCacheEntry(LatestReleaseUrl, "W/\"rel\"", null, null));
        var (api, handler) = Create(Routes((LatestReleaseUrl, () => Json("{}"))), cache);

        await api.GetLatestReleaseAsync(conditional: false, CancellationToken.None);

        Assert.Null(Assert.Single(handler.Requests).Header("If-None-Match"));
        Assert.Empty(cache.Lookups);
    }

    [Fact]
    public async Task conditional為true時帶存下的ETag()
    {
        var cached = new HttpCacheEntry(LatestReleaseUrl, "W/\"rel\"", null, null);
        var cache = new FakeCacheStore();
        cache.Put(cached);
        var (api, handler) = Create(Routes((LatestReleaseUrl, () => FakeHttpHandler.Status(HttpStatusCode.NotModified))), cache);

        var result = await api.GetLatestReleaseAsync(conditional: true, CancellationToken.None);

        Assert.Equal(ApiOutcome.NotModified, result.Outcome);
        Assert.Null(result.Value);
        Assert.Same(cached, result.CacheEntry);
        Assert.Equal("W/\"rel\"", Assert.Single(handler.Requests).Header("If-None-Match"));
    }

    [Fact]
    public async Task _304時Value為null並帶原快取項()
    {
        var cached = new HttpCacheEntry(RunsUrl, "W/\"runs\"", null, [9001, 9002]);
        var cache = new FakeCacheStore();
        cache.Put(cached);
        var (api, handler) = Create(Routes((RunsUrl, () => FakeHttpHandler.Status(HttpStatusCode.NotModified))), cache);

        var result = await api.GetRunsAsync(Repo, CancellationToken.None);

        Assert.Equal(ApiOutcome.NotModified, result.Outcome);
        Assert.Null(result.Value);
        Assert.Same(cached, result.CacheEntry);
        Assert.Equal("W/\"runs\"", Assert.Single(handler.Requests).Header("If-None-Match"));
    }

    [Fact]
    public async Task 錯誤結果原樣傳回()
    {
        const string url = Api + "/repos/octo-org/sample-app/actions/workflows?per_page=100";
        var (api, _) = Create(Routes((url, () => Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound,
            ("x-ratelimit-remaining", "4321")))));

        var result = await api.GetWorkflowsAsync(Repo, CancellationToken.None);

        Assert.Equal(ApiOutcome.NotFound, result.Outcome);
        Assert.Equal(404, result.StatusCode);
        Assert.Equal("Not Found", result.Message);
        Assert.Equal(4321, result.RateLimit?.Remaining);
        Assert.Null(result.Value);
        Assert.Null(result.CacheEntry);
    }

    [Fact]
    public async Task _301的MovedTo會傳回呼叫端()
    {
        const string moved = Api + "/repositories/2001/actions/runs?per_page=30";
        var (api, _) = Create(Routes(
            (RunsUrl, () => FakeHttpHandler.Status(HttpStatusCode.MovedPermanently, new Dictionary<string, string> { ["Location"] = moved })),
            (moved, () => Json(Fixture.Read("runs.json")))));

        var result = await api.GetRunsAsync(Repo, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(new Uri(moved), result.MovedTo);
        Assert.Equal(RunsUrl, result.CacheEntry!.Url);
    }

    [Theory]
    [InlineData("sample-app")]
    [InlineData("octo-org/")]
    [InlineData("octo-org/sample-app/extra")]
    public async Task repo的full_name格式不對時丟出ArgumentException(string fullName)
    {
        var (api, handler) = Create(Routes());

        await Assert.ThrowsAsync<ArgumentException>(() => api.GetRunsAsync(Repo with { FullName = fullName }, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(WriteAction.RerunFailedJobs, "/rerun-failed-jobs")]
    [InlineData(WriteAction.RerunAll, "/rerun")]
    [InlineData(WriteAction.Cancel, "/cancel")]
    [InlineData(WriteAction.ForceCancel, "/force-cancel")]
    public async Task 四個寫入端點的網址與方法(WriteAction action, string suffix)
    {
        var expected = Api + "/repos/octo-org/sample-app/actions/runs/123456789012" + suffix;
        var (api, handler) = Create(Routes((expected, () => FakeHttpHandler.Status(HttpStatusCode.Accepted,
            new Dictionary<string, string> { ["x-ratelimit-remaining"] = "4321" }))));

        var result = await api.WriteAsync(action, Repo, 123456789012, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(4321, result.RateLimit?.Remaining);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(expected, request.Uri.AbsoluteUri);
        Assert.Equal(0, request.ContentLength);
    }

    [Theory]
    [InlineData(WriteAction.RerunFailedJobs, "sample-app")]
    [InlineData(WriteAction.RerunAll, "octo-org/")]
    [InlineData(WriteAction.Cancel, "octo-org/sample-app/extra")]
    [InlineData(WriteAction.ForceCancel, "sample-app")]
    public async Task 寫入時full_name格式不對丟ArgumentException而且沒有請求(WriteAction action, string fullName)
    {
        var (api, handler) = Create(Routes());

        await Assert.ThrowsAsync<ArgumentException>(() => api.WriteAsync(action, Repo with { FullName = fullName }, 9002, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 寫入的錯誤結果原樣傳回()
    {
        const string url = Api + "/repos/octo-org/sample-app/actions/runs/9002/cancel";
        var (api, _) = Create(Routes((url, () => Json("""{"message":"Cannot cancel a workflow run that is completed."}""",
            HttpStatusCode.Conflict, ("x-ratelimit-remaining", "4321")))));

        var result = await api.CancelRunAsync(Repo, 9002, CancellationToken.None);

        Assert.Equal(ApiOutcome.OtherError, result.Outcome);
        Assert.Equal(WriteRejectReason.Conflict, result.WriteRejected);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("Cannot cancel a workflow run that is completed.", result.Message);
        Assert.Equal(4321, result.RateLimit?.Remaining);
        Assert.True(result.Dispatched);
    }

    [Fact]
    public async Task 寫入動作不認得時丟ArgumentOutOfRangeException()
    {
        var (api, handler) = Create(Routes());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => api.WriteAsync((WriteAction)99, Repo, 9002, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    private static (GitHubApi Api, FakeHttpHandler Handler) Create(FakeHttpHandler handler, IHttpCacheStore? cache = null)
    {
        var client = new GitHubClient(handler, cache ?? new FakeCacheStore(), () => Fixture.FakeToken, null,
            new ManualTimeProvider(Now, ManualTimeProvider.Taipei), UserAgent);
        return (new GitHubApi(client), handler);
    }

    /// <summary>依完整網址回應；沒列到的網址直接丟例外，讓測試看出送了不該送的請求。</summary>
    private static FakeHttpHandler Routes(params (string Url, Func<HttpResponseMessage> Respond)[] routes)
    {
        var map = routes.ToDictionary(route => route.Url, route => route.Respond, StringComparer.Ordinal);
        return new FakeHttpHandler(request => map.TryGetValue(request.RequestUri!.AbsoluteUri, out var respond)
            ? respond()
            : throw new InvalidOperationException($"沒有預期的請求：{request.RequestUri.AbsoluteUri}"));
    }

    private static HttpResponseMessage Json(string json, params (string Name, string Value)[] headers) =>
        Json(json, HttpStatusCode.OK, headers);

    private static HttpResponseMessage Json(string json, HttpStatusCode status, params (string Name, string Value)[] headers) =>
        FakeHttpHandler.Json(json, status, headers.ToDictionary(header => header.Name, header => header.Value, StringComparer.OrdinalIgnoreCase));

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    /// <summary>比對 run 的每一個欄位（PullRequestNumbers 是清單，另外逐項比對）。</summary>
    private static void AssertRun(RunInfo expected, RunInfo actual)
    {
        Assert.Equal(expected.PullRequestNumbers, actual.PullRequestNumbers);
        Assert.Equal(expected, actual with { PullRequestNumbers = expected.PullRequestNumbers });
    }

    /// <summary>一筆欄位齊全的 run；<paramref name="without"/> 指定要拿掉的欄位，或把它換成 <paramref name="value"/>。</summary>
    private static string RunJson(long id, string? without = null, string? value = null)
    {
        var fields = new List<(string Name, string Json)>
        {
            ("id", id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("name", "\"CI\""),
            ("display_title", "\"Title\""),
            ("run_number", "5"),
            ("run_attempt", "1"),
            ("event", "\"push\""),
            ("status", "\"completed\""),
            ("conclusion", "\"success\""),
            ("workflow_id", "161335"),
            ("head_branch", "\"main\""),
            ("head_sha", "\"dcf27ad0c1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6\""),
            ("path", "\".github/workflows/ci.yml\""),
            ("run_started_at", "\"2026-10-24T01:00:05Z\""),
            ("created_at", "\"2026-10-24T01:00:00Z\""),
            ("updated_at", "\"2026-10-24T01:05:00Z\""),
            ("html_url", $"\"https://github.com/octo-org/sample-app/actions/runs/{id}\""),
            ("pull_requests", "[]"),
            ("head_repository", """{"id":2001,"full_name":"octo-org/sample-app","html_url":"https://github.com/octo-org/sample-app"}"""),
        };

        var parts = fields
            .Where(field => value is not null || field.Name != without)
            .Select(field => $"\"{field.Name}\":{(value is not null && field.Name == without ? value : field.Json)}");
        return "{" + string.Join(",", parts) + "}";
    }
}
