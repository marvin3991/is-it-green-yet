using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Polling;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Engine;

/// <summary>repo 的分類與它底下各目標的排程（spec §5.2、§5.3、§6.1）。</summary>
public sealed partial class MonitorEngine
{
    /// <summary>
    /// 第一次 E1 驗證成功後排好啟動時的目標：完整重抓（啟動時一律重抓一次，帶 ETag）、探測、
    /// 監看中 repo 的 runs 與 PR、workflow 清單、stale repo 的首次檢查。暫停模式下不排任何週期性輪詢。
    /// </summary>
    private void SeedInitialSchedule()
    {
        _scheduleSeeded = true;
        if (_refreshMode == RefreshMode.Paused)
        {
            return;
        }

        _scheduler.ScheduleNow(RepoListTarget, RequestPriority.Maintenance);
        if (NormalInterval(ProbeATarget) is { } probe)
        {
            Schedule(ProbeATarget, probe);
            if (NeedsCollaboratorList())
            {
                Schedule(ProbeBTarget, probe);
            }
        }

        foreach (var repo in _repos.Values.OrderBy(repo => repo.Id))
        {
            if (IsMonitored(repo.Id) && StateOf(repo.Id).WorkflowsTotal is not null)
            {
                _scheduler.ScheduleNow(new PollTarget(TargetKind.Runs, repo.Id), RunsPriority(repo));
                _scheduler.ScheduleNow(new PollTarget(TargetKind.PullRequests, repo.Id), RequestPriority.PullRequests);
                EnsureRunDetails(repo);
            }

            EnsureRepoTargets(repo.Id);
        }
    }

    /// <summary>從暫停或未登入恢復時，把週期性的目標排回去（已經排著的不動）。</summary>
    /// <remarks>
    /// repo 完整清單從上一次送出起算一個 RepoListRefresh（spec §5.3 每小時）。這次執行還沒抓完過（以暫停模式啟動，
    /// 啟動時的完整重抓沒有排；或暫停中按 F5 的那一輪中途失敗）時立即補做，否則要等一小時才抓到完整清單。
    /// 等錯誤重試或 5xx 退避中的不提前（spec §6.1、§6.4）。
    /// </remarks>
    private void EnsurePeriodicTargets()
    {
        if (_refreshMode == RefreshMode.Paused)
        {
            return;
        }

        if (!_scheduler.Contains(RepoListTarget) && _round is null && NormalInterval(RepoListTarget) is { } list)
        {
            if (IsHeld(RepoListTarget))
            {
                EnsureRetry(RepoListTarget);
            }
            else if (_lastSent.TryGetValue(RepoListTarget, out var listSent) && (_repoListFetched || _errorBackoff.Contains(RepoListTarget)))
            {
                Schedule(RepoListTarget, Clamp(list - _time.GetElapsedTime(listSent), list));
            }
            else
            {
                _scheduler.ScheduleNow(RepoListTarget, RequestPriority.Maintenance);
            }
        }

        if (NormalInterval(ProbeATarget) is { } probe)
        {
            EnsureProbe(ProbeATarget, probe);
            if (NeedsCollaboratorList())
            {
                EnsureProbe(ProbeBTarget, probe);
            }
        }

        foreach (var repoId in _repos.Keys.ToList())
        {
            EnsureRepoTargets(repoId);
            if (IsMonitored(repoId) && _repos.TryGetValue(repoId, out var repo))
            {
                EnsureRunDetails(repo);
                EnsureSeeds(repo);
            }
        }
    }

    /// <summary>不在排程裡的探測排回去：等錯誤重試的（非限流的 403／404）照重試間隔（spec §6.1），其他一個探測間隔之後。</summary>
    private void EnsureProbe(PollTarget target, TimeSpan interval)
    {
        if (_scheduler.Contains(target))
        {
            return;
        }

        if (IsHeld(target))
        {
            EnsureRetry(target);
        }
        else
        {
            Schedule(target, interval);
        }
    }

