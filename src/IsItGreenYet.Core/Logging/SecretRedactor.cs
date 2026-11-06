using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace IsItGreenYet.Core.Logging;

/// <summary>
/// 寫進任何 log 之前的遮罩（spec §13.4）：目前 token 原值、token 樣式、Authorization 內容，
/// 以及非 api.github.com 網址的查詢字串（log 下載的簽名）。
/// </summary>
public sealed class SecretRedactor
{
    private const string Mask = "***";

    private const RegexOptions PatternOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // spec §13.4 規則 2：GitHub 的 token 前綴（OAuth gho_、classic ghp_、使用者對伺服器 ghu_、
    // 伺服器對伺服器 ghs_、更新用 ghr_，以及 fine-grained github_pat_）。
    private static readonly Regex TokenPattern = new(
        "gh[opusr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}",
        PatternOptions);

    // spec §13.4 規則 3：Authorization: 後面到行尾一律遮掉。header 名稱不分大小寫，所以比對也不分。
    private static readonly Regex AuthorizationPattern = new(
        "(?<name>authorization:)[^\r\n]*",
        PatternOptions | RegexOptions.IgnoreCase);

    // 自由文字（例如例外訊息）裡的網址開頭：scheme 加上可見 ASCII（!-~）的主機與路徑，到問號、井號、引號或角括號為止。
    private static readonly Regex UrlPattern = new(
        "https?://[!-~-[?#'\"<>]]*",
        PatternOptions | RegexOptions.IgnoreCase);

    // Uri.ToString()（字串內插、HttpRequestMessage.ToString() 都用它）會把 %20、%22、%3C、中文、全形空白
    // 還原成原字元，網址裡可能夾著空白與引號，看不出在哪裡結束；換行與 Tab 則一定維持 %0D、%0A、%09，
    // 所以網址最多延伸到這三個字元之前。
    private static readonly char[] SegmentBreaks = ['\r', '\n', '\t'];

    private static readonly Uri ApiBase = new(Tuning.ApiBaseUrl);

    // 背景迴圈與 UI 執行緒都會寫 log，換 token 時要讓其他執行緒立刻看到。
    private volatile string? _activeToken;

    public void SetActiveToken(string? token) =>
        _activeToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // 規則 1：目前使用中的 token 原值（精確比對，不當成樣式）。
        var token = _activeToken;
        var result = token is null ? text : text.Replace(token, Mask, StringComparison.Ordinal);

