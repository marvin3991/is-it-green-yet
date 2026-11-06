namespace IsItGreenYet.Core.Polling;

/// <summary>請求優先序，數字越小越優先（spec §6.3）。</summary>
public enum RequestPriority
{
    User = 0,
    RunningRepo = 1,
    PushedRepo = 2,
    Probe = 3,
    RunsPoll = 4,
    PullRequests = 5,
    Maintenance = 6,
}

/// <summary>
/// 每分鐘上限（滑動視窗）、全域暫停（spec §6.3、§6.4）與寫入之間的間隔（第二階段 spec §3.2）。全部用單調時鐘，系統時間被調整不受影響。
/// 輪詢與使用者操作兩條工作共用同一個節流器（spec §3.1），所以所有狀態都在鎖內存取。
/// </summary>
public sealed class RequestThrottle(TimeProvider time)
{
    /// <summary>老化最高升到第 2 級（repoRunning 的 repo），不會追上使用者操作（spec §6.3）。</summary>
    private const RequestPriority AgingCeiling = RequestPriority.RunningRepo;

    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly Lock _gate = new();

    /// <summary>視窗內每個已送出請求的單調時間戳，由舊到新。</summary>
    private readonly List<long> _sent = [];

    private long _pauseStartedAt;

    /// <summary>TimeSpan.Zero 代表沒有暫停。</summary>
    private TimeSpan _pauseDuration;

    private string? _pauseReason;

    /// <summary>上一個寫入請求送出時的單調時間戳；還沒送過寫入時是 null。</summary>
    private long? _lastMutativeAt;

    /// <summary>排隊每滿 PriorityAgingStep 升一級，最高升到 RunningRepo；User 不變。</summary>
    public static RequestPriority Aged(RequestPriority priority, TimeSpan waited)
    {
        if (priority <= AgingCeiling || waited < Tuning.PriorityAgingStep)
        {
            return priority;
        }

        var steps = waited.Ticks / Tuning.PriorityAgingStep.Ticks;
        var maxSteps = (long)priority - (long)AgingCeiling;
        return steps >= maxSteps ? AgingCeiling : (RequestPriority)((long)priority - steps);
    }

    /// <summary>null 代表現在可以送；否則是還要等多久（全域暫停或每分鐘上限，取較久者）。</summary>
    public TimeSpan? TimeUntilAllowed()
    {
        lock (_gate)
        {
            var pause = CurrentPauseRemaining();
            var window = WindowWait();
            if (pause is null)
            {
                return window;
            }

            if (window is null)
            {
                return pause;
            }

            return pause > window ? pause : window;
        }
    }

    /// <summary>每送出一個請求（含之後回 304 的）就記一次。</summary>
    public void RecordSent()
    {
        lock (_gate)
        {
            PruneExpired();
            _sent.Add(_time.GetTimestamp());
        }
    }

    /// <summary>
    /// 距上一個寫入請求不到 <see cref="Tuning.MutativeRequestSpacing"/> 時回剩餘時間，否則回 null（第二階段 spec §3.2）。
    /// 只看寫入請求，不影響一般請求；寫入另外也要用 <see cref="TimeUntilAllowed"/> 與 <see cref="RecordSent"/> 算進每分鐘上限。
    /// </summary>
    public TimeSpan? TimeUntilMutativeAllowed()
    {
        lock (_gate)
        {
            if (_lastMutativeAt is not { } last)
            {
                return null;
            }

            var remaining = Tuning.MutativeRequestSpacing - _time.GetElapsedTime(last);
            return remaining > TimeSpan.Zero ? remaining : null;
        }
    }

    /// <summary>每送出一個寫入請求（POST）就記一次。</summary>
    public void RecordMutativeSent()
    {
        lock (_gate)
        {
            _lastMutativeAt = _time.GetTimestamp();
        }
    }

    /// <summary>
    /// 全域暫停。已經在暫停中、而且剩餘時間比這次長時，保留原本的暫停（不會被較短的暫停縮短）。
    /// duration ≤ 0 時用 MinRateLimitWait（spec §6.4 第 1 列）。
    /// </summary>
    public void PauseFor(TimeSpan duration, string reason)
    {
        if (duration <= TimeSpan.Zero)
        {
            duration = Tuning.MinRateLimitWait;
        }

        lock (_gate)
        {
            if (CurrentPauseRemaining() is { } remaining && remaining >= duration)
            {
                return;
            }

            _pauseStartedAt = _time.GetTimestamp();
            _pauseDuration = duration;
            _pauseReason = reason ?? string.Empty;
        }
    }

    public void ClearPause()
    {
        lock (_gate)
        {
            EndPause();
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return CurrentPauseRemaining() is not null;
            }
        }
    }

    public TimeSpan? PauseRemaining
    {
        get
        {
            lock (_gate)
            {
                return CurrentPauseRemaining();
            }
        }
    }

    /// <summary>沒有暫停時為 null。</summary>
    public string? PauseReason
    {
        get
        {
            lock (_gate)
            {
                return CurrentPauseRemaining() is null ? null : _pauseReason;
            }
        }
    }

    /// <summary>暫停的剩餘時間；已到期就清掉暫停並回 null。呼叫端要持有鎖。</summary>
    private TimeSpan? CurrentPauseRemaining()
    {
        if (_pauseDuration <= TimeSpan.Zero)
        {
            return null;
        }

        var elapsed = _time.GetElapsedTime(_pauseStartedAt);
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var remaining = _pauseDuration - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            return remaining;
        }

        EndPause();
        return null;
    }

    private void EndPause()
    {
        _pauseDuration = TimeSpan.Zero;
        _pauseReason = null;
    }

    /// <summary>
    /// 每分鐘上限要等多久；未達上限回 null。呼叫端要持有鎖。
    /// 視窗內超過上限（例如兩條工作各送了一個）時，要等到降回上限以下，也就是第（數量 − 上限 + 1）舊的那筆滿一個視窗。
    /// </summary>
    private TimeSpan? WindowWait()
    {
        PruneExpired();
        if (_sent.Count < Tuning.MaxRequestsPerMinute)
        {
            return null;
        }

        var blocking = _sent[_sent.Count - Tuning.MaxRequestsPerMinute];
        return Tuning.RequestWindow - _time.GetElapsedTime(blocking);
    }

    /// <summary>移除已滿 RequestWindow 的紀錄（「滿 60 秒才放行」，spec §6.3）。呼叫端要持有鎖。</summary>
    private void PruneExpired()
    {
        var expired = 0;
        while (expired < _sent.Count && _time.GetElapsedTime(_sent[expired]) >= Tuning.RequestWindow)
        {
            expired++;
        }

        if (expired > 0)
        {
            _sent.RemoveRange(0, expired);
        }
    }
}
