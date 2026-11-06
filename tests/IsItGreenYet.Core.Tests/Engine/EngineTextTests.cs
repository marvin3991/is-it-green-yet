using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>Engine 的畫面文字（橫幅、tooltip 原因、問題標示、操作結果）兩種語言的代表性案例。</summary>
public class EngineTextTests
{
    private const AppLanguage Chinese = AppLanguage.TraditionalChinese;
    private const AppLanguage English = AppLanguage.English;

    [Fact]
    public void 離線與5xx的最後更新()
    {
        Assert.Equal("離線 · 最後更新 10:07:33", EngineText.Offline("10:07:33", Chinese));
        Assert.Equal("Offline · Last updated 10:07:33", EngineText.Offline("10:07:33", English));
        Assert.Equal("GitHub 回應異常 · 尚未更新", EngineText.ServerErrors(null, Chinese));
        Assert.Equal("GitHub is returning errors · Not updated yet", EngineText.ServerErrors(null, English));
    }

    [Fact]
    public void 次級限流的次數與持續被限流()
    {
        Assert.Equal("被 GitHub 限流（第 3 次），程式預定 10:31 再試；GitHub 沒有提供恢復時間。",
            EngineText.SecondaryLimitWithoutRetryAfter(3, persistent: false, "10:31", Chinese));
        Assert.Equal("持續被限流（第 4 次），程式預定 10:31 再試；GitHub 沒有提供恢復時間。",
            EngineText.SecondaryLimitWithoutRetryAfter(4, persistent: true, "10:31", Chinese));
        Assert.Equal("Rate limited by GitHub (backoff level 3). Retrying at 10:31; GitHub didn't say when the limit resets.",
            EngineText.SecondaryLimitWithoutRetryAfter(3, persistent: false, "10:31", English));
        Assert.Equal("Still rate limited (backoff level 4). Retrying at 10:31; GitHub didn't say when the limit resets.",
            EngineText.SecondaryLimitWithoutRetryAfter(4, persistent: true, "10:31", English));
        Assert.Equal("Rate limited by GitHub until 10:31.", EngineText.SecondaryLimitWithRetryAfter("10:31", English));
    }

    [Fact]
    public void 權限不足與存取被拒()
    {
        Assert.Equal("權限不足，需要 actions=read", EngineText.Forbidden(true, "actions=read", null, null, Chinese));
        Assert.Equal("Insufficient permissions (requires actions=read)", EngineText.Forbidden(true, "actions=read", "repo", null, English));
        Assert.Equal("Insufficient permissions (requires repo)", EngineText.Forbidden(true, null, "repo", null, English));
        Assert.Equal("Insufficient permissions", EngineText.Forbidden(true, " ", null, null, English));
        Assert.Equal("Access denied: Resource not accessible", EngineText.Forbidden(false, null, null, "Resource not accessible", English));
        Assert.Equal("Access denied", EngineText.Forbidden(false, null, null, null, English));
    }

    [Fact]
    public void 無法更新的repo數量用單數與複數()
    {
        Assert.Equal("1 個 repo 無法更新", EngineText.StaleRepos(1, Chinese));
        Assert.Equal("1 repo can't be updated", EngineText.StaleRepos(1, English));
        Assert.Equal("2 repos can't be updated", EngineText.StaleRepos(2, English));
    }

    [Fact]
    public void 複製失敗log的結果()
    {
        Assert.Equal("已複製 2 個失敗 job 的 log", EngineText.Copied(2, 0, Chinese));
        Assert.Equal("已複製 1 個失敗 job 的 log；1 個拿不到", EngineText.Copied(1, 1, Chinese));
        Assert.Equal("Copied the log of 1 failed job.", EngineText.Copied(1, 0, English));
        Assert.Equal("Copied the logs of 2 failed jobs. 1 couldn't be fetched.", EngineText.Copied(2, 1, English));
        Assert.Equal("=== build (Failed (timed out)) ===", EngineText.JobHeader("build", "Failed (timed out)", English));
    }

    [Fact]
    public void 狀態碼與連線失敗()
    {
        Assert.Equal("拿不到這個 job 的 log（狀態碼 410）", EngineText.LogUnavailable(410, Chinese));
        Assert.Equal("拿不到這個 job 的 log（連線失敗）", EngineText.LogUnavailable(0, Chinese));
        Assert.Equal("Couldn't get this job's log (HTTP 410).", EngineText.LogUnavailable(410, English));
        Assert.Equal("Couldn't get this job's log (connection failed).", EngineText.LogUnavailable(0, English));
        Assert.Equal("Couldn't verify the token (HTTP 401)", EngineText.ConnectionFailed(401, English));
        Assert.Equal("Couldn't get the repo list (HTTP 502)", EngineText.ReposUnavailable(502, English));
    }

