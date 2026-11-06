namespace IsItGreenYet.Core.GitHub;

/// <summary>
/// 一個 URL 的條件請求快取：ETag、該頁的 next 連結、該頁項目 id。
/// 304 回應不保證帶 Link header，所以 next 與項目 id 要一起存（spec §4.1 分頁）。
/// </summary>
public sealed record HttpCacheEntry(string Url, string? ETag, string? NextUrl, IReadOnlyList<long>? ItemIds);

/// <summary>GitHubClient 只讀快取；寫入由呼叫端和處理結果在同一個交易完成。</summary>
public interface IHttpCacheStore
{
    HttpCacheEntry? Get(string url);
}