        // 規則 2、3，最後是自由文字裡的簽名網址。
        result = TokenPattern.Replace(result, Mask);
        result = AuthorizationPattern.Replace(result, "${name} " + Mask);
        return MaskUrls(result);
    }

    /// <summary>api.github.com → PathAndQuery；其他主機 → <c>host/path?***</c>（沒有查詢字串時不加 <c>?***</c>）。</summary>
    public static string DescribeUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        // 解析不出主機就無從判斷，整個遮掉。
        if (!TryResolveAgainstApi(url, out var absolute))
        {
            return Mask;
        }

        // http(s) 以外的 scheme 不一定把問號當成查詢字串（例如 file: 會把它編成路徑），無法只遮查詢字串，整個遮掉。
        if (absolute.Scheme != Uri.UriSchemeHttps && absolute.Scheme != Uri.UriSchemeHttp)
        {
            return Mask;
        }

        return IsApiHost(absolute) ? absolute.PathAndQuery : HostAndPath(absolute, absolute.Query.Length > 0);
    }

    /// <summary>
    /// 轉址的新位置（spec §14.4）。相對的 Location 依 HTTP 規則（RFC 9110 §10.2.2）對請求網址解析，
    /// 才知道新位置在哪個主機：簽名下載主機回的 <c>/path?sig=…</c> 仍是簽名主機，不是 api.github.com。
    /// </summary>
    internal static string DescribeRedirect(Uri request, Uri location)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(location);

        if (location.IsAbsoluteUri)
        {
            return DescribeUrl(location);
        }

        // 請求網址本身是相對的，照 DescribeUrl 的規則先當成 api.github.com 的路徑；兩段都解析不出來就整個遮掉。
        return TryResolveAgainstApi(request, out var origin) && Uri.TryCreate(origin, location, out var target)
            ? DescribeUrl(target)
            : Mask;
    }

    /// <summary>相對網址（包括 //host/path 這種省略 scheme 的寫法）對 api.github.com 解析；絕對網址原樣回傳。</summary>
    private static bool TryResolveAgainstApi(Uri url, [NotNullWhen(true)] out Uri? absolute)
    {
        if (url.IsAbsoluteUri)
        {
            absolute = url;
            return true;
        }

        return Uri.TryCreate(ApiBase, url, out absolute);
    }

    private static bool IsApiHost(Uri url) =>
        string.Equals(url.Host, Tuning.ApiHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>只留主機與路徑：帳密、連接埠、片段都不寫，查詢字串（簽名）換成 ***。</summary>
    private static string HostAndPath(Uri url, bool hasQuery)
    {
        var described = url.Host + url.AbsolutePath;
        return hasQuery ? $"{described}?{Mask}" : described;
    }

    /// <summary>逐段（以換行與 Tab 分段）處理自由文字裡的網址。</summary>
    private static string MaskUrls(string text)
    {
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var start = 0;
        while (true)
        {
            var breakIndex = text.IndexOfAny(SegmentBreaks, start);
            var end = breakIndex < 0 ? text.Length : breakIndex;
            AppendMaskedSegment(builder, text, start, end);

            if (breakIndex < 0)
            {
                return builder.ToString();
            }

            builder.Append(text[breakIndex]);
            start = breakIndex + 1;
        }
    }

    /// <summary>
    /// spec §13.4「非 api.github.com 的 URL 一律寫成 host/path?***」：網址後面同一段裡只要還有問號，
    /// 就當成查詢字串從那裡開始，連同後面的文字一起換成 ***。寧可多遮，也不要因為空白或引號漏掉簽名。
    /// </summary>
    private static void AppendMaskedSegment(StringBuilder builder, string text, int start, int end)
    {
        // 一段只找一次最後一個問號，一行有很多網址時也不會變成平方時間。
        var lastQuestionMark = end > start ? text.LastIndexOf('?', end - 1, end - start) : -1;
        var position = start;

        for (var match = UrlPattern.Match(text, start, end - start); match.Success; match = match.NextMatch())
        {
            builder.Append(text, position, match.Index - position);
            position = match.Index + match.Length;

            var queryFollows = lastQuestionMark >= position;
            var described = DescribeTextUrl(match.Value, queryFollows);
            builder.Append(described ?? match.Value);

            if (described is not null && queryFollows)
            {
                // 查詢字串到這一段結尾都遮掉了，後面的網址也一起被蓋過。
                return;
            }
        }

        builder.Append(text, position, end - position);
    }

    /// <returns>要換上的文字；<see langword="null"/> 代表原樣保留。</returns>
    private static string? DescribeTextUrl(string url, bool queryFollows)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // 解析不出主機：後面有查詢字串、或看起來帶帳密，就整個遮掉。
            return queryFollows || url.Contains('@') ? Mask : null;
        }

        var hasUserInfo = uri.UserInfo.Length > 0;

        // api.github.com 的查詢字串是分頁與篩選參數，要保留；帶帳密的就當成外部網址處理。
        if (IsApiHost(uri) && !hasUserInfo)
        {
            return null;
        }

        if (queryFollows)
        {
            return HostAndPath(uri, hasQuery: true);
        }

        // 沒有查詢字串、也沒有帳密的外部網址原樣保留，scheme 與連接埠有助於判讀（例如 HTTPS 轉 HTTP）。
        return hasUserInfo ? HostAndPath(uri, hasQuery: false) : null;
    }
}