    /// <summary>
    /// 重新分類（spec §5.2「重新分類的時機」），處理監看與略過之間的轉換。
    /// 從略過轉成監看時立即排入 E3 與 E4，並依原因設定首輪的時間界線（spec §9.3）。
    /// </summary>
    /// <param name="operationAt">你的操作的時間（<see cref="ClassifyCause.User"/>）；null 時是現在。</param>
    private void Reclassify(long repoId, ClassifyCause cause, DateTimeOffset? operationAt = null)
    {
        if (!_repos.TryGetValue(repoId, out var repo))
        {
            return;
        }

        var hadPrevious = _classes.TryGetValue(repoId, out var previous);
        var next = Classify(repo);
        _classes[repoId] = next;

        var reason = next.Reason == SkipReason.None ? null : next.Reason.ToString();
        if (StateOf(repoId).SkipReason != reason)
        {
            SaveState(StateOf(repoId) with { SkipReason = reason });
        }

        // 你的操作解除了第 1、4、5 條（取消靜音、包含 fork、包含協作 repo），但 repo 落在其他略過原因（例如第 6 條）：
        // 之後經由 stale 首次檢查或 workflow 清單才轉成監看時，也是因為這次操作才開始監看，界線是操作的時間（spec §9.3）。
        // 直接轉成監看的由 OnStartMonitoring 設定同一個界線。
        if (cause == ClassifyCause.User && !next.Monitored && !IsUserExclusion(next.Reason)
            && (!hadPrevious || IsUserExclusion(previous!.Reason)))
        {
            SaveState(StateOf(repoId) with { BaselineT0 = operationAt ?? _time.GetUtcNow() });
        }

        if (next.Monitored && (!hadPrevious || !previous!.Monitored))
        {
            OnStartMonitoring(repo, cause, isNew: !hadPrevious, operationAt);
        }
        else if (!next.Monitored && hadPrevious && previous!.Monitored)
        {
            OnStopMonitoring(repoId);
        }
        else if (!hadPrevious && EndsPostActions(next))
        {
            // 重建資料庫清空了 _classes：重建前在監看、這一次才知道不再監看的 repo（例如強制納入遺失）走不到上面那一條，
            // 待確認的操作在這裡清（第二階段 spec §6.2）。第一次看到的 repo 沒有待確認的操作，這裡什麼都不會做。
            ForgetRepoPostActions(repoId);
        }

        EnsureRepoTargets(repoId);
    }

    /// <summary>由你的設定或操作決定的略過原因：第 1 條靜音、第 4 條 fork、第 5 條協作 repo（spec §5.2）。</summary>
    private static bool IsUserExclusion(SkipReason reason) =>
        reason is SkipReason.Muted or SkipReason.Fork or SkipReason.Collaborator;

    private void OnStartMonitoring(RepoInfo repo, ClassifyCause cause, bool isNew, DateTimeOffset? operationAt)
    {
        _monitoredSince[repo.Id] = _time.GetTimestamp();

        DateTimeOffset? baseline = cause switch
        {
            ClassifyCause.Push => repo.PushedAt,
            ClassifyCause.User => operationAt ?? _time.GetUtcNow(),
            _ => null,
        };
        if (baseline is not null)
        {
            SaveState(StateOf(repo.Id) with { BaselineT0 = baseline });
        }

        if (_refreshMode == RefreshMode.Paused && !isNew && cause == ClassifyCause.Discovery)
        {
            return;
        }

        // workflow 清單還沒查過：先 E3，有 workflow 才 E4（EnsureRepoTargets 會在 E3 回來後排 E4）。
        if (StateOf(repo.Id).WorkflowsTotal is null)
        {
            return;
        }

        // 正在處理的就是這個 repo 的 E3（push 觸發的重查讓它轉成監看）時不再排一次。
        var workflowsTarget = new PollTarget(TargetKind.Workflows, repo.Id);
        if (cause != ClassifyCause.Discovery && _current != workflowsTarget && !IsHeld(repo.Id, TargetKind.Workflows))
        {
            _scheduler.ScheduleNow(workflowsTarget, RequestPriority.Maintenance);
        }

        if (_current != new PollTarget(TargetKind.Runs, repo.Id) && !IsHeld(repo.Id, TargetKind.Runs))
        {
            _scheduler.ScheduleNow(new PollTarget(TargetKind.Runs, repo.Id), cause == ClassifyCause.Push ? RequestPriority.PushedRepo : RequestPriority.RunsPoll);
        }

        if (!_prStopped.Contains(repo.Id) && !IsHeld(repo.Id, TargetKind.PullRequests))
        {
            _scheduler.ScheduleNow(new PollTarget(TargetKind.PullRequests, repo.Id), RequestPriority.PullRequests);
        }
    }

