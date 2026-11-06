using System.Globalization;
using IsItGreenYet.Core.Display;
using IsItGreenYet.Core.Tests.TestSupport;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Display;

public class DisplayFormatterTests
{
    private static readonly TimeSpan TaipeiOffset = TimeSpan.FromHours(8);

    /// <summary>觀看時間：台北 2026-10-24 10:00（UTC+8，沒有日光節約）。</summary>
    private static readonly DateTimeOffset TaipeiNow = Taipei(2026, 10, 24, 10, 0);

    private static TimeZoneInfo NewYork => TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static DateTimeOffset Taipei(int year, int month, int day, int hour, int minute, int second = 0) =>
        new(year, month, day, hour, minute, second, TaipeiOffset);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static DisplayFormatter TaipeiFormatter(DateTimeOffset now) =>
        new(new ManualTimeProvider(now, ManualTimeProvider.Taipei));

    // ── 開始時間（spec §10.8）──────────────────────────────────────────

    [Fact]
    public void 未來時間顯示剛剛()
    {
        var formatter = TaipeiFormatter(TaipeiNow);

        // GitHub 的時間戳比本機快（時鐘誤差），不顯示負的分鐘數。
        Assert.Equal("剛剛", formatter.StartTime(TaipeiNow.AddSeconds(5)));
        Assert.Equal("剛剛", formatter.StartTime(TaipeiNow.AddMinutes(40)));
        Assert.Equal("剛剛", formatter.StartTime(TaipeiNow.AddDays(3)));
    }

    [Fact]
    public void 開始於59秒前顯示剛剛()
    {
        Assert.Equal("剛剛", TaipeiFormatter(TaipeiNow).StartTime(TaipeiNow.AddSeconds(-59)));
    }

    [Fact]
    public void 剛好60秒改顯示1分鐘前()
    {
        Assert.Equal("1 分鐘前", TaipeiFormatter(TaipeiNow).StartTime(TaipeiNow.AddSeconds(-60)));
    }

    [Fact]
    public void 開始於40分鐘前顯示40分鐘前()
    {
        Assert.Equal("40 分鐘前", TaipeiFormatter(TaipeiNow).StartTime(TaipeiNow.AddMinutes(-40)));
    }

    [Fact]
    public void 分鐘數無條件捨去且不到1小時都顯示分鐘()
    {
        var formatter = TaipeiFormatter(TaipeiNow);

        Assert.Equal("40 分鐘前", formatter.StartTime(TaipeiNow.AddMinutes(-40).AddSeconds(-59)));
        Assert.Equal("59 分鐘前", formatter.StartTime(TaipeiNow.AddMinutes(-60).AddSeconds(1)));
    }

    [Fact]
    public void 剛好1小時改顯示今天()
    {
        Assert.Equal("今天 09:00", TaipeiFormatter(TaipeiNow).StartTime(TaipeiNow.AddHours(-1)));
    }

    [Fact]
    public void 今天0832()
    {
        Assert.Equal("今天 08:32", TaipeiFormatter(TaipeiNow).StartTime(Taipei(2026, 10, 24, 8, 32)));
    }

    [Fact]
    public void 今天的判斷看本地日期()
    {
        // 台北 00:05 開始的 run，在 UTC 是前一天 16:05；仍要算「今天」。
        Assert.Equal("今天 00:05", TaipeiFormatter(TaipeiNow).StartTime(Taipei(2026, 10, 24, 0, 5)));
    }

    [Fact]
    public void 今年()
    {
        var formatter = TaipeiFormatter(TaipeiNow);

        Assert.Equal("10/23 23:50", formatter.StartTime(Taipei(2026, 10, 23, 23, 50)));
        // 月、日不補零，時、分補零。
        Assert.Equal("1/5 07:03", formatter.StartTime(Taipei(2026, 1, 5, 7, 3)));
    }

