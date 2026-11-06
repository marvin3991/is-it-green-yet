using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Polling;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Engine;

/// <summary>App 的指令、全量刷新、鎖定與刷新模式、認證的重新檢查。</summary>
public sealed partial class MonitorEngine
{
    /// <summary>先處理完所有排隊的指令，這些指令就不會排在網路請求後面（spec §3.1）。</summary>
    private async Task ProcessCommandsAsync(CancellationToken ct)
    {
        while (_commands.TryDequeue(out var command))
        {
            try
            {
                await ApplyCommandAsync(command, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportUnexpected("處理指令 " + command.GetType().Name, ex);
            }
        }
    }

    private async Task ApplyCommandAsync(EngineCommand command, CancellationToken ct)
    {
        switch (command)
        {
            case EngineCommand.RefreshAll:
                FullRefresh();
                break;

            case EngineCommand.CliRefresh:
                // 序號、被擋住時立即完成、刷新完成的判斷（第二階段 spec §10.5，MonitorEngine.Status.cs）。
                OnCliRefresh();
                break;

            case EngineCommand.SetLocked(var locked):
                SetLocked(locked);
                break;

            case EngineCommand.Suspend:
                _suspended = true;
                break;

            case EngineCommand.Resume:
                // 喚醒：立即全量刷新；網路可能還沒起來，失敗就走離線流程（spec §6.5）。
                _suspended = false;
                _updateEvalRequested = true;
                FullRefresh();
                break;

            case EngineCommand.NetworkAvailable:
                if (_offline.IsOffline)
                {
                    _scheduler.ScheduleNow(OfflineProbeTarget, RequestPriority.Probe);
                }

                break;

            case EngineCommand.SetRefreshMode(var mode):
                ApplyRefreshMode(mode);
                break;

            case EngineCommand.SetOverride(var repoId, var value):
                ArgumentNullException.ThrowIfNull(value);

                _cache.SetOverride(repoId, value);

                // 寫入成功才算設過：失敗時這份檔裡沒有這一筆，留著只會佔名額，滿額時還會蓋掉 latest 裡真正的設定。
                // 其實寫進去了卻回報失敗的話，丟棄之前 IsDiscardable 讀得到資料列，一樣會留。
                _databaseMayHoldSettings = true;
                _overrides[repoId] = value;
                if (_repos.ContainsKey(repoId))
                {
                    Reclassify(repoId, ClassifyCause.User);
                    if (NeedsCollaboratorList())
                    {
                        EnsureProbeB();
                    }
                }

                break;

            case EngineCommand.SetWatched(var runId, var watched):
                if (!watched || CanWatch(runId))
                {
                    SetWatched(runId, watched);
                }

                break;

            case EngineCommand.SettingsChanged(var settings):
                await ApplySettingsAsync(settings, ct).ConfigureAwait(false);
                break;

            case EngineCommand.RecheckAuth:
                await RecheckAuthNowAsync(ct).ConfigureAwait(false);
                break;

            case EngineCommand.TimeZoneChanged:
                // 時區只影響顯示；TimeZoneInfo.ClearCachedData 由 App 呼叫（spec §6.5），這一步結束時照常發布新快照。
                break;

            case EngineCommand.ToastDismissed:
                _toasts.Dismissed();
                break;

            case EngineCommand.RepoManagerOpened:
                RequestCollaboratorListOnce();
                break;

            default:
                throw new NotSupportedException("不認得的指令：" + command.GetType().Name);
        }
    }

    /// <summary>
    /// 開啟 repo 管理頁時抓一次清單 B（spec §5.1）：進行中的那一輪還沒抓到清單 B 就接著抓；沒有進行中的就立即開一輪
    /// （帶 ETag，沒變時是 304）。抓完一輪完整的清單 B 才清掉要求；離線或這一輪中途失敗時留著，下一輪照樣帶上。
    /// </summary>
    private void RequestCollaboratorListOnce()
    {
        _collaboratorListOnce = true;
        if (_round is { } round)
        {
            if (!round.OnCollaboratorList)
            {
                round.IncludeCollaborators = true;
            }

            return;
        }

        if (!_offline.IsOffline && (!IsHeld(RepoListTarget) || RetryDue(RepoListTarget)))
        {
            _scheduler.ScheduleNow(RepoListTarget, RequestPriority.User);
        }
    }

    /// <summary>
    /// 全量刷新（spec §6.6）：push 探測、所有監看中 repo 的 runs 與 PR 清單排成現在到期，照樣帶 ETag、受節流控制。
    /// 離線時改成立即探測一次連線；等錯誤重試的目標（404、權限錯誤）在重試間隔內不提前（<see cref="RetryDue"/>）。
    /// 暫停模式下這次執行還沒抓完過 repo 清單時，用一輪完整重抓代替探測（<see cref="RefreshRepoListInsteadOfProbes"/>）。
    /// </summary>
    /// <remarks>
    /// workflow 清單還沒查過的監看中 repo 要先有 E3 才排 E4（有 workflow 才 E4），所以改成立即補查 E3、回來後照常排 E4
    /// （暫停模式也一樣，見 <see cref="RefreshAfterFirstWorkflowCheck"/>）；
    /// 這不是週期性的 workflow 清單刷新（那仍依 WorkflowsRefresh）。第一次 E3 遇到暫時性錯誤時，F5 不必再等一個間隔。
    /// </remarks>
    /// <returns>這次排成現在到期的目標（離線時只有離線探測）：命令列刷新把它們當成這一批（第二階段 spec §10.5 第 3 點）。</returns>
    private IReadOnlyList<PollTarget> FullRefresh()
    {
        var scheduled = new List<PollTarget>();
        if (_offline.IsOffline)
        {
            ScheduleNow(OfflineProbeTarget, RequestPriority.Probe);
            return scheduled;
        }

        if (!RefreshRepoListInsteadOfProbes(scheduled))
        {
            // 等錯誤重試的探測（非限流的 403／404）同樣在重試間隔內不提前。
            if (!IsHeld(ProbeATarget) || RetryDue(ProbeATarget))
            {
                ScheduleNow(ProbeATarget, RequestPriority.Probe);
            }

            if (NeedsCollaboratorList() && (!IsHeld(ProbeBTarget) || RetryDue(ProbeBTarget)))
            {
                ScheduleNow(ProbeBTarget, RequestPriority.Probe);
            }
        }

        foreach (var repo in _repos.Values.Where(repo => IsMonitored(repo.Id)).OrderBy(repo => repo.Id))
        {
            if (StateOf(repo.Id).WorkflowsTotal is null)
            {
                var workflowsTarget = new PollTarget(TargetKind.Workflows, repo.Id);
                if (_current != workflowsTarget && (!IsHeld(repo.Id, TargetKind.Workflows) || RetryDue(workflowsTarget)))
                {
                    ScheduleNow(workflowsTarget, RequestPriority.Maintenance);
                }

                continue;
            }

            var runsTarget = new PollTarget(TargetKind.Runs, repo.Id);
            if (!IsHeld(repo.Id, TargetKind.Runs) || RetryDue(runsTarget))
            {
                ScheduleNow(runsTarget, RunsPriority(repo));
            }

            var pullsTarget = new PollTarget(TargetKind.PullRequests, repo.Id);
            if ((!_prStopped.Contains(repo.Id) && !IsHeld(repo.Id, TargetKind.PullRequests)) || RetryDue(pullsTarget))
            {
                ScheduleNow(pullsTarget, RequestPriority.PullRequests);
            }
        }

        return scheduled;

        void ScheduleNow(PollTarget target, RequestPriority priority)
        {
            _scheduler.ScheduleNow(target, priority);
            scheduled.Add(target);
        }
    }

    /// <summary>
    /// 暫停模式沒有週期性的完整重抓，也沒有啟動時的那一輪（spec §5.3）：這次執行還沒抓完過清單時，全量刷新補做一輪，代替探測（spec §6.6）。
    /// 探測只有最近 push 的第 1 頁，repo 超過一頁時看不到其他的，它們的失敗算不進總狀態。一輪完整重抓已經包含探測要看的內容，不必兩個都送；
    /// 這一輪也排進命令列刷新的這一批，最後一頁回來才算處理完（<see cref="RunTargetAsync"/>）。
    /// 已經排好或進行中的那一輪（例如開啟 repo 管理頁開始的）就是這一輪：ScheduleNow 不改它的到期時間，優先序也不降低。
    /// </summary>
    /// <remarks>
    /// 這兩種情況照舊送探測，總狀態不會因此變成成功（<see cref="Summarize"/>）：等錯誤重試的清單在重試間隔內不提前（spec §6.1）；
    /// 重建資料庫的退避期間，抓回來的清單會被下一次重建清掉，儲存層還壞時也寫不進去，每按一次就多送一輪是白花額度。
    /// </remarks>
    /// <returns>補做完整重抓、不必送探測時回 true。</returns>
    private bool RefreshRepoListInsteadOfProbes(List<PollTarget> scheduled)
    {
        if (_refreshMode != RefreshMode.Paused || _repoListFetched || RecoveryWaiting()
            || (_round is null && IsHeld(RepoListTarget) && !RetryDue(RepoListTarget)))
        {
            return false;
        }

        _scheduler.ScheduleNow(RepoListTarget, RequestPriority.Maintenance);
        scheduled.Add(RepoListTarget);
        return true;
    }

    /// <summary>
    /// 等錯誤重試（權限錯誤、補抓或 E7 停用等）而且不在排程裡的目標，已經到了重試的時間：從上一次送出起算該問題的重試間隔（spec §6.1）；
    /// 沒有送出紀錄（例如長時間暫停後被清理掉）時當成已到。暫停模式下沒有週期性輪詢會排它（<see cref="EnsureRetry"/> 不排），
    /// F5 是唯一的刷新方式（spec §6.2、§6.6），所以全量刷新照這個規則送。還在排程裡的照它排定的時間；
    /// 404 等完整重抓確認的交給完整重抓（spec §5.4），都不算。
    /// </summary>
    private bool RetryDue(PollTarget target)
    {
        if ((target.RepoId is { } repoId && _pending404.Contains((repoId, target.Kind)))
            || !TryGetRetryInterval(target, out var retryInterval)
            || _scheduler.Contains(target))
        {
            return false;
        }

        // 重試間隔同 EnsureRetry（乘上 k、受鎖定下限約束）；刷新模式在這裡只會讓暫停時回 null，所以固定用自動模式算間隔本身。
        var interval = IntervalPolicy.Periodic(retryInterval, RefreshMode.Auto, Stretch(), _locked, followsRefreshMode: false)
            ?? retryInterval;
        return !_lastSent.TryGetValue(target, out var sent) || _time.GetElapsedTime(sent) >= interval;
    }

    /// <summary>鎖定時所有間隔下限變 LockedMinInterval、通知改為暫存；解鎖時全量刷新、暫存的通知彙總成一則（spec §6.5、§9.5）。</summary>
    private void SetLocked(bool locked)
    {
        if (_locked == locked)
        {
            return;
        }

        _locked = locked;
        _toasts.SetLocked(locked);
        if (locked)
        {
            ApplyLockedFloor();
        }
        else
        {
            _updateEvalRequested = true;
            FullRefresh();
        }
    }

    /// <summary>
    /// 已經排好的目標也套用鎖定下限：離上一次送出（沒送過就是現在）不到 LockedMinInterval 的往後延。
    /// 重跑確認的 E6 不套用（第二階段 spec §6.2：最多 5 分鐘、額度可以忽略）。
    /// </summary>
    private void ApplyLockedFloor()
    {
        var now = _time.GetTimestamp();
        foreach (var target in _scheduler.Scheduled)
        {
            if (target.Kind == TargetKind.OfflineProbe
                || IsRerunConfirmation(target)
                || _scheduler.TimeUntilDue(target) is not { } due
                || _scheduler.PriorityOf(target) is not { } priority)
            {
                continue;
            }

            var since = _lastSent.TryGetValue(target, out var sent) ? _time.GetElapsedTime(sent, now) : TimeSpan.Zero;
            var floor = Tuning.LockedMinInterval - since;
            if (floor > due)
            {
                _scheduler.Schedule(target, floor, priority);
            }
        }
    }

    /// <summary>「自動更新」下拉（spec §6.2、§10.3）：暫停時不排任何週期性輪詢；恢復時全量刷新並排回週期性目標。</summary>
    private void ApplyRefreshMode(RefreshMode mode)
    {
        if (!Enum.IsDefined(mode) || mode == _refreshMode)
        {
            return;
        }

        var wasPaused = _refreshMode == RefreshMode.Paused;
        _refreshMode = mode;

        if (mode == RefreshMode.Paused)
        {
            // 進行到一半的完整重抓也放棄（不完整的一輪本來就不算數），恢復時重新排。
            // 操作後追蹤的 E6 不是週期性輪詢，照常追到結束（第二階段 spec §6.2）。
            _scheduler.RemoveWhere(target => target.Kind != TargetKind.OfflineProbe && !IsPostActionTarget(target));
            _round = null;
            return;
        }

        if (wasPaused)
        {
            FullRefresh();
            EnsurePeriodicTargets();
            return;
        }

        // 固定秒數只影響探測與 runs：依新的間隔、從上一次送出起算重排。
        // 等錯誤重試的 E4 與探測照原本的重試間隔（PermissionRetryInterval／NotFoundRetryInterval 不跟著刷新模式，spec §6.1），不提前；
        // 正在 5xx 等錯誤退避中的不短於退避後的間隔，退避等級也不重設（spec §6.4）。
        foreach (var target in _scheduler.Scheduled.Where(t => t.Kind is TargetKind.Probe or TargetKind.Runs))
        {
            if (IsHeld(target) || !_lastSent.TryGetValue(target, out var sent) || NormalInterval(target) is not { } interval
                || RescheduleBackoff(target, interval))
            {
                continue;
            }

            var remaining = interval - _time.GetElapsedTime(sent);
            Schedule(target, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
    }

    private async Task ApplySettingsAsync(AppSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var old = _settings;
        var updated = EngineSettings.From(settings);
        _settings = updated;

        if (old.Language != updated.Language)
        {
            // 換語言：記住的橫幅與問題標示用新的語言重建；這一步結尾發布的快照、之後的提示與操作結果都是新的語言。
            ApplyLanguage();
        }

        if (old.IncludeForks != updated.IncludeForks || old.IncludeCollaboratorRepos != updated.IncludeCollaboratorRepos)
        {
            // 設定「包含協作／fork」改變：完整重抓（spec §5.3），並以操作時間為界線重新分類（spec §9.3）。
            var changedAt = _time.GetUtcNow();
            RequestRepoList();

            // 進行中的那一輪開始時還沒打開：同一輪接著抓清單 B（同 RequestCollaboratorListOnce），否則它抓完就算抓過，清單 B 還沒抓。
            if (updated.IncludeCollaboratorRepos && _round is { OnCollaboratorList: false } round)
            {
                round.IncludeCollaborators = true;
            }

            foreach (var repoId in _repos.Keys.ToList())
            {
                Reclassify(repoId, ClassifyCause.User, changedAt);
            }

            // 剛打開「包含協作 repo」：之後才從清單 B 第一次看到的協作 repo 也以這個時間為界線（ApplyListedRepo）。
            if (updated.IncludeCollaboratorRepos && !old.IncludeCollaboratorRepos)
            {
                _collaboratorListBaseline ??= changedAt;
            }
            else if (!updated.IncludeCollaboratorRepos)
            {
                _collaboratorListBaseline = null;
            }

            if (NeedsCollaboratorList())
            {
                EnsureProbeB();
            }
            else
            {
                _scheduler.Remove(ProbeBTarget);
            }
        }

        if (old.RefreshMode != updated.RefreshMode)
        {
            ApplyRefreshMode(updated.RefreshMode);
        }

        if (old.AuthMode != updated.AuthMode || old.IgnoreGhTokenEnv != updated.IgnoreGhTokenEnv)
        {
            // token 的來源變了：和「重新檢查」一樣，重新取得 token 並用 E1 驗證一次。
            await RecheckAuthNowAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 「完成時通知我」只標記本地還沒完成（未完成或等待）的 run：已經完成的不會再有完成觀察，
    /// 標記永遠不會自動取消（spec §9.1 N3）。例如畫面還是舊快照時，Engine 已經先處理了完成。
    /// </summary>
    private bool CanWatch(long runId) =>
        _repos.Keys.SelectMany(repoId => Data(repoId).Runs).FirstOrDefault(run => run.Id == runId) is { } run
        && !RunStateMapper.Map(run).IsCompleted;

    private void SetWatched(long runId, bool watched)
    {
        _cache.SetWatched(runId, watched);
        if (watched)
        {
            _watched.Add(runId);
        }
        else
        {
            _watched.Remove(runId);
        }

        // 標記改變 E6 的間隔：被標記的 run 用所屬 repo 的間隔（spec §6.1）。等權限重試的 E6 照重試間隔，不提前；
        // 正在 5xx 等錯誤退避中的不短於退避後的間隔，退避等級也不重設（spec §6.4）。
        foreach (var repo in _repos.Values.Where(repo => IsMonitored(repo.Id) && Data(repo.Id).Runs.Any(run => run.Id == runId)))
        {
            var target = new PollTarget(TargetKind.RunDetail, repo.Id, runId);
            if (_scheduler.Contains(target) && !IsHeld(target) && RunDetailInterval(repo, runId) is { } interval
                && !RescheduleBackoff(target, interval))
            {
                Schedule(target, interval);
            }

            EnsureRunDetails(repo);
        }
    }

    /// <summary>「重新檢查」：立即重新取得 token，不論有沒有變都用 E1 驗證一次（spec §13.2 第 3 點）。</summary>
    /// <remarks>
    /// 已經是 Ok、token 也沒變時，這個 E1 是手動按才送的單一請求：不擋其他輪詢，非 401 的失敗也不連續重試（<see cref="RecheckAccountAsync"/>）。
    /// 其他情況（未登入、換到還沒驗證的新 token）照舊：E1 驗證成功之前不輪詢。其中未登入時拿回的仍是被拒的那一把 token，
    /// 這個 E1 同樣只送一次，非 401 的失敗不退避重送（<see cref="OnVerifyFailed"/>）；換到的新 token 則驗證到成功為止（handoff Task 10）。
    /// </remarks>
    private async Task RecheckAuthNowAsync(CancellationToken ct)
    {
        var verifiedToken = _auth.Status == AuthStatus.Ok && !_needsVerify ? _auth.Token : null;
        var verifyingNewToken = _needsVerify && !_verifyOnce;
        var changed = await RecheckTokenAsync(ct).ConfigureAwait(false);
        if (_auth.Token is not { } token)
        {
            // 拿不到任何 token（gh 已登出、改成 PAT 但還沒貼上）：原本在等的驗證由 SendNextAsync 放棄（AbandonVerify）。
            return;
        }

        if (_auth.Status == AuthStatus.Ok && verifiedToken is not null && string.Equals(token, verifiedToken, StringComparison.Ordinal))
        {
            _recheckPending = true;
            return;
        }

        // 未登入、token 沒變，而且不是正在驗證換到的新 token：拿回的就是被拒的那一把。
        _needsVerify = true;
        _verifyOnce = !changed && _auth.Status == AuthStatus.Unauthorized && !verifyingNewToken;
        _verifyAt = null;
        _recheckPending = false;
    }

    /// <summary>
    /// 沒有 token 時不再等驗證：沒有東西可以驗證，未登入期間也不送任何請求（spec §13.2 第 2 點），否則等退避的 E1 會不帶憑證送出。
    /// 恢復交給定期重新檢查（gh 模式）、設定與「重新檢查」按鈕。
    /// </summary>
    private void AbandonVerify()
    {
        _needsVerify = false;
        _verifyOnce = false;
        _verifyAt = null;
        _recheckPending = false;
        _lastAuthRecheckAt = _time.GetTimestamp();
    }

    /// <summary>未登入期間（gh 模式）每 AuthRecheckInterval 在本機重新取得 token，不打 API；值改變才用 E1 驗證（spec §13.2）。</summary>
    private async Task MaintainAuthAsync(CancellationToken ct)
    {
        if (_auth.Status != AuthStatus.Unauthorized
            || _needsVerify
            || _settings.AuthMode != AuthMode.GhCli
            || _time.GetElapsedTime(_lastAuthRecheckAt) < Tuning.AuthRecheckInterval)
        {
            return;
        }

        if (await RecheckTokenAsync(ct).ConfigureAwait(false))
        {
            _needsVerify = true;
            _verifyAt = null;
        }
    }

    /// <returns>token 改變時回 true。</returns>
    private async Task<bool> RecheckTokenAsync(CancellationToken ct)
    {
        _lastAuthRecheckAt = _time.GetTimestamp();
        try
        {
            return await _auth.RecheckAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportUnexpected("重新取得 token", ex);
            return false;
        }
    }

    private void EnsureProbeB()
    {
        if (!_scheduler.Contains(ProbeBTarget) && _current != ProbeBTarget)
        {
            _scheduler.ScheduleNow(ProbeBTarget, RequestPriority.Probe);
        }
    }
}