    /// <summary>
    /// 轉成略過：不再輪詢 runs、PR、E6 與補抓，立即從清單移除（spec §11.1）。workflow 清單依 §5.3 另外決定。
    /// 這個 repo 的操作後追蹤一起清除（第二階段 spec §6.2）。
    /// </summary>
    private void OnStopMonitoring(long repoId)
    {
        _scheduler.RemoveWhere(target => target.RepoId == repoId
            && target.Kind is TargetKind.Runs or TargetKind.PullRequests or TargetKind.RunDetail
                or TargetKind.DefaultBranchSeed or TargetKind.PullRequestSeed);
        ForgetRepoPostActions(repoId);
    }

    /// <summary>
    /// 這個 repo 該有的目標都在：監看中且 workflow 已查過的有 E4 與 E7；workflow 清單的候選有 E3；
    /// 被第 6 條略過、還沒做過首次檢查的有 stale 檢查（Q7）。正在處理的目標不重排。
    /// 等錯誤重試的目標不提前，但被移出排程（暫停、略過、靜音）之後要照重試間隔排回去（<see cref="EnsureRetry"/>）。
    /// </summary>
    private void EnsureRepoTargets(long repoId)
    {
        if (!_repos.ContainsKey(repoId) || !_classes.TryGetValue(repoId, out var cls))
        {
            return;
        }

        var state = StateOf(repoId);
        var paused = _refreshMode == RefreshMode.Paused;
        var workflowsTarget = new PollTarget(TargetKind.Workflows, repoId);

        if (IsWorkflowCandidate(repoId))
        {
            if (!_scheduler.Contains(workflowsTarget) && _current != workflowsTarget)
            {
                if (IsHeld(repoId, TargetKind.Workflows))
                {
                    if (!paused)
                    {
                        EnsureRetry(workflowsTarget);
                    }
                }
                else if (state.WorkflowsCheckedAt is not { } checkedAt)
                {
                    _scheduler.ScheduleNow(workflowsTarget, RequestPriority.Maintenance);
                }
                else if (!paused && NormalInterval(workflowsTarget) is { } interval)
                {
                    // 依上一次查的時間排下一次（WorkflowsRefresh 一次，spec §5.3）。這次執行查過就用單調時鐘；
                    // 只有上一次執行留下的時間才看牆鐘，而且限制在 0～interval，系統時間被調整也不會延後超過一個間隔（spec §15）。
                    var elapsed = _lastSent.TryGetValue(workflowsTarget, out var sent) ? _time.GetElapsedTime(sent) : _time.GetUtcNow() - checkedAt;
                    Schedule(workflowsTarget, Clamp(interval - elapsed, interval));
                }
            }
        }
        else
        {
            _scheduler.Remove(workflowsTarget);
        }

        var staleTarget = new PollTarget(TargetKind.StaleCheck, repoId);
        if (!cls.Monitored && cls.Reason == SkipReason.Stale && state.WorkflowsCheckedAt is null)
        {
            if (!_scheduler.Contains(staleTarget) && _current != staleTarget)
            {
                if (!IsHeld(repoId, TargetKind.StaleCheck))
                {
                    // 5xx 等錯誤退避中、被暫停移出排程的 stale 檢查照退避排回去，不立即重送（spec §6.4）。
                    if (!RestoreBackoff(staleTarget))
                    {
                        _scheduler.ScheduleNow(staleTarget, RequestPriority.Maintenance);
                    }
                }
                else if (!paused)
                {
                    EnsureRetry(staleTarget);
                }
            }
        }
        else
        {
            _scheduler.Remove(staleTarget);
        }

        var runsTarget = new PollTarget(TargetKind.Runs, repoId);
        if (!cls.Monitored)
        {
            EnsureStaleRuns(repoId, cls, state, runsTarget, paused);
            return;
        }

        // 暫停模式不自動排 E4／E7：只有全量刷新（F5）或你的操作才送（spec §6.2）。
        if (state.WorkflowsTotal is null || paused)
        {
            return;
        }

        if (!_scheduler.Contains(runsTarget) && _current != runsTarget)
        {
            if (IsHeld(repoId, TargetKind.Runs))
            {
                EnsureRetry(runsTarget);
            }
            else
            {
                _scheduler.ScheduleNow(runsTarget, RequestPriority.RunsPoll);
            }
        }

        var pullsTarget = new PollTarget(TargetKind.PullRequests, repoId);
        if (!_scheduler.Contains(pullsTarget) && _current != pullsTarget)
        {
            if (_prStopped.Contains(repoId) || IsHeld(repoId, TargetKind.PullRequests))
            {
                EnsureRetry(pullsTarget);
            }
            else
            {
                _scheduler.ScheduleNow(pullsTarget, RequestPriority.PullRequests);
            }
        }
    }

