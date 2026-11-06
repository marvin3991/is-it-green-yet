using IsItGreenYet.Core.Display;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Tests.TestSupport;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Display;

/// <summary>開始時間、耗時、ETA 與觸發說明的英文（spec §8、§10.6、§10.8 的英文版）。</summary>
public class DisplayFormatterEnglishTests
{
    private const AppLanguage English = AppLanguage.English;

    private static readonly TimeSpan TaipeiOffset = TimeSpan.FromHours(8);

    /// <summary>觀看時間：台北 2026-10-24 10:00（同 DisplayFormatterTests）。</summary>
    private static readonly DateTimeOffset TaipeiNow = new(2026, 10, 24, 10, 0, 0, TaipeiOffset);

    private static DisplayFormatter Formatter() => new(new ManualTimeProvider(TaipeiNow, ManualTimeProvider.Taipei));

    [Fact]
    public void 開始時間的相對與絕對寫法()
    {
        var formatter = Formatter();

        Assert.Equal("Just now", formatter.StartTime(TaipeiNow.AddSeconds(-59), English));
        Assert.Equal("1 min ago", formatter.StartTime(TaipeiNow.AddSeconds(-60), English));
        Assert.Equal("40 min ago", formatter.StartTime(TaipeiNow.AddMinutes(-40), English));
        Assert.Equal("Today 08:32", formatter.StartTime(new DateTimeOffset(2026, 10, 24, 8, 32, 0, TaipeiOffset), English));
        Assert.Equal("Oct 23 23:50", formatter.StartTime(new DateTimeOffset(2026, 10, 23, 23, 50, 0, TaipeiOffset), English));
        Assert.Equal("Dec 31, 2025", formatter.StartTime(new DateTimeOffset(2025, 12, 31, 12, 0, 0, TaipeiOffset), English));
    }

    [Fact]
    public void 同一個時間的中文寫法不變()
    {
        var formatter = Formatter();

        Assert.Equal("10/23 23:50", formatter.StartTime(new DateTimeOffset(2026, 10, 23, 23, 50, 0, TaipeiOffset), AppLanguage.TraditionalChinese));
        Assert.Equal("2025/12/31", formatter.StartTime(new DateTimeOffset(2025, 12, 31, 12, 0, 0, TaipeiOffset), AppLanguage.TraditionalChinese));
    }

    [Fact]
    public void 完整時間兩種語言相同()
    {
        Assert.Equal("2026-10-24 08:32:00", Formatter().FullLocal(new DateTimeOffset(2026, 10, 24, 0, 32, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void 估算值加波浪號且不到1分鐘寫小於()
    {
        Assert.Equal("~3m", DisplayFormatter.Approx(TimeSpan.FromSeconds(130), English));
        Assert.Equal("< 1m", DisplayFormatter.Approx(TimeSpan.FromSeconds(59), English));
    }

    [Fact]
    public void 已執行時間與剩餘或超過預估()
    {
        var running = new EtaInfo(TimeSpan.FromSeconds(252), TimeSpan.FromMinutes(7), TimeSpan.FromSeconds(168), null, 0.6);
        var underMinute = running with { Remaining = TimeSpan.FromSeconds(30) };
        var overrun = new EtaInfo(TimeSpan.FromSeconds(580), TimeSpan.FromMinutes(7), null, TimeSpan.FromSeconds(160), 1);
        var noSamples = new EtaInfo(TimeSpan.FromSeconds(45), null, null, null, null);

        Assert.Equal("Elapsed 4m 12s · ~3m left", DisplayFormatter.Running(running, English));
        Assert.Equal("Elapsed 4m 12s · < 1m left", DisplayFormatter.Running(underMinute, English));
        Assert.Equal("Elapsed 9m 40s · ~3m over estimate", DisplayFormatter.Running(overrun, English));
        Assert.Equal("Elapsed 9m 40s · < 1m over estimate", DisplayFormatter.Running(overrun with { Overrun = TimeSpan.FromSeconds(20) }, English));
        Assert.Equal("Elapsed 45s", DisplayFormatter.Running(noSamples, English));
    }

    [Fact]
    public void 排隊時間()
    {
        Assert.Equal("Queued 1m 05s", DisplayFormatter.Queued(TimeSpan.FromSeconds(65), English));
    }

    [Fact]
    public void 觸發說明()
    {
        var run = DisplaySamples.Run;

        Assert.Equal("Scheduled", DisplayFormatter.Trigger(run with { Event = "schedule" }, null, English).Text);
        Assert.Equal("Manual", DisplayFormatter.Trigger(run with { Event = "workflow_dispatch" }, null, English).Text);
        // PR、commit、其他 event 與觸發者兩種語言相同。
        Assert.Equal("Pull request #17", DisplayFormatter.Trigger(run with { Event = "pull_request" }, 17, English).Text);
        Assert.Equal("Commit dcf27ad", DisplayFormatter.Trigger(run, null, English).Text);
        Assert.Equal("dynamic", DisplayFormatter.Trigger(run with { Event = "dynamic" }, null, English).Text);
        Assert.Equal("by alice", DisplayFormatter.ActorText(run));
    }
}
