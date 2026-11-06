using System.Collections;
using System.Globalization;
using System.Text.Json;
using IsItGreenYet.Core.GitHub.Dto;
using IsItGreenYet.Core.Logging;

namespace IsItGreenYet.Core.GitHub;

/// <summary>
/// GitHub REST 用戶端（spec §4.1）：共通 header、ETag、手動轉址、額度 header、錯誤分類、api.log。
/// 不自己寫快取：200 時把新的 <see cref="HttpCacheEntry"/> 交回呼叫端，由呼叫端和處理結果同一個交易寫入。
/// </summary>
/// <remarks>
/// 讀取送 GET；POST 只用在第二階段 spec §3.1 的四個寫入端點（<see cref="PostAsync"/>）。
/// handler 由外部提供（App 給 AllowAutoRedirect=false 的 SocketsHttpHandler），轉址一律在這裡處理，
/// 不依賴 handler；handler 的生命週期也歸外部管，<see cref="Dispose"/> 不會釋放它。
/// 沒有共用的可變狀態，輪詢迴圈與使用者操作（複製 log、寫入）可以同時呼叫。
/// </remarks>
public sealed class GitHubClient : IDisposable
{
    private const string GitHubJsonMediaType = "application/vnd.github+json";

    private const string ApiVersionHeader = "X-GitHub-Api-Version";

    /// <summary>連線層錯誤沒有狀態碼，api.log 與 <see cref="ApiResult{T}.StatusCode"/> 記 0。</summary>
    private const int NoResponse = 0;

    private const int StatusNoContent = 204;
    private const int StatusMovedPermanently = 301;
    private const int StatusFound = 302;
    private const int StatusNotModified = 304;
    private const int StatusTemporaryRedirect = 307;
    private const int StatusPermanentRedirect = 308;
    private const int SuccessMin = 200;
    private const int SuccessMax = 299;

    /// <summary>body 是空白時視為沒有內容（spec §4.1：列表回 204 或空 body 當 0 筆）。</summary>
    private static readonly byte[] JsonWhitespace = " \t\r\n"u8.ToArray();

    // 欄位名稱都在 DTO 上用 JsonPropertyName 指定；不認得的欄位忽略（spec §4.1 回應容錯）。
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpMessageInvoker _invoker;
    private readonly IHttpCacheStore _cache;
    private readonly Func<string?> _tokenAccessor;
    private readonly ApiLog? _log;
    private readonly TimeProvider _time;
    private readonly string _userAgent;

