using System.Net;
using System.Text;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Logging;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.GitHub;

/// <summary>GitHubClient 的寫入請求（第二階段 spec §3.2、§3.3）。</summary>
public class GitHubClientWriteTests
{
    private const string UserAgent = "IsItGreenYet/0.1.0";

    private const string CancelPath = "/repos/octo-org/sample-app/actions/runs/9002/cancel";

    private const string CancelUrl = "https://api.github.com" + CancelPath;

    private const string RerunPath = "/repos/octo-org/sample-app/actions/runs/9004/rerun-failed-jobs";

    private const string RerunUrl = "https://api.github.com" + RerunPath;

    private const string ForceCancelPath = "/repos/octo-org/sample-app/actions/runs/9002/force-cancel";

    private const string ForceCancelUrl = "https://api.github.com" + ForceCancelPath;

    private const string Date = "Sat, 24 Oct 2026 02:07:33 GMT";

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 2, 7, 33, TimeSpan.Zero);

    /// <summary>等待背景請求的上限；遠大於實際所需，只用來攔下卡住的實作。</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task POST帶必要header而且不帶If_None_Match()
    {
        // 快取裡有同一個網址的 ETag，寫入也不能查、不能帶。
        var cache = new FakeCacheStore();
        cache.Put(new HttpCacheEntry(CancelUrl, "W/\"abc123\"", null, null));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Status(HttpStatusCode.Accepted));
        using var client = CreateClient(handler, cache);

        var result = await client.PostAsync(CancelPath, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(CancelUrl, request.Uri.AbsoluteUri);
        Assert.Equal("application/vnd.github+json", request.Header("Accept"));
        Assert.Equal(UserAgent, request.Header("User-Agent"));
        Assert.Equal("2026-03-10", request.Header("X-GitHub-Api-Version"));
        Assert.Equal($"Bearer {Fixture.FakeToken}", request.Header("Authorization"));
        Assert.Null(request.Header("If-None-Match"));
        Assert.Equal(0, request.ContentLength);
        Assert.Empty(cache.Lookups);
        Assert.Null(result.CacheEntry);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task 各種2xx都算受理(HttpStatusCode status)
    {
        // 回應 schema 是空物件，不解析 body：就算 body 不是 JSON 也算受理。
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("<not json>", Encoding.UTF8, "text/plain"),
        });
        using var client = CreateClient(handler);

        var result = await client.PostAsync(RerunPath, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal((int)status, result.StatusCode);
        Assert.Null(result.WriteRejected);
        Assert.Null(result.Value);
        Assert.Null(result.CacheEntry);
        Assert.True(result.Dispatched);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task 任何3xx都不跟隨(HttpStatusCode status)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Status(status, new Dictionary<string, string>
        {
            ["Location"] = "https://api.github.com/repositories/2001/actions/runs/9004/rerun-failed-jobs",
        }));
        using var client = CreateClient(handler);

        var result = await client.PostAsync(RerunPath, CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(ApiOutcome.OtherError, result.Outcome);
        Assert.Equal(WriteRejectReason.Moved, result.WriteRejected);
        Assert.Equal((int)status, result.StatusCode);
        // MovedTo 代表「跟隨過的 301」，Engine 會依它多算一個請求；沒跟隨就不能填。
        Assert.Null(result.MovedTo);
    }

    [Fact]
    public async Task 權限不足的403標成InsufficientPermission並帶需要的權限()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(Fixture.Read("error-not-accessible.json"), HttpStatusCode.Forbidden,
            new Dictionary<string, string>
            {
                ["X-Accepted-GitHub-Permissions"] = "actions=write",
                ["x-ratelimit-remaining"] = "4000",
            }));
        using var client = CreateClient(handler);

        var result = await client.PostAsync(CancelPath, CancellationToken.None);

        Assert.Equal(ApiOutcome.Forbidden, result.Outcome);
        Assert.True(result.InsufficientPermission);
        Assert.Equal(WriteRejectReason.InsufficientPermission, result.WriteRejected);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("Resource not accessible by personal access token", result.Message);
        Assert.Equal("actions=write", result.AcceptedPermissions);
    }

    [Fact]
    public async Task _409是狀態衝突()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"message":"Cannot cancel a workflow run that is completed."}""", HttpStatusCode.Conflict));
        using var client = CreateClient(handler);

        var result = await client.PostAsync(CancelPath, CancellationToken.None);

        Assert.Equal(ApiOutcome.OtherError, result.Outcome);
        Assert.Equal(WriteRejectReason.Conflict, result.WriteRejected);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("Cannot cancel a workflow run that is completed.", result.Message);
    }

    [Fact]
    public async Task _422是無法處理()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"message":"Unable to retry this workflow run."}""", HttpStatusCode.UnprocessableEntity));
        using var client = CreateClient(handler);

        var result = await client.PostAsync(RerunPath, CancellationToken.None);

        Assert.Equal(ApiOutcome.Unprocessable, result.Outcome);
        Assert.Equal(WriteRejectReason.Unprocessable, result.WriteRejected);
        Assert.Equal(422, result.StatusCode);
        Assert.Equal("Unable to retry this workflow run.", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task _400與410不是版本錯誤(HttpStatusCode status)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"message":"Bad request"}""", status));
        using var client = CreateClient(handler);

        var result = await client.PostAsync(RerunPath, CancellationToken.None);

        Assert.Equal(ApiOutcome.OtherError, result.Outcome);
        Assert.NotEqual(ApiOutcome.VersionError, result.Outcome);
        Assert.Equal(WriteRejectReason.Other, result.WriteRejected);
        Assert.Equal("Bad request", result.Message);
    }

    [Fact]
    public async Task 限流時回RateLimited與等待時間()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden,
            new Dictionary<string, string>
            {
                ["x-ratelimit-remaining"] = "0",
                ["x-ratelimit-limit"] = "5000",
                ["x-ratelimit-reset"] = "1792807753",
                ["Date"] = Date,
            }));
        using var client = CreateClient(handler);

        var result = await client.PostAsync(CancelPath, CancellationToken.None);

        Assert.Equal(ApiOutcome.RateLimited, result.Outcome);
        Assert.Equal(WriteRejectReason.RateLimited, result.WriteRejected);
        Assert.Equal(RateLimitKind.Primary, result.LimitKind);
        // reset − Date = 100 秒，加上 RateLimitResetMargin 5 秒。
        Assert.Equal(TimeSpan.FromSeconds(105), result.RetryWait);
        Assert.Equal(0, result.RateLimit?.Remaining);
    }

    [Fact]
    public async Task 逾時回NetworkError而且Dispatched()
    {
        var time = new TimerControlledTimeProvider(Now);
        var handler = new HangingHandler();
        using var client = new GitHubClient(handler, new FakeCacheStore(), () => Fixture.FakeToken, null, time, UserAgent);

        var pending = client.PostAsync(CancelPath, CancellationToken.None);
        await handler.Started.Task.WaitAsync(TestTimeout);
        Assert.Equal([Tuning.HttpTimeout], time.DueTimes);
        time.FireAll();
        var result = await pending.WaitAsync(TestTimeout);

        Assert.Equal(ApiOutcome.NetworkError, result.Outcome);
        Assert.True(result.Dispatched);
        Assert.Null(result.WriteRejected);
        Assert.Equal(0, result.StatusCode);
    }

    [Fact]
    public async Task 送出後呼叫端取消回NetworkError不丟例外()
    {
        using var workspace = new TempWorkspace();
        var time = new ManualTimeProvider(Now, ManualTimeProvider.Taipei);
        var handler = new HangingHandler();
        using var client = new GitHubClient(handler, new FakeCacheStore(), () => Fixture.FakeToken, CreateApiLog(workspace, time), time, UserAgent);
        using var cancellation = new CancellationTokenSource();

        var pending = client.PostAsync(CancelPath, cancellation.Token);
        await handler.Started.Task.WaitAsync(TestTimeout);
        await cancellation.CancelAsync();
        var result = await pending.WaitAsync(TestTimeout);

        // 請求可能已經送到伺服器：Engine 要當成「結果不確定」，不能當成沒送出（第二階段 spec §5.2）。
        Assert.Equal(ApiOutcome.NetworkError, result.Outcome);
        Assert.True(result.Dispatched);
        Assert.Null(result.WriteRejected);
        Assert.DoesNotContain("逾時", result.Message ?? "", StringComparison.Ordinal);
        var line = Assert.Single(ReadApiLog(workspace));
        Assert.Contains("\tPOST\t" + CancelPath + "\t0\t", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 送出前呼叫端取消丟OperationCanceledException而且沒有請求()
    {
        using var workspace = new TempWorkspace();
        var time = new ManualTimeProvider(Now, ManualTimeProvider.Taipei);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Status(HttpStatusCode.Accepted));
        using var client = new GitHubClient(handler, new FakeCacheStore(), () => Fixture.FakeToken, CreateApiLog(workspace, time), time, UserAgent);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostAsync(CancelPath, cancellation.Token));
        Assert.Empty(handler.Requests);
        // 請求沒有離開本機，api.log 也不記。
        Assert.Empty(ReadApiLog(workspace));
    }

    [Fact]
    public async Task 連線失敗回NetworkError而且Dispatched()
    {
        var handler = new FakeHttpHandler(_ =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, $"connection refused（{Fixture.FakeToken}）"));
        using var client = CreateClient(handler);

        var result = await client.PostAsync(CancelPath, CancellationToken.None);

        Assert.Equal(ApiOutcome.NetworkError, result.Outcome);
        Assert.True(result.Dispatched);
        Assert.DoesNotContain(Fixture.FakeToken, result.Message ?? "", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("repos/octo-org/sample-app/actions/runs/9002/cancel")]
    [InlineData("https://api.github.com/repos/octo-org/sample-app/actions/runs/9002/cancel")]
    public async Task 寫入只接受斜線開頭的路徑而且不送請求(string path)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Status(HttpStatusCode.Accepted));
        using var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.PostAsync(path, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task api_log的方法欄是POST而且沒有token()
    {
        using var workspace = new TempWorkspace();
        var time = new ManualTimeProvider(Now, ManualTimeProvider.Taipei);
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            CancelUrl => FakeHttpHandler.Status(HttpStatusCode.Accepted, new Dictionary<string, string>
            {
                ["x-ratelimit-resource"] = "core",
                ["x-ratelimit-remaining"] = "4999",
                ["x-ratelimit-limit"] = "5000",
            }),
            RerunUrl => FakeHttpHandler.Status(HttpStatusCode.MovedPermanently, new Dictionary<string, string>
            {
                ["Location"] = "https://api.github.com/repositories/2001/actions/runs/9004/rerun-failed-jobs",
            }),
            ForceCancelUrl => FakeHttpHandler.Json("""{"message":"Conflict"}""", HttpStatusCode.Conflict),
            _ => throw new HttpRequestException($"connection refused, token {Fixture.FakeToken}"),
        });
        using var client = new GitHubClient(handler, new FakeCacheStore(), () => Fixture.FakeToken, CreateApiLog(workspace, time), time, UserAgent);

        await client.PostAsync(CancelPath, CancellationToken.None);
        await client.PostAsync(RerunPath, CancellationToken.None);
        await client.PostAsync(ForceCancelPath, CancellationToken.None);
        await client.PostAsync("/repos/octo-org/sample-app/actions/runs/9002/rerun", CancellationToken.None);

        var lines = ReadApiLog(workspace);
        Assert.Equal(handler.Requests.Count, lines.Length);
        Assert.Equal(4, lines.Length);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Post, request.Method));
        Assert.All(lines, line => Assert.Equal("POST", line.Split('\t')[1]));
        Assert.Contains("\t" + CancelPath + "\t202\tcore 4999/5000\t", lines[0], StringComparison.Ordinal);
        // 沒有跟隨，下一行就是下一個請求；新位置照樣記下來方便查原因。
        Assert.Contains("\t" + RerunPath + "\t301\t", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("\t→ /repositories/2001/actions/runs/9004/rerun-failed-jobs", lines[1], StringComparison.Ordinal);
        Assert.Contains("\t" + ForceCancelPath + "\t409\t", lines[2], StringComparison.Ordinal);
        Assert.Contains("\t/repos/octo-org/sample-app/actions/runs/9002/rerun\t0\t", lines[3], StringComparison.Ordinal);
        var content = string.Join("\n", lines);
        Assert.DoesNotContain(Fixture.FakeToken, content, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", content, StringComparison.Ordinal);
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
}