    [Fact]
    public void 跨年()
    {
        Assert.Equal("2025/12/31", TaipeiFormatter(TaipeiNow).StartTime(Taipei(2025, 12, 31, 23, 50)));
        // 元旦早上看前一晚的 run：不是今天、也不是今年，只顯示日期。
        Assert.Equal("2025/12/31", TaipeiFormatter(Taipei(2026, 1, 1, 8, 0)).StartTime(Taipei(2025, 12, 31, 23, 0)));
        Assert.Equal("2024/3/9", TaipeiFormatter(TaipeiNow).StartTime(Taipei(2024, 3, 9, 12, 0)));
    }

    [Fact]
    public void 跨午夜0030看2350顯示40分鐘前()
    {
        var formatter = TaipeiFormatter(Taipei(2026, 10, 24, 0, 30));

        Assert.Equal("40 分鐘前", formatter.StartTime(Taipei(2026, 10, 23, 23, 50)));
    }

    [Fact]
    public void 跨午夜0110看2350顯示日期()
    {
        var formatter = TaipeiFormatter(Taipei(2026, 10, 24, 1, 10));

        Assert.Equal("10/23 23:50", formatter.StartTime(Taipei(2026, 10, 23, 23, 50)));
    }

    [Fact]
    public void 日光節約時區()
    {
        // 美東 2026-11-01 06:00 UTC（當地 02:00 EDT）結束日光節約，改回 EST（UTC−5）。
        var formatter = new DisplayFormatter(new ManualTimeProvider(Utc(2026, 11, 1, 15, 0), NewYork));

        // 轉換前用 EDT（UTC−4）：04:30 UTC 是當地 00:30；誤用 UTC−5 會變成前一天 23:30。
        Assert.Equal("今天 00:30", formatter.StartTime(Utc(2026, 11, 1, 4, 30)));
        Assert.Equal("10/31 10:00", formatter.StartTime(Utc(2026, 10, 31, 14, 0)));
        // 轉換後用 EST（UTC−5）：07:15 UTC 是當地 02:15；誤用 UTC−4 會變成 03:15。
        Assert.Equal("今天 02:15", formatter.StartTime(Utc(2026, 11, 1, 7, 15)));
        Assert.Equal("2026-11-01 01:30:00", formatter.FullLocal(Utc(2026, 11, 1, 5, 30)));
        Assert.Equal("2026-11-01 01:30:00", formatter.FullLocal(Utc(2026, 11, 1, 6, 30)));
    }

    [Fact]
    public void 日光節約結束時1小時內以實際經過時間計算()
    {
        // 當地 01:10 EST 看 01:30 EDT 開始的 run：牆上時間看起來在未來，實際已經過 40 分鐘。
        var formatter = new DisplayFormatter(new ManualTimeProvider(Utc(2026, 11, 1, 6, 10), NewYork));

        Assert.Equal("40 分鐘前", formatter.StartTime(Utc(2026, 11, 1, 5, 30)));
    }

    [Fact]
    public void 輸入帶時差的時間以同一瞬間計算()
    {
        var formatter = TaipeiFormatter(TaipeiNow);

        Assert.Equal("40 分鐘前", formatter.StartTime(TaipeiNow.ToUniversalTime().AddMinutes(-40)));
        Assert.Equal("今天 08:32", formatter.StartTime(Utc(2026, 10, 24, 0, 32)));
        Assert.Equal("10/23 23:50", formatter.StartTime(new DateTimeOffset(2026, 10, 23, 10, 50, 0, TimeSpan.FromHours(-5))));
    }

    [Fact]
    public void 本地時區變更後依新時區換算()
    {
        var time = new ManualTimeProvider(Utc(2026, 10, 24, 2, 0), ManualTimeProvider.Taipei);
        var formatter = new DisplayFormatter(time);
        var start = Utc(2026, 10, 24, 0, 32);

        Assert.Equal("今天 08:32", formatter.StartTime(start));

        time.SetLocalTimeZone(TimeZoneInfo.Utc);

        Assert.Equal("今天 00:32", formatter.StartTime(start));
    }