    /// <summary>
    /// stale 首次檢查的 E4（spec §5.3、Q7）：E3 查到有 workflow、這個 repo 還沒做過 E4 時排一次（同 stale 檢查的 E3，暫停時也排）。
    /// 「做過」看 E4 的快取項：200 一定會寫（0 筆 run 也一樣）。所以 E3 之後被暫停、靜音或重新啟動移出排程時，照同一個規則補排。
    /// </summary>
    private void EnsureStaleRuns(long repoId, RepoClassification cls, RepoState state, PollTarget runsTarget, bool paused)
    {
        if (cls.Reason != SkipReason.Stale || state.WorkflowsTotal is not > 0
            || _scheduler.Contains(runsTarget) || _current == runsTarget
            || _cache.Get(RunsCacheKey(_repos[repoId])) is not null)
        {
            return;
        }

        if (!IsHeld(repoId, TargetKind.Runs))
        {
            _scheduler.ScheduleNow(runsTarget, RequestPriority.Maintenance);
        }
        else if (!paused)
        {
            EnsureRetry(runsTarget);
        }
    }

    /// <summary>
    /// 等錯誤重試、卻已經不在排程裡的目標排回去：E4／E3 的 404 還在等完整重抓確認時，補排那一次完整重抓（spec §5.4）；
    /// 其他（含探測）依問題的重試間隔（PermissionRetryInterval／NotFoundRetryInterval，spec §6.1），從上一次送出起算。
    /// </summary>
    private void EnsureRetry(PollTarget target)
    {
        if (target.RepoId is { } repoId && _pending404.Contains((repoId, target.Kind)))
        {
            RequestRepoList();
            return;
        }

        if (!TryGetRetryInterval(target, out var retryInterval)
            || IntervalPolicy.Periodic(retryInterval, _refreshMode, Stretch(), _locked, followsRefreshMode: false) is not { } interval)
        {
            return;
        }

        // 上一次送出的時間不在了（長時間暫停後被清理掉）時，保守地從現在起算一個間隔。
        var remaining = _lastSent.TryGetValue(target, out var sent) ? interval - _time.GetElapsedTime(sent) : interval;
        Schedule(target, Clamp(remaining, interval));
    }

    /// <summary>限制在 0～<paramref name="max"/> 之間。</summary>
    private static TimeSpan Clamp(TimeSpan value, TimeSpan max) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value > max ? max : value;

    /// <summary>workflow 清單的候選：通過第 1～5 條、沒有被第 6 條略過的 repo，含被第 7、8 條略過的（spec §5.3）。</summary>
    private bool IsWorkflowCandidate(long repoId) =>
        _classes.TryGetValue(repoId, out var cls)
        && (cls.Monitored || cls.Reason is SkipReason.NoWorkflows or SkipReason.AllWorkflowsDisabled);

    /// <summary>
    /// E6 的對象（spec §4.2 E6、§6.1）：本地顯示未完成或等待、不在最近一次 E4 第 1 頁的 run，加上有操作後追蹤的 run（第二階段 spec §6.2）。
    /// 追蹤中或被標記的用所屬 repo 的間隔，其他用 QuietMax；第一次在一個間隔之後送。完成、回到第 1 頁而且沒有待確認操作的停止追蹤。
    /// </summary>
    private void EnsureRunDetails(RepoInfo repo)
    {
        var pending = RunDetailTargets(repo);

        _scheduler.RemoveWhere(target => target.Kind == TargetKind.RunDetail && target.RepoId == repo.Id
            && target.ItemId is { } runId && !pending.Contains(runId));

        // 不再有任何 E6 目標：這次執行 E6 的問題標示已經不是目前的問題。
        if (pending.Count == 0 && HasProblem(repo.Id, TargetKind.RunDetail))
        {
            ClearProblem(repo.Id, TargetKind.RunDetail);
        }

        foreach (var runId in pending)
        {
            var target = new PollTarget(TargetKind.RunDetail, repo.Id, runId);
            if (_scheduler.Contains(target) || _current == target)
            {
                continue;
            }

            // 等權限重試、被暫停或靜音移出排程的 E6：從上一次送出起算重試間隔，不用一般的 E6 間隔（spec §6.1 錯誤狀態的目標）。
            if (IsHeld(target) && _lastSent.ContainsKey(target))
            {
                EnsureRetry(target);
                continue;
            }

            // 5xx 等錯誤退避中的 E6：從上一次送出起算退避後的間隔，退避等級不重設（spec §6.4）。
            if (RunDetailInterval(repo, runId) is { } interval && !RescheduleBackoff(target, interval))
            {
                Schedule(target, interval);
            }
        }
    }

