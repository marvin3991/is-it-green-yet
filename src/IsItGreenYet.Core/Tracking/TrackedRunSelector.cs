using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Tracking;

/// <summary>
/// 選出「要追蹤的 run」並判定每個鍵（spec §7.2）。純函式，不碰 I/O；牆鐘由呼叫端傳入。
/// 只處理 <see cref="RepoTrackingInput.Repo"/> 自己的資料：RepoId 不符的 run、workflow、PR 一律略過。
/// </summary>
public static class TrackedRunSelector
{
    /// <summary>pull_request、pull_request_target、pull_request_review、pull_request_review_comment 共用的前綴（spec §7.2 規則 1、Q26）。</summary>
    private const string PullRequestEventPrefix = "pull_request";

    /// <summary>lastDecisive 是失敗、但那一組最晚的 updated_at 超過這個時間時，不算 failing（Q6）。</summary>
    private static readonly TimeSpan TrackedFailureMaxAge = TimeSpan.FromDays(Tuning.TrackedFailureMaxAgeDays);

    /// <summary>由新到舊：run_number 大的在前；資料異常時再依 created_at、id。</summary>
    private static readonly Comparer<RunInfo> NewestFirst = Comparer<RunInfo>.Create((a, b) =>
    {
        var byNumber = b.RunNumber.CompareTo(a.RunNumber);
        if (byNumber != 0)
        {
            return byNumber;
        }

        var byCreated = b.CreatedAt.CompareTo(a.CreatedAt);
        return byCreated != 0 ? byCreated : b.Id.CompareTo(a.Id);
    });

    public static IReadOnlyList<KeyEvaluation> Evaluate(RepoTrackingInput input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);

        var repo = input.Repo;
        var runs = RepoRuns(input);
        var trackedWorkflows = TrackedWorkflowIds(input, runs);
        var pullRequestBranches = OpenPullRequestBranches(input);
        var buckets = new Dictionary<TrackingKey, List<RunInfo>>();

        foreach (var run in runs)
        {
            // head_branch 為 null 的 run 不屬於任何鍵；只看 active（或還不認得）的 workflow。
            if (run.HeadBranch is null || !trackedWorkflows.Contains(run.WorkflowId))
            {
                continue;
            }

            // 規則 2：開啟中 PR 的分支不論事件都算；規則 1：預設分支排除 pull_request 開頭的事件。
            var onPullRequestBranch = pullRequestBranches.Contains((run.HeadRepoId, run.HeadBranch));
            var countsForDefaultBranch = IsDefaultBranch(repo, run.HeadRepoId, run.HeadBranch) && !IsPullRequestEvent(run.Event);
            if (!onPullRequestBranch && !countsForDefaultBranch)
            {
                continue;
            }

            var key = new TrackingKey(repo.Id, run.WorkflowId, run.HeadRepoId, run.HeadBranch);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = [];
                buckets[key] = bucket;
            }