    [Fact]
    public void 檢查連線看得到的repo()
    {
        Assert.Equal("看得到 12 個 repo（其中私有 3 個）", EngineText.VisibleRepos(12, 3, Chinese));
        Assert.Equal("12 repos visible (3 private)", EngineText.VisibleRepos(12, 3, English));
        Assert.Equal("1 repo visible (1 private)", EngineText.VisibleRepos(1, 1, English));
    }

    [Fact]
    public void 快取資料庫復原的檔名()
    {
        Assert.Equal(
            "The cache database couldn't be read, so it was renamed to cache.db.corrupt-20261024100000.bak and rebuilt. "
            + "Repo mute and force-include settings were lost; set them again in Repo manager.",
            UiText.DatabaseRebuilt("cache.db.corrupt-20261024100000.bak", English));
    }

    [Fact]
    public void 快取資料庫重建但沒有留下備份()
    {
        Assert.Equal(
            "快取資料庫無法讀取，已重建，這次沒有留下備份；repo 的靜音與強制納入設定可能已遺失，請到 repo 管理確認。",
            UiText.DatabaseRebuilt(null, Chinese));
        Assert.Equal(
            "The cache database couldn't be read, so it was rebuilt without keeping a backup. "
            + "Repo mute and force-include settings may have been lost; check them in Repo manager.",
            UiText.DatabaseRebuilt(null, English));
    }

    /// <summary>NotifyIcon.Text 的上限（同 App 的 TrayIconController.MaxTooltipLength；測試專案不參考 App）。</summary>
    private const int TrayTooltipMaxLength = 127;

    /// <summary>「常見情況」的計數上限：進行中、失敗、無法更新的 repo 都在 2 位數以內。</summary>
    private const int TwoDigitMax = 99;

    /// <summary>「常見情況」的原因個數上限（含無法更新的 repo）。</summary>
    private const int CommonMaxReasons = 3;

    /// <summary>
    /// 審查回報：英文的系統匣 tooltip 在限流、5xx 與無法更新的 repo 同時出現時超過上限，尾端被截掉（中文放得下）。
    /// 常見情況（計數 2 位數以內、原因最多 3 個）兩種語言都要放得下，原因行不必截短。
    /// </summary>
    [Theory]
    [InlineData(Chinese)]
    [InlineData(English)]
    public void 系統匣tooltip在常見情況下放得下(AppLanguage language)
    {
        var local = new DateTimeOffset(2026, 10, 24, 10, 7, 33, TimeSpan.FromHours(8));
        string[] reasons =
        [
            EngineText.TrayUnauthorized(language),
            EngineText.TrayRateLimited("10:45", language),
            EngineText.TrayOffline(language),
            EngineText.TrayServerErrors(language),
            EngineText.TrayVersionError(language),
        ];
        var summary = new StatusSummary(OverallState.Failure, TwoDigitMax, TwoDigitMax, TwoDigitMax, ConnectionIssue.None);
        var checkedCount = 0;
        for (var mask = 1; mask < 1 << reasons.Length; mask++)
        {
            var parts = reasons.Where((_, index) => (mask & 1 << index) != 0).Append(EngineText.StaleRepos(TwoDigitMax, language)).ToList();
            if (parts.Count > CommonMaxReasons)
            {
                continue;
            }

            var tooltip = StatusAggregator.TrayTooltip(summary, local, string.Join(EngineText.Separator, parts), language);
            Assert.True(tooltip.Length <= TrayTooltipMaxLength, $"{tooltip.Length}：{tooltip}");
            checkedCount++;
        }

        // 1 個或 2 個連線原因加上無法更新的 repo：C(5,1) + C(5,2) 種。
        Assert.Equal(reasons.Length + (reasons.Length * (reasons.Length - 1) / 2), checkedCount);
    }

    [Fact]
    public void error_log的除錯文字維持中文()
    {
        Assert.Contains("全域暫停", EngineText.ForbiddenBurstLog, StringComparison.Ordinal);
        Assert.Contains("連線失敗", EngineText.TargetErrorLog(new PollTarget(TargetKind.Probe), "NetworkError", 0, null), StringComparison.Ordinal);
    }
}
