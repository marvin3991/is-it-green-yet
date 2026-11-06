namespace IsItGreenYet.Core.Tests.TestSupport;

/// <summary>
/// 可手動控制的時間：牆鐘（GetUtcNow）與單調時鐘（GetTimestamp）分開，
/// 才能模擬「系統時間被往回調、單調時鐘照走」。單調時鐘的頻率為每秒 TimeSpan.TicksPerSecond。
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _utcNow;
    private long _timestamp;
    private TimeZoneInfo _localZone;

    public ManualTimeProvider(DateTimeOffset utcNow, TimeZoneInfo? localZone = null)
    {
        _utcNow = utcNow.ToUniversalTime();
        _localZone = localZone ?? TimeZoneInfo.Utc;
        // 單調時鐘從一個不為 0 的值開始，避免程式把 0 當成「沒有值」。
        _timestamp = TimeSpan.TicksPerDay;
    }

    public static TimeZoneInfo Taipei => TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    public override TimeZoneInfo LocalTimeZone
    {
        get
        {
            lock (_gate)
            {
                return _localZone;
            }
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    /// <summary>只改牆鐘（可往回調），單調時鐘不動。</summary>
    public void SetUtcNow(DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            _utcNow = utcNow.ToUniversalTime();
        }
    }

    /// <summary>只推進單調時鐘，例如模擬睡眠期間 QPC 照走。</summary>
    public void AdvanceMonotonic(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        lock (_gate)
        {
            _timestamp += delta.Ticks;
        }
    }

    /// <summary>牆鐘與單調時鐘一起前進。</summary>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        lock (_gate)
        {
            _utcNow += delta;
            _timestamp += delta.Ticks;
        }
    }

    public void SetLocalTimeZone(TimeZoneInfo zone)
    {
        lock (_gate)
        {
            _localZone = zone;
        }
    }
}
