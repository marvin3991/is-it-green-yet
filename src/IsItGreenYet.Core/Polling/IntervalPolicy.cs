using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Polling;

/// <summary>輪詢間隔（spec §6.1、§6.2、§6.4）。純函式；回 null 代表暫停（不排週期性輪詢）。</summary>
public static class IntervalPolicy
{
    /// <summary>不拉長時的倍率；k 只會是 1 或 StretchMin～StretchMax（spec §6.4）。</summary>
    private const double NoStretch = 1;

    /// <summary>單一目標錯誤退避：上次間隔 × 2（spec §6.4）。</summary>
    private const double ErrorBackoffFactor = 2;

    // 「自動更新」下拉的固定秒數選項（spec §10.3：30 秒、1 分、5 分）。
    private static readonly TimeSpan FixedSeconds30 = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FixedMinute1 = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FixedMinutes5 = TimeSpan.FromMinutes(5);

    /// <summary>低額度拉長倍率 k；ratio 為 null（還不知道額度）時回 1。</summary>
    public static double StretchFactor(double? remainingRatio)
    {
        // NaN（例如 limit 為 0 算不出比例）和還不知道額度一樣，不拉長。
        if (remainingRatio is not { } ratio || double.IsNaN(ratio) || ratio >= Tuning.LowQuotaRatio)
        {
            return NoStretch;
        }

        // ratio = 0（或不合理的負值）直接用上限，避免除以 0。
        if (ratio <= 0)
        {
            return Tuning.StretchMax;
        }

        return Math.Clamp(Tuning.LowQuotaRatio / ratio, Tuning.StretchMin, Tuning.StretchMax);
    }

    /// <summary>max(所有 run 的 created_at 與 updated_at, pushed_at)；都沒有回 null。</summary>
    public static DateTimeOffset? LastActivity(IEnumerable<RunInfo> runs, DateTimeOffset? pushedAt)
    {
        ArgumentNullException.ThrowIfNull(runs);

        // DateTimeOffset 依實際時刻比較，時區不同也正確。
        var latest = pushedAt;
        foreach (var run in runs)
        {
            var runLatest = run.UpdatedAt > run.CreatedAt ? run.UpdatedAt : run.CreatedAt;
            if (latest is null || runLatest > latest)
            {
                latest = runLatest;
            }
        }

        return latest;
    }

    public static TimeSpan? RepoRunsInterval(
        bool repoRunning,
        DateTimeOffset? lastActivity,
        DateTimeOffset now,
        RefreshMode mode,
        double k,
        bool locked)
    {
        if (mode == RefreshMode.Paused)
        {
            return null;
        }

        // 固定秒數模式：base 換成該秒數，有 run 在跑也一樣（spec §6.2）。
        var baseInterval = FixedModeInterval(mode)
            ?? (repoRunning ? Tuning.ActiveRepoInterval : QuietInterval(lastActivity, now));
        return Stretch(baseInterval, k, locked);
    }

    /// <param name="followsRefreshMode">true 時（push 探測），固定模式改用該模式的秒數。</param>
    public static TimeSpan? Periodic(TimeSpan baseInterval, RefreshMode mode, double k, bool locked, bool followsRefreshMode)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseInterval, TimeSpan.Zero);

        if (mode == RefreshMode.Paused)
        {
            return null;
        }

        var interval = followsRefreshMode ? (FixedModeInterval(mode) ?? baseInterval) : baseInterval;
        return Stretch(interval, k, locked);
    }

    /// <summary>max(normal, min(previous × 2, RepoErrorBackoffMax × k))：退避後不會比正常間隔短。</summary>
    public static TimeSpan ErrorBackoff(TimeSpan normal, TimeSpan previous, double k)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(normal, TimeSpan.Zero);

        var cap = Multiply(Tuning.RepoErrorBackoffMax, NormalizeStretch(k));
        if (previous < TimeSpan.Zero)
        {
            previous = TimeSpan.Zero;
        }

        // 先和上限比再加倍，previous 很大時不會溢位。
        var doubled = previous >= cap ? cap : previous * ErrorBackoffFactor;
        var backoff = doubled < cap ? doubled : cap;
        return backoff > normal ? backoff : normal;
    }

    /// <summary>30 秒／1 分／5 分模式的秒數；Auto 與 Paused 回 null。</summary>
    public static TimeSpan? FixedModeInterval(RefreshMode mode) => mode switch
    {
        RefreshMode.Seconds30 => FixedSeconds30,
        RefreshMode.Minute1 => FixedMinute1,
        RefreshMode.Minutes5 => FixedMinutes5,
        _ => null,
    };

    /// <summary>安靜公式：clamp((now − lastActivity) ÷ QuietDivisor, QuietMin, QuietMax)。</summary>
    private static TimeSpan QuietInterval(DateTimeOffset? lastActivity, DateTimeOffset now)
    {
        // 都沒有活動時視為「很久以前」，直接用上限。
        if (lastActivity is not { } last)
        {
            return Tuning.QuietMax;
        }

        // GitHub 的時間戳比本機快（時鐘誤差）時，經過時間視為 0。
        var elapsed = now - last;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var scaled = elapsed / Tuning.QuietDivisor;
        if (scaled < Tuning.QuietMin)
        {
            return Tuning.QuietMin;
        }

        return scaled > Tuning.QuietMax ? Tuning.QuietMax : scaled;
    }

    /// <summary>interval = base × k；鎖定時不短於 LockedMinInterval。</summary>
    private static TimeSpan Stretch(TimeSpan baseInterval, double k, bool locked)
    {
        var interval = Multiply(baseInterval, NormalizeStretch(k));
        return locked && interval < Tuning.LockedMinInterval ? Tuning.LockedMinInterval : interval;
    }

    /// <summary>
    /// k 的合理範圍是 1～StretchMax（spec §6.4）。小於 1 或 NaN 會讓間隔變短、甚至變成 0，一律當成 1；
    /// 超過上限（含無限大）當成上限。
    /// </summary>
    private static double NormalizeStretch(double k)
    {
        if (double.IsNaN(k) || k < NoStretch)
        {
            return NoStretch;
        }

        return Math.Min(k, Tuning.StretchMax);
    }

    /// <summary>乘上倍率；結果超出 TimeSpan 範圍時取最大值，不丟 OverflowException。</summary>
    private static TimeSpan Multiply(TimeSpan value, double factor) =>
        value.Ticks >= TimeSpan.MaxValue.Ticks / factor ? TimeSpan.MaxValue : value * factor;
}
