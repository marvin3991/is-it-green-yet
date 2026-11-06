using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Notifications;

/// <summary>spec §9.1 的三種通知。</summary>
public enum NotificationKind
{
    TrackedFailure,
    MyRunSucceeded,
    WatchedRunDone,
}

/// <summary>通知去重紀錄（cache.db 的 notified 表），重啟後不重發。</summary>
public interface INotifiedStore
{
    bool Contains(long runId, int runAttempt, string conclusion);

    void Add(long runId, int runAttempt, string conclusion, NotificationKind? kind, bool suppressed, DateTimeOffset at);
}

/// <summary>一次「完成觀察」：某個 (run_id, run_attempt) 在本次執行中第一次被看到是完成狀態。</summary>
public sealed record CompletionObservation(RunInfo Run, RunState State, string RepoFullName, bool IsTracked, bool IsWatched);

public sealed record NotificationOptions(bool TrackedFailure, bool MyRunSucceeded, bool WatchedRunDone);

public sealed record PlannedNotification(RunInfo Run, RunState State, string RepoFullName, NotificationKind Kind);

/// <summary>決定哪些完成觀察要通知（spec §9.1～§9.3）：時間界線、去重、三種條件。</summary>
public sealed class NotificationPlanner(INotifiedStore store)
{
    private readonly INotifiedStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// <paramref name="previous"/> 是本地已知的同一 run 最新 attempt；null 代表第一次看到。
    /// 目前已完成，而且之前沒看過、或 attempt 比之前大、或同一 attempt 之前未完成，就是新的完成；比之前小的 attempt 是過時資料，不算。
    /// </summary>
    public static bool IsNewCompletion(RunAttemptRecord? previous, RunInfo current)
    {
        ArgumentNullException.ThrowIfNull(current);

        // 等待核准（waiting、completed＋action_required）與未完成都不是最終結果。
        if (!RunStateMapper.Map(current).IsCompleted)
        {
            return false;
        }

        if (previous is null)
        {
            return true;
        }

        if (previous.RunId != current.Id)
        {
            throw new ArgumentException("previous 必須是同一筆 run 的 attempt 紀錄。", nameof(previous));
        }

        // 重跑讓 run_attempt 遞增，就是另一組 (run_id, run_attempt)。
        // 比本地紀錄舊的 attempt 是過時的資料（多區域延遲），不算新的完成。
        if (current.RunAttempt < previous.Attempt)
        {
            return false;
        }

        if (current.RunAttempt > previous.Attempt)
        {
            return true;
        }

        return !RunStateMapper.Map(previous).IsCompleted;
    }

    /// <param name="baselineForRepo">每個 repo 的時間界線 T0（spec §9.3）；updated_at 早於 T0 的只記 suppressed。</param>
    public IReadOnlyList<PlannedNotification> Plan(
        IReadOnlyList<CompletionObservation> completions,
        long? myUserId,
        NotificationOptions options,
        Func<long, DateTimeOffset> baselineForRepo,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(completions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(baselineForRepo);

        var planned = new List<PlannedNotification>();
        // 同一批裡重複的觀察（例如 E4 與 E6 都回了同一筆）只判定一次，不依賴 store 的寫入時機。
        var handled = new HashSet<(long RunId, int Attempt, string Conclusion)>();

        foreach (var observation in completions)
        {
            if (!observation.State.IsCompleted)
            {
                continue;
            }

            var run = observation.Run;
            var conclusion = ConclusionKey(run);
            if (!handled.Add((run.Id, run.RunAttempt, conclusion)) || _store.Contains(run.Id, run.RunAttempt, conclusion))
            {
                continue;
            }

            var beforeBaseline = run.UpdatedAt < baselineForRepo(run.RepoId);
            var regularKind = RegularKind(observation, myUserId, options);

            // N3 是你明確要求的，不受時間界線限制（Q14）；同時符合 N1／N2 時也只發這一則。
            NotificationKind? kind = options.WatchedRunDone && observation.IsWatched
                ? NotificationKind.WatchedRunDone
                : beforeBaseline ? null : regularKind;

            if (kind is { } notifyKind)
            {
                _store.Add(run.Id, run.RunAttempt, conclusion, notifyKind, suppressed: false, now);
                planned.Add(new PlannedNotification(run, observation.State, observation.RepoFullName, notifyKind));
            }
            else if (beforeBaseline)
            {
                // 界線之前的完成只記下來不發（spec §9.3）；kind 記的是本來會發的種類，沒有就是 null。
                _store.Add(run.Id, run.RunAttempt, conclusion, regularKind, suppressed: true, now);
            }

            // 界線之後、不符合任何條件（或選項關閉）的不通知，也不寫 notified。
        }

        return planned;
    }

    /// <summary>N1、N2（spec §9.1）；兩者的結論類別互斥。</summary>
    private static NotificationKind? RegularKind(CompletionObservation observation, long? myUserId, NotificationOptions options) =>
        observation.State.Category switch
        {
            RunCategory.Failure when options.TrackedFailure && observation.IsTracked => NotificationKind.TrackedFailure,
            // 比對 actor.id（Q10），不看 login，也不看 triggering_actor。
            RunCategory.Success when options.MyRunSucceeded && myUserId is { } me && observation.Run.ActorId == me =>
                NotificationKind.MyRunSucceeded,
            _ => null,
        };

    /// <summary>去重鍵的 conclusion；API 回 null 或空白時（例如 status 本身是 startup_failure）用空字串。</summary>
    private static string ConclusionKey(RunInfo run) =>
        string.IsNullOrWhiteSpace(run.Conclusion) ? string.Empty : run.Conclusion;
}
