using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>一個假 repo 在 API 上的樣子：workflow、E4 第 1 頁、不在第 1 頁的舊 run、開啟中 PR、job 與 log。</summary>
internal sealed class RepoWorld(JsonObject repo)
{
    public JsonObject Repo { get; } = repo;

    public string FullName => Repo["full_name"]!.GetValue<string>();

    public long Id => Repo["id"]!.GetValue<long>();

    public List<JsonObject> Workflows { get; } = [FakeData.Workflow(repo: repo["full_name"]!.GetValue<string>())];

    /// <summary>E4 第 1 頁。</summary>
    public List<JsonObject> Runs { get; } = [];

    /// <summary>不在 E4 第 1 頁、但 E5、E6、head_sha 補抓查得到的 run。</summary>
    public List<JsonObject> OlderRuns { get; } = [];

    public List<JsonObject> Pulls { get; } = [];

    public Dictionary<long, List<JsonObject>> Jobs { get; } = [];

    public Dictionary<long, string[]> Logs { get; } = [];

    /// <summary>false 時 repo 清單與探測都看不到它，但它自己的端點照常回應（例如失去清單存取）。</summary>
    public bool Listed { get; set; } = true;

    public void SetPushedAt(DateTimeOffset pushedAt) => Repo["pushed_at"] = FakeData.Time(pushedAt);

    public IEnumerable<JsonObject> AllRuns => Runs.Concat(OlderRuns.Where(old => Runs.All(run => RunId(run) != RunId(old))));

    public static long RunId(JsonObject run) => run["id"]!.GetValue<long>();
}

/// <summary>
/// 依網址路由的假 GitHub：回應由 <see cref="RepoWorld"/> 的內容產生，ETag 是內容的雜湊，
/// 內容沒變而請求帶了同一個 ETag 時回 304。可以對特定網址覆寫回應（錯誤、例外）。
/// 只回應手寫的假資料，不會連到任何真實主機。
/// </summary>
internal sealed class FakeGitHub
{
    public const string ApiHost = "api.github.com";
    public const string ApiBase = "https://" + ApiHost;

    /// <summary>job log 的假下載主機（簽名網址）。</summary>
    public const string LogHost = "logs.example.test";

    /// <summary>簽名網址的查詢字串，log 裡絕對不能出現。</summary>
    public const string LogSignature = "SIGNED0000SECRET";

    public const int RateLimit = 5000;

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<OverrideRule> _overrides = [];
    private readonly Dictionary<string, string> _moved = new(StringComparer.Ordinal);
    private readonly List<(string PathAndQuery, int Status)> _responses = [];
    private readonly Dictionary<string, Dictionary<string, string>> _extraHeaders = new(StringComparer.Ordinal);

    /// <summary>用這把 token 送出的 api.github.com 請求，回應帶的 X-OAuth-Scopes；沒有登記的 token 不帶這個 header。</summary>
    private readonly Dictionary<string, string> _scopesByToken = new(StringComparer.Ordinal);

    public FakeGitHub(TimeProvider time)
    {
        _time = time;
        Handler = new FakeHttpHandler(Respond);
    }

    public FakeHttpHandler Handler { get; }

    public JsonObject User { get; set; } = FakeData.User();

    /// <summary>清單 A（affiliation=owner,organization_member）。</summary>
    public List<RepoWorld> Owned { get; } = [];

    /// <summary>清單 B（affiliation=collaborator）。</summary>
    public List<RepoWorld> Collaborated { get; } = [];

    /// <summary>設為 true 時每個請求都丟出連線層錯誤。</summary>
    public bool NetworkDown { get; set; }

    /// <summary>
    /// 設為 true 時照真實的 handler：回應產生完（例如 <see cref="OnRequest"/> 擋住的請求被放行）時，送出用的
    /// CancellationToken 已經取消（用戶端逾時或呼叫端取消），就丟出 <see cref="TaskCanceledException"/>，不回這個回應。
    /// </summary>
    public bool AbortWhenCancelled { get; set; }

