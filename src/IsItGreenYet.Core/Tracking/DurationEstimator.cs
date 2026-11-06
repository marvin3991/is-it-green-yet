using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Tracking;

/// <summary>進行中 run 的已執行時間與預估（spec §8.2）。樣本不足時 Median／Remaining／Overrun／Progress 都是 null。</summary>
public sealed record EtaInfo(
    TimeSpan Elapsed,
    TimeSpan? Median,
    TimeSpan? Remaining,
    TimeSpan? Overrun,
    double? Progress);

/// <summary>
/// 耗時與預估剩餘時間（spec §8）。時間一律用 GitHub 的 UTC 時間戳和呼叫端傳入的牆鐘 now 相減（spec §3.1）。
/// 回傳的都是實際差值，「約」與無條件進位到分鐘交給 DisplayFormatter。
/// </summary>
public static class DurationEstimator
{
    // 偶數筆時取中間兩筆的平均：位置 count / 2 和它前一筆（spec §8.2）。
    private const int Halves = 2;

    // 細進度條的範圍：0 是剛開始，1 是滿格（spec §8.2）。
    private const double ProgressEmpty = 0.0;
    private const double ProgressFull = 1.0;

    /// <summary>已完成 run 的耗時：updated_at − (run_started_at ?? created_at)；未完成回 null，負值當 0。</summary>
    public static TimeSpan? CompletedDuration(RunInfo run)
    {
        ArgumentNullException.ThrowIfNull(run);

        // 等待核准不算完成（RunState.IsCompleted），不產生耗時。
        if (!RunStateMapper.Map(run).IsCompleted)
        {
            return null;
        }

        return NonNegative(run.UpdatedAt - StartOf(run));
    }

    /// <summary>最近 EtaSampleSize 筆的中位數；少於 EtaMinSamples 筆回 null。</summary>
    public static TimeSpan? Median(IReadOnlyList<TimeSpan> samplesNewestFirst)
    {
        ArgumentNullException.ThrowIfNull(samplesNewestFirst);

        // 傳入順序是新到舊，所以前 EtaSampleSize 筆就是最近的；複製一份再排序，不動呼叫端的清單。
        var recent = samplesNewestFirst
            .Take(Tuning.EtaSampleSize)
            .Select(NonNegative)
            .ToArray();

        if (recent.Length < Tuning.EtaMinSamples)
        {
            return null;
        }

        Array.Sort(recent);
        var upper = recent[recent.Length / Halves];
        if (recent.Length % Halves != 0)
        {
            return upper;
        }

        // 兩筆都不是負值，lower + (upper − lower) / 2 不會溢位。
        var lower = recent[recent.Length / Halves - 1];
        return TimeSpan.FromTicks(lower.Ticks + (upper.Ticks - lower.Ticks) / Halves);
    }

    /// <summary>只對 in_progress 的 run 有值；排隊中回 null。</summary>
    public static EtaInfo? Estimate(RunInfo run, IReadOnlyList<TimeSpan> samplesNewestFirst, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(samplesNewestFirst);

        // 排隊中只顯示「排隊 1m 05s」，等待核准與完成的 run 也不預估。
        if (RunStateMapper.Map(run).Display != DisplayState.InProgress)
        {
            return null;
        }

        // GitHub 的時間戳可能比本機快（時鐘誤差），已執行時間不得為負。
        var elapsed = NonNegative(now - StartOf(run));

        if (Median(samplesNewestFirst) is not { } median)
        {
            return new EtaInfo(elapsed, null, null, null, null);
        }

        if (elapsed > median)
        {
            // 超過預估：進度條滿格，改用 Warning 色由 App 決定。
            return new EtaInfo(elapsed, median, null, elapsed - median, ProgressFull);
        }

        // median 為 0 時 elapsed 也只能是 0，視為已到預估時間，避免 0 ÷ 0。
        var progress = median == TimeSpan.Zero
            ? ProgressFull
            : Math.Clamp((double)elapsed.Ticks / median.Ticks, ProgressEmpty, ProgressFull);

        return new EtaInfo(elapsed, median, median - elapsed, null, progress);
    }

    /// <summary>
    /// 排隊中的 run 已經排了多久（「排隊 1m 05s」，spec §8.2）：now − (run_started_at ?? created_at)，負值當 0。
    /// 不看 run_attempt：重跑時 GitHub 保留最初的 created_at，run_started_at 換成這次嘗試的開始時間；
    /// 重跑剛送出時的回應 run_attempt 還是舊的，但 run_started_at 已經是新的。第 1 次嘗試的 run_started_at 和 created_at 相同。
    /// </summary>
    public static TimeSpan QueuedDuration(RunInfo run, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(run);

        return NonNegative(now - StartOf(run));
    }

    /// <summary>重跑會重設 run_started_at，所以算的是最新一次 attempt；缺值時退回 created_at（spec §8.1）。</summary>
    private static DateTimeOffset StartOf(RunInfo run) => run.RunStartedAt ?? run.CreatedAt;

    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
