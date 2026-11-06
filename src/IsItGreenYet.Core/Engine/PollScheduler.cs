using IsItGreenYet.Core.Polling;

namespace IsItGreenYet.Core.Engine;

public enum TargetKind
{
    Probe,
    RepoList,
    Workflows,
    Runs,
    RunDetail,
    PullRequests,
    DefaultBranchSeed,
    PullRequestSeed,
    StaleCheck,
    OfflineProbe,
}

/// <param name="ItemId">RunDetail 的 run id、DefaultBranchSeed 的 workflow id。</param>
/// <param name="Sha">PullRequestSeed 的 head sha。</param>
public sealed record PollTarget(TargetKind Kind, long? RepoId = null, long? ItemId = null, string? Sha = null);

/// <summary>每個輪詢目標的下次到期時間與優先序（單調時鐘）。</summary>
/// <remarks>
/// 到期時間記成「排入時的單調時間戳＋延遲」，系統時間被調整不受影響（spec §3.1），也不會因為延遲很長而溢位。
/// 同一個目標只會有一筆：再排一次就取代原本的到期時間。執行緒安全。
/// </remarks>
public sealed class PollScheduler(TimeProvider time)
{
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly Lock _gate = new();
    private readonly Dictionary<PollTarget, Entry> _entries = [];

    /// <summary>同一刻排入、同一級、同樣到期的目標，依排入順序。</summary>
    private long _sequence;

    /// <summary>排在 <paramref name="delay"/> 之後到期（負值當成現在），取代原本的排程。</summary>
    public void Schedule(PollTarget target, TimeSpan delay, RequestPriority priority)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            _entries[target] = new Entry(_time.GetTimestamp(), delay < TimeSpan.Zero ? TimeSpan.Zero : delay, priority, _sequence++);
        }
    }

    /// <summary>
    /// 現在到期。原本就已經到期的目標保留原本的到期時間（老化不歸零），優先序取兩者中較高的。
    /// </summary>
    public void ScheduleNow(PollTarget target, RequestPriority priority)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_entries.TryGetValue(target, out var existing) && Overdue(existing, now) is not null)
            {
                _entries[target] = existing with { Priority = priority < existing.Priority ? priority : existing.Priority };
                return;
            }

            _entries[target] = new Entry(now, TimeSpan.Zero, priority, _sequence++);
        }
    }

    public void Remove(PollTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            _entries.Remove(target);
        }
    }

    public void RemoveRepo(long repoId) => RemoveWhere(target => target.RepoId == repoId);

    /// <summary>取出一個已到期的目標：依 RequestThrottle.Aged 後的優先序，再依到期先後。沒有回 null。</summary>
    /// <remarks>老化看的是已經到期多久（spec §6.3「排隊每滿 PriorityAgingStep」）；同一級時到期越久的先送。</remarks>
    public PollTarget? TakeNextDue()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            PollTarget? best = null;
            var bestPriority = RequestPriority.Maintenance;
            var bestOverdue = TimeSpan.Zero;
            var bestSequence = long.MaxValue;

            foreach (var (target, entry) in _entries)
            {
                if (Overdue(entry, now) is not { } overdue)
                {
                    continue;
                }

                var aged = RequestThrottle.Aged(entry.Priority, overdue);
                var better = best is null
                    || aged < bestPriority
                    || (aged == bestPriority && (overdue > bestOverdue || (overdue == bestOverdue && entry.Sequence < bestSequence)));
                if (better)
                {
                    best = target;
                    bestPriority = aged;
                    bestOverdue = overdue;
                    bestSequence = entry.Sequence;
                }
            }

            if (best is not null)
            {
                _entries.Remove(best);
            }

            return best;
        }
    }

    /// <summary>離最早到期的目標還有多久；已經有到期的回 0，沒有任何排程回 null。</summary>
    public TimeSpan? TimeUntilNextDue()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            TimeSpan? earliest = null;
            foreach (var entry in _entries.Values)
            {
                var remaining = Remaining(entry, now);
                if (earliest is null || remaining < earliest)
                {
                    earliest = remaining;
                }
            }

            return earliest;
        }
    }

    public IReadOnlyCollection<PollTarget> Scheduled
    {
        get
        {
            lock (_gate)
            {
                return _entries.Keys.ToArray();
            }
        }
    }

    internal bool Contains(PollTarget target)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(target);
        }
    }

    /// <summary>這個目標還要多久到期；不在排程裡回 null。</summary>
    internal TimeSpan? TimeUntilDue(PollTarget target)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(target, out var entry) ? Remaining(entry, _time.GetTimestamp()) : null;
        }
    }

    internal RequestPriority? PriorityOf(PollTarget target)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(target, out var entry) ? entry.Priority : null;
        }
    }

    /// <summary>只在這個目標已到期時取出它（離線時只放行 E10 探測用）。</summary>
    internal bool TryTakeIfDue(PollTarget target)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(target, out var entry) || Overdue(entry, _time.GetTimestamp()) is null)
            {
                return false;
            }

            _entries.Remove(target);
            return true;
        }
    }

    internal void RemoveWhere(Func<PollTarget, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        lock (_gate)
        {
            foreach (var target in _entries.Keys.Where(predicate).ToList())
            {
                _entries.Remove(target);
            }
        }
    }

    /// <summary>已到期多久；還沒到期回 null。</summary>
    private TimeSpan? Overdue(Entry entry, long now)
    {
        var elapsed = _time.GetElapsedTime(entry.ScheduledAt, now);
        return elapsed >= entry.Delay ? elapsed - entry.Delay : null;
    }

    private TimeSpan Remaining(Entry entry, long now)
    {
        var remaining = entry.Delay - _time.GetElapsedTime(entry.ScheduledAt, now);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private sealed record Entry(long ScheduledAt, TimeSpan Delay, RequestPriority Priority, long Sequence);
}