    public int RateRemaining { get; set; } = RateLimit - 1;

    /// <summary>每個請求進來時呼叫（在背景迴圈的測試裡用來等請求出現）。</summary>
    public Action<HttpRequestMessage>? OnRequest { get; set; }

    /// <summary>送往 api.github.com 的請求的路徑加查詢字串，依送出順序。</summary>
    public IReadOnlyList<string> ApiRequests =>
        Handler.Requests.Where(r => r.Uri.Host == ApiHost).Select(r => r.Uri.PathAndQuery).ToList();

    /// <summary>送往 api.github.com 的 POST（寫入，第二階段 spec §3.1）的路徑，依送出順序。</summary>
    public IReadOnlyList<string> Posts =>
        Handler.Requests.Where(r => r.Uri.Host == ApiHost && r.Method == HttpMethod.Post).Select(r => r.Uri.PathAndQuery).ToList();

    public IReadOnlyList<(string PathAndQuery, int Status)> Responses
    {
        get
        {
            lock (_gate)
            {
                return _responses.ToList();
            }
        }
    }

    public RepoWorld AddRepo(JsonObject repo, bool collaborator = false)
    {
        var world = new RepoWorld(repo);
        lock (_gate)
        {
            (collaborator ? Collaborated : Owned).Add(world);
        }

        return world;
    }

    public RepoWorld World(string fullName)
    {
        lock (_gate)
        {
            return Owned.Concat(Collaborated).Single(w => w.FullName == fullName);
        }
    }

    public int Count(string pathAndQuery) => ApiRequests.Count(p => p == pathAndQuery);

    /// <summary>repo 改名：清單用新名稱，舊名稱底下的網址回 301 到新名稱。</summary>
    public void Rename(string oldName, string newName)
    {
        lock (_gate)
        {
            var world = Owned.Concat(Collaborated).Single(w => w.FullName == oldName);
            world.Repo["full_name"] = newName;
            world.Repo["name"] = newName.Split('/')[1];
            world.Repo["html_url"] = "https://github.com/" + newName;
            _moved[oldName] = newName;
        }
    }

    /// <summary>這個路徑（含查詢字串）改用 <paramref name="respond"/> 回應，最多 <paramref name="times"/> 次。</summary>
    public void Override(string pathAndQuery, Func<HttpRequestMessage, HttpResponseMessage> respond, int times = int.MaxValue)
    {
        lock (_gate)
        {
            _overrides.Add(new OverrideRule(pathAndQuery, respond, times));
        }
    }

    /// <summary>這個路徑的回應另外加上 header（例如 x-poll-interval）。</summary>
    public void AddHeader(string pathAndQuery, string name, string value)
    {
        lock (_gate)
        {
            if (!_extraHeaders.TryGetValue(pathAndQuery, out var headers))
            {
                headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _extraHeaders[pathAndQuery] = headers;
            }

            headers[name] = value;
        }
    }

    /// <summary>用 <paramref name="token"/> 送出的 api.github.com 請求，回應都帶 <c>X-OAuth-Scopes: <paramref name="scopes"/></c>（classic token 的樣子）。</summary>
    public void SetOAuthScopes(string token, string scopes)
    {
        lock (_gate)
        {
            _scopesByToken[token] = scopes;
        }
    }

    public void RemoveOverrides(string pathAndQuery)
    {
        lock (_gate)
        {
            _overrides.RemoveAll(o => o.PathAndQuery == pathAndQuery);
        }
    }

    /// <summary>api.github.com 的錯誤回應：JSON message 加上額度與 Date header，<paramref name="headers"/> 會蓋過預設值。</summary>
    public HttpResponseMessage Error(HttpStatusCode status, string message, params (string Name, string Value)[] headers)
    {
        var all = StandardHeaders();
        foreach (var (name, value) in headers)
        {
            all[name] = value;
        }

        return FakeHttpHandler.Json(new JsonObject { ["message"] = message }.ToJsonString(), status, all);
    }

