using System.Globalization;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Tracking;

/// <summary>系統匣與摘要列使用的彙總結果（spec §7.3、§7.4）。</summary>
public sealed record StatusSummary(
    OverallState State,
    int PendingCount,
    int FailingCount,
    int StaleRepoCount,
    ConnectionIssue Issues);

public static class StatusAggregator
{
    /// <summary>系統匣 tooltip 的第一行（spec §7.4）。</summary>
    private const string TooltipTitle = "IsItGreenYet";

    /// <summary>更新時間只顯示到分（spec §7.4 範例 <c>更新於 10:07</c>）；用 InvariantCulture，避免文化設定把冒號換掉。</summary>
    private const string TooltipTimeFormat = "HH:mm";

    /// <param name="staleRepoIds">超過 StaleDataThreshold 沒有成功更新的 repo：它的鍵不計入進行中，失敗仍計入。</param>
    public static StatusSummary Aggregate(
        IReadOnlyCollection<KeyEvaluation> keys,
        IReadOnlySet<long> staleRepoIds,
        ConnectionIssue issues)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(staleRepoIds);

        var failingCount = 0;
        var pendingCount = 0;
        var anySuccess = false;

        // 計數以追蹤鍵為單位（Q12）；同一個鍵重複出現時只算一次。
        foreach (var sameKey in keys.GroupBy(k => k.Key))
        {
            if (sameKey.Any(k => k.Failing))
            {
                failingCount++;
            }

            // 無法更新的 repo 已經不知道 run 有沒有結束，不再計入進行中（Q24）。
            if (!staleRepoIds.Contains(sameKey.Key.RepoId) && sameKey.Any(k => k.Pending))
            {
                pendingCount++;
            }

            anySuccess |= sameKey.Any(k => k.LastDecisive == RunCategory.Success);
        }

        // 優先序：失敗 > 進行中或排隊 > 至少一個成功（Q4）> 無資料。連線問題只疊角標，不改顏色。
        var state = failingCount > 0 ? OverallState.Failure
            : pendingCount > 0 ? OverallState.Pending
            : anySuccess ? OverallState.Success
            : OverallState.NoData;

        return new StatusSummary(state, pendingCount, failingCount, staleRepoIds.Count, issues);
    }

    /// <summary>
    /// 系統匣 tooltip：第一行 <c>IsItGreenYet</c>，第二行 <c>2 進行中 · 0 失敗 · 更新於 10:07</c>，
    /// 有 <paramref name="issueLine"/> 時再加一行。
    /// </summary>
    /// <param name="lastUpdatedLocal">呼叫端已轉成本地時區的最後更新時間；這裡照原樣顯示，不再轉換。null 時寫「尚未更新」。</param>
    /// <param name="issueLine">原因行（例如「離線」「2 個 repo 無法更新」）；null 或空白時不加。長度截短交給 App。</param>
    /// <remarks>用共用的目前語言（<see cref="UiText.Current"/>）；英文的第二行是 <c>2 in progress · 0 failed · Updated 10:07</c>。</remarks>
    public static string TrayTooltip(StatusSummary summary, DateTimeOffset? lastUpdatedLocal, string? issueLine) =>
        TrayTooltip(summary, lastUpdatedLocal, issueLine, UiText.Current);

    public static string TrayTooltip(StatusSummary summary, DateTimeOffset? lastUpdatedLocal, string? issueLine, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var table = UiText.Of(language);
        var updated = lastUpdatedLocal is { } local
            ? UiText.Format(table.TrayUpdatedAt, local.ToString(TooltipTimeFormat, CultureInfo.InvariantCulture))
            : table.CommonNeverUpdated;
        var counts = UiText.Format(table.TrayCounts, summary.PendingCount, summary.FailingCount, updated);
        var text = TooltipTitle + "\n" + counts;

        return string.IsNullOrWhiteSpace(issueLine) ? text : text + "\n" + issueLine;
    }
}