    /// <param name="tokenAccessor">每次送請求時取目前的 token；只對 api.github.com 帶上。</param>
    public GitHubClient(
        HttpMessageHandler handler,
        IHttpCacheStore cache,
        Func<string?> tokenAccessor,
        ApiLog? log,
        TimeProvider time,
        string userAgent)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(tokenAccessor);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);

        // 用 HttpMessageInvoker 而不是 HttpClient：HttpClient 預設把整個 body 讀進記憶體、逾時也不經 TimeProvider；
        // 這裡 body 由各呼叫自己讀（job log 以串流取尾端），逾時用 TimeProvider 的 CancellationTokenSource。
        // handler 由外部擁有，這裡不釋放它。
        _invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        _cache = cache;
        _tokenAccessor = tokenAccessor;
        _log = log;
        _time = time;
        _userAgent = userAgent;
    }

    /// <param name="pathOrAbsoluteUrl">相對於 api.github.com 的路徑（含查詢字串），或 Link header 給的完整網址。</param>
    /// <param name="itemIds">從回應取出這一頁的項目 id，存進快取項供 304 時使用。</param>
    /// <remarks>
    /// 快取鍵是完整網址：路徑會接在 <see cref="Tuning.ApiBaseUrl"/> 後面，完整網址照原樣使用。
    /// 只接受 https；其他網址不送出請求，回 <see cref="ApiOutcome.OtherError"/>。
    /// </remarks>
    public async Task<ApiResult<T>> GetAsync<T>(
        string pathOrAbsoluteUrl,
        Func<T, IReadOnlyList<long>>? itemIds = null,
        bool conditional = true,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrAbsoluteUrl);

        if (!TryResolve(pathOrAbsoluteUrl, out var uri, out var cacheKey))
        {
            return new ApiResult<T> { Outcome = ApiOutcome.OtherError, Message = "網址不是 https:// 的完整網址或 / 開頭的路徑，不送出請求" };
        }

        var cached = conditional ? _cache.Get(cacheKey) : null;
        var etag = string.IsNullOrWhiteSpace(cached?.ETag) ? null : cached.ETag;
        var exchange = await ExchangeAsync(uri, HttpMethod.Get, etag, followRedirects: true, Tuning.HttpTimeout, ReadBodyBytesAsync, ct)
            .ConfigureAwait(false);
        return ToResult(exchange, cacheKey, cached, itemIds);
    }

    /// <summary>
    /// 寫入請求（第二階段 spec §3.2、§3.3）：POST、不送 body（Content-Length: 0）、不帶 If-None-Match、不讀寫快取、任何 3xx 都不跟隨。
    /// </summary>
    /// <param name="path">相對於 api.github.com 的路徑（/ 開頭）；不接受完整網址，寫入只送 api.github.com。</param>
    /// <remarks>
    /// 2xx 一律 Ok，不解析 body；其他狀態碼讀 body 的 message 交給 <see cref="ErrorClassifier.ClassifyWrite"/>。
    /// 3xx 不填 <see cref="ApiResult{T}.MovedTo"/>：它代表「跟隨過的 301」，Engine 會依它多算一個請求。
    /// 交給 handler 之前被取消：丟 <see cref="OperationCanceledException"/>（請求沒有離開本機）。
    /// 交給 handler 之後被取消、逾時、連線錯誤：回 NetworkError、<see cref="ApiResult{T}.Dispatched"/> 為 true，不丟例外，
    /// 讓 Engine 分辨「未送出：程式正在結束」和「結果不確定」（第二階段 spec §5.2）。這一層不重送（401 也是，由 Engine 處理）。
    /// </remarks>
    public async Task<ApiResult<object>> PostAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/') || !TryResolve(path, out var uri, out _))
        {
            throw new ArgumentException("寫入只接受 / 開頭的 api.github.com 路徑。", nameof(path));
        }

        var exchange = await ExchangeAsync(uri, HttpMethod.Post, etag: null, followRedirects: false, Tuning.HttpTimeout,
            ReadWriteBodyAsync, ct).ConfigureAwait(false);
        return ToWriteResult(exchange);
    }

    /// <summary>
    /// E9：取得 302 的簽名網址後，以不帶 Authorization 的請求下載，只保留最後 <paramref name="tailLines"/> 行。
    /// 下載主機的非 2xx 不經 ErrorClassifier，直接回 OtherError。
    /// </summary>
    /// <remarks>
    /// 不帶 If-None-Match（一次性請求，Q27）。api.github.com 的請求用 <see cref="Tuning.HttpTimeout"/>，
    /// 下載主機用 <see cref="Tuning.LogDownloadTimeout"/>。下載失敗時「重新呼叫 E9 一次」（spec §4.2 E9）由呼叫端負責。
    /// 結果的 <see cref="ApiResult{T}.RateLimit"/> 取自 api.github.com 的回應（下載主機沒有額度 header）。
    /// </remarks>
    public async Task<(ApiResult<object> Result, IReadOnlyList<string> Lines)> DownloadJobLogTailAsync(
        string repoFullName,
        long jobId,
        int tailLines,
        CancellationToken ct)
    {
        var repoPath = RepoPath(repoFullName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tailLines);

        var uri = new Uri(string.Create(CultureInfo.InvariantCulture, $"{Tuning.ApiBaseUrl}{repoPath}/actions/jobs/{jobId}/logs"));
        var exchange = await ExchangeAsync(uri, HttpMethod.Get, etag: null, followRedirects: true, Tuning.LogDownloadTimeout,
            (response, current, token) => ReadLogBodyAsync(response, current, tailLines, token), ct).ConfigureAwait(false);

        if (exchange.Failure is { } failure)
        {
            return (BuildResult<object>(exchange, failure, message: exchange.FailureMessage), []);
        }

        var body = exchange.Body!;
        if (IsSuccess(exchange.StatusCode))
        {
            return (BuildResult<object>(exchange, ApiOutcome.Ok), body.Lines ?? []);
        }

        if (IsApiHost(exchange.FinalUri))
        {
            var message = ReadErrorMessage(body.ErrorBytes);
            var classification = ErrorClassifier.Classify(exchange.StatusCode, exchange.Final!.Get, message);
            return (BuildResult<object>(exchange, classification.Outcome, classification: classification, message: message), []);
        }

        // 簽名下載主機的非 2xx 只顯示訊息，不改 repo 狀態、不觸發全域暫停（spec §4.1 錯誤分類的適用範圍）。
        return (BuildResult<object>(exchange, ApiOutcome.OtherError), []);
    }

    public void Dispose() => _invoker.Dispose();

    /// <summary><c>/repos/{owner}/{repo}</c>，兩段各自編碼。<paramref name="fullName"/> 必須是 <c>owner/repo</c>。</summary>
    internal static string RepoPath(string fullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        if (!DtoMapper.IsRepoFullName(fullName))
        {
            throw new ArgumentException("repo 名稱必須是 owner/repo 的形式。", nameof(fullName));
        }

        var slash = fullName.IndexOf('/', StringComparison.Ordinal);
        return "/repos/" + Uri.EscapeDataString(fullName[..slash]) + "/" + Uri.EscapeDataString(fullName[(slash + 1)..]);
    }

    /// <summary>只有 https、主機是 api.github.com、預設連接埠，才帶 Authorization 與 If-None-Match。</summary>
    internal static bool IsApiHost(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, Tuning.ApiHost, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort;

    private static bool TryResolve(string pathOrAbsoluteUrl, out Uri uri, out string cacheKey)
    {
        cacheKey = pathOrAbsoluteUrl.StartsWith('/') ? Tuning.ApiBaseUrl + pathOrAbsoluteUrl : pathOrAbsoluteUrl;
        if (Uri.TryCreate(cacheKey, UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && parsed.Host.Length > 0)
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    /// <summary>
    /// 送出請求並手動跟隨 301／302／307／308（最多 <see cref="Tuning.MaxRedirects"/> 次），每個請求寫一行 api.log。
    /// <paramref name="followRedirects"/> 為 false 時轉址回應就是最後的回應。
    /// 最後一個非轉址的回應交給 <paramref name="readBody"/> 讀 body，讀取時間也算在該請求的逾時內。
    /// 呼叫端取消或遇到非預期例外時同樣先寫 api.log，再原樣丟出；
    /// 例外是寫入（非 GET）在交給 handler 之後被取消：回 NetworkError，不丟例外（第二階段 spec §5.2）。
    /// </summary>
    private async Task<Exchange<TBody>> ExchangeAsync<TBody>(
        Uri start,
        HttpMethod method,
        string? etag,
        bool followRedirects,
        TimeSpan otherHostTimeout,
        Func<HttpResponseMessage, Uri, CancellationToken, Task<TBody>> readBody,
        CancellationToken ct)
    {
        var exchange = new Exchange<TBody>(start);
        var current = start;
        // 寫入一交給 handler 就可能已經生效，被取消也要回結果，讓 Engine 當成「結果不確定」。
        var cancelAfterSendIsFailure = method != HttpMethod.Get;

        for (var redirects = 0; ; redirects++)
        {
            ct.ThrowIfCancellationRequested();

            var toApi = IsApiHost(current);
            var timeout = toApi ? Tuning.HttpTimeout : otherHostTimeout;
            // ETag 屬於 api.github.com 的資源；Authorization 只送 api.github.com（spec §4.1 轉址）。
            using var request = BuildRequest(method, current, toApi, toApi ? etag : null);
            using var timeoutSource = new CancellationTokenSource(timeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
            var started = _time.GetTimestamp();

            HttpResponseMessage response;
            exchange.Dispatched = true;
            try
            {
                response = await _invoker.SendAsync(request, linked.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTransportFailure(exception, ct, cancelAfterSendIsFailure))
            {
                WriteLog(method, current, NoResponse, null, started, null);
                return exchange.Fail(current, ApiOutcome.NetworkError, NoResponse, TransportMessage(exception, timeout, ct));
            }
            catch
            {
                // 呼叫端取消或非預期例外：請求已經送出，照樣記一行（spec §14.4 一個請求一行），再原樣丟出。
                WriteLog(method, current, NoResponse, null, started, null);
                throw;
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                var headers = ResponseHeaders.From(response);
                exchange.Observe(current, status, headers, toApi);

                if (followRedirects && IsFollowedRedirect(status))
                {
                    var location = response.Headers.Location;
                    WriteLog(method, current, status, headers, started, location);

                    if (location is null || !Uri.TryCreate(current, location, out var target))
                    {
                        return exchange.Fail(current, ApiOutcome.RedirectError, status, "轉址回應沒有可用的 Location");
                    }

                    if (status == StatusMovedPermanently)
                    {
                        // 只回報，不存成之後的輪詢網址（spec §4.1、§5.4）。
                        exchange.MovedTo = target;
                    }

                    if (target.Scheme != Uri.UriSchemeHttps)
                    {
                        return exchange.Fail(current, ApiOutcome.RedirectError, status,
                            "不跟隨 HTTPS 以外的轉址：" + SecretRedactor.DescribeRedirect(current, location));
                    }

                    if (redirects >= Tuning.MaxRedirects)
                    {
                        return exchange.Fail(current, ApiOutcome.RedirectError, status,
                            string.Create(CultureInfo.InvariantCulture, $"轉址超過 {Tuning.MaxRedirects} 次"));
                    }

                    current = target;
                    continue;
                }

                // 不跟隨的轉址（寫入）照樣在 api.log 記下新位置，方便查「位置改變」的原因。
                var unfollowedLocation = IsFollowedRedirect(status) ? response.Headers.Location : null;

                TBody body;
                try
                {
                    body = await readBody(response, current, linked.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsTransportFailure(exception, ct, cancelAfterSendIsFailure))
                {
                    WriteLog(method, current, status, headers, started, unfollowedLocation);
                    return exchange.Fail(current, ApiOutcome.NetworkError, status, TransportMessage(exception, timeout, ct));
                }
                catch
                {
                    // 同上：讀 body 時被取消或遇到非預期例外，記下已收到的狀態碼後原樣丟出。
                    WriteLog(method, current, status, headers, started, unfollowedLocation);
                    throw;
                }

                WriteLog(method, current, status, headers, started, unfollowedLocation);
                exchange.Body = body;
                return exchange;
            }
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, Uri uri, bool toApi, string? etag)
    {
        var request = new HttpRequestMessage(method, uri);
        if (method != HttpMethod.Get)
        {
            // 寫入一律不送 body，但要明確送出 Content-Length: 0（第二階段 spec §3.2）。
            request.Content = new ByteArrayContent([]);
        }

        // 存下什麼就送什麼：一律不經驗證直接放進 header，不讓 .NET 改寫格式。
        request.Headers.TryAddWithoutValidation("Accept", GitHubJsonMediaType);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.TryAddWithoutValidation(ApiVersionHeader, Tuning.ApiVersion);

        if (toApi)
        {
            var token = _tokenAccessor()?.Trim();
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            }
        }

        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        return request;
    }

    private static ApiResult<T> ToResult<T>(Exchange<byte[]> exchange, string cacheKey, HttpCacheEntry? cached,
        Func<T, IReadOnlyList<long>>? itemIds)
    {
        if (exchange.Failure is { } failure)
        {
            return BuildResult<T>(exchange, failure, message: exchange.FailureMessage);
        }

        var status = exchange.StatusCode;
        var body = exchange.Body ?? [];
        if (status == StatusNotModified)
        {
            // 304 不保證帶 Link：交回快取中原有的項目，呼叫端用存下的 next 繼續（spec §4.1 分頁）。
            return BuildResult<T>(exchange, ApiOutcome.NotModified, cacheEntry: cached);
        }

        if (IsSuccess(status))
        {
            if (!TryDeserialize<T>(status, body, out var value, out var error))
            {
                // JSON 錯誤時丟棄回應、不更新 ETag（spec §15）。
                return BuildResult<T>(exchange, ApiOutcome.ParseError, message: error);
            }

            var final = exchange.Final!;
            var entry = new HttpCacheEntry(cacheKey, final.Get("ETag"), LinkHeader.Next(final.Get("Link")), itemIds?.Invoke(value).ToArray());
            return BuildResult(exchange, ApiOutcome.Ok, value, entry);
        }

        var message = ReadErrorMessage(body);
        if (!IsApiHost(exchange.FinalUri))
        {
            // 錯誤分類只用在 api.github.com 的回應（spec §4.1）。
            return BuildResult<T>(exchange, ApiOutcome.OtherError, message: message);
        }

        var classification = ErrorClassifier.Classify(status, exchange.Final!.Get, message);
        return BuildResult<T>(exchange, classification.Outcome, classification: classification, message: message);
    }

    /// <summary>寫入的結果：2xx 一律受理、不看 body；其他狀態碼照第二階段 spec §3.3 分類。不帶快取項。</summary>
    private static ApiResult<object> ToWriteResult(Exchange<byte[]> exchange)
    {
        if (exchange.Failure is { } failure)
        {
            return BuildResult<object>(exchange, failure, message: exchange.FailureMessage);
        }

        if (IsSuccess(exchange.StatusCode))
        {
            return BuildResult<object>(exchange, ApiOutcome.Ok);
        }

        // 寫入只送 api.github.com、不跟隨轉址，最後的回應一定來自 api.github.com，可以直接分類。
        var message = ReadErrorMessage(exchange.Body);
        var classification = ErrorClassifier.ClassifyWrite(exchange.StatusCode, exchange.Final!.Get, message);
        return BuildResult<object>(exchange, classification.Outcome, classification: classification, message: message);
    }

    private static ApiResult<T> BuildResult<T>(
        Exchange exchange,
        ApiOutcome outcome,
        T? value = default,
        HttpCacheEntry? cacheEntry = null,
        ErrorClassification? classification = null,
        string? message = null)
    {
        // GitHub 的額度、權限、SSO 等 header 取自最後一個 api.github.com 的回應。
        var api = exchange.LastApi;
        return new ApiResult<T>
        {
            Outcome = outcome,
            Value = value,
            StatusCode = exchange.StatusCode,
            RateLimit = api?.RateLimit(),
            CacheEntry = cacheEntry,
            MovedTo = exchange.MovedTo,
            LimitKind = classification?.LimitKind,
            RetryWait = classification?.Wait,
            HasRetryAfter = classification?.HasRetryAfter ?? false,
            InsufficientPermission = classification?.InsufficientPermission ?? false,
            AcceptedPermissions = api?.Get("X-Accepted-GitHub-Permissions"),
            OAuthScopes = api?.Get("X-OAuth-Scopes"),
            AcceptedOAuthScopes = api?.Get("X-Accepted-OAuth-Scopes"),
            Message = message,
            PollInterval = api?.PollInterval(),
            Deprecated = exchange.Deprecated,
            Sso = api?.Get("X-GitHub-SSO"),
            WriteRejected = classification?.WriteRejected,
            Dispatched = exchange.Dispatched,
        };
    }

    /// <summary>
    /// 2xx 的 body 轉成 <typeparamref name="T"/>。204 或空白 body：清單型別與包著清單的物件當成 0 筆，其他型別算錯誤。
    /// JSON 字面值 null 也算錯誤（呼叫端拿到 Ok 就能直接使用 Value）。
    /// </summary>
    private static bool TryDeserialize<T>(int status, byte[] body, out T value, out string? error)
    {
        value = default!;
        error = null;

        if (status == StatusNoContent || body.AsSpan().Trim(JsonWhitespace).IsEmpty)
        {
            if (TryCreateEmpty(out value))
            {
                return true;
            }

            error = "回應沒有內容";
            return false;
        }

        T? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException exception)
        {
            error = "JSON 格式錯誤：" + exception.Message;
            return false;
        }

        if (parsed is null)
        {
            error = "JSON 內容是 null";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryCreateEmpty<T>(out T value)
    {
        var type = typeof(T);
        var emptyJson = type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type)
            ? "[]"
            : typeof(IListEnvelope).IsAssignableFrom(type) ? "{}" : null;

        value = default!;
        if (emptyJson is null)
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(emptyJson, JsonOptions)!;
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>錯誤回應 body 的 message；不是 JSON 物件或沒有 message 時回 null。</summary>
    private static string? ReadErrorMessage(byte[]? body)
    {
        if (body is null || body.AsSpan().Trim(JsonWhitespace).IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ErrorDto>(body, JsonOptions)?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBodyBytesAsync(HttpResponseMessage response, Uri uri, CancellationToken ct) =>
        (int)response.StatusCode == StatusNotModified
            ? []
            : await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// 寫入的回應：2xx 不讀 body（不解析，也不讓讀 body 的逾時把已受理的請求變成「結果不確定」）；其他狀態碼讀 body 取 message。
    /// </summary>
    private static async Task<byte[]> ReadWriteBodyAsync(HttpResponseMessage response, Uri uri, CancellationToken ct) =>
        IsSuccess((int)response.StatusCode)
            ? []
            : await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

    /// <summary>2xx 用 LogTail 串流取尾端；api.github.com 的錯誤讀 body 取 message；下載主機的錯誤不讀 body。</summary>
    private static async Task<LogBody> ReadLogBodyAsync(HttpResponseMessage response, Uri uri, int tailLines, CancellationToken ct)
    {
        if (IsSuccess((int)response.StatusCode))
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return new LogBody(await LogTail.ReadLastLinesAsync(stream, tailLines, ct).ConfigureAwait(false), null);
            }
        }

        return IsApiHost(uri)
            ? new LogBody(null, await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false))
            : new LogBody(null, null);
    }

    /// <summary>
    /// 連線層錯誤（DNS、連線被拒、TLS、連線中斷）與用戶端逾時，都算 NetworkError（spec §6.4 網路中斷）。
    /// 呼叫端自己取消的不算，照樣丟出 OperationCanceledException；
    /// <paramref name="cancelIsFailure"/> 為 true（寫入已交給 handler）時也算 NetworkError。
    /// </summary>
    private static bool IsTransportFailure(Exception exception, CancellationToken ct, bool cancelIsFailure) => exception switch
    {
        OperationCanceledException => cancelIsFailure || !ct.IsCancellationRequested,
        HttpRequestException or IOException => true,
        _ => false,
    };

    /// <summary>例外訊息可能帶任何東西（網址、token），結果裡只放分類後的說明。</summary>
    private static string TransportMessage(Exception exception, TimeSpan timeout, CancellationToken ct) => exception switch
    {
        OperationCanceledException when ct.IsCancellationRequested => "送出後被取消",
        OperationCanceledException => string.Create(CultureInfo.InvariantCulture, $"逾時（{timeout.TotalSeconds:0} 秒）"),
        HttpRequestException http => "連線失敗（" + http.HttpRequestError + "）",
        _ => "連線中斷",
    };

    private static bool IsSuccess(int status) => status is >= SuccessMin and <= SuccessMax;

    private static bool IsFollowedRedirect(int status) =>
        status is StatusMovedPermanently or StatusFound or StatusTemporaryRedirect or StatusPermanentRedirect;

    private void WriteLog(HttpMethod method, Uri url, int status, ResponseHeaders? headers, long started, Uri? redirectTo)
    {
        _log?.Write(new ApiLogEntry(
            method.Method,
            url,
            status,
            headers?.Get("x-ratelimit-resource"),
            headers?.Int("x-ratelimit-remaining"),
            headers?.Int("x-ratelimit-limit"),
            _time.GetElapsedTime(started),
            redirectTo,
            headers?.RetryAfterSeconds()));
    }

    /// <summary>log 下載最後一個回應的內容：成功時是尾端的行，api.github.com 的錯誤時是 body。</summary>
    private sealed record LogBody(IReadOnlyList<string>? Lines, byte[]? ErrorBytes);

    /// <summary>一次請求（含轉址）的過程與結果；<see cref="Body"/> 是最後一個非轉址回應讀出的內容。</summary>
    private sealed class Exchange<TBody>(Uri start) : Exchange(start)
    {
        public TBody? Body { get; set; }

        public Exchange<TBody> Fail(Uri uri, ApiOutcome outcome, int status, string message)
        {
            MarkFailed(uri, outcome, status, message);
            return this;
        }
    }

    /// <summary>一次請求（含轉址）的狀態碼、header 與失敗原因，組 <see cref="ApiResult{T}"/> 用。</summary>
    private abstract class Exchange(Uri start)
    {
        public ApiOutcome? Failure { get; private set; }

        public string? FailureMessage { get; private set; }

        public int StatusCode { get; private set; }

        public Uri FinalUri { get; private set; } = start;

        public ResponseHeaders? Final { get; private set; }

        public ResponseHeaders? LastApi { get; private set; }

        public Uri? MovedTo { get; set; }

        /// <summary>至少一個請求已交給 handler。</summary>
        public bool Dispatched { get; set; }

        public bool Deprecated { get; private set; }

        public void Observe(Uri uri, int status, ResponseHeaders headers, bool fromApi)
        {
            FinalUri = uri;
            StatusCode = status;
            Final = headers;
            if (fromApi)
            {
                LastApi = headers;
                // 版本接近停止支援時 GitHub 會帶 Deprecation／Sunset（spec §4.1）。
                Deprecated |= headers.Has("Deprecation") || headers.Has("Sunset");
            }
        }

        protected void MarkFailed(Uri uri, ApiOutcome outcome, int status, string message)
        {
            FinalUri = uri;
            StatusCode = status;
            Failure = outcome;
            FailureMessage = message;
        }
    }

    /// <summary>回應 header 的快照（名稱不分大小寫）；回應釋放之後仍可讀。</summary>
    private sealed class ResponseHeaders
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public static ResponseHeaders From(HttpResponseMessage response)
        {
            var headers = new ResponseHeaders();
            foreach (var (name, values) in response.Headers)
            {
                headers._values[name] = string.Join(", ", values);
            }

            foreach (var (name, values) in response.Content.Headers)
            {
                headers._values.TryAdd(name, string.Join(", ", values));
            }

            return headers;
        }

        public string? Get(string name) => _values.GetValueOrDefault(name);

        public bool Has(string name) => _values.ContainsKey(name);

        public int? Int(string name) =>
            int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

        public int? RetryAfterSeconds() =>
            ErrorClassifier.ParseRetryAfter(Get("retry-after")) is { } wait ? (int)wait.TotalSeconds : null;

        /// <summary>x-poll-interval 的秒數；不是正整數時不理會。</summary>
        public TimeSpan? PollInterval() =>
            Int("x-poll-interval") is { } seconds && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;

        /// <summary>x-ratelimit-* 與 Date；全部都沒有（或都解析不出來）時回 null。</summary>
        public RateLimitSnapshot? RateLimit()
        {
            var limit = Int("x-ratelimit-limit");
            var remaining = Int("x-ratelimit-remaining");
            var used = Int("x-ratelimit-used");
            var reset = long.TryParse(Get("x-ratelimit-reset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
                ? epoch
                : (long?)null;
            var resource = Get("x-ratelimit-resource");
            var date = ErrorClassifier.ParseHttpDate(Get("Date"));

            if (limit is null && remaining is null && used is null && reset is null && resource is null && date is null)
            {
                return null;
            }

            return new RateLimitSnapshot(limit, remaining, used, reset, resource, date);
        }
    }
}