    public long ResetEpoch(TimeSpan fromNow) => (_time.GetUtcNow() + fromNow).ToUnixTimeSeconds();

    private HttpResponseMessage Respond(HttpRequestMessage request, CancellationToken ct)
    {
        OnRequest?.Invoke(request);
        if (NetworkDown)
        {
            throw new HttpRequestException("模擬的連線失敗");
        }

        var uri = request.RequestUri!;
        var key = uri.PathAndQuery;
        Func<HttpRequestMessage, HttpResponseMessage>? overridden = null;

        lock (_gate)
        {
            var match = _overrides.FirstOrDefault(o => o.PathAndQuery == key && o.Remaining > 0 && uri.Host == ApiHost);
            if (match is not null)
            {
                match.Remaining--;
                overridden = match.Respond;
            }
        }

        var response = overridden is not null
            ? overridden(request)
            : uri.Host == LogHost ? LogResponse(uri) : Route(request, uri);

        if (AbortWhenCancelled && ct.IsCancellationRequested)
        {
            response.Dispose();
            throw new TaskCanceledException("模擬的請求在回應之前被取消");
        }

        lock (_gate)
        {
            _responses.Add((key, (int)response.StatusCode));
            if (_extraHeaders.TryGetValue(key, out var extra))
            {
                foreach (var (name, value) in extra)
                {
                    response.Headers.TryAddWithoutValidation(name, value);
                }
            }

            if (uri.Host == ApiHost
                && request.Headers.TryGetValues("Authorization", out var authorization)
                && authorization.FirstOrDefault()?.Split(' ', 2) is ["Bearer", var token]
                && _scopesByToken.TryGetValue(token, out var scopes))
            {
                response.Headers.TryAddWithoutValidation("X-OAuth-Scopes", scopes);
            }
        }

        return response;
    }

    private HttpResponseMessage Route(HttpRequestMessage request, Uri uri)
    {
        lock (_gate)
        {
            var path = uri.AbsolutePath;
            var query = ParseQuery(uri.Query);

            if (path == "/user")
            {
                return Json(request, User);
            }

            if (path == "/rate_limit")
            {
                var core = new JsonObject
                {
                    ["limit"] = RateLimit,
                    ["remaining"] = RateRemaining,
                    ["used"] = RateLimit - RateRemaining,
                    ["reset"] = ResetEpoch(TimeSpan.FromHours(1)),
                };
                return Json(request, new JsonObject { ["resources"] = new JsonObject { ["core"] = core } }, conditional: false);
            }

            if (path == "/user/repos")
            {
                var list = query.GetValueOrDefault("affiliation") == "collaborator" ? Collaborated : Owned;
                var ordered = list.Where(w => w.Listed);
                if (query.GetValueOrDefault("sort") == "pushed")
                {
                    ordered = ordered.OrderByDescending(w => w.Repo["pushed_at"]?.GetValue<string>(), StringComparer.Ordinal);
                }

                return RepoPage(request, uri, query, ordered.ToList());
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3 && segments[0] == "repos")
            {
                return RepoRoute(request, uri, segments, query);
            }

            return NotFound();
        }
    }

