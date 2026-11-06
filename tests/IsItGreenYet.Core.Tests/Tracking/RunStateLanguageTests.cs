using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Tracking;

/// <summary>顯示狀態（spec §7.1）、系統匣 tooltip（spec §7.4）與略過原因（spec §5.2）的英文。</summary>
public class RunStateLanguageTests
{
    [Theory]
    [InlineData("queued", null, "Queued")]
    [InlineData("requested", null, "Queued")]
    [InlineData("in_progress", null, "In progress")]
    [InlineData("waiting", null, "Waiting for approval")]
    [InlineData("completed", "action_required", "Waiting for approval")]
    [InlineData("completed", "success", "Succeeded")]
    [InlineData("completed", "failure", "Failed")]
    [InlineData("completed", "timed_out", "Failed (timed out)")]
    [InlineData("completed", "startup_failure", "Failed (startup failure)")]
    [InlineData("startup_failure", null, "Failed (startup failure)")]
    [InlineData("completed", "cancelled", "Cancelled")]
    [InlineData("completed", "skipped", "Skipped")]
    [InlineData("completed", "neutral", "Skipped (neutral)")]
    [InlineData("completed", "stale", "Skipped (stale)")]
    [InlineData("completed", "brand_new", "Unknown (brand_new)")]
    [InlineData("brand_new_status", null, "Unknown (brand_new_status)")]
    [InlineData(null, null, "Unknown")]
    public void 英文的顯示狀態(string? status, string? conclusion, string label)
    {
        var english = RunStateMapper.Map(status, conclusion, AppLanguage.English);
        var chinese = RunStateMapper.Map(status, conclusion, AppLanguage.TraditionalChinese);

        Assert.Equal(label, english.Label);
        // 語言只影響文字：顯示狀態、判定類別與原字串相同。
        Assert.Equal(chinese with { Label = label }, english);
    }

    [Fact]
    public void 同一個狀態可以換成另一種語言()
    {
        var chinese = RunStateMapper.Map("completed", "timed_out", AppLanguage.TraditionalChinese);

        var english = RunStateMapper.InLanguage(chinese, AppLanguage.English);

        Assert.Equal("Failed (timed out)", english.Label);
        Assert.Equal(chinese, RunStateMapper.InLanguage(english, AppLanguage.TraditionalChinese));
        Assert.Throws<ArgumentNullException>(() => RunStateMapper.InLanguage(null!, AppLanguage.English));
    }

    [Fact]
    public void 沒有語言參數的多載用繁體中文跑既有的測試()
    {
        Assert.Equal("失敗（逾時）", RunStateMapper.Map("completed", "timed_out").Label);
    }

    [Fact]
    public void 英文的系統匣tooltip()
    {
        var summary = new StatusSummary(OverallState.Pending, 2, 0, 0, ConnectionIssue.None);
        var local = new DateTimeOffset(2026, 10, 24, 10, 7, 33, TimeSpan.FromHours(8));

        Assert.Equal("IsItGreenYet\n2 in progress · 0 failed · Updated 10:07", StatusAggregator.TrayTooltip(summary, local, null, AppLanguage.English));
        Assert.Equal(
            "IsItGreenYet\n0 in progress · 1 failed · Not updated yet\nOffline",
            StatusAggregator.TrayTooltip(summary with { PendingCount = 0, FailingCount = 1 }, null, "Offline", AppLanguage.English));
    }

    [Theory]
    [InlineData(SkipReason.Muted, "Muted")]
    [InlineData(SkipReason.Archived, "Archived")]
    [InlineData(SkipReason.Disabled, "Disabled")]
    [InlineData(SkipReason.Fork, "Fork")]
    [InlineData(SkipReason.Collaborator, "Collaborator repo")]
    [InlineData(SkipReason.Stale, "No activity for over 90 days")]
    [InlineData(SkipReason.NoWorkflows, "No workflows, or Actions is disabled (can't tell which)")]
    [InlineData(SkipReason.AllWorkflowsDisabled, "All workflows are disabled")]
    [InlineData(SkipReason.None, "")]
    public void 英文的略過原因(SkipReason reason, string expected)
    {
        Assert.Equal(expected, RepoDiscovery.SkipReasonText(reason, AppLanguage.English));
    }
}
