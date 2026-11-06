using System.Globalization;
using IsItGreenYet.Core.GitHub;

namespace IsItGreenYet.Core.Tests.GitHub;

public class ErrorClassifierTests
{
    /// <summary>回應的 Date header：2026-10-24T02:07:33Z。</summary>
    private const string Date = "Sat, 24 Oct 2026 02:07:33 GMT";

    private const long DateEpoch = 1792807653;

    [Theory]
    [InlineData(403, 30, 120, 125)]
    [InlineData(403, 300, 120, 300)]
    [InlineData(429, 30, 120, 125)]
    [InlineData(429, 125, 120, 125)]
    public void retry_after與remaining為0取較晚(int status, int retryAfter, int resetAfterDate, int expectedSeconds)
    {
        var result = ErrorClassifier.Classify(status, Headers(
            ("retry-after", Text(retryAfter)),
            ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-reset", Text(DateEpoch + resetAfterDate)),
            ("date", Date)), null);

        // 有 retry-after 就算次級限流；等待取 max(retry-after, reset − Date + 5 秒)。
        Assert.Equal(
            new ErrorClassification(ApiOutcome.RateLimited, RateLimitKind.Secondary, TimeSpan.FromSeconds(expectedSeconds), true),
            result);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    public void 只有remaining為0算主額度且等到reset減Date加5秒(int status)
    {
        var result = ErrorClassifier.Classify(status, Headers(
            ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-reset", Text(DateEpoch + 100)),
            ("date", Date)), "API rate limit exceeded for user ID 1001.");

        Assert.Equal(
            new ErrorClassification(ApiOutcome.RateLimited, RateLimitKind.Primary, TimeSpan.FromSeconds(105), false),
            result);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(-5)]
    public void reset已過去時等60秒(int resetAfterDate)
    {
        var result = ErrorClassifier.Classify(403, Headers(
            ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-reset", Text(DateEpoch + resetAfterDate)),
            ("date", Date)), null);

        Assert.Equal(
            new ErrorClassification(ApiOutcome.RateLimited, RateLimitKind.Primary, Tuning.MinRateLimitWait, false),
            result);
    }

    [Fact]
    public void reset剛過去但加上緩衝仍為正時照公式算()
    {
        // reset − Date = −4 秒，加上 5 秒緩衝後是 1 秒，> 0 所以不改用 60 秒（spec §6.4 第 1 列字面）。
        var result = ErrorClassifier.Classify(403, Headers(
            ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-reset", Text(DateEpoch - 4)),
            ("date", Date)), null);

        Assert.Equal(TimeSpan.FromSeconds(1), result.Wait);
    }

    [Theory]
    [InlineData(null, "0")]
    [InlineData("not a date", "0")]
    [InlineData(Date, null)]
    [InlineData(Date, "soon")]
    [InlineData(Date, "99999999999999")]
    public void 主額度缺少Date或reset時等60秒(string? date, string? reset)
    {
        var result = ErrorClassifier.Classify(403, Headers(
            ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-reset", reset),
            ("date", date)), null);

        Assert.Equal(
            new ErrorClassification(ApiOutcome.RateLimited, RateLimitKind.Primary, Tuning.MinRateLimitWait, false),
            result);
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("0", 60)]
    public void 只有retry_after算次級並依秒數等待(string retryAfter, int expectedSeconds)
    {
        var result = ErrorClassifier.Classify(403, Headers(
            ("retry-after", retryAfter),
            ("x-ratelimit-remaining", "4000"),
            ("date", Date)), null);

        Assert.Equal(
            new ErrorClassification(ApiOutcome.RateLimited, RateLimitKind.Secondary, TimeSpan.FromSeconds(expectedSeconds), true),
            result);
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("")]
    public void 無法解析的retry_after當成沒有(string retryAfter)
    {
        var result = ErrorClassifier.Classify(403, Headers(("retry-after", retryAfter), ("x-ratelimit-remaining", "10")), null);

        Assert.Equal(new ErrorClassification(ApiOutcome.Forbidden, null, null, false), result);
    }

    [Fact]
    public void _429沒有header算次級()
    {
        var result = ErrorClassifier.Classify(429, Headers(), null);

        // 沒有 retry-after：等待時間交給 Engine 的次級退避等級決定。
        Assert.Equal(new ErrorClassification(ApiOutcome.RateLimited, RateLimitKind.Secondary, null, false), result);
    }

    [Theory]
    [InlineData("You have exceeded a secondary rate limit. Please wait a few minutes before you try again.")]
    [InlineData("SECONDARY RATE LIMIT triggered")]
    [InlineData("You have exceeded a Secondary Rate Limit for this test fixture.")]
    public void 訊息含secondary_rate_limit算次級(string message)
    {
        var result = ErrorClassifier.Classify(403, Headers(("x-ratelimit-remaining", "4000")), message);

        Assert.Equal(new ErrorClassification(ApiOutcome.RateLimited, RateLimitKind.Secondary, null, false), result);
    }

    [Theory]
    [InlineData("Resource not accessible by personal access token")]
    [InlineData("Resource not accessible by integration")]
    public void Resource_not_accessible算權限不足(string message)
    {
        var result = ErrorClassifier.Classify(403, Headers(("x-accepted-github-permissions", "pull_requests=read")), message);

        Assert.Equal(ApiOutcome.Forbidden, result.Outcome);
        Assert.True(result.InsufficientPermission);
        Assert.Null(result.LimitKind);
        Assert.Null(result.Wait);
    }

    [Theory]
    [InlineData("Although you appear to have the correct authorization credentials, the organization has enabled OAuth App access restrictions.")]
    [InlineData("")]
    [InlineData(null)]
    public void 其他403算存取被拒(string? message)
    {
        var result = ErrorClassifier.Classify(403, Headers(("x-ratelimit-remaining", "4000")), message);

        Assert.Equal(new ErrorClassification(ApiOutcome.Forbidden, null, null, false), result);
        Assert.False(result.InsufficientPermission);
    }

    [Theory]
    [InlineData("resource not accessible by integration")]
    [InlineData(" Resource not accessible by integration")]
    public void 權限不足的訊息比對開頭且區分大小寫(string message)
    {
        // spec §6.4 第 3 列寫「以 Resource not accessible by 開頭」，只有第 2 列註明不分大小寫。
        var result = ErrorClassifier.Classify(403, Headers(), message);

        Assert.Equal(ApiOutcome.Forbidden, result.Outcome);
        Assert.False(result.InsufficientPermission);
    }

    [Fact]
    public void 限流header優先於訊息判斷()
    {
        var result = ErrorClassifier.Classify(403, Headers(("retry-after", "45")), "Resource not accessible by integration");

        Assert.Equal(
            new ErrorClassification(ApiOutcome.RateLimited, RateLimitKind.Secondary, TimeSpan.FromSeconds(45), true),
            result);
        Assert.False(result.InsufficientPermission);
    }

    [Fact]
    public void _401()
    {
        Assert.Equal(new ErrorClassification(ApiOutcome.Unauthorized, null, null, false),
            ErrorClassifier.Classify(401, Headers(("x-ratelimit-remaining", "0")), "Bad credentials"));
    }

    [Fact]
    public void _404()
    {
        Assert.Equal(new ErrorClassification(ApiOutcome.NotFound, null, null, false),
            ErrorClassifier.Classify(404, Headers(), "Not Found"));
    }

    [Fact]
    public void _422()
    {
        Assert.Equal(new ErrorClassification(ApiOutcome.Unprocessable, null, null, false),
            ErrorClassifier.Classify(422, Headers(), "Validation Failed"));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(599)]
    public void _5xx(int status)
    {
        Assert.Equal(new ErrorClassification(ApiOutcome.ServerError, null, null, false),
            ErrorClassifier.Classify(status, Headers(("retry-after", "30")), null));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(410)]
    public void _400與410算版本錯誤(int status)
    {
        Assert.Equal(new ErrorClassification(ApiOutcome.VersionError, null, null, false),
            ErrorClassifier.Classify(status, Headers(), "Unsupported 'X-GitHub-Api-Version'"));
    }

    [Theory]
    [InlineData(405)]
    [InlineData(409)]
    [InlineData(451)]
    [InlineData(302)]
    [InlineData(100)]
    public void 其他狀態碼算其他錯誤(int status)
    {
        Assert.Equal(new ErrorClassification(ApiOutcome.OtherError, null, null, false),
            ErrorClassifier.Classify(status, Headers(("retry-after", "30")), null));
    }

    [Theory]
    [InlineData(200, ApiOutcome.Ok)]
    [InlineData(204, ApiOutcome.Ok)]
    [InlineData(304, ApiOutcome.NotModified)]
    public void 成功與304不算錯誤(int status, ApiOutcome expected)
    {
        Assert.Equal(new ErrorClassification(expected, null, null, false),
            ErrorClassifier.Classify(status, Headers(("x-ratelimit-remaining", "0")), null));
    }

    [Fact]
    public void header查詢函式不可為null()
    {
        Assert.Throws<ArgumentNullException>(() => ErrorClassifier.Classify(403, null!, null));
    }

    [Fact]
    public void ClassifyWrite的header查詢函式不可為null()
    {
        Assert.Throws<ArgumentNullException>(() => ErrorClassifier.ClassifyWrite(403, null!, null));
    }

    /// <summary>第二階段 spec §3.3 的每一列；retry-after／remaining 為 null 表示沒有這個 header。</summary>
    [Theory]
    // 第 1 列：2xx。
    [InlineData(200, null, null, null, ApiOutcome.Ok, null)]
    [InlineData(201, null, "0", null, ApiOutcome.Ok, null)]
    [InlineData(202, null, null, null, ApiOutcome.Ok, null)]
    [InlineData(204, null, null, null, ApiOutcome.Ok, null)]
    // 第 2 列：限流，同 P1 §6.4 第 1、2 列。
    [InlineData(403, "30", null, null, ApiOutcome.RateLimited, WriteRejectReason.RateLimited)]
    [InlineData(403, null, "0", null, ApiOutcome.RateLimited, WriteRejectReason.RateLimited)]
    [InlineData(429, null, null, null, ApiOutcome.RateLimited, WriteRejectReason.RateLimited)]
    [InlineData(403, null, "4000", "You have exceeded a secondary rate limit.", ApiOutcome.RateLimited, WriteRejectReason.RateLimited)]
    [InlineData(403, "45", null, "Resource not accessible by integration", ApiOutcome.RateLimited, WriteRejectReason.RateLimited)]
    // 第 3 列：401（remaining=0 也不算限流，同 Classify）。
    [InlineData(401, null, "0", "Bad credentials", ApiOutcome.Unauthorized, WriteRejectReason.Unauthorized)]
    // 第 4、5 列：403 權限不足／其他 403。
    [InlineData(403, null, "4000", "Resource not accessible by personal access token", ApiOutcome.Forbidden, WriteRejectReason.InsufficientPermission)]
    [InlineData(403, null, "4000", "Resource not accessible by integration", ApiOutcome.Forbidden, WriteRejectReason.InsufficientPermission)]
    [InlineData(403, null, "4000", "resource not accessible by integration", ApiOutcome.Forbidden, WriteRejectReason.Forbidden)]
    [InlineData(403, null, "4000", "Must have admin rights to Repository.", ApiOutcome.Forbidden, WriteRejectReason.Forbidden)]
    [InlineData(403, null, null, null, ApiOutcome.Forbidden, WriteRejectReason.Forbidden)]
    // 第 6～8 列。
    [InlineData(404, null, null, "Not Found", ApiOutcome.NotFound, WriteRejectReason.NotFound)]
    [InlineData(409, "30", null, "Cannot cancel a workflow run that is completed.", ApiOutcome.OtherError, WriteRejectReason.Conflict)]
    [InlineData(422, null, null, "Unable to retry this workflow run.", ApiOutcome.Unprocessable, WriteRejectReason.Unprocessable)]
    // 第 9 列：400、410 不當成 API 版本錯誤。
    [InlineData(400, null, null, "Unsupported 'X-GitHub-Api-Version'", ApiOutcome.OtherError, WriteRejectReason.Other)]
    [InlineData(410, null, null, "Unsupported 'X-GitHub-Api-Version'", ApiOutcome.OtherError, WriteRejectReason.Other)]
    // 第 10 列：任何 3xx。
    [InlineData(300, null, null, null, ApiOutcome.OtherError, WriteRejectReason.Moved)]
    [InlineData(301, null, null, null, ApiOutcome.OtherError, WriteRejectReason.Moved)]
    [InlineData(302, null, null, null, ApiOutcome.OtherError, WriteRejectReason.Moved)]
    [InlineData(304, null, null, null, ApiOutcome.OtherError, WriteRejectReason.Moved)]
    [InlineData(307, null, null, null, ApiOutcome.OtherError, WriteRejectReason.Moved)]
    [InlineData(308, null, null, null, ApiOutcome.OtherError, WriteRejectReason.Moved)]
    // 第 11 列：5xx 可能已經生效，不是被拒（Engine 回結果不確定）；其他狀態碼是被拒：其他。
    [InlineData(500, "30", null, null, ApiOutcome.ServerError, null)]
    [InlineData(502, null, null, null, ApiOutcome.ServerError, null)]
    [InlineData(503, null, null, null, ApiOutcome.ServerError, null)]
    [InlineData(504, null, null, null, ApiOutcome.ServerError, null)]
    [InlineData(599, null, null, null, ApiOutcome.ServerError, null)]
    [InlineData(405, null, null, null, ApiOutcome.OtherError, WriteRejectReason.Other)]
    [InlineData(451, null, null, null, ApiOutcome.OtherError, WriteRejectReason.Other)]
    [InlineData(100, null, null, null, ApiOutcome.OtherError, WriteRejectReason.Other)]
    [InlineData(600, null, null, null, ApiOutcome.OtherError, WriteRejectReason.Other)]
    public void ClassifyWrite的每一列(int status, string? retryAfter, string? remaining, string? message,
        ApiOutcome expectedOutcome, WriteRejectReason? expectedReason)
    {
        var result = ErrorClassifier.ClassifyWrite(status, Headers(("retry-after", retryAfter), ("x-ratelimit-remaining", remaining)), message);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedReason, result.WriteRejected);
        Assert.Equal(expectedReason == WriteRejectReason.InsufficientPermission, result.InsufficientPermission);
    }

    [Fact]
    public void ClassifyWrite的限流等待時間同Classify()
    {
        var headers = Headers(
            ("retry-after", "30"),
            ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-reset", Text(DateEpoch + 120)),
            ("date", Date));

        var write = ErrorClassifier.ClassifyWrite(403, headers, null);

        Assert.Equal(ErrorClassifier.Classify(403, headers, null) with { WriteRejected = WriteRejectReason.RateLimited }, write);
        Assert.Equal(TimeSpan.FromSeconds(125), write.Wait);
    }

    [Theory]
    [InlineData(403, "Resource not accessible by integration")]
    [InlineData(404, null)]
    [InlineData(400, null)]
    [InlineData(409, null)]
    public void Classify本身不帶WriteRejected(int status, string? message)
    {
        Assert.Null(ErrorClassifier.Classify(status, Headers(), message).WriteRejected);
    }

    private static Func<string, string?> Headers(params (string Name, string? Value)[] headers)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (value is not null)
            {
                map[name] = value;
            }
        }

        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