    /// <summary>repo 清單的一頁：照 per_page 與 page 切，後面還有 repo 時帶 Link next（spec §4.1 分頁）。</summary>
    private HttpResponseMessage RepoPage(HttpRequestMessage request, Uri uri, Dictionary<string, string> query, List<RepoWorld> repos)
    {
        var perPage = int.Parse(query["per_page"], CultureInfo.InvariantCulture);
        var page = int.Parse(query.GetValueOrDefault("page") ?? "1", CultureInfo.InvariantCulture);
        var response = Json(request, new JsonArray([.. repos.Skip((page - 1) * perPage).Take(perPage).Select(w => Clone(w.Repo))]));
        if (page * perPage < repos.Count)
        {
            var firstPage = page == 1 ? uri.PathAndQuery : uri.PathAndQuery[..uri.PathAndQuery.LastIndexOf("&page=", StringComparison.Ordinal)];
            var next = string.Create(CultureInfo.InvariantCulture, $"{ApiBase}{firstPage}&page={page + 1}");
            response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
        }

        return response;
    }

    private HttpResponseMessage RepoRoute(HttpRequestMessage request, Uri uri, string[] segments, Dictionary<string, string> query)
    {
        var fullName = Uri.UnescapeDataString(segments[1]) + "/" + Uri.UnescapeDataString(segments[2]);
        if (_moved.TryGetValue(fullName, out var movedTo))
        {
            // 改名後舊網址回 301 到新名稱（spec §4.1 轉址）。
            var location = ApiBase + "/repos/" + movedTo + uri.PathAndQuery[("/repos/" + fullName).Length..];
            var headers = StandardHeaders();
            headers["Location"] = location;
            return FakeHttpHandler.Status(HttpStatusCode.MovedPermanently, headers);
        }

        var world = Owned.Concat(Collaborated).SingleOrDefault(w => w.FullName == fullName);
        if (world is null)
        {
            return NotFound();
        }

        var rest = segments[3..];
        if (request.Method == HttpMethod.Post)
        {
            return WriteRoute(world, rest);
        }

        switch (rest)
        {
            case ["actions", "workflows"]:
                return Json(request, new JsonObject
                {
                    ["total_count"] = world.Workflows.Count,
                    ["workflows"] = new JsonArray([.. world.Workflows.Select(Clone)]),
                });

            case ["actions", "runs"]:
                var runs = query.TryGetValue("head_sha", out var sha)
                    ? world.AllRuns.Where(run => run["head_sha"]!.GetValue<string>() == sha).ToList()
                    : world.Runs;
                return RunsPage(request, runs);

            case ["actions", "workflows", var workflowId, "runs"]:
                var branch = query.GetValueOrDefault("branch");
                var seeded = world.AllRuns
                    .Where(run => run["workflow_id"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture) == workflowId
                        && run["head_branch"]?.GetValue<string>() == branch)
                    .OrderByDescending(run => run["run_number"]!.GetValue<int>())
                    .Take(int.Parse(query.GetValueOrDefault("per_page") ?? "30", CultureInfo.InvariantCulture))
                    .ToList();
                return RunsPage(request, seeded);

            case ["actions", "runs", var runId]:
                var single = world.AllRuns.FirstOrDefault(run => RepoWorld.RunId(run).ToString(CultureInfo.InvariantCulture) == runId);
                return single is null ? NotFound() : Json(request, Clone(single));

            case ["actions", "runs", var jobsRunId, "jobs"]:
                var jobs = world.Jobs.GetValueOrDefault(long.Parse(jobsRunId, CultureInfo.InvariantCulture)) ?? [];
                return Json(request, new JsonObject
                {
                    ["total_count"] = jobs.Count,
                    ["jobs"] = new JsonArray([.. jobs.Select(Clone)]),
                }, conditional: false);

            case ["actions", "jobs", var jobId, "logs"]:
                if (!world.Logs.ContainsKey(long.Parse(jobId, CultureInfo.InvariantCulture)))
                {
                    return NotFound();
                }

                var redirect = StandardHeaders();
                redirect["Location"] = $"https://{LogHost}/jobs/{jobId}?sig={LogSignature}&se=2026-10-24T02%3A01%3A00Z";
                return FakeHttpHandler.Status(HttpStatusCode.Found, redirect);

            case ["pulls"]:
                return Json(request, new JsonArray([.. world.Pulls.Select(Clone)]));

            default:
                return NotFound();
        }
    }

