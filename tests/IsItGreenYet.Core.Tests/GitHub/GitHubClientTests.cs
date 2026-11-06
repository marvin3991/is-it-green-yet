using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Logging;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.GitHub;

public class GitHubClientTests
{
    private const string UserAgent = "IsItGreenYet/0.1.0";

    private const string RunsPath = "/repos/octo-org/sample-app/actions/runs?per_page=30";

    private const string RunsUrl = "https://api.github.com/repos/octo-org/sample-app/actions/runs?per_page=30";

    private const string JobLogUrl = "https://api.github.com/repos/octo-org/sample-app/actions/jobs/7002/logs";

    private const string SignedUrl =
        "https://pipelines.actions.githubusercontent.com/logs/7002?sig=FAKESIGNATURE&se=2026-10-24T02%3A08%3A33Z";

    private const string Date = "Sat, 24 Oct 2026 02:07:33 GMT";

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 2, 7, 33, TimeSpan.Zero);

    /// <summary>等待背景請求的上限；遠大於實際所需，只用來攔下卡住的實作。</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task 帶必要header()
    {
        var handler = new FakeHttpHandler(_ => Json("""{"id":1}"""));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/user", request.Uri.AbsoluteUri);
        Assert.Equal("application/vnd.github+json", request.Header("Accept"));
        Assert.Equal(UserAgent, request.Header("User-Agent"));
        Assert.Equal("2026-03-10", request.Header("X-GitHub-Api-Version"));
        Assert.Equal($"Bearer {Fixture.FakeToken}", request.Header("Authorization"));
    }

    [Theory]
    [InlineData("https://api.github.com/user", true)]
    [InlineData("https://API.GitHub.com/user", true)]
    [InlineData("https://api.github.com.example.test/user", false)]
    [InlineData("https://example.test/api.github.com/user", false)]
    [InlineData("https://uploads.github.com/user", false)]
    [InlineData("https://api.github.com:8443/user", false)]
    public async Task 只對api主機帶Authorization(string url, bool expectAuthorization)
    {
        var handler = new FakeHttpHandler(_ => Json("""{"id":1}"""));
        using var client = CreateClient(handler);

        await client.GetAsync<Item>(url);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(expectAuthorization, request.Header("Authorization") is not null);
        // 其他共通 header 不分主機都帶（Global Constraints）。
        Assert.Equal("application/vnd.github+json", request.Header("Accept"));
        Assert.Equal(UserAgent, request.Header("User-Agent"));
        Assert.Equal("2026-03-10", request.Header("X-GitHub-Api-Version"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task 沒有token時不帶Authorization(string? token)
    {
        var handler = new FakeHttpHandler(_ => Json("""{"id":1}"""));
        using var client = new GitHubClient(handler, new FakeCacheStore(), () => token, null, new ManualTimeProvider(Now), UserAgent);

        await client.GetAsync<Item>("/user");

        Assert.Null(Assert.Single(handler.Requests).Header("Authorization"));
    }

    [Fact]
    public async Task 有ETag時帶If_None_Match()
    {
        var cache = new FakeCacheStore();
        cache.Put(new HttpCacheEntry(RunsUrl, "W/\"abc123\"", null, [9001]));
        var handler = new FakeHttpHandler(_ => Status(HttpStatusCode.NotModified));
        using var client = CreateClient(handler, cache);

        await client.GetAsync<Item>(RunsPath);

        // 相對路徑一律以完整網址當快取鍵；存下什麼就送什麼，不改寫。
        Assert.Equal([RunsUrl], cache.Lookups);
        Assert.Equal("W/\"abc123\"", Assert.Single(handler.Requests).Header("If-None-Match"));
    }

    [Fact]
    public async Task 不帶條件或快取沒有ETag時不送If_None_Match()
    {
        var cache = new FakeCacheStore();
        cache.Put(new HttpCacheEntry(RunsUrl, "W/\"abc123\"", null, null));
        cache.Put(new HttpCacheEntry("https://api.github.com/user", null, null, null));
        var handler = new FakeHttpHandler(_ => Json("""{"id":1}"""));
        using var client = CreateClient(handler, cache);

        await client.GetAsync<Item>(RunsPath, conditional: false);
        await client.GetAsync<Item>("/user");

        Assert.All(handler.Requests, request => Assert.Null(request.Header("If-None-Match")));
        // 不帶條件時連快取都不查。
        Assert.Equal(["https://api.github.com/user"], cache.Lookups);
    }

    [Fact]
    public async Task _304回NotModified並帶原快取項()
    {
        var cached = new HttpCacheEntry(RunsUrl, "W/\"abc123\"", "https://api.github.com/repos/octo-org/sample-app/actions/runs?per_page=30&page=2", [9001, 9002]);
        var cache = new FakeCacheStore();
        cache.Put(cached);
        // 304 不保證帶 Link，這裡故意不帶。
        var handler = new FakeHttpHandler(_ => Status(HttpStatusCode.NotModified, ("ETag", "W/\"abc123\"")));
        using var client = CreateClient(handler, cache);

        var result = await client.GetAsync<Item>(RunsPath);

        Assert.Equal(ApiOutcome.NotModified, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(304, result.StatusCode);
        Assert.Same(cached, result.CacheEntry);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task _200回新的快取項含next與ids()
    {
        const string page2 = "https://api.github.com/user/repos?affiliation=owner,organization_member&per_page=100&page=2";
        var handler = new FakeHttpHandler(_ => Json("""[{"id":2001},{"id":2002}]""",
            ("ETag", "W/\"page1v2\""),
            ("Link", $"<{page2}>; rel=\"next\", <https://api.github.com/user/repos?affiliation=owner,organization_member&per_page=100&page=3>; rel=\"last\"")));
        var cache = new FakeCacheStore();
        cache.Put(new HttpCacheEntry("https://api.github.com/user/repos?affiliation=owner,organization_member&per_page=100", "W/\"page1v1\"", null, [2001]));
        using var client = CreateClient(handler, cache);

        var result = await client.GetAsync<List<Item>>(
            "/user/repos?affiliation=owner,organization_member&per_page=100",
            items => items.Select(item => item.Id).ToList());

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal([2001L, 2002L], result.Value!.Select(item => item.Id));
        Assert.NotNull(result.CacheEntry);
        Assert.Equal("https://api.github.com/user/repos?affiliation=owner,organization_member&per_page=100", result.CacheEntry.Url);
        Assert.Equal("W/\"page1v2\"", result.CacheEntry.ETag);
        Assert.Equal(page2, result.CacheEntry.NextUrl);
        Assert.Equal([2001L, 2002L], result.CacheEntry.ItemIds!);
    }

    [Fact]
    public async Task _200沒有ETag與Link時快取項只有網址()
    {
        var handler = new FakeHttpHandler(_ => Json("""{"id":1}"""));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(new HttpCacheEntry("https://api.github.com/user", null, null, null), result.CacheEntry);
    }

    [Fact]
    public async Task 完整網址照原樣當快取鍵()
    {
        const string next = "https://api.github.com/user/repos?affiliation=owner,organization_member&per_page=100&page=2";
        var cache = new FakeCacheStore();
        var handler = new FakeHttpHandler(_ => Json("[]", ("ETag", "\"p2\"")));
        using var client = CreateClient(handler, cache);

        var result = await client.GetAsync<List<Item>>(next, items => items.Select(item => item.Id).ToList());

        Assert.Equal([next], cache.Lookups);
        Assert.Equal(next, result.CacheEntry!.Url);
        Assert.Equal(next, Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task _301照Location重送一次並回報MovedTo()
    {
        const string moved = "https://api.github.com/repositories/2001/actions/runs?per_page=30";
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri == RunsUrl
            ? Redirect(HttpStatusCode.MovedPermanently, moved)
            : Json("""[{"id":9001}]""", ("ETag", "W/\"moved\"")));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<List<Item>>(RunsPath, items => items.Select(item => item.Id).ToList());

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(moved, handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal($"Bearer {Fixture.FakeToken}", handler.Requests[1].Header("Authorization"));
        Assert.Equal(new Uri(moved), result.MovedTo);
        // 不把 Location 存成之後的輪詢網址：快取鍵仍是原本的網址。
        Assert.Equal(RunsUrl, result.CacheEntry!.Url);
        Assert.Equal("W/\"moved\"", result.CacheEntry.ETag);
    }

    [Fact]
    public async Task _301跟隨時沿用If_None_Match且304回原快取項()
    {
        const string moved = "https://api.github.com/repositories/2001/actions/runs?per_page=30";
        var cached = new HttpCacheEntry(RunsUrl, "W/\"abc123\"", null, [9001]);
        var cache = new FakeCacheStore();
        cache.Put(cached);
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri == RunsUrl
            ? Redirect(HttpStatusCode.MovedPermanently, moved)
            : Status(HttpStatusCode.NotModified));
        using var client = CreateClient(handler, cache);

        var result = await client.GetAsync<List<Item>>(RunsPath);

        Assert.Equal(ApiOutcome.NotModified, result.Outcome);
        Assert.Same(cached, result.CacheEntry);
        Assert.Equal(new Uri(moved), result.MovedTo);
        Assert.All(handler.Requests, request => Assert.Equal("W/\"abc123\"", request.Header("If-None-Match")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task 其他轉址照樣跟隨但不回報MovedTo(HttpStatusCode status)
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri == RunsUrl
            ? Redirect(status, "https://api.github.com/repos/octo-org/sample-app/actions/runs?per_page=30&x=1")
            : Json("[]"));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<List<Item>>(RunsPath);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(result.MovedTo);
    }

    [Fact]
    public async Task 相對的Location依請求網址解析()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri == RunsUrl
            ? Redirect(HttpStatusCode.MovedPermanently, "/repositories/2001/actions/runs?per_page=30")
            : Json("[]"));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<List<Item>>(RunsPath);

        Assert.Equal("https://api.github.com/repositories/2001/actions/runs?per_page=30", handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal(new Uri("https://api.github.com/repositories/2001/actions/runs?per_page=30"), result.MovedTo);
    }

    [Fact]
    public async Task 轉到其他主機不帶Authorization()
    {
        var cache = new FakeCacheStore();
        cache.Put(new HttpCacheEntry(RunsUrl, "W/\"abc123\"", null, null));
        var handler = new FakeHttpHandler(request => request.RequestUri!.Host == "api.github.com"
            ? Redirect(HttpStatusCode.TemporaryRedirect, "https://objects.example.test/runs/9001?token=FAKESIGNATURE")
            : Json("[]"));
        using var client = CreateClient(handler, cache);

        var result = await client.GetAsync<List<Item>>(RunsPath);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal($"Bearer {Fixture.FakeToken}", handler.Requests[0].Header("Authorization"));
        Assert.Null(handler.Requests[1].Header("Authorization"));
        // ETag 屬於 api.github.com 的資源，也不送給其他主機。
        Assert.Null(handler.Requests[1].Header("If-None-Match"));
    }

    [Fact]
    public async Task 從其他主機轉回api主機時重新帶Authorization()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            RunsUrl => Redirect(HttpStatusCode.Found, "https://objects.example.test/hop"),
            "https://objects.example.test/hop" => Redirect(HttpStatusCode.Found, "https://api.github.com/repositories/2001/actions/runs"),
            _ => Json("[]"),
        });
        using var client = CreateClient(handler);

        await client.GetAsync<List<Item>>(RunsPath);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Null(handler.Requests[1].Header("Authorization"));
        Assert.Equal($"Bearer {Fixture.FakeToken}", handler.Requests[2].Header("Authorization"));
    }

    [Fact]
    public async Task 超過3次轉址回RedirectError()
    {
        var count = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            count++;
            return Redirect(HttpStatusCode.Found, $"https://api.github.com/hop/{count}");
        });
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(ApiOutcome.RedirectError, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Equal(1 + Tuning.MaxRedirects, handler.Requests.Count);
        Assert.Null(result.CacheEntry);
    }

    [Fact]
    public async Task 剛好3次轉址仍然成功()
    {
        var count = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            count++;
            return count <= Tuning.MaxRedirects
                ? Redirect(HttpStatusCode.Found, $"https://api.github.com/hop/{count}")
                : Json("""{"id":1}""");
        });
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(1 + Tuning.MaxRedirects, handler.Requests.Count);
    }

    [Theory]
    [InlineData("http://api.github.com/user")]
    [InlineData("http://objects.example.test/log?sig=FAKESIGNATURE")]
    [InlineData("ftp://api.github.com/user")]
    public async Task HTTPS轉HTTP回RedirectError(string location)
    {
        var handler = new FakeHttpHandler(_ => Redirect(HttpStatusCode.Found, location));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(ApiOutcome.RedirectError, result.Outcome);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("FAKESIGNATURE", result.Message ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task 轉址沒有Location回RedirectError()
    {
        var handler = new FakeHttpHandler(_ => Status(HttpStatusCode.Found));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(ApiOutcome.RedirectError, result.Outcome);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://api.github.com/user")]
    [InlineData("ftp://api.github.com/user")]
    [InlineData("user")]
    [InlineData("https://")]
    public async Task 不是HTTPS的網址不送出請求(string url)
    {
        var handler = new FakeHttpHandler(_ => Json("""{"id":1}"""));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>(url);

        Assert.Equal(ApiOutcome.OtherError, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 網址不可為空(string? url)
    {
        using var client = CreateClient(new FakeHttpHandler(_ => Json("{}")));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.GetAsync<Item>(url!));
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, "")]
    [InlineData(HttpStatusCode.OK, "")]
    [InlineData(HttpStatusCode.OK, "  \r\n ")]
    public async Task _204回空清單(HttpStatusCode status, string body)
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        using var client = CreateClient(handler);

        var list = await client.GetAsync<List<Item>>(RunsPath, items => items.Select(item => item.Id).ToList());
        var readOnly = await client.GetAsync<IReadOnlyList<Item>>(RunsPath);

        Assert.Equal(ApiOutcome.Ok, list.Outcome);
        Assert.Empty(list.Value!);
        Assert.NotNull(list.CacheEntry);
        Assert.Equal(RunsUrl, list.CacheEntry.Url);
        Assert.Null(list.CacheEntry.ETag);
        Assert.Null(list.CacheEntry.NextUrl);
        Assert.Empty(list.CacheEntry.ItemIds!);
        Assert.Equal(ApiOutcome.Ok, readOnly.Outcome);
        Assert.Empty(readOnly.Value!);
    }

    [Fact]
    public async Task 單一物件遇到空body回ParseError()
    {
        var handler = new FakeHttpHandler(_ => Status(HttpStatusCode.NoContent));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(ApiOutcome.ParseError, result.Outcome);
        Assert.Null(result.CacheEntry);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"id":"not a number"}""")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("<html>oops</html>")]
    public async Task JSON格式錯誤回ParseError且不帶快取項(string body)
    {
        var handler = new FakeHttpHandler(_ => Json(body, ("ETag", "W/\"broken\"")));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(ApiOutcome.ParseError, result.Outcome);
        Assert.Equal(200, result.StatusCode);
        // JSON 錯誤時丟棄回應、不更新 ETag（spec §15）。
        Assert.Null(result.CacheEntry);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task 逾時回NetworkError()
    {
        var time = new TimerControlledTimeProvider(Now);
        var handler = new HangingHandler();
        using var client = new GitHubClient(handler, new FakeCacheStore(), () => Fixture.FakeToken, null, time, UserAgent);

        var pending = client.GetAsync<Item>("/user");
        await handler.Started.Task.WaitAsync(TestTimeout);
        Assert.Equal([Tuning.HttpTimeout], time.DueTimes);
        time.FireAll();
        var result = await pending.WaitAsync(TestTimeout);

        Assert.Equal(ApiOutcome.NetworkError, result.Outcome);
        Assert.Equal(0, result.StatusCode);
    }

    [Fact]
    public async Task 讀取body時逾時也回NetworkError()
    {
        var time = new TimerControlledTimeProvider(Now);
        var body = new HangingBodyStream();
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var client = new GitHubClient(handler, new FakeCacheStore(), () => Fixture.FakeToken, null, time, UserAgent);

        var pending = client.GetAsync<Item>("/user");
        await body.Started.Task.WaitAsync(TestTimeout);
        time.FireAll();
        var result = await pending.WaitAsync(TestTimeout);

        Assert.Equal(ApiOutcome.NetworkError, result.Outcome);
    }

    [Fact]
    public async Task 連線失敗回NetworkError()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath == "/user"
            ? throw new HttpRequestException(HttpRequestError.NameResolutionError, $"No such host（{Fixture.FakeToken}）")
            : throw new IOException("connection reset"));
        using var client = CreateClient(handler);

        var dns = await client.GetAsync<Item>("/user");
        var reset = await client.GetAsync<Item>(RunsPath);

        Assert.Equal(ApiOutcome.NetworkError, dns.Outcome);
        Assert.Equal(ApiOutcome.NetworkError, reset.Outcome);
        // 例外訊息可能帶任何東西，不原樣放進結果。
        Assert.DoesNotContain(Fixture.FakeToken, dns.Message ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task 呼叫端取消時丟出OperationCanceledException()
    {
        var handler = new HangingHandler();
        using var client = new GitHubClient(handler, new FakeCacheStore(), () => Fixture.FakeToken, null, new ManualTimeProvider(Now), UserAgent);
        using var cancellation = new CancellationTokenSource();

        var pending = client.GetAsync<Item>("/user", ct: cancellation.Token);
        await handler.Started.Task.WaitAsync(TestTimeout);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task 呼叫端取消時仍寫一行api_log再丟出例外()
    {
        using var workspace = new TempWorkspace();
        var time = new ManualTimeProvider(Now, ManualTimeProvider.Taipei);
        var handler = new HangingHandler();
        using var client = new GitHubClient(handler, new FakeCacheStore(), () => Fixture.FakeToken, CreateApiLog(workspace, time), time, UserAgent);
        using var cancellation = new CancellationTokenSource();

        var pending = client.GetAsync<Item>("/user", ct: cancellation.Token);
        await handler.Started.Task.WaitAsync(TestTimeout);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestTimeout));
        // 請求已經送出：沒有回應也照樣記一行，狀態碼記 0（spec §14.4 一個請求一行）。
        var line = Assert.Single(ReadApiLog(workspace));
        Assert.Contains("\t/user\t0\t", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 讀取body時呼叫端取消也寫一行api_log()
    {
        using var workspace = new TempWorkspace();
        var time = new ManualTimeProvider(Now, ManualTimeProvider.Taipei);
        var body = new HangingBodyStream();
        var handler = new FakeHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Headers.TryAddWithoutValidation("x-ratelimit-resource", "core");
            return response;
        });
        using var client = new GitHubClient(handler, new FakeCacheStore(), () => Fixture.FakeToken, CreateApiLog(workspace, time), time, UserAgent);
        using var cancellation = new CancellationTokenSource();

        var pending = client.GetAsync<Item>("/user", ct: cancellation.Token);
        await body.Started.Task.WaitAsync(TestTimeout);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestTimeout));
        var line = Assert.Single(ReadApiLog(workspace));
        Assert.Contains("\t/user\t200\tcore\t", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task log下載中途取消時每個請求仍各寫一行api_log()
    {
        using var workspace = new TempWorkspace();
        var time = new ManualTimeProvider(Now, ManualTimeProvider.Taipei);
        var body = new HangingBodyStream();
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri == JobLogUrl
            ? Redirect(HttpStatusCode.Found, SignedUrl)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var client = new GitHubClient(handler, new FakeCacheStore(), () => Fixture.FakeToken, CreateApiLog(workspace, time), time, UserAgent);
        using var cancellation = new CancellationTokenSource();

        var pending = client.DownloadJobLogTailAsync("octo-org/sample-app", 7002, 200, cancellation.Token);
        await body.Started.Task.WaitAsync(TestTimeout);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestTimeout));
        var lines = ReadApiLog(workspace);
        Assert.Equal(handler.Requests.Count, lines.Length);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("\t→ pipelines.actions.githubusercontent.com/logs/7002?***", lines[0], StringComparison.Ordinal);
        Assert.Contains("\tpipelines.actions.githubusercontent.com/logs/7002?***\t200\t", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("FAKESIGNATURE", string.Join("\n", lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task handler丟出非預期例外時仍寫一行api_log再原樣丟出()
    {
        using var workspace = new TempWorkspace();
        var time = new ManualTimeProvider(Now, ManualTimeProvider.Taipei);
        var handler = new FakeHttpHandler(_ => throw new InvalidOperationException("handler 狀態錯誤"));
        using var client = new GitHubClient(handler, new FakeCacheStore(), () => Fixture.FakeToken, CreateApiLog(workspace, time), time, UserAgent);

        // 不屬於連線層錯誤的例外不改成 NetworkError，照樣往外丟。
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync<Item>("/user"));
        var line = Assert.Single(ReadApiLog(workspace));
        Assert.Contains("\t/user\t0\t", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 解析額度header與Date()
    {
        var handler = new FakeHttpHandler(_ => Json("""{"id":1}""",
            ("x-ratelimit-limit", "5000"),
            ("x-ratelimit-remaining", "4870"),
            ("x-ratelimit-used", "130"),
            ("x-ratelimit-reset", "1792809000"),
            ("x-ratelimit-resource", "core"),
            ("Date", Date)));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(new RateLimitSnapshot(5000, 4870, 130, 1792809000, "core", Now), result.RateLimit);
    }

    [Fact]
    public async Task 額度header缺值或格式錯誤時該欄為null()
    {
        var handler = new FakeHttpHandler(_ => Json("""{"id":1}""",
            ("x-ratelimit-limit", "lots"),
            ("x-ratelimit-remaining", "12"),
            ("x-ratelimit-resource", "search")));
        var none = new FakeHttpHandler(_ => Json("""{"id":1}"""));
        using var client = CreateClient(handler);
        using var bare = CreateClient(none);

        var partial = await client.GetAsync<Item>("/user");
        var missing = await bare.GetAsync<Item>("/user");

        Assert.Equal(new RateLimitSnapshot(null, 12, null, null, "search", null), partial.RateLimit);
        Assert.Null(missing.RateLimit);
    }

    [Theory]
    [InlineData("Deprecation", "@1793000000", true)]
    [InlineData("Sunset", "Fri, 10 Mar 2028 00:00:00 GMT", true)]
    [InlineData("X-Other", "1", false)]
    public async Task Deprecation標記(string header, string value, bool expected)
    {
        var handler = new FakeHttpHandler(_ => Json("""{"id":1}""", (header, value)));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(expected, result.Deprecated);
    }

    [Fact]
    public async Task 解析poll_interval與SSO與權限header()
    {
        var handler = new FakeHttpHandler(_ => Json("""{"id":1}""",
            ("x-poll-interval", "60"),
            ("X-GitHub-SSO", "partial-results; organizations=3001"),
            ("X-OAuth-Scopes", "repo, read:org"),
            ("X-Accepted-OAuth-Scopes", "repo"),
            ("X-Accepted-GitHub-Permissions", "actions=read")));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(TimeSpan.FromSeconds(60), result.PollInterval);
        Assert.Equal("partial-results; organizations=3001", result.Sso);
        Assert.Equal("repo, read:org", result.OAuthScopes);
        Assert.Equal("repo", result.AcceptedOAuthScopes);
        Assert.Equal("actions=read", result.AcceptedPermissions);
    }

    [Fact]
    public async Task 權限不足的403帶出message與需要的權限()
    {
        var handler = new FakeHttpHandler(_ => Json(Fixture.Read("error-not-accessible.json"), HttpStatusCode.Forbidden,
            ("X-Accepted-GitHub-Permissions", "pull_requests=read"),
            ("x-ratelimit-remaining", "4000")));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<List<Item>>("/repos/octo-org/sample-app/pulls?state=open&per_page=100");

        Assert.Equal(ApiOutcome.Forbidden, result.Outcome);
        Assert.True(result.InsufficientPermission);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("Resource not accessible by personal access token", result.Message);
        Assert.Equal("pull_requests=read", result.AcceptedPermissions);
        Assert.Null(result.CacheEntry);
    }

    [Fact]
    public async Task 次級限流的訊息回RateLimited()
    {
        var handler = new FakeHttpHandler(_ => Json(Fixture.Read("error-secondary.json"), HttpStatusCode.Forbidden,
            ("x-ratelimit-remaining", "4000")));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(ApiOutcome.RateLimited, result.Outcome);
        Assert.Equal(RateLimitKind.Secondary, result.LimitKind);
        Assert.Null(result.RetryWait);
        Assert.False(result.HasRetryAfter);
    }

    [Fact]
    public async Task 主額度用完回等待時間()
    {
        var handler = new FakeHttpHandler(_ => Json("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden,
            ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-limit", "5000"),
            ("x-ratelimit-reset", "1792807753"),
            ("Date", Date)));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(ApiOutcome.RateLimited, result.Outcome);
        Assert.Equal(RateLimitKind.Primary, result.LimitKind);
        Assert.Equal(TimeSpan.FromSeconds(105), result.RetryWait);
        Assert.Equal("API rate limit exceeded", result.Message);
    }

    [Fact]
    public async Task 錯誤body不是JSON時message為null()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>bad gateway</html>", Encoding.UTF8, "text/html"),
        });
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(ApiOutcome.ServerError, result.Outcome);
        Assert.Equal(502, result.StatusCode);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task 其他主機的錯誤不經過分類()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.Host == "api.github.com"
            ? Redirect(HttpStatusCode.Found, "https://objects.example.test/x")
            : Status(HttpStatusCode.TooManyRequests, ("retry-after", "60")));
        using var client = CreateClient(handler);

        var result = await client.GetAsync<Item>("/user");

        Assert.Equal(ApiOutcome.OtherError, result.Outcome);
        Assert.Equal(429, result.StatusCode);
        Assert.Null(result.LimitKind);
    }

    [Fact]
    public async Task api_log每個請求一行且無token()
    {
        using var workspace = new TempWorkspace();
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(Fixture.FakeToken);
        var time = new ManualTimeProvider(Now, ManualTimeProvider.Taipei);
        var apiLog = new ApiLog(workspace.Paths.LogsDirectory, redactor, time);
        var cache = new FakeCacheStore();
        cache.Put(new HttpCacheEntry(RunsUrl, "W/\"abc123\"", null, null));
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://api.github.com/user" => Json("""{"id":1}""", ("x-ratelimit-resource", "core"), ("x-ratelimit-remaining", "4999"), ("x-ratelimit-limit", "5000")),
            RunsUrl => Status(HttpStatusCode.NotModified),
            "https://api.github.com/user/emails" => Json("""{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized),
            "https://api.github.com/repos/octo-org/sample-app/actions/workflows?per_page=100" =>
                Redirect(HttpStatusCode.MovedPermanently, "https://api.github.com/repositories/2001/actions/workflows?per_page=100"),
            "https://api.github.com/repositories/2001/actions/workflows?per_page=100" => Json("""{"id":2}"""),
            JobLogUrl => Redirect(HttpStatusCode.Found, SignedUrl),
            SignedUrl => Text("line 1\nline 2\n"),
            _ => throw new HttpRequestException($"connection refused, token {Fixture.FakeToken}"),
        });
        using var client = new GitHubClient(handler, cache, () => Fixture.FakeToken, apiLog, time, UserAgent);

        await client.GetAsync<Item>("/user");
        await client.GetAsync<Item>(RunsPath);
        await client.GetAsync<Item>("/user/emails");
        await client.GetAsync<Item>("/repos/octo-org/sample-app/actions/workflows?per_page=100");
        await client.GetAsync<Item>("/rate_limit");
        await client.DownloadJobLogTailAsync("octo-org/sample-app", 7002, 10, CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(workspace.Paths.LogsDirectory, "api.log"));
        Assert.Equal(handler.Requests.Count, lines.Length);
        Assert.Equal(8, lines.Length);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.All(lines, line => Assert.Equal("GET", line.Split('\t')[1]));
        Assert.Contains("\t/user\t200\tcore 4999/5000\t", lines[0], StringComparison.Ordinal);
        Assert.Contains("\t/repos/octo-org/sample-app/actions/runs?per_page=30\t304\t", lines[1], StringComparison.Ordinal);
        Assert.Contains("\t/user/emails\t401\t", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("\t→ /repositories/2001/actions/workflows?per_page=100", lines[3], StringComparison.Ordinal);
        Assert.Contains("\t/rate_limit\t0\t", lines[5], StringComparison.Ordinal);
        Assert.EndsWith("\t→ pipelines.actions.githubusercontent.com/logs/7002?***", lines[6], StringComparison.Ordinal);
        Assert.Contains("\tpipelines.actions.githubusercontent.com/logs/7002?***\t200\t", lines[7], StringComparison.Ordinal);
        var content = string.Join("\n", lines);
        Assert.DoesNotContain(Fixture.FakeToken, content, StringComparison.Ordinal);
        Assert.DoesNotContain("FAKESIGNATURE", content, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task log下載的302不帶token且403不分類()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri == JobLogUrl
            ? Redirect(HttpStatusCode.Found, SignedUrl, ("x-ratelimit-resource", "core"), ("x-ratelimit-remaining", "4998"), ("x-ratelimit-limit", "5000"))
            // 這組 header 若經過 ErrorClassifier 會被當成限流，但下載主機的錯誤不分類。
            : Status(HttpStatusCode.Forbidden, ("retry-after", "60"), ("x-ratelimit-remaining", "0")));
        using var client = CreateClient(handler);

        var (result, lines) = await client.DownloadJobLogTailAsync("octo-org/sample-app", 7002, 200, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal($"Bearer {Fixture.FakeToken}", handler.Requests[0].Header("Authorization"));
        Assert.Equal(SignedUrl, handler.Requests[1].Uri.AbsoluteUri);
        Assert.Null(handler.Requests[1].Header("Authorization"));
        Assert.All(handler.Requests, request => Assert.Null(request.Header("If-None-Match")));
        Assert.Equal(ApiOutcome.OtherError, result.Outcome);
        Assert.Equal(403, result.StatusCode);
        Assert.Null(result.LimitKind);
        Assert.Null(result.RetryWait);
        Assert.False(result.HasRetryAfter);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task log下載只回最後N行並帶API的額度()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri == JobLogUrl
            ? Redirect(HttpStatusCode.Found, SignedUrl, ("x-ratelimit-resource", "core"), ("x-ratelimit-remaining", "4998"), ("x-ratelimit-limit", "5000"))
            : Text("2026-10-24T01:42:05Z step 1\r\n2026-10-24T01:43:00Z step 2\n2026-10-24T01:44:59Z ##[error]Process completed with exit code 1."));
        using var client = CreateClient(handler);

        var (result, lines) = await client.DownloadJobLogTailAsync("octo-org/sample-app", 7002, 2, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(["2026-10-24T01:43:00Z step 2", "2026-10-24T01:44:59Z ##[error]Process completed with exit code 1."], lines);
        Assert.Equal(4998, result.RateLimit?.Remaining);
        Assert.Null(result.Value);
        Assert.Null(result.CacheEntry);
    }

    [Fact]
    public async Task log下載的API錯誤照常分類()
    {
        var handler = new FakeHttpHandler(_ => Json(Fixture.Read("error-not-accessible.json"), HttpStatusCode.Forbidden));
        using var client = CreateClient(handler);

        var (result, lines) = await client.DownloadJobLogTailAsync("octo-org/sample-app", 7002, 200, CancellationToken.None);

        Assert.Equal(ApiOutcome.Forbidden, result.Outcome);
        Assert.True(result.InsufficientPermission);
        Assert.Single(handler.Requests);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task log下載的API直接回200時也取尾端()
    {
        var handler = new FakeHttpHandler(_ => Text("a\nb\nc\n"));
        using var client = CreateClient(handler);

        var (result, lines) = await client.DownloadJobLogTailAsync("octo-org/sample-app", 7002, 2, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(["b", "c"], lines);
    }

    [Fact]
    public async Task log下載的轉址網址是HTTP時回RedirectError()
    {
        var handler = new FakeHttpHandler(_ => Redirect(HttpStatusCode.Found, "http://pipelines.actions.githubusercontent.com/logs/7002?sig=FAKESIGNATURE"));
        using var client = CreateClient(handler);

        var (result, lines) = await client.DownloadJobLogTailAsync("octo-org/sample-app", 7002, 200, CancellationToken.None);

        Assert.Equal(ApiOutcome.RedirectError, result.Outcome);
        Assert.Single(handler.Requests);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task log下載的連線失敗回NetworkError()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri == JobLogUrl
            ? Redirect(HttpStatusCode.Found, SignedUrl)
            : throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));
        using var client = CreateClient(handler);

        var (result, lines) = await client.DownloadJobLogTailAsync("octo-org/sample-app", 7002, 200, CancellationToken.None);

        Assert.Equal(ApiOutcome.NetworkError, result.Outcome);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task log下載的逾時分別用HttpTimeout與LogDownloadTimeout()
    {
        var time = new TimerControlledTimeProvider(Now);
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri == JobLogUrl
            ? Redirect(HttpStatusCode.Found, SignedUrl)
            : Text("x\n"));
        using var client = new GitHubClient(handler, new FakeCacheStore(), () => Fixture.FakeToken, null, time, UserAgent);

        await client.DownloadJobLogTailAsync("octo-org/sample-app", 7002, 200, CancellationToken.None);

        Assert.Equal([Tuning.HttpTimeout, Tuning.LogDownloadTimeout], time.DueTimes);
    }

    [Fact]
    public async Task log下載的網址依repo與job組成()
    {
        var handler = new FakeHttpHandler(_ => Text("x\n"));
        using var client = CreateClient(handler);

        await client.DownloadJobLogTailAsync("fork-user/sample-app", 123456789012, 5, CancellationToken.None);

        Assert.Equal("https://api.github.com/repos/fork-user/sample-app/actions/jobs/123456789012/logs",
            Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("octo-org/sample-app", 0)]
    [InlineData("octo-org/sample-app", -1)]
    [InlineData("sample-app", 10)]
    [InlineData("octo-org/sample-app/extra", 10)]
    [InlineData("/sample-app", 10)]
    [InlineData("", 10)]
    public async Task log下載的參數不合法時丟出例外且不送請求(string repoFullName, int tailLines)
    {
        var handler = new FakeHttpHandler(_ => Text("x\n"));
        using var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            client.DownloadJobLogTailAsync(repoFullName, 7002, tailLines, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Dispose不會釋放外部提供的handler()
    {
        var tracking = new DisposeTrackingHandler(new FakeHttpHandler(_ => Json("""{"id":1}""")));
        var client = new GitHubClient(tracking, new FakeCacheStore(), () => Fixture.FakeToken, null, new ManualTimeProvider(Now), UserAgent);
        await client.GetAsync<Item>("/user");

        client.Dispose();
        client.Dispose();

        Assert.False(tracking.Disposed);
        tracking.Dispose();
    }

    private static ApiLog CreateApiLog(TempWorkspace workspace, TimeProvider time)
    {
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(Fixture.FakeToken);
        return new ApiLog(workspace.Paths.LogsDirectory, redactor, time);
    }

    /// <summary>api.log 的所有行；檔案不存在（一行都沒寫）時回空陣列。</summary>
    private static string[] ReadApiLog(TempWorkspace workspace)
    {
        var path = Path.Combine(workspace.Paths.LogsDirectory, "api.log");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    private static GitHubClient CreateClient(HttpMessageHandler handler, IHttpCacheStore? cache = null) =>
        new(handler, cache ?? new FakeCacheStore(), () => Fixture.FakeToken, null, new ManualTimeProvider(Now, ManualTimeProvider.Taipei), UserAgent);

    private static HttpResponseMessage Json(string json, params (string Name, string Value)[] headers) =>
        FakeHttpHandler.Json(json, HttpStatusCode.OK, ToDictionary(headers));

    private static HttpResponseMessage Json(string json, HttpStatusCode status, params (string Name, string Value)[] headers) =>
        FakeHttpHandler.Json(json, status, ToDictionary(headers));

    private static HttpResponseMessage Status(HttpStatusCode status, params (string Name, string Value)[] headers) =>
        FakeHttpHandler.Status(status, ToDictionary(headers));

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location, params (string Name, string Value)[] headers) =>
        FakeHttpHandler.Status(status, ToDictionary([("Location", location), .. headers]));

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/plain"),
    };

    private static Dictionary<string, string> ToDictionary((string Name, string Value)[] headers) =>
        headers.ToDictionary(header => header.Name, header => header.Value, StringComparer.OrdinalIgnoreCase);

    public sealed class Item
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }
}
