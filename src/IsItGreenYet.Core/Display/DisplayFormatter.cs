using System.Globalization;
using System.Text;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Display;

/// <summary>觸發說明可點的部分（spec §10.6）。</summary>
public enum TriggerLinkKind
{
    None,
    PullRequest,
    Commit,
}

public sealed record TriggerText(string Text, TriggerLinkKind LinkKind, int? PullRequestNumber);

/// <summary>畫面上所有時間、耗時與觸發說明文字（spec §8、§10.6、§10.8）。時間一律換成本地時區。</summary>
/// <remarks>
/// 文字依語言產生（<see cref="TextTable"/>）：沒有語言參數的多載用共用的目前語言（<see cref="UiText.Current"/>）。
/// 耗時（<c>45s</c>、<c>3m 12s</c>、<c>1h 05m</c>）、<c>Pull request #17</c>、<c>Commit dcf27ad</c>、<c>by alice</c> 兩種語言相同。
/// </remarks>
public sealed class DisplayFormatter(TimeProvider time)
{
    // spec §10.8 的顯示格式（今年、更早的格式依語言，在字串表裡）。月、日不補零，時、分補零；一律用 InvariantCulture，
    // 否則自訂格式裡的「:」「/」會被換成目前文化的分隔符號，英文的月份縮寫（Oct）也固定是英文。
    private const string TodayFormat = "HH:mm";
    private const string FullFormat = "yyyy-MM-dd HH:mm:ss";

    // spec §8.2 的文字，「·」是 U+00B7。
    private const string Separator = " · ";

    private const string PullRequestEvent = "pull_request";
    private const string PushEvent = "push";
    private const string ScheduleEvent = "schedule";
    private const string WorkflowDispatchEvent = "workflow_dispatch";

    // 換行字元：CR、LF，以及 WPF 也會斷行的 NEL、LINE SEPARATOR、PARAGRAPH SEPARATOR。
    private const char NextLine = (char)0x0085;
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    private readonly TimeProvider _time = time;

    /// <summary>剛剛／N 分鐘前／今天 HH:mm／M/d HH:mm／yyyy/M/d（英文：Just now／N min ago／Today HH:mm／MMM d HH:mm／MMM d, yyyy）。</summary>
    public string StartTime(DateTimeOffset startUtc) => StartTime(startUtc, UiText.Current);

    public string StartTime(DateTimeOffset startUtc, AppLanguage language)
    {
        var text = UiText.Of(language);
        var nowUtc = _time.GetUtcNow();

        // 「多久以前」用兩個瞬間直接相減，不看本地牆上時間，日光節約切換時才不會差一小時。
        // 開始時間晚於現在（時鐘誤差）時差值為負，同樣落在「剛剛」。
        var ago = nowUtc - startUtc;
        if (ago < Tuning.JustNowThreshold)
        {
            return text.TimeJustNow;
        }

        // 「1 小時內」優先於「今天」（00:30 看 23:50 開始的 run 顯示 40 分鐘前）。
        if (ago < Tuning.RelativeTimeWindow)
        {
            return UiText.Format(text.TimeMinutesAgo, ago.Ticks / TimeSpan.TicksPerMinute);
        }

        var zone = _time.LocalTimeZone;
        var localStart = TimeZoneInfo.ConvertTime(startUtc, zone);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);

        if (localStart.Date == localNow.Date)
        {
            return UiText.Format(text.TimeToday, localStart.ToString(TodayFormat, CultureInfo.InvariantCulture));
        }