    [Fact]
    public void 時間格式不受目前文化設定影響()
    {
        var original = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.TimeSeparator = ".";
        culture.DateTimeFormat.DateSeparator = "-";
        try
        {
            CultureInfo.CurrentCulture = culture;
            var formatter = TaipeiFormatter(TaipeiNow);

            Assert.Equal("今天 08:32", formatter.StartTime(Taipei(2026, 10, 24, 8, 32)));
            Assert.Equal("10/23 23:50", formatter.StartTime(Taipei(2026, 10, 23, 23, 50)));
            Assert.Equal("2025/12/31", formatter.StartTime(Taipei(2025, 12, 31, 23, 50)));
            Assert.Equal("2026-10-24 08:32:05", formatter.FullLocal(Taipei(2026, 10, 24, 8, 32, 5)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void 極端時間不丟例外()
    {
        var taipei = TaipeiFormatter(TaipeiNow);
        var newYork = new DisplayFormatter(new ManualTimeProvider(Utc(2026, 11, 1, 15, 0), NewYork));

        Assert.Equal("0001/1/1", taipei.StartTime(DateTimeOffset.MinValue));
        Assert.Equal("剛剛", taipei.StartTime(DateTimeOffset.MaxValue));
        Assert.Null(Record.Exception(() => newYork.StartTime(DateTimeOffset.MinValue)));
        Assert.Null(Record.Exception(() => newYork.FullLocal(DateTimeOffset.MinValue)));
        Assert.Null(Record.Exception(() => taipei.FullLocal(DateTimeOffset.MaxValue)));
    }

    [Fact]
    public void 完整本地時間格式()
    {
        var formatter = TaipeiFormatter(TaipeiNow);

        Assert.Equal("2026-10-24 08:32:05", formatter.FullLocal(Utc(2026, 10, 24, 0, 32).AddSeconds(5)));
        Assert.Equal("2026-01-05 07:03:09", formatter.FullLocal(Taipei(2026, 1, 5, 7, 3, 9)));
    }

    // ── 耗時（spec §8.1）───────────────────────────────────────────────

    [Fact]
    public void 耗時45秒()
    {
        Assert.Equal("45s", DisplayFormatter.Duration(TimeSpan.FromSeconds(45)));
    }

    [Fact]
    public void 耗時192秒()
    {
        Assert.Equal("3m 12s", DisplayFormatter.Duration(TimeSpan.FromSeconds(192)));
    }

    [Fact]
    public void 耗時3900秒()
    {
        Assert.Equal("1h 05m", DisplayFormatter.Duration(TimeSpan.FromSeconds(3900)));
    }

    [Fact]
    public void 耗時0秒()
    {
        Assert.Equal("0s", DisplayFormatter.Duration(TimeSpan.Zero));
    }

    [Theory]
    [InlineData(59, "59s")]
    [InlineData(60, "1m 00s")]
    [InlineData(65, "1m 05s")]
    [InlineData(3599, "59m 59s")]
    [InlineData(3600, "1h 00m")]
    [InlineData(3659, "1h 00m")]
    [InlineData(90000, "25h 00m")]
    public void 耗時邊界值(int seconds, string expected)
    {
        Assert.Equal(expected, DisplayFormatter.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void 耗時捨去不足一秒的部分()
    {
        Assert.Equal("59s", DisplayFormatter.Duration(TimeSpan.FromMilliseconds(59_900)));
        Assert.Equal("0s", DisplayFormatter.Duration(TimeSpan.FromMilliseconds(999)));
    }

    [Fact]
    public void 負的耗時當成0()
    {
        Assert.Equal("0s", DisplayFormatter.Duration(TimeSpan.FromSeconds(-5)));
        Assert.Equal("0s", DisplayFormatter.Duration(TimeSpan.MinValue));
    }

    [Fact]
    public void 極大耗時不丟例外()
    {
        Assert.EndsWith("m", DisplayFormatter.Duration(TimeSpan.MaxValue));
    }

    // ── 估算值（spec §8.2）─────────────────────────────────────────────

    [Fact]
    public void 估算2分10秒()
    {
        Assert.Equal("約 3m", DisplayFormatter.Approx(new TimeSpan(0, 2, 10)));
    }

    [Fact]
    public void 估算剛好3分()
    {
        Assert.Equal("約 3m", DisplayFormatter.Approx(TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void 估算40秒()
    {
        Assert.Equal("不到 1m", DisplayFormatter.Approx(TimeSpan.FromSeconds(40)));
    }

    [Fact]
    public void 估算邊界值()
    {
        Assert.Equal("不到 1m", DisplayFormatter.Approx(TimeSpan.Zero));
        Assert.Equal("不到 1m", DisplayFormatter.Approx(TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1)));
        Assert.Equal("約 1m", DisplayFormatter.Approx(TimeSpan.FromMinutes(1)));
        Assert.Equal("約 2m", DisplayFormatter.Approx(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1)));
        Assert.Equal("約 75m", DisplayFormatter.Approx(TimeSpan.FromMinutes(75)));
    }

    [Fact]
    public void 負的估算值當成不到1分鐘()
    {
        Assert.Equal("不到 1m", DisplayFormatter.Approx(TimeSpan.FromMinutes(-3)));
        Assert.Equal("不到 1m", DisplayFormatter.Approx(TimeSpan.MinValue));
    }

    [Fact]
    public void 極大估算值不溢位()
    {
        var text = DisplayFormatter.Approx(TimeSpan.MaxValue);

        Assert.StartsWith("約 ", text);
        Assert.DoesNotContain("-", text);
    }

    // ── 已執行與排隊（spec §8.2）──────────────────────────────────────

    [Fact]
    public void 樣本不足只顯示已執行()
    {
        var eta = new EtaInfo(new TimeSpan(0, 4, 12), Median: null, Remaining: null, Overrun: null, Progress: null);

        Assert.Equal("已執行 4m 12s", DisplayFormatter.Running(eta));
    }

    [Fact]
    public void 在預估內()
    {
        var eta = new EtaInfo(new TimeSpan(0, 4, 12), TimeSpan.FromMinutes(7), new TimeSpan(0, 2, 48), Overrun: null, Progress: 0.6);

        Assert.Equal("已執行 4m 12s · 約剩 3m", DisplayFormatter.Running(eta));
    }

    [Fact]
    public void 超過預估()
    {
        var eta = new EtaInfo(new TimeSpan(0, 9, 40), new TimeSpan(0, 6, 50), Remaining: null, new TimeSpan(0, 2, 50), Progress: 1);

        Assert.Equal("已執行 9m 40s · 超過預估約 3m", DisplayFormatter.Running(eta));
    }

    [Fact]
    public void 負的elapsed當成0()
    {
        Assert.Equal("已執行 0s", DisplayFormatter.Running(new EtaInfo(TimeSpan.FromSeconds(-30), null, null, null, null)));
        Assert.Equal(
            "已執行 0s · 約剩 5m",
            DisplayFormatter.Running(new EtaInfo(TimeSpan.FromSeconds(-30), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), null, 0)));
    }

    [Fact]
    public void 剩不到1分鐘時顯示剩不到1m()
    {
        var almostDone = new EtaInfo(new TimeSpan(0, 6, 20), TimeSpan.FromMinutes(7), TimeSpan.FromSeconds(40), null, 0.9);
        var exactlyAtMedian = new EtaInfo(TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(7), TimeSpan.Zero, null, 1);

        Assert.Equal("已執行 6m 20s · 剩不到 1m", DisplayFormatter.Running(almostDone));
        Assert.Equal("已執行 7m 00s · 剩不到 1m", DisplayFormatter.Running(exactlyAtMedian));
    }

    [Fact]
    public void 超過預估不到1分鐘()
    {
        var eta = new EtaInfo(new TimeSpan(0, 7, 20), TimeSpan.FromMinutes(7), null, TimeSpan.FromSeconds(20), 1);

        Assert.Equal("已執行 7m 20s · 超過預估不到 1m", DisplayFormatter.Running(eta));
    }

    [Fact]
    public void 剩餘與超過量同時有值時以大於0的超過量為準()
    {
        var over = new EtaInfo(new TimeSpan(0, 9, 40), new TimeSpan(0, 6, 50), TimeSpan.Zero, new TimeSpan(0, 2, 50), 1);
        var within = new EtaInfo(new TimeSpan(0, 4, 12), TimeSpan.FromMinutes(7), new TimeSpan(0, 2, 48), TimeSpan.Zero, 0.6);

        Assert.Equal("已執行 9m 40s · 超過預估約 3m", DisplayFormatter.Running(over));
        Assert.Equal("已執行 4m 12s · 約剩 3m", DisplayFormatter.Running(within));
    }

    [Fact]
    public void 負的剩餘時間不顯示負值()
    {
        var eta = new EtaInfo(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(-10), null, 1);

        Assert.Equal("已執行 5m 00s · 剩不到 1m", DisplayFormatter.Running(eta));
    }

    [Fact]
    public void 排隊時間顯示到秒()
    {
        Assert.Equal("排隊 1m 05s", DisplayFormatter.Queued(TimeSpan.FromSeconds(65)));
        Assert.Equal("排隊 45s", DisplayFormatter.Queued(TimeSpan.FromSeconds(45)));
    }

    [Fact]
    public void 負的排隊時間當成0()
    {
        Assert.Equal("排隊 0s", DisplayFormatter.Queued(TimeSpan.FromSeconds(-3)));
    }

    // ── 觸發說明（spec §10.6）──────────────────────────────────────────

    [Fact]
    public void 觸發說明pull_request有編號()
    {
        var run = DisplaySamples.Run with { Event = "pull_request" };

        Assert.Equal(new TriggerText("Pull request #17", TriggerLinkKind.PullRequest, 17), DisplayFormatter.Trigger(run, 17));
    }

    [Fact]
    public void 觸發說明pull_request沒有編號()
    {
        var run = DisplaySamples.Run with { Event = "pull_request" };

        Assert.Equal(new TriggerText("Pull request", TriggerLinkKind.None, null), DisplayFormatter.Trigger(run, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 觸發說明pull_request編號不合理時當成沒有編號(int number)
    {
        var run = DisplaySamples.Run with { Event = "pull_request" };

        Assert.Equal(new TriggerText("Pull request", TriggerLinkKind.None, null), DisplayFormatter.Trigger(run, number));
    }

    [Fact]
    public void 觸發說明push顯示短sha()
    {
        Assert.Equal(new TriggerText("Commit dcf27ad", TriggerLinkKind.Commit, null), DisplayFormatter.Trigger(DisplaySamples.Run, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 觸發說明push沒有sha時不可點(string sha)
    {
        var run = DisplaySamples.Run with { HeadSha = sha };

        Assert.Equal(new TriggerText("Commit", TriggerLinkKind.None, null), DisplayFormatter.Trigger(run, null));
    }

    [Theory]
    [InlineData("schedule", "排程")]
    [InlineData("workflow_dispatch", "手動觸發")]
    [InlineData("dynamic", "dynamic")]
    [InlineData("workflow_run", "workflow_run")]
    [InlineData("pull_request_target", "pull_request_target")]
    [InlineData("release", "release")]
    public void 觸發說明不可點的事件(string eventName, string expected)
    {
        var run = DisplaySamples.Run with { Event = eventName };

        Assert.Equal(new TriggerText(expected, TriggerLinkKind.None, null), DisplayFormatter.Trigger(run, 17));
    }

    [Fact]
    public void 觸發說明只有pull_request帶PR編號()
    {
        // push 有 PR 編號時仍顯示 commit（§10.6 依 event 決定，不依有沒有編號）。
        Assert.Equal(new TriggerText("Commit dcf27ad", TriggerLinkKind.Commit, null), DisplayFormatter.Trigger(DisplaySamples.Run, 17));
    }

    [Fact]
    public void 觸發說明的event比對區分大小寫()
    {
        var run = DisplaySamples.Run with { Event = "Pull_Request" };

        Assert.Equal(new TriggerText("Pull_Request", TriggerLinkKind.None, null), DisplayFormatter.Trigger(run, 17));
    }

    [Fact]
    public void 觸發說明event缺值時留白()
    {
        Assert.Equal(new TriggerText("", TriggerLinkKind.None, null), DisplayFormatter.Trigger(DisplaySamples.Run with { Event = "" }, null));
        Assert.Equal(new TriggerText("", TriggerLinkKind.None, null), DisplayFormatter.Trigger(DisplaySamples.Run with { Event = null! }, null));
    }

    // ── 短 sha、觸發者、標題 ─────────────────────────────────────────

    [Fact]
    public void 短sha取前7碼()
    {
        Assert.Equal(Tuning.ShortShaLength, DisplayFormatter.ShortSha(DisplaySamples.Sha).Length);
        Assert.Equal("dcf27ad", DisplayFormatter.ShortSha(DisplaySamples.Sha));
    }

    [Fact]
    public void sha不足7碼時原樣回傳()
    {
        Assert.Equal("abc", DisplayFormatter.ShortSha("abc"));
        Assert.Equal("dcf27ad", DisplayFormatter.ShortSha("dcf27ad"));
        Assert.Equal("", DisplayFormatter.ShortSha(""));
        Assert.Equal("", DisplayFormatter.ShortSha(null!));
    }

    [Fact]
    public void 有actor時顯示by()
    {
        Assert.Equal("by alice", DisplayFormatter.ActorText(DisplaySamples.Run));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void actor缺席時by留白(string? login)
    {
        var run = DisplaySamples.Run with { ActorId = null, ActorLogin = login };

        Assert.Equal("", DisplayFormatter.ActorText(run));
    }

    [Fact]
    public void display_title含換行時單行化()
    {
        Assert.Equal("Fix login and logout flow", DisplayFormatter.SingleLine("Fix login\r\nand logout\nflow\r"));
        Assert.Equal("Fix", DisplayFormatter.SingleLine("  Fix\n  "));
    }

    [Theory]
    [InlineData("a\rb")]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    public void 各種換行字元都換成一個空白(string text)
    {
        Assert.Equal("a b", DisplayFormatter.SingleLine(text));
    }

    [Theory]
    [InlineData(0x0085)] // NEL
    [InlineData(0x2028)] // LINE SEPARATOR
    [InlineData(0x2029)] // PARAGRAPH SEPARATOR
    public void Unicode的換行字元也換成空白(int codePoint)
    {
        var text = "a" + (char)codePoint + "b";

        Assert.Equal("a b", DisplayFormatter.SingleLine(text));
    }

    [Fact]
    public void 連續換行逐一換成空白()
    {
        Assert.Equal("Fix  details", DisplayFormatter.SingleLine("Fix\n\ndetails"));
        Assert.Equal("Fix  details", DisplayFormatter.SingleLine("Fix\r\n\r\ndetails"));
    }

    [Fact]
    public void 超長標題不截斷也不丟例外()
    {
        // 截斷加「…」是清單 UI 的工作（spec §10.5），這裡保留全文給 tooltip。
        var title = new string('長', 10_000) + "\n尾";

        var line = DisplayFormatter.SingleLine(title);

        Assert.Equal(10_002, line.Length);
        Assert.EndsWith(" 尾", line);
    }

    [Fact]
    public void 標題為空或null時留白()
    {
        Assert.Equal("", DisplayFormatter.SingleLine(""));
        Assert.Equal("", DisplayFormatter.SingleLine("\r\n"));
        Assert.Equal("", DisplayFormatter.SingleLine(null!));
    }

    [Fact]
    public void name與actor與head_branch缺值時不丟例外且留白()
    {
        var run = DisplaySamples.Run with
        {
            WorkflowName = null,
            DisplayTitle = "",
            Event = "pull_request",
            HeadBranch = null,
            ActorId = null,
            ActorLogin = null,
            TriggeringActorLogin = null,
            PullRequestNumbers = [],
            RunStartedAt = null,
        };
        var formatter = TaipeiFormatter(TaipeiNow);

        Assert.Equal("", DisplayFormatter.SingleLine(run.DisplayTitle));
        Assert.Equal("", DisplayFormatter.ActorText(run));
        Assert.Equal(new TriggerText("Pull request", TriggerLinkKind.None, null), DisplayFormatter.Trigger(run, null));
        Assert.Null(WebLinks.Branch(run, DisplaySamples.Repo));
        Assert.Empty(PrNumberResolver.Resolve(run, openPullRequests: null, storedNumbers: []));
        Assert.Null(Record.Exception(() => formatter.StartTime(run.RunStartedAt ?? run.CreatedAt)));
    }
}
