using System.Net;
using System.Text;

namespace IsItGreenYet.Core.Tests.TestSupport;

/// <summary>一個被送出的請求的快照（HttpRequestMessage 送出後不保證還能讀）。</summary>
/// <param name="ContentLength">body 的 Content-Length；沒有 body（GET）時為 null。</param>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, long? ContentLength = null)
{
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// 假的 HttpMessageHandler：由測試提供回應器，並記錄所有請求與 header 供斷言。
/// 回應器丟出例外時原樣傳出，可用來模擬連線失敗。
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _gate = new();

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = (request, _) => responder(request);
    }

    /// <summary>回應器另外拿到送出時的 CancellationToken（例如模擬「回應到之前已經逾時或被取消」）。</summary>
    public FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, IDictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        AddHeaders(response, headers);
        return response;
    }

    public static HttpResponseMessage Status(HttpStatusCode status, IDictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
        AddHeaders(response, headers);
        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(
            h => h.Key,
            h => string.Join(", ", h.Value),
            StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, request.Content?.Headers.ContentLength));
        }

        var response = _responder(request, cancellationToken);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }

    private static void AddHeaders(HttpResponseMessage response, IDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var (name, value) in headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }
}