    /// <summary>這個 repo 所有該有 E6 的 run：P1 的對象（<see cref="RunDetailCandidates"/>）與操作後追蹤的 run 取聯集。</summary>
    private HashSet<long> RunDetailTargets(RepoInfo repo)
    {
        var targets = RunDetailCandidates(Data(repo.Id), _latestPage.GetValueOrDefault(repo.Id) ?? []);
        targets.UnionWith(PostActionRunIds(repo.Id));
        return targets;
    }

    /// <summary>E6 的對象：本地顯示未完成或等待、不在最近一次 E4 第 1 頁的 run（spec §4.2 E6、§10.10 第 4 類）。</summary>
    private static HashSet<long> RunDetailCandidates(RepoData data, IReadOnlyList<long> latestPage)
    {
        var page = latestPage.ToHashSet();
        return data.Runs
            .Where(run => !page.Contains(run.Id) && !RunStateMapper.Map(run).IsCompleted)
            .Select(run => run.Id)
            .ToHashSet();
    }

    /// <summary>預設分支鍵缺資料時用 E5 補抓（spec §4.2 E5）；SeedRecheck 內補抓過的不重複送。</summary>
    private void EnsureSeeds(RepoInfo repo)
    {
        var candidates = DefaultBranchSeedCandidates(repo);
        _scheduler.RemoveWhere(target => target.Kind == TargetKind.DefaultBranchSeed && target.RepoId == repo.Id
            && target.ItemId is { } workflowId && !candidates.Contains(workflowId));

        // 不再需要任何 E5：這次執行 E5 的問題標示已經不是目前的問題。
        if (candidates.Count == 0 && HasProblem(repo.Id, TargetKind.DefaultBranchSeed))
        {
            ClearProblem(repo.Id, TargetKind.DefaultBranchSeed);
        }

        // 暫停模式不自動補抓（spec §6.2）。
        if (_refreshMode == RefreshMode.Paused)
        {
            return;
        }

        foreach (var workflowId in candidates)
        {
            var target = new PollTarget(TargetKind.DefaultBranchSeed, repo.Id, workflowId);
            if (_scheduler.Contains(target) || _current == target)
            {
                continue;
            }

            // 等權限重試、被暫停或靜音移出排程的 E5：從上一次送出起算重試間隔，不立即補抓（spec §6.1 錯誤狀態的目標）。
            if (IsHeld(target) && _lastSent.ContainsKey(target))
            {
                EnsureRetry(target);
                continue;
            }

            // 5xx 等錯誤退避中、被暫停或靜音移出排程的 E5：照退避排回去，不立即補抓（spec §6.4）。
            if (RestoreBackoff(target))
            {
                continue;
            }

            if (_seededAt.TryGetValue((repo.Id, workflowId), out var seededAt) && _time.GetElapsedTime(seededAt) < Tuning.SeedRecheck)
            {
                continue;
            }

            _scheduler.ScheduleNow(target, RequestPriority.Maintenance);
        }
    }

    private HashSet<long> DefaultBranchSeedCandidates(RepoInfo repo)
    {
        var input = TrackingInput(repo, Data(repo.Id));
        var keys = TrackedRunSelector.Evaluate(input, _time.GetUtcNow());
        return TrackedRunSelector.DefaultBranchSeedCandidates(input, keys).ToHashSet();
    }

    /// <summary>清理時要保留的 run：各追蹤鍵的 latest 與 lastDecisive 組、被標記的 run（spec §14.3）。</summary>
    private HashSet<long> ProtectedRunIds()
    {
        var ids = new HashSet<long>(_watched);
        foreach (var repo in _repos.Values.Where(repo => IsMonitored(repo.Id)))
        {
            foreach (var key in EvaluateKeys(repo))
            {
                ids.Add(key.Latest.Id);
                ids.UnionWith(key.LastDecisiveGroupRunIds);
            }
        }

        return ids;
    }
}
