using System.Globalization;

namespace IsItGreenYet.Core.GitHub;

/// <summary>
/// 錯誤分類的結果。<see cref="Wait"/> 只在限流且算得出等待時間時有值；
/// 次級限流沒有 retry-after 時為 <see langword="null"/>，由 Engine 的退避等級決定。
/// </summary>
public sealed record ErrorClassification(ApiOutcome Outcome, RateLimitKind? LimitKind, TimeSpan? Wait, bool HasRetryAfter)
{
    /// <summary>
    /// 403 屬於 spec §6.4 第 3 列「權限不足」（body message 以 Resource not accessible by 開頭）。
    /// <see langword="false"/> 的 Forbidden 是第 4 列「存取被拒」，Engine 用它判斷第 5 列（多個 repo 同時 403）。
    /// </summary>
    public bool InsufficientPermission { get; init; }

    /// <summary>
    /// 寫入被拒的原因；只有 <see cref="ErrorClassifier.ClassifyWrite"/> 會填，2xx 與 5xx（不是被拒，Engine 回結果不確定）為 <see langword="null"/>。
    /// </summary>
    public WriteRejectReason? WriteRejected { get; init; }
}

/// <summary>
/// 依狀態碼、header 與 body message 分類錯誤（spec §6.4 錯誤分類表第 1～4 列）。
/// 只用在 api.github.com 的回應；第 5 列（多個 repo 同時 403）由 Engine 處理。
/// </summary>
public static class ErrorClassifier
{
    // spec §6.4 第 2 列：不分大小寫比對。
    private const string SecondaryRateLimitText = "secondary rate limit";

    // spec §6.4 第 3 列：以這段文字開頭（spec 沒寫不分大小寫，照字面區分）。
    private const string NotAccessiblePrefix = "Resource not accessible by";

    private const int Unauthorized = 401;
    private const int Forbidden = 403;
    private const int NotFound = 404;

    // 第二階段 spec §3.3 第 7 列：取消的端點另列 409（spec §3.1）。
    private const int Conflict = 409;
    private const int Unprocessable = 422;
    private const int TooManyRequests = 429;
    private const int NotModified = 304;

    // spec §4.1：不存在的版本回 400、已停止支援的版本回 410。
    private const int BadRequest = 400;
    private const int Gone = 410;

    private const int SuccessMin = 200;
    private const int SuccessMax = 299;
    private const int RedirectionMin = 300;
    private const int RedirectionMax = 399;
    private const int ServerErrorMin = 500;
    private const int ServerErrorMax = 599;

    /// <param name="header">依名稱（不分大小寫）取 header 值，沒有回 null。</param>
    public static ErrorClassification Classify(int statusCode, Func<string, string?> header, string? bodyMessage)
    {
        ArgumentNullException.ThrowIfNull(header);

        switch (statusCode)
        {
            case >= SuccessMin and <= SuccessMax:
                return Simple(ApiOutcome.Ok);
            case NotModified:
                return Simple(ApiOutcome.NotModified);
            case Forbidden or TooManyRequests:
                return ClassifyLimitedOrForbidden(statusCode, header, bodyMessage);
            case Unauthorized:
                return Simple(ApiOutcome.Unauthorized);
            case NotFound:
                return Simple(ApiOutcome.NotFound);
            case Unprocessable:
                return Simple(ApiOutcome.Unprocessable);
            case BadRequest or Gone:
                return Simple(ApiOutcome.VersionError);
            case >= ServerErrorMin and <= ServerErrorMax:
                return Simple(ApiOutcome.ServerError);
            default:
                return Simple(ApiOutcome.OtherError);
        }
    }