    /// <summary>
    /// 四個寫入端點（第二階段 spec §3.1）的 POST：run 存在時重跑回 201、取消回 202（空物件），不改 run 的內容；
    /// 其他路徑的 POST 與不存在的 run 回 404。同樣的路徑用 GET 送也是 404（不在讀取的路由裡）。
    /// </summary>
    private HttpResponseMessage WriteRoute(RepoWorld world, string[] rest)
    {
        if (rest is not ["actions", "runs", var runId, var action])
        {
            return NotFound();
        }

        var status = action switch
        {
            "rerun-failed-jobs" or "rerun" => HttpStatusCode.Created,
            "cancel" or "force-cancel" => HttpStatusCode.Accepted,
            _ => (HttpStatusCode?)null,
        };
        var exists = world.AllRuns.Any(run => RepoWorld.RunId(run).ToString(CultureInfo.InvariantCulture) == runId);
        return status is { } accepted && exists
            ? FakeHttpHandler.Json("{}", accepted, StandardHeaders())
            : NotFound();
    }

    private HttpResponseMessage LogResponse(Uri uri)
    {
        lock (_gate)
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is ["jobs", var id] && long.TryParse(id, CultureInfo.InvariantCulture, out var jobId))
            {
                var lines = Owned.Concat(Collaborated).Select(w => w.Logs.GetValueOrDefault(jobId)).FirstOrDefault(l => l is not null);
                if (lines is not null)
                {
                    var text = string.Join("\n", lines) + "\n";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
        }
    }

    private HttpResponseMessage RunsPage(HttpRequestMessage request, IReadOnlyCollection<JsonObject> runs) =>
        Json(request, new JsonObject
        {
            ["total_count"] = runs.Count,
            ["workflow_runs"] = new JsonArray([.. runs.Select(Clone)]),
        });

    private HttpResponseMessage NotFound() => Error(HttpStatusCode.NotFound, "Not Found");

    /// <summary>200（帶 ETag）；請求帶的 If-None-Match 和目前內容的 ETag 相同時回 304。</summary>
    private HttpResponseMessage Json(HttpRequestMessage request, JsonNode body, bool conditional = true)
    {
        var text = body.ToJsonString();
        var etag = "W/\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16] + "\"";
        var headers = StandardHeaders();
        headers["ETag"] = etag;

        if (conditional
            && request.Headers.TryGetValues("If-None-Match", out var values)
            && values.Any(value => value == etag))
        {
            return FakeHttpHandler.Status(HttpStatusCode.NotModified, headers);
        }

        return FakeHttpHandler.Json(text, HttpStatusCode.OK, headers);
    }

    private Dictionary<string, string> StandardHeaders()
    {
        var now = _time.GetUtcNow();
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-ratelimit-limit"] = RateLimit.ToString(CultureInfo.InvariantCulture),
            ["x-ratelimit-remaining"] = RateRemaining.ToString(CultureInfo.InvariantCulture),
            ["x-ratelimit-used"] = (RateLimit - RateRemaining).ToString(CultureInfo.InvariantCulture),
            ["x-ratelimit-reset"] = ResetEpoch(TimeSpan.FromHours(1)).ToString(CultureInfo.InvariantCulture),
            ["x-ratelimit-resource"] = "core",
            ["Date"] = now.ToString("r", CultureInfo.InvariantCulture),
        };
    }

    private static JsonObject Clone(JsonObject value) => (JsonObject)value.DeepClone();

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            result[Uri.UnescapeDataString(pieces[0])] = pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
        }

        return result;
    }

    private sealed class OverrideRule(string pathAndQuery, Func<HttpRequestMessage, HttpResponseMessage> respond, int remaining)
    {
        public string PathAndQuery { get; } = pathAndQuery;

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; } = respond;

        public int Remaining { get; set; } = remaining;
    }
}
