namespace IsItGreenYet.Core.Polling;

/// <summary>
/// 次級限流的退避等級（spec §6.4）：60、120、240、480、960 秒，第 5 次起固定 960 秒並標示「持續被限流」。
/// 成功不會直接歸零：安靜一段不短於上一次等待的時間後才降一級。
/// </summary>
/// <remarks>
/// 安靜期從上一次的等待（全域暫停）結束後才開始算，暫停本身不算安靜；否則每次暫停一結束就降級，
/// 限流時好時壞時會永遠停在 60 秒、每分鐘撞一次（spec §6.4 降級）。
/// 降級只看「沒有限流回應」的時間，不需要有成功回應，所以等級是用單調時鐘即時算出來的。
/// 等級最高停在 SecondaryBackoffPersistentLevel（此時等待已達上限 960 秒），避免長時間被限流後要很久才降得下來。
/// </remarks>
public sealed class SecondaryBackoff(TimeProvider time)
{
    /// <summary>每多一次限流等待時間加倍：SecondaryBackoffInitial × 2^(n−1)（spec §6.4）。</summary>
    private const double BackoffFactor = 2;

    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly Lock _gate = new();

    /// <summary>最近一次限流後的等級；0 代表從沒被限流或已完全降回來。</summary>
    private int _levelAtLimit;

    /// <summary>最近一次限流的單調時間戳。</summary>
    private long _limitedAt;

    /// <summary>最近一次限流的等待時間，也是每次降級所需的安靜時間。</summary>
    private TimeSpan _lastWait;

    public int Level
    {
        get
        {
            lock (_gate)
            {
                return CurrentLevel();
            }
        }
    }

    public bool Persistent
    {
        get
        {
            lock (_gate)
            {
                return CurrentLevel() >= Tuning.SecondaryBackoffPersistentLevel;
            }
        }
    }

    /// <returns>這次要暫停多久。</returns>
    public TimeSpan OnLimited()
    {
        lock (_gate)
        {
            // 先套用到目前為止的降級，再升一級。
            var level = Math.Min(CurrentLevel() + 1, Tuning.SecondaryBackoffPersistentLevel);
            var wait = WaitForLevel(level);

            _levelAtLimit = level;
            _limitedAt = _time.GetTimestamp();
            _lastWait = wait;
            return wait;
        }
    }

    /// <summary>
    /// 收到成功回應。成功本身不改變等級（不歸零）；降級只看距離上一次限流的安靜時間，已由 <see cref="Level"/> 即時反映。
    /// </summary>
    public void OnSuccess()
    {
    }

    /// <summary>呼叫端要持有鎖。</summary>
    private int CurrentLevel()
    {
        if (_levelAtLimit <= 0)
        {
            return 0;
        }

        var elapsed = _time.GetElapsedTime(_limitedAt);
        var quiet = elapsed - _lastWait;
        if (quiet < _lastWait)
        {
            return _levelAtLimit;
        }

        // 每安靜滿一段上一次的等待時間降一級。
        var steps = quiet.Ticks / _lastWait.Ticks;
        return steps >= _levelAtLimit ? 0 : _levelAtLimit - (int)steps;
    }

    /// <summary>min(SecondaryBackoffInitial × 2^(level−1), SecondaryBackoffMax)；逐次加倍、碰到上限就停，不會溢位。</summary>
    private static TimeSpan WaitForLevel(int level)
    {
        var wait = Tuning.SecondaryBackoffInitial;
        for (var n = 1; n < level && wait < Tuning.SecondaryBackoffMax; n++)
        {
            wait *= BackoffFactor;
        }

        return wait < Tuning.SecondaryBackoffMax ? wait : Tuning.SecondaryBackoffMax;
    }
}

/// <summary>60 秒內有 3 個以上不同 repo 出現無法分類的 403（spec §6.4 第 5 列，Q23）。</summary>
public sealed class ForbiddenBurstDetector(TimeProvider time)
{
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly Lock _gate = new();

    /// <summary>每個 repo 最近一次出現 403 的單調時間戳；同一個 repo 只算一個。</summary>
    private readonly Dictionary<long, long> _lastSeen = new();

    /// <returns>這次記錄後達到門檻時回 true，並清空紀錄。</returns>
    public bool Record(long repoId)
    {
        lock (_gate)
        {
            _lastSeen[repoId] = _time.GetTimestamp();

            // 「60 秒內」含剛好 60 秒；超過的紀錄移除。
            List<long>? expired = null;
            foreach (var (id, seenAt) in _lastSeen)
            {
                if (_time.GetElapsedTime(seenAt) > Tuning.ForbiddenBurstWindow)
                {
                    (expired ??= []).Add(id);
                }
            }

            if (expired is not null)
            {
                foreach (var id in expired)
                {
                    _lastSeen.Remove(id);
                }
            }

            if (_lastSeen.Count < Tuning.ForbiddenBurstRepos)
            {
                return false;
            }

            _lastSeen.Clear();
            return true;
        }
    }
}

/// <summary>連續 OfflineThreshold 次連線層錯誤、中間沒有任何成功，就判定離線。</summary>
public sealed class OfflineDetector
{
    private readonly Lock _gate = new();
    private int _consecutiveErrors;
    private bool _offline;

    public bool IsOffline
    {
        get
        {
            lock (_gate)
            {
                return _offline;
            }
        }
    }

    /// <summary>連線層錯誤：DNS、連線被拒、TLS、用戶端 HttpTimeout 逾時（spec §6.4）。</summary>
    /// <returns>這次錯誤讓狀態剛轉為離線時回 true。</returns>
    public bool OnNetworkError()
    {
        lock (_gate)
        {
            // 已經離線時不再累計，也不重複回報。
            if (_offline)
            {
                return false;
            }

            _consecutiveErrors++;
            if (_consecutiveErrors < Tuning.OfflineThreshold)
            {
                return false;
            }

            _offline = true;
            return true;
        }
    }

    /// <summary>收到回應（連線層成功）：恢復連線，連續錯誤重新計算。</summary>
    public void OnSuccess()
    {
        lock (_gate)
        {
            _consecutiveErrors = 0;
            _offline = false;
        }
    }
}

/// <summary>連續 ServerErrorBadgeThreshold 個 5xx、中間沒有 2xx 或 304，就加角標。</summary>
public sealed class ServerErrorDetector
{
    private readonly Lock _gate = new();
    private int _consecutiveServerErrors;

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _consecutiveServerErrors >= Tuning.ServerErrorBadgeThreshold;
            }
        }
    }

    public void OnServerError()
    {
        lock (_gate)
        {
            // 到門檻後就不再累加，長時間異常也不會溢位。
            if (_consecutiveServerErrors < Tuning.ServerErrorBadgeThreshold)
            {
                _consecutiveServerErrors++;
            }
        }
    }

    /// <summary>收到任何非 5xx 的回應（含 304）。</summary>
    public void OnOtherResponse()
    {
        lock (_gate)
        {
            _consecutiveServerErrors = 0;
        }
    }
}
