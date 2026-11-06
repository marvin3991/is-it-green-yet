using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>
/// 包住 <see cref="ManualTimeProvider"/>，另外讓計時器（Task.Delay、CancellationTokenSource 的逾時）
/// 只在測試推進時間、跨過到期時間時才觸發，測試不必等真實時間。
/// </summary>
internal sealed class EngineClock : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ClockTimer> _timers = [];

    public EngineClock(DateTimeOffset utcNow)
    {
        Manual = new ManualTimeProvider(utcNow, ManualTimeProvider.Taipei);
    }

    public ManualTimeProvider Manual { get; }

    public override TimeZoneInfo LocalTimeZone => Manual.LocalTimeZone;

    public override long TimestampFrequency => Manual.TimestampFrequency;

    /// <summary>
    /// 每次讀牆鐘之前在讀的那個執行緒上呼叫：測試用它在 Engine 執行到某個方法時注入失敗（見 <see cref="CalledFrom"/>）。
    /// </summary>
    public Action? BeforeGetUtcNow { get; set; }

    public override DateTimeOffset GetUtcNow()
    {
        BeforeGetUtcNow?.Invoke();
        return Manual.GetUtcNow();
    }

    /// <summary>目前的呼叫堆疊裡有沒有叫這個名稱的方法（給 <see cref="BeforeGetUtcNow"/> 判斷 Engine 執行到哪裡）。</summary>
    public static bool CalledFrom(string methodName) =>
        new System.Diagnostics.StackTrace().GetFrames().Any(frame => frame.GetMethod()?.Name == methodName);

    public override long GetTimestamp() => Manual.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>牆鐘與單調時鐘一起前進，觸發所有已到期的計時器。</summary>
    public void Advance(TimeSpan delta)
    {
        Manual.Advance(delta);
        FireDue();
    }

    private void Register(ClockTimer timer)
    {
        lock (_gate)
        {
            if (!_timers.Contains(timer))
            {
                _timers.Add(timer);
            }
        }
    }

    private void Unregister(ClockTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private void FireDue()
    {
        while (true)
        {
            ClockTimer? due;
            lock (_gate)
            {
                var now = GetTimestamp();
                due = _timers.FirstOrDefault(timer => timer.DueAt <= now);
                if (due is not null)
                {
                    _timers.Remove(due);
                }
            }

            if (due is null)
            {
                return;
            }

            // 回呼在鎖外執行：回呼裡可能再建立或釋放計時器。
            due.Fire();
        }
    }

    private sealed class ClockTimer(EngineClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public long DueAt { get; private set; } = long.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }

            _period = period;
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                clock.Unregister(this);
                DueAt = long.MaxValue;
                return true;
            }

            DueAt = clock.GetTimestamp() + Math.Max(0, dueTime.Ticks);
            clock.Register(this);
            if (dueTime <= TimeSpan.Zero)
            {
                clock.FireDue();
            }

            return true;
        }

        public void Fire()
        {
            if (_disposed)
            {
                return;
            }

            if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
            {
                DueAt = clock.GetTimestamp() + _period.Ticks;
                clock.Register(this);
            }

            callback(state);
        }

        public void Dispose()
        {
            _disposed = true;
            clock.Unregister(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