    /// <summary>
    /// 寫入回應的分類（第二階段 spec §3.3），依表的順序判斷。限流沿用 <see cref="Classify"/> 的判斷（spec §6.4 第 1、2 列）；
    /// 400／410 不當成 API 版本錯誤、任何 3xx 都是「位置改變」，這兩點和輪詢不同。
    /// </summary>
    /// <param name="header">依名稱（不分大小寫）取 header 值，沒有回 null。</param>
    public static ErrorClassification ClassifyWrite(int statusCode, Func<string, string?> header, string? bodyMessage)
    {
        ArgumentNullException.ThrowIfNull(header);

        switch (statusCode)
        {
            case >= SuccessMin and <= SuccessMax:
                return Simple(ApiOutcome.Ok);
            case Forbidden or TooManyRequests:
                // 第 2、4、5 列：先判斷限流，再分權限不足與其他 403（同 spec §6.4 第 1～4 列的順序）。
                var polling = ClassifyLimitedOrForbidden(statusCode, header, bodyMessage);
                return polling with
                {
                    WriteRejected = polling.Outcome == ApiOutcome.RateLimited ? WriteRejectReason.RateLimited
                        : polling.InsufficientPermission ? WriteRejectReason.InsufficientPermission
                        : WriteRejectReason.Forbidden,
                };
            case Unauthorized:
                return Rejected(ApiOutcome.Unauthorized, WriteRejectReason.Unauthorized);
            case NotFound:
                return Rejected(ApiOutcome.NotFound, WriteRejectReason.NotFound);
            case Conflict:
                return Rejected(ApiOutcome.OtherError, WriteRejectReason.Conflict);
            case Unprocessable:
                return Rejected(ApiOutcome.Unprocessable, WriteRejectReason.Unprocessable);
            case BadRequest or Gone:
                // 第 9 列：寫入回 400／410 不代表版本錯誤，不停止輪詢。
                return Rejected(ApiOutcome.OtherError, WriteRejectReason.Other);
            case >= RedirectionMin and <= RedirectionMax:
                return Rejected(ApiOutcome.OtherError, WriteRejectReason.Moved);
            case >= ServerErrorMin and <= ServerErrorMax:
                // 第 11 列：5xx 不是被拒，請求可能已經生效（§3.2），Engine 回結果不確定。
                return Simple(ApiOutcome.ServerError);
            default:
                return Rejected(ApiOutcome.OtherError, WriteRejectReason.Other);
        }
    }

    /// <summary>從 retry-after header 取出秒數；不是非負整數就當成沒有（GitHub 文件寫的是秒數）。</summary>
    internal static TimeSpan? ParseRetryAfter(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;

    /// <summary>HTTP 的 Date header（RFC 9110 的 IMF-fixdate，例如 <c>Sat, 24 Oct 2026 02:07:33 GMT</c>）。</summary>
    internal static DateTimeOffset? ParseHttpDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(value.Trim(), "r", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
    }

    /// <summary>x-ratelimit-reset 的 epoch 秒；超出 DateTimeOffset 範圍的值當成沒有。</summary>
    internal static DateTimeOffset? ParseEpoch(string? value)
    {
        if (!long.TryParse(value, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        var min = DateTimeOffset.MinValue.ToUnixTimeSeconds();
        var max = DateTimeOffset.MaxValue.ToUnixTimeSeconds();
        return seconds < min || seconds > max ? null : DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static ErrorClassification ClassifyLimitedOrForbidden(int statusCode, Func<string, string?> header, string? bodyMessage)
    {
        // 第 1 列：有 retry-after 或 x-ratelimit-remaining == 0。
        var retryAfter = ParseRetryAfter(header("retry-after"));
        var exhausted = int.TryParse(header("x-ratelimit-remaining"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining)
            && remaining == 0;

        if (retryAfter is not null || exhausted)
        {
            // 有 retry-after 算次級；只有 remaining=0 算主額度。
            var kind = retryAfter is not null ? RateLimitKind.Secondary : RateLimitKind.Primary;
            return new ErrorClassification(ApiOutcome.RateLimited, kind, LimitWait(retryAfter, exhausted, header), retryAfter is not null);
        }

        // 第 2 列：429（沒有上述 header），或 403 的訊息含 secondary rate limit。
        if (statusCode == TooManyRequests
            || (bodyMessage?.Contains(SecondaryRateLimitText, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return new ErrorClassification(ApiOutcome.RateLimited, RateLimitKind.Secondary, null, false);
        }

        // 第 3 列在第 4 列之前，否則權限錯誤永遠走不到（spec §6.4）。
        var insufficient = bodyMessage?.StartsWith(NotAccessiblePrefix, StringComparison.Ordinal) ?? false;
        return Simple(ApiOutcome.Forbidden) with { InsufficientPermission = insufficient };
    }

    /// <summary>
    /// 兩個 header 都有時取較晚者：max(retry-after, reset − Date + RateLimitResetMargin)；
    /// 算不出來或 ≤ 0 時用 MinRateLimitWait（spec §6.4 第 1 列）。用回應的 Date，不用本機牆鐘（spec §3.1）。
    /// </summary>
    private static TimeSpan LimitWait(TimeSpan? retryAfter, bool exhausted, Func<string, string?> header)
    {
        TimeSpan? primary = null;
        if (exhausted
            && ParseEpoch(header("x-ratelimit-reset")) is { } reset
            && ParseHttpDate(header("date")) is { } date)
        {
            primary = reset - date + Tuning.RateLimitResetMargin;
        }

        var wait = (retryAfter, primary) switch
        {
            ({ } a, { } b) => a > b ? a : b,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => TimeSpan.Zero,
        };

        return wait > TimeSpan.Zero ? wait : Tuning.MinRateLimitWait;
    }

    private static ErrorClassification Simple(ApiOutcome outcome) => new(outcome, null, null, false);

    private static ErrorClassification Rejected(ApiOutcome outcome, WriteRejectReason reason) =>
        Simple(outcome) with { WriteRejected = reason };
}
