using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace IsItGreenYet.Core.Status;

/// <summary>和系統匣相同的計數（spec §7.4）。</summary>
/// <param name="Failing">失敗的追蹤鍵數。</param>
/// <param name="InProgress"><c>keyPending</c> 的追蹤鍵數。</param>
public sealed record StatusCounts(int Failing, int InProgress);

/// <summary>連線狀態；這三項不改變命令列的 state 與退出碼，和系統匣只加角標一致（spec §7.3）。</summary>
/// <param name="PausedUntil">限流的全域暫停預定恢復的時間；沒有暫停時是 null。</param>
/// <param name="ServerErrors">GitHub 持續回 5xx。</param>
public sealed record StatusConnection(bool Offline, DateTimeOffset? PausedUntil, bool ServerErrors);

/// <summary>「只看需要注意」的一筆 run（第二階段 spec §10.3 的 <c>runs[]</c>）。</summary>
/// <param name="Repo">repo 的 full_name。</param>
/// <param name="Status">GitHub 給的 status 原值。</param>
/// <param name="Conclusion">GitHub 給的 conclusion 原值。</param>
/// <param name="State">spec §7.1 的判定類別：<c>pending</c>、<c>waiting</c>、<c>success</c>、<c>failure</c>、<c>neutral</c>。</param>
/// <param name="Cancelling">是否「取消中」（第二階段 spec §6.3）。</param>
public sealed record StatusRun(
    string Repo,
    string Workflow,
    long RunId,
    int RunNumber,
    int Attempt,
    string Title,
    string? Branch,
    string Event,
    string? Status,
    string? Conclusion,
    string State,
    bool Cancelling,
    DateTimeOffset? StartedAt,
    string Url);

/// <summary>
/// App 寫進狀態表 <c>status_snapshot.status_json</c> 的內容，也就是第二階段 spec §10.3 標「App 寫」的欄位；
/// 命令列讀出來，再加上自己的欄位輸出。鍵名固定英文 camelCase，不隨介面語言改變。
/// </summary>
/// <param name="AppVersion">App 的完整 InformationalVersion（<see cref="Engine.EngineDependencies.AppVersion"/>）。</param>
/// <param name="SnapshotAt">這份狀態對應的快照時間。</param>
/// <param name="Overall">系統匣的整體狀態（spec §7.3）：<c>failure</c>、<c>in_progress</c>、<c>success</c>、<c>no_data</c>。</param>
/// <param name="SignedIn">true：已登入；false：未登入（401 後）；null：還在確認。</param>
/// <param name="ApiVersionError">發生 API 版本錯誤（spec §4.1）。</param>
/// <param name="RefreshMode">自動更新的設定值，用 settings.json 的寫法：<c>auto</c>、<c>30s</c>、<c>1m</c>、<c>5m</c>、<c>paused</c>。</param>
/// <param name="MonitoredRepos">監看中的 repo 數。</param>
/// <param name="StaleRepos">無法更新的 repo 數（spec §7.3）。</param>
/// <param name="LastUpdatedAt">監看中 repo 最近一次成功回應的時間；還沒有任何成功回應時是 null。</param>
/// <param name="Runs">「只看需要注意」的 run，排序同主清單；沒有時是空陣列。</param>
public sealed record StatusPayload(
    string AppVersion,
    DateTimeOffset SnapshotAt,
    string Overall,
    bool? SignedIn,
    bool ApiVersionError,
    string RefreshMode,
    StatusCounts Counts,
    int MonitoredRepos,
    int StaleRepos,
    StatusConnection Connection,
    DateTimeOffset? LastUpdatedAt,
    IReadOnlyList<StatusRun> Runs);

/// <summary>
/// 狀態表與命令列輸出共用的 JSON 設定（第二階段 spec §10.3）：camelCase、不縮排（一行），
/// 用 System.Text.Json 的預設 encoder：非 ASCII 字元一律跳脫成 <c>\uXXXX</c> 的形式，整個輸出是純 ASCII（第二階段 spec §11.3）。
/// 版本字串的 <c>+</c> 這類字元也會被跳脫，解析後的值不變。值是 null 的欄位照樣寫出來。
/// 時間一律寫成 UTC、結尾 <c>Z</c>（同第二階段 spec §10.3 的範例 <c>2026-10-29T02:00:00Z</c>）。
/// 嚴格解析：缺少建構子參數、不可為 null 的參考型別是 null，都丟 <see cref="JsonException"/>，
/// 不會補成 0、false、最小日期或 null（命令列靠這些欄位判斷退出碼，讀不懂就當成 db_error）。
/// </summary>
public static class StatusJson
{
    /// <summary>UTC 的 ISO 8601；小數秒是 0 時連小數點一起省略，有小數秒時去掉結尾的 0。</summary>
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    /// <summary>唯讀；App 與命令列共用同一份，不能被任何一方改掉。</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <returns>JSON 本身是 <c>null</c> 時回傳 null。</returns>
    /// <exception cref="JsonException">
    /// 格式不是合法 JSON、內容對不上 <typeparamref name="T"/>、缺少建構子參數，或不可為 null 的欄位是 null。
    /// </exception>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            // 嚴格解析。寫入時也一樣：不可為 null 的欄位是 null 就丟例外，App 不會寫出命令列讀不了的內容。
            // 只看欄位本身的宣告，清單元素（runs 裡的 null）不在檢查範圍。
            // DefaultIgnoreCondition 維持 Never：可為 null 的欄位是 null 時照樣寫出 null。
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            // 註冊 DateTimeOffset 的轉換器時，DateTimeOffset? 也會用它；null 照樣寫成 null，不經過轉換器。
            Converters = { new UtcDateTimeOffsetConverter() },
        };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// 寫入時轉成 UTC 的 <see cref="UtcFormat"/>；讀取時接受 ISO 8601 的兩種時區寫法（<c>Z</c>、<c>±hh:mm</c>，例如 <c>+08:00</c>），
    /// 回傳同一個時間點。沒有時區的時間（<c>2026-10-29T02:00:00</c>、<c>2026-10-29</c>）不知道是哪個時區，拒收，不當成本機時間。
    /// </summary>
    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        /// <summary><c>±hh:mm</c> 的長度：正負號在倒數第 6 個字元、冒號在倒數第 3 個字元。</summary>
        private const int OffsetLength = 6;

        private const int OffsetColonFromEnd = 3;

        /// <exception cref="JsonException">不是字串、不是 ISO 8601 的時間，或沒有時區。訊息是英文（命令列的 stderr 只能有英文）。</exception>
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || !HasExplicitOffset(reader.GetString())
                || !reader.TryGetDateTimeOffset(out var value))
            {
                throw new JsonException("Timestamp must be an ISO 8601 string ending with Z or a +hh:mm offset.");
            }

            return value;
        }

        /// <summary>結尾是 <c>Z</c> 或 <c>±hh:mm</c>；數字是否合法交給 <see cref="Utf8JsonReader.TryGetDateTimeOffset"/>。</summary>
        private static bool HasExplicitOffset(string? text) =>
            text is not null
            && (text.EndsWith('Z')
                || (text.Length >= OffsetLength
                    && text[^OffsetLength] is '+' or '-'
                    && text[^OffsetColonFromEnd] == ':'));

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture));
    }
}
