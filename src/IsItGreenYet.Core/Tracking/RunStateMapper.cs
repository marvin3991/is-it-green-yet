using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Tracking;

/// <summary>
/// 把 API 的 status／conclusion 字串轉成顯示狀態與判定類別（spec §7.1）。
/// run 的 status／conclusion 在 schema 裡沒有 enum，遇到沒見過的值一律不丟例外。
/// <see cref="RunState.RawValue"/>：完成的 run 放 conclusion，未完成的放 status；不認得時呼叫端可據此寫 error.log。
/// </summary>
/// <remarks>
/// <see cref="RunState.Label"/> 依語言產生：沒有語言參數的多載用共用的目前語言（<see cref="UiText.Current"/>），
/// Engine 明確傳入自己設定的語言。顯示狀態與判定類別和語言無關。
/// </remarks>
public static class RunStateMapper
{
    private const string Completed = "completed";
    private const string StartupFailure = "startup_failure";
    private const string TimedOut = "timed_out";
    private const string Neutral = "neutral";
    private const string Stale = "stale";

    public static RunState Map(string? status, string? conclusion) => Map(status, conclusion, UiText.Current);

    public static RunState Map(string? status, string? conclusion, AppLanguage language)
    {
        var (display, category, raw) = Classify(status, conclusion);
        return new RunState(display, category, Label(display, raw, language), raw);
    }

    public static RunState Map(RunInfo run) => Map(run.Status, run.Conclusion);

    public static RunState Map(RunInfo run, AppLanguage language) => Map(run.Status, run.Conclusion, language);

    public static RunState Map(RunAttemptRecord attempt) => Map(attempt.Status, attempt.Conclusion);

    /// <summary>同一個狀態換成另一種語言的 <see cref="RunState.Label"/>（例如 App 切換語言後，沿用上一份快照的列）。</summary>
    public static RunState InLanguage(RunState state, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { Label = Label(state.Display, state.RawValue, language) };
    }

    /// <summary>
    /// 畫面上的顯示狀態，例如「失敗（逾時）」：顯示狀態決定主要的字，原字串決定括號裡的細分（逾時、啟動失敗、neutral、stale）與未知的值。
    /// </summary>
    public static string Label(DisplayState display, string? rawValue, AppLanguage language)
    {
        var text = UiText.Of(language);
        return display switch
        {
            DisplayState.Queued => text.RunQueued,
            DisplayState.InProgress => text.RunInProgress,
            DisplayState.Waiting => text.RunWaiting,
            DisplayState.Success => text.RunSucceeded,
            DisplayState.Failure => rawValue switch
            {
                TimedOut => text.RunTimedOut,
                StartupFailure => text.RunStartupFailure,
                _ => text.RunFailed,
            },
            DisplayState.Cancelled => text.RunCancelled,
            DisplayState.Skipped => rawValue switch
            {
                Neutral => text.RunNeutral,
                Stale => text.RunStale,
                _ => text.RunSkipped,
            },
            _ => rawValue is null ? text.RunUnknown : UiText.Format(text.RunUnknownValue, rawValue),
        };
    }

    private static (DisplayState Display, RunCategory Category, string? Raw) Classify(string? status, string? conclusion)
    {
        status = NullIfBlank(status);
        conclusion = NullIfBlank(conclusion);

        // startup_failure 多數官方來源放在 conclusion，少數放在 status，兩邊都認。
        if (string.Equals(status, StartupFailure, StringComparison.Ordinal))
        {
            return (DisplayState.Failure, RunCategory.Failure, StartupFailure);
        }

        if (!string.Equals(status, Completed, StringComparison.Ordinal))
        {
            return ClassifyIncomplete(status);
        }

        return conclusion switch
        {
            "success" => (DisplayState.Success, RunCategory.Success, conclusion),
            "failure" or TimedOut or StartupFailure => (DisplayState.Failure, RunCategory.Failure, conclusion),
            "action_required" => (DisplayState.Waiting, RunCategory.Waiting, conclusion),
            "cancelled" => (DisplayState.Cancelled, RunCategory.Gray, conclusion),
            "skipped" or Neutral or Stale => (DisplayState.Skipped, RunCategory.Gray, conclusion),
            // 只有 completed 而且結論不認得時才歸灰。
            _ => (DisplayState.Unknown, RunCategory.Gray, conclusion),
        };
    }

    private static (DisplayState Display, RunCategory Category, string? Raw) ClassifyIncomplete(string? status) => status switch
    {
        "queued" or "requested" or "pending" => (DisplayState.Queued, RunCategory.Pending, status),
        "in_progress" => (DisplayState.InProgress, RunCategory.Pending, status),
        "waiting" => (DisplayState.Waiting, RunCategory.Waiting, status),
        // 完成前不認得的狀態一律當未完成，避免 GitHub 新增執行中的值時系統匣誤顯示綠燈。
        _ => (DisplayState.Unknown, RunCategory.Pending, status),
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
