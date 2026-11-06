namespace IsItGreenYet.Core.GitHub;

/// <summary>一個 API 請求的結果分類，每一種對應 spec §6.4／§15 的一列。</summary>
public enum ApiOutcome
{
    Ok,
    NotModified,
    Unauthorized,
    RateLimited,
    Forbidden,
    NotFound,
    Unprocessable,
    ServerError,
    NetworkError,
    RedirectError,
    ParseError,
    VersionError,
    OtherError,
}

public enum RateLimitKind
{
    Primary,
    Secondary,
}

/// <summary>回應的 x-ratelimit-* 與 Date header。ResetEpoch 是 UTC epoch 秒。</summary>
public sealed record RateLimitSnapshot(
    int? Limit,
    int? Remaining,
    int? Used,
    long? ResetEpoch,
    string? Resource,
    DateTimeOffset? ServerDate);

/// <summary>
/// API 請求結果。200 時 <see cref="CacheEntry"/> 是要和處理結果在同一個交易寫入的新快取項；
/// 304 時是快取中原有的項目。
/// </summary>
public sealed class ApiResult<T>
{
    public required ApiOutcome Outcome { get; init; }

    public T? Value { get; init; }

    public int StatusCode { get; init; }

    public RateLimitSnapshot? RateLimit { get; init; }

    public HttpCacheEntry? CacheEntry { get; init; }

    /// <summary>過程中遇到 301 時的 Location（只供重抓 repo 清單判斷用，不保存成輪詢網址）。</summary>
    public Uri? MovedTo { get; init; }

    public RateLimitKind? LimitKind { get; init; }

    /// <summary>限流時建議暫停的時間（已套用 retry-after、reset−Date＋margin、MinRateLimitWait 規則）。</summary>
    public TimeSpan? RetryWait { get; init; }

    public bool HasRetryAfter { get; init; }

    /// <summary>
    /// Forbidden 屬於 spec §6.4 第 3 列「權限不足」（Resource not accessible by…）；
    /// <see langword="false"/> 的 Forbidden 是第 4 列「存取被拒」，Engine 用來判斷第 5 列。
    /// </summary>
    public bool InsufficientPermission { get; init; }

    /// <summary>X-Accepted-GitHub-Permissions。</summary>
    public string? AcceptedPermissions { get; init; }

    /// <summary>X-OAuth-Scopes。</summary>
    public string? OAuthScopes { get; init; }

    /// <summary>X-Accepted-OAuth-Scopes。</summary>
    public string? AcceptedOAuthScopes { get; init; }

    /// <summary>錯誤回應 body 的 message。</summary>
    public string? Message { get; init; }

    /// <summary>x-poll-interval。</summary>
    public TimeSpan? PollInterval { get; init; }

    /// <summary>回應帶 Deprecation 或 Sunset header。</summary>
    public bool Deprecated { get; init; }

    /// <summary>X-GitHub-SSO。</summary>
    public string? Sso { get; init; }

    /// <summary>
    /// 寫入被伺服器拒絕的原因（第二階段 spec §3.3）；只有 <see cref="GitHubClient.PostAsync"/> 收到 2xx、5xx 以外的回應時有值。
    /// 2xx、5xx（可能已經生效，不是被拒）、連線層錯誤與所有讀取的結果都是 <see langword="null"/>。
    /// </summary>
    public WriteRejectReason? WriteRejected { get; init; }

    /// <summary>
    /// 請求已交給 handler（之後才失敗或被取消也算）。寫入的 NetworkError 帶這個旗標，代表請求可能已經生效（結果不確定，第二階段 spec §5.2）。
    /// </summary>
    public bool Dispatched { get; init; }

    public bool IsSuccess => Outcome is ApiOutcome.Ok or ApiOutcome.NotModified;
}