            bucket.Add(run);
        }

        return buckets
            .Select(pair => EvaluateKey(pair.Key, KindOf(repo, pair.Key), pair.Value, input.AttemptsByRun, now))
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Key.WorkflowId)
            .ThenBy(e => e.Key.HeadRepoId)
            .ThenBy(e => e.Key.HeadBranch, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>以 <c>pull_request</c> 開頭的事件（pull_request、pull_request_target…）不算預設分支的 run。</summary>
    public static bool IsPullRequestEvent(string eventName) =>
        eventName?.StartsWith(PullRequestEventPrefix, StringComparison.Ordinal) == true;

    /// <summary>run 用到、但本地 workflow 清單裡沒有的 workflow_id（先當成 active，並排 E3）。</summary>
    public static IReadOnlyList<long> UnknownWorkflowIds(RepoTrackingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return UnknownWorkflowIds(RepoRuns(input), KnownWorkflowStates(input));
    }

    /// <summary>需要用 E5 補抓預設分支 run 的 workflow id。</summary>
    public static IReadOnlyList<long> DefaultBranchSeedCandidates(RepoTrackingInput input, IReadOnlyList<KeyEvaluation> keys)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(keys);

        // 沒有預設分支可查時，E5 的 branch= 沒有意義。
        if (string.IsNullOrWhiteSpace(input.Repo.DefaultBranch))
        {
            return [];
        }

        // spec §4.2 E5：本地沒有任何預設分支 run，或找不到 lastDecisive（只有未完成、灰色或未知）時補抓。
        var decided = keys
            .Where(k => k.Kind == KeyKind.DefaultBranch && k.Key.RepoId == input.Repo.Id && k.LastDecisive is not null)
            .Select(k => k.Key.WorkflowId)
            .ToHashSet();

        return TrackedWorkflowIds(input, RepoRuns(input))
            .Where(id => !decided.Contains(id))
            .Order()
            .ToList();
    }

    /// <summary>需要用 head_sha 補抓的開啟中 PR。</summary>
    public static IReadOnlyList<PullRequestInfo> PullRequestSeedCandidates(RepoTrackingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.OpenPullRequests is null)
        {
            return [];
        }

        var runs = RepoRuns(input);
        var seenShas = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<PullRequestInfo>();

        foreach (var pr in RepoPullRequests(input).OrderBy(pr => pr.Number))
        {
            // 來源 repo 不明的 PR 無法和 run 比對；空白的 sha 無法補抓。
            if (pr.HeadRepoId is not { } headRepoId || string.IsNullOrWhiteSpace(pr.HeadSha))
            {
                continue;
            }

            // 不同 workflow 的 run_number 不能互相比較，跨 workflow 的「最新」用 created_at。
            var latest = runs
                .Where(r => r.HeadRepoId == headRepoId && string.Equals(r.HeadBranch, pr.HeadRef, StringComparison.Ordinal))
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.Id)
                .FirstOrDefault();

            var needsSeed = latest is null || !string.Equals(latest.HeadSha, pr.HeadSha, StringComparison.Ordinal);

            // 每個 head sha 只需要補抓一次。
            if (needsSeed && seenShas.Add(pr.HeadSha))
            {
                candidates.Add(pr);
            }
        }

        return candidates;
    }

    private static KeyEvaluation EvaluateKey(
        TrackingKey key,
        KeyKind kind,
        List<RunInfo> runs,
        IReadOnlyDictionary<long, IReadOnlyList<RunAttemptRecord>> attemptsByRun,
        DateTimeOffset now)
    {
        // 依 head_sha 分組，組內與組間都由新到舊；組序看組內最大的 run_number。
        var groups = runs
            .GroupBy(r => r.HeadSha, StringComparer.Ordinal)
            .Select(g => g.Order(NewestFirst).ToList())
            .Order(Comparer<List<RunInfo>>.Create((a, b) => NewestFirst.Compare(a[0], b[0])))
            .ToList();

        var latest = groups[0][0];
        var latestAttempt = AttemptsNewestFirst(latest, attemptsByRun)[0];
        var pending = RunStateMapper.Map(latestAttempt).Category is RunCategory.Pending or RunCategory.Waiting;

        foreach (var group in groups)
        {
            var conclusions = group.Select(r => DecisiveCategory(r, attemptsByRun)).ToList();

            // 組內任一 run 是失敗就算失敗；否則有成功就算成功；整組都沒有決定性結論就往下一組找。
            RunCategory? decisive = conclusions.Contains(RunCategory.Failure) ? RunCategory.Failure
                : conclusions.Contains(RunCategory.Success) ? RunCategory.Success
                : null;
            if (decisive is null)
            {
                continue;
            }

            var decisiveAt = group.Max(r => r.UpdatedAt);
            var failing = decisive == RunCategory.Failure && now - decisiveAt <= TrackedFailureMaxAge;
            return new KeyEvaluation(key, kind, latest, pending, decisive, group.Select(r => r.Id).ToList(), decisiveAt, failing);
        }

        return new KeyEvaluation(key, kind, latest, pending, null, [], null, false);
    }

    /// <summary>run 的決定性結論：由新到舊看 attempt，取第一個成功或失敗；灰色與未完成的 attempt 跳過（Q9）。</summary>
    private static RunCategory? DecisiveCategory(RunInfo run, IReadOnlyDictionary<long, IReadOnlyList<RunAttemptRecord>> attemptsByRun)
    {
        foreach (var attempt in AttemptsNewestFirst(run, attemptsByRun))
        {
            var category = RunStateMapper.Map(attempt).Category;
            if (category is RunCategory.Success or RunCategory.Failure)
            {
                return category;
            }
        }

        return null;
    }

    /// <summary>
    /// 本地保存的 attempt 加上 RunInfo 本身（API 回傳的最新 attempt），依 attempt 由新到舊。
    /// 同一個 attempt 有兩份時取 updated_at 較晚的；一樣晚時以 RunInfo 為準。
    /// </summary>
    private static List<RunAttemptRecord> AttemptsNewestFirst(
        RunInfo run,
        IReadOnlyDictionary<long, IReadOnlyList<RunAttemptRecord>> attemptsByRun)
    {
        var byAttempt = new Dictionary<int, RunAttemptRecord>();
        if (attemptsByRun.TryGetValue(run.Id, out var recorded))
        {
            foreach (var attempt in recorded.Where(a => a.RunId == run.Id))
            {
                if (!byAttempt.TryGetValue(attempt.Attempt, out var existing) || attempt.UpdatedAt > existing.UpdatedAt)
                {
                    byAttempt[attempt.Attempt] = attempt;
                }
            }
        }

        var current = new RunAttemptRecord(run.Id, run.RunAttempt, run.Status, run.Conclusion, run.RunStartedAt, run.UpdatedAt);
        if (!byAttempt.TryGetValue(current.Attempt, out var stored) || current.UpdatedAt >= stored.UpdatedAt)
        {
            byAttempt[current.Attempt] = current;
        }

        return byAttempt.Values.OrderByDescending(a => a.Attempt).ToList();
    }

    private static KeyKind KindOf(RepoInfo repo, TrackingKey key) =>
        IsDefaultBranch(repo, key.HeadRepoId, key.HeadBranch) ? KeyKind.DefaultBranch : KeyKind.PullRequestBranch;

    /// <summary>規則 1 的分支條件：分支是預設分支，而且來源 repo 是自己（排除 fork 的同名分支）。</summary>
    private static bool IsDefaultBranch(RepoInfo repo, long headRepoId, string headBranch) =>
        !string.IsNullOrWhiteSpace(repo.DefaultBranch)
        && headRepoId == repo.Id
        && string.Equals(headBranch, repo.DefaultBranch, StringComparison.Ordinal);

    /// <summary>這個 repo 的 run；同一個 id 出現多次時取 attempt 較新（再來是 updated_at 較晚）的那一份。</summary>
    private static List<RunInfo> RepoRuns(RepoTrackingInput input)
    {
        var byId = new Dictionary<long, RunInfo>();
        foreach (var run in input.Runs.Where(r => r.RepoId == input.Repo.Id))
        {
            if (!byId.TryGetValue(run.Id, out var existing) || IsNewerSnapshot(run, existing))
            {
                byId[run.Id] = run;
            }
        }

        return [.. byId.Values];
    }

    private static bool IsNewerSnapshot(RunInfo candidate, RunInfo existing) =>
        candidate.RunAttempt != existing.RunAttempt
            ? candidate.RunAttempt > existing.RunAttempt
            : candidate.UpdatedAt > existing.UpdatedAt;

    private static IEnumerable<PullRequestInfo> RepoPullRequests(RepoTrackingInput input) =>
        input.OpenPullRequests?.Where(pr => pr.RepoId == input.Repo.Id) ?? [];

    /// <summary>規則 2 的 (head.repo.id, head.ref)；OpenPullRequests 為 null（PR 追蹤停用）時是空集合。</summary>
    private static HashSet<(long HeadRepoId, string HeadRef)> OpenPullRequestBranches(RepoTrackingInput input)
    {
        var branches = new HashSet<(long HeadRepoId, string HeadRef)>();
        foreach (var pr in RepoPullRequests(input))
        {
            // 來源 repo 不明的 PR 無法精確比對，不產生鍵。
            if (pr.HeadRepoId is { } headRepoId)
            {
                branches.Add((headRepoId, pr.HeadRef));
            }
        }

        return branches;
    }

    /// <summary>本地 workflow 清單裡的 id 與是否 active。</summary>
    private static Dictionary<long, bool> KnownWorkflowStates(RepoTrackingInput input)
    {
        var states = new Dictionary<long, bool>();
        foreach (var workflow in input.Workflows.Where(w => w.RepoId == input.Repo.Id))
        {
            states[workflow.Id] = states.GetValueOrDefault(workflow.Id) || workflow.IsActive;
        }

        return states;
    }

    private static List<long> UnknownWorkflowIds(IEnumerable<RunInfo> runs, Dictionary<long, bool> known) =>
        runs.Select(r => r.WorkflowId).Where(id => !known.ContainsKey(id)).Distinct().Order().ToList();

    /// <summary>要追蹤的 workflow：active 的，加上還不認得的（先當成 active，spec §7.2）。</summary>
    private static HashSet<long> TrackedWorkflowIds(RepoTrackingInput input, IEnumerable<RunInfo> runs)
    {
        var known = KnownWorkflowStates(input);
        var tracked = known.Where(pair => pair.Value).Select(pair => pair.Key).ToHashSet();
        tracked.UnionWith(UnknownWorkflowIds(runs, known));
        return tracked;
    }
}