        return localStart.Year == localNow.Year
            ? localStart.ToString(text.TimeThisYearFormat, CultureInfo.InvariantCulture)
            : localStart.ToString(text.TimeOlderFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>tooltip 用的完整本地時間 <c>yyyy-MM-dd HH:mm:ss</c>（兩種語言相同）。</summary>
    public string FullLocal(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, _time.LocalTimeZone).ToString(FullFormat, CultureInfo.InvariantCulture);

    /// <summary><c>45s</c>、<c>3m 12s</c>、<c>1h 05m</c>（兩種語言相同）。</summary>
    /// <remarks>實測值，不足的部分無條件捨去；1 小時以上不顯示秒（spec §8.1 暫定格式）。負值當成 0。</remarks>
    public static string Duration(TimeSpan duration)
    {
        var d = NonNegative(duration);

        if (d.Ticks < TimeSpan.TicksPerMinute)
        {
            return $"{d.Seconds}s";
        }

        if (d.Ticks < TimeSpan.TicksPerHour)
        {
            return $"{d.Minutes}m {d.Seconds:00}s";
        }

        // 超過一天仍以總時數表示（例如 25h 00m）。
        return $"{d.Ticks / TimeSpan.TicksPerHour}h {d.Minutes:00}m";
    }

    /// <summary>估算值：無條件進位到分鐘，<c>約 3m</c>（<c>~3m</c>）；不到 1 分鐘為 <c>不到 1m</c>（<c>&lt; 1m</c>）。</summary>
    public static string Approx(TimeSpan duration) => Approx(duration, UiText.Current);

    public static string Approx(TimeSpan duration, AppLanguage language)
    {
        var text = UiText.Of(language);
        return IsUnderOneMinute(duration) ? text.DurationLessThanMinute : UiText.Format(text.DurationApprox, CeilingMinutes(duration));
    }

    /// <summary><c>已執行 4m 12s</c>、<c>已執行 4m 12s · 約剩 3m</c>、<c>已執行 9m 40s · 超過預估約 3m</c>。</summary>
    /// <remarks>
    /// 已執行時間是實測值，顯示到秒；剩餘與超過量是估算值，加「約」並無條件進位到分鐘（spec §8.2）。
    /// 不到 1 分鐘時改成「剩不到 1m」「超過預估不到 1m」。樣本不足（Remaining 與 Overrun 都沒有值）時只顯示已執行時間。
    /// 英文：<c>Elapsed 4m 12s · ~3m left</c>、<c>Elapsed 9m 40s · ~3m over estimate</c>。
    /// </remarks>
    public static string Running(EtaInfo eta) => Running(eta, UiText.Current);

    public static string Running(EtaInfo eta, AppLanguage language)
    {
        var text = UiText.Of(language);
        var elapsed = UiText.Format(text.DurationElapsed, Duration(eta.Elapsed));

        // 只有大於 0 的超過量才算超過預估；這樣不論 Remaining 在超過時是 null 還是 0 都判斷得對。
        if (eta.Overrun is { } overrun && overrun > TimeSpan.Zero)
        {
            return elapsed + Separator + UiText.Format(text.DurationOverrun, Approx(overrun, language));
        }

        if (eta.Remaining is { } remaining)
        {
            var remainingText = IsUnderOneMinute(remaining)
                ? text.DurationRemainingUnderMinute
                : UiText.Format(text.DurationRemaining, CeilingMinutes(remaining));
            return elapsed + Separator + remainingText;
        }

        return elapsed;
    }

    /// <summary><c>排隊 1m 05s</c>（<c>Queued 1m 05s</c>）。</summary>
    public static string Queued(TimeSpan waited) => Queued(waited, UiText.Current);

    public static string Queued(TimeSpan waited, AppLanguage language) =>
        UiText.Format(UiText.Of(language).DurationQueued, Duration(waited));

    /// <summary>
    /// 觸發說明（spec §10.6）。<paramref name="pullRequestNumber"/> 只在 event 是 <c>pull_request</c> 時使用；
    /// event 比對區分大小寫，不認得的 event 照原字串顯示。
    /// </summary>
    public static TriggerText Trigger(RunInfo run, int? pullRequestNumber) => Trigger(run, pullRequestNumber, UiText.Current);

    public static TriggerText Trigger(RunInfo run, int? pullRequestNumber, AppLanguage language)
    {
        var eventName = run.Event ?? string.Empty;
        var text = UiText.Of(language);

        return eventName switch
        {
            PullRequestEvent when pullRequestNumber is { } number && number > 0 =>
                new TriggerText($"Pull request #{number}", TriggerLinkKind.PullRequest, number),
            PullRequestEvent => new TriggerText("Pull request", TriggerLinkKind.None, null),
            // head_sha 是 required，缺值時比照「pull_request 但沒有編號」：只顯示文字、不可點。
            PushEvent when string.IsNullOrWhiteSpace(run.HeadSha) => new TriggerText("Commit", TriggerLinkKind.None, null),
            PushEvent => new TriggerText($"Commit {ShortSha(run.HeadSha)}", TriggerLinkKind.Commit, null),
            ScheduleEvent => new TriggerText(text.TriggerSchedule, TriggerLinkKind.None, null),
            WorkflowDispatchEvent => new TriggerText(text.TriggerManual, TriggerLinkKind.None, null),
            _ => new TriggerText(eventName, TriggerLinkKind.None, null),
        };
    }

    /// <summary>前 <see cref="Tuning.ShortShaLength"/> 碼；較短時原樣回傳。</summary>
    public static string ShortSha(string sha)
    {
        if (string.IsNullOrEmpty(sha))
        {
            return string.Empty;
        }

        return sha.Length <= Tuning.ShortShaLength ? sha : sha[..Tuning.ShortShaLength];
    }

    /// <summary><c>by alice</c>；actor 缺席時回空字串。</summary>
    public static string ActorText(RunInfo run) =>
        string.IsNullOrWhiteSpace(run.ActorLogin) ? string.Empty : "by " + run.ActorLogin;

    /// <summary>把換行換成空白、去掉頭尾空白，讓標題保持單行。</summary>
    /// <remarks>CRLF 算一個換行；連續換行逐一換成空白，不合併。不截斷：過長加「…」由清單 UI 處理（spec §10.5）。</remarks>
    public static string SingleLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                continue;
            }

            builder.Append(c is '\r' or '\n' or NextLine or LineSeparator or ParagraphSeparator ? ' ' : c);
        }

        return builder.ToString().Trim();
    }

    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static bool IsUnderOneMinute(TimeSpan value) => value.Ticks < TimeSpan.TicksPerMinute;

    /// <summary>無條件進位到整分鐘。用除法加餘數判斷，不做 ticks 相加，TimeSpan.MaxValue 也不會溢位。</summary>
    private static long CeilingMinutes(TimeSpan value)
    {
        var minutes = value.Ticks / TimeSpan.TicksPerMinute;
        return value.Ticks % TimeSpan.TicksPerMinute == 0 ? minutes : minutes + 1;
    }
}
