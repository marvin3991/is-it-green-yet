using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Polling;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Engine;

/// <summary>每種輪詢目標的處理：呼叫哪個端點、成功後寫哪些資料表、重新評估與重排哪些目標。</summary>
public sealed partial class MonitorEngine
{
    /// <summary>離線探測每次失敗間隔加倍（spec §6.4「從 30 秒起倍增到 300 秒」）。</summary>
    private const int OfflineProbeBackoffFactor = 2;

    /// <summary>還不知道自己的 user id 時（不會發生：完整重抓一定在 E1 之後）用這個值，不會等於任何 owner.id。</summary>
    private const long UnknownUserId = 0;

    /// <summary>第 1 次執行的 run_attempt：只有它的成功存 ETA 樣本（第二階段 spec §8.2）。</summary>
    private const int FirstAttempt = 1;

    private Task<int> ProcessTargetAsync(PollTarget target, CancellationToken ct) => target.Kind switch
    {
        TargetKind.Probe => ProcessProbeAsync(target, ct),
        TargetKind.RepoList => ProcessRepoListAsync(ct),
        TargetKind.Workflows or TargetKind.StaleCheck => ProcessWorkflowsAsync(target, ct),
        TargetKind.Runs => ProcessRunsAsync(target, ct),
        TargetKind.RunDetail => ProcessRunDetailAsync(target, ct),
        TargetKind.PullRequests => ProcessPullRequestsAsync(target, ct),
        TargetKind.DefaultBranchSeed => ProcessDefaultBranchSeedAsync(target, ct),
        TargetKind.PullRequestSeed => ProcessPullRequestSeedAsync(target, ct),
        TargetKind.OfflineProbe => ProcessOfflineProbeAsync(ct),
        _ => throw new NotSupportedException("不認得的輪詢目標：" + target.Kind),
    };

    /// <summary>repo 已經被移除（例如連續兩輪不見）時，它殘留的目標直接丟掉。</summary>
    private bool TryGetRepo(PollTarget target, out RepoInfo repo)
    {
        if (target.RepoId is { } id && _repos.TryGetValue(id, out var found))
        {
            repo = found;
            return true;
        }

        repo = null!;
        return false;
    }

    // ── E1、E10 ───────────────────────────────────────────────────────

    /// <summary>E1：取得 token 後、重新檢查時、換 token 時驗證一次（spec §4.2 E1、§13.2）。</summary>
    private async Task<int> VerifyAccountAsync(CancellationToken ct)
    {
        var (result, requests) = await CallAsync(_api.GetUserAsync, repoId: null, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ApiOutcome.Ok when TryMarkVerified(_lastCallSent):
                OnAccountVerified(result.Value!);
                break;

            case ApiOutcome.Ok:
                // 送出之後使用者操作用同一把 token 收到 401（因此可能換了 token）：這次成功不算驗證了目前的 token（spec §13.2）。
                // 進入未登入時同下面的 401；換到還沒驗證的新 token 時下一步再用它驗證。別的 token 的 401 不會走到這裡（TryMarkVerified）。
                if (_auth.Status == AuthStatus.Unauthorized)
                {
                    StopVerifyingRejected();
                }
                else
                {
                    _verifyAt = null;
                }

                break;

            case ApiOutcome.Unauthorized:
                // 401 流程已經走完；仍是未登入時改由定期重新檢查恢復。
                if (_auth.Status == AuthStatus.Unauthorized)
                {
                    StopVerifyingRejected();
                }

                break;

            case ApiOutcome.RateLimited:
            case ApiOutcome.VersionError:
                // 暫停或停止由 Observe 處理；驗證留到恢復後。
                break;

            default:
                OnVerifyFailed();
                break;
        }

        return requests;
    }

    /// <summary>等驗證的 token 已經被拒、進入未登入：不再等驗證，改由定期重新檢查恢復（spec §13.2 第 3 點）。</summary>
    private void StopVerifyingRejected()
    {
        _needsVerify = false;
        _verifyOnce = false;
        _lastAuthRecheckAt = _time.GetTimestamp();
    }

    /// <summary>E1 遇到 401 以外的失敗（連線失敗、伺服器錯誤、處理時丟出例外）。</summary>
    /// <remarks>
    /// 未登入時「重新檢查」拿回的仍是被拒的那一把 token：這次 E1 是手動按才送的單一請求（spec §13.2 第 3 點），
    /// 不退避重送，回到定期重新檢查（值改變才再用 E1 驗證）。其他情況（啟動時、換到的新 token）記住「還沒驗證」，
    /// 退避後再送，網路恢復時立即再送（handoff Task 10）。
    /// </remarks>
    private void OnVerifyFailed()
    {
        if (_verifyOnce)
        {
            _verifyOnce = false;
            _needsVerify = false;
            _verifyAt = null;
            _lastAuthRecheckAt = _time.GetTimestamp();
            return;
        }

        _verifyDelay = IntervalPolicy.ErrorBackoff(Tuning.OfflineProbeInitial, _verifyDelay, Stretch());
        _verifyAt = FutureTimestamp(_verifyDelay);
    }

    /// <remarks>
    /// 驗證的狀態先改好再寫 DB：帳號寫不進 DB（例如磁碟滿、唯讀）時留在記憶體、照常開始輪詢，只寫 error.log，
    /// 不會因為還在等驗證而一直重送 E1（spec §15）。DB 損毀時由 ReportUnexpected 記下，步驟結尾改名重建時帳號會寫回新 DB。
    /// </remarks>
    private void OnAccountVerified(AccountInfo account)
    {
        _updateEvalRequested = true;

        // 這次執行中改用另一個帳號（設定換成另一把 PAT、gh 換了登入帳號）：之後第一次看到的 repo 是因為你的操作才看得到，
        // 界線是驗證的時間（spec §9.3「改設定」），到下一輪完整重抓結束為止。進行到一半的完整重抓混了舊帳號的分頁，放棄重來。
        // 這次執行第一次驗證不算：那時看到的 repo 屬於「第一次完整重抓就看到」，界線是程式啟動時間。
        if (_scheduleSeeded && _account is { } previous && previous.UserId != account.UserId)
        {
            _accountSwitchBaseline = _time.GetUtcNow();
            _round = null;
        }

        _account = account;
        _needsVerify = false;
        _verifyOnce = false;
        _recheckPending = false;
        _verifyAt = null;
        _verifyDelay = TimeSpan.Zero;

        // 換到另一個帳號：舊帳號讀到的 push 不適用（第二階段 spec §4.1），改讀新帳號存過的值。
        if (_pushPermissions?.UserId != account.UserId)
        {
            ReloadPushPermissions(account.UserId);
        }

        try
        {
            _cache.SaveAccount(account);
        }
        catch (Exception ex)
        {
            ReportUnexpected("寫入帳號", ex);
        }

        if (!_scheduleSeeded)
        {
            // 啟動時一次 E10（spec §4.2 E10），之後開始輪詢。
            _startupRateLimitPending = true;
            SeedInitialSchedule();
        }
        else
        {
            // 從未登入恢復：照「恢復輪詢」做一次全量刷新。
            FullRefresh();
            EnsurePeriodicTargets();
        }
    }

    /// <summary>
    /// 已登入、token 沒變時「重新檢查」的 E1（spec §13.2 第 3 點）：手動按才送的單一請求，不擋其他輪詢。
    /// 401 照 §13.2 處理（<see cref="CallAsync{T}"/>）；限流時等暫停結束再送這一次；其他失敗不自動重送（spec §6.4 只影響這個請求）。
    /// </summary>
    private async Task<int> RecheckAccountAsync(CancellationToken ct)
    {
        var (result, requests) = await CallAsync(_api.GetUserAsync, repoId: null, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ApiOutcome.Ok when TryMarkVerified(_lastCallSent):
                // 送出之後使用者操作收到 401 時不算數（同 VerifyAccountAsync），狀態由那次 401 的流程決定。
                var account = result.Value!;
                _account = account;
                _cache.SaveAccount(account);
                break;

            case ApiOutcome.Unauthorized when _auth.Status == AuthStatus.Unauthorized:
                // 進入未登入之後改由定期重新檢查恢復（同 VerifyAccountAsync）。
                _lastAuthRecheckAt = _time.GetTimestamp();
                break;

            case ApiOutcome.RateLimited:
                _recheckPending = true;
                break;
        }

        return requests;
    }

    private async Task<int> StartupRateLimitAsync(CancellationToken ct)
    {
        var (result, requests) = await CallAsync(_api.GetRateLimitAsync, repoId: null, ct).ConfigureAwait(false);
        if (result.Outcome == ApiOutcome.Ok)
        {
            RecordRateLimit(result.Value);
        }

        return requests;
    }

    /// <summary>離線時的 E10 探測：有任何回應就恢復（Observe 會全量刷新），連線仍失敗就倍增間隔。</summary>
    private async Task<int> ProcessOfflineProbeAsync(CancellationToken ct)
    {
        var (result, requests) = await CallAsync(_api.GetRateLimitAsync, repoId: null, ct).ConfigureAwait(false);
        if (result.Outcome == ApiOutcome.NetworkError)
        {
            var doubled = _offlineProbeDelay * OfflineProbeBackoffFactor;
            _offlineProbeDelay = doubled < Tuning.OfflineProbeMax ? doubled : Tuning.OfflineProbeMax;
            _scheduler.Schedule(OfflineProbeTarget, _offlineProbeDelay, RequestPriority.Probe);
        }
        else if (result.Outcome == ApiOutcome.Ok)
        {
            RecordRateLimit(result.Value);
        }

        return requests;
    }

    // ── E2 ────────────────────────────────────────────────────────────

    /// <summary>push 探測（探測 A；需要時另有探測 B）：pushed_at 變動的 repo 立即輪詢（spec §5.3）。</summary>
    private async Task<int> ProcessProbeAsync(PollTarget target, CancellationToken ct)
    {
        var collaborator = target.ItemId == CollaboratorListItem;
        if (collaborator && !NeedsCollaboratorList())
        {
            return 0;
        }

        var url = GitHubApi.RepoListUrl(collaborator, probe: true);
        var (result, requests) = await CallAsync(c => _api.GetRepoPageAsync(url, c), repoId: null, ct).ConfigureAwait(false);
        if (result.Outcome == ApiOutcome.Ok)
        {
            ApplyProbePage(result.Value!, collaborator, result.CacheEntry);
        }
        else if (result.Outcome != ApiOutcome.NotModified)
        {
            HandleFailure(target, OutcomeOf(result));
            return requests;
        }

        _targetRetries.Remove(target);
        Reschedule(target, WithPollInterval(NormalInterval(target), result.PollInterval));
        return requests;
    }

    /// <summary>
    /// 探測只看第 1 頁：新的 repo id 立即分類、pushed_at 變動的重新分類並立即輪詢、名稱變了當成改名。
    /// 不在這一頁的 repo 不算消失（spec §5.4，消失只看完整重抓）。
    /// </summary>
    private void ApplyProbePage(RepoPage page, bool collaborator, HttpCacheEntry? cacheEntry)
    {
        var me = _account?.UserId;
        var repos = page.Repos.Select(repo => repo with { Category = CategoryFor(repo, collaborator, me) }).ToList();
        var permissions = PermissionsUpdate(page.CanPush);
        _cache.UpsertRepos(repos, cacheEntry is null ? [] : [cacheEntry], permissions);
        RememberPushPermissions(permissions);

        foreach (var repo in repos)
        {
            ApplyListedRepo(repo);
        }

        PublishRepoLookup();
    }

    /// <summary>
    /// repo 清單回應的 permissions.push，交給 UpsertRepos 和 repo、ETag 同一個交易寫入（第二階段 spec §4.1）。
    /// 還不知道帳號時不寫：權限的鍵是 (repo_id, user_id)。
    /// </summary>
    private RepoPermissionsUpdate? PermissionsUpdate(IReadOnlyDictionary<long, bool?> canPush) =>
        _account is { } account ? new RepoPermissionsUpdate(account.UserId, canPush) : null;

    /// <summary>清單 A 依 owner.id 分自己的／組織的；清單 B 已知是清單 A 的維持原類別，其他是協作（spec §5.1）。</summary>
    private RepoCategory CategoryFor(RepoInfo repo, bool collaborator, long? me)
    {
        if (!collaborator)
        {
            return me is { } id && repo.OwnerId == id ? RepoCategory.Own : RepoCategory.Organization;
        }

        return _repos.TryGetValue(repo.Id, out var known) && known.Category != RepoCategory.Collaborator
            ? known.Category
            : RepoCategory.Collaborator;
    }

    /// <summary>清單或探測裡看到的一個 repo（已寫入 DB）：新增、改名、push、其他欄位變動。</summary>
    private void ApplyListedRepo(RepoInfo repo)
    {
        if (!_repos.TryGetValue(repo.Id, out var old))
        {
            _repos[repo.Id] = repo;

            // 執行中第一次出現的 repo：時間界線是程式啟動時間（spec §9.3）。剛打開「包含協作 repo」之後才抓到的協作 repo、
            // 剛換了帳號之後才看到的 repo 原本就存在，是因為你的操作才開始監看，界線是操作的時間（同表「因為你的操作才開始監看」）。
            if (OperationBaselineFor(repo) is { } changedAt)
            {
                Reclassify(repo.Id, ClassifyCause.User, changedAt);
            }
            else
            {
                Reclassify(repo.Id, ClassifyCause.Discovery);
            }

            return;
        }

        _repos[repo.Id] = repo;
        if (!string.Equals(old.FullName, repo.FullName, StringComparison.Ordinal))
        {
            HandleRename(repo.Id, old.FullName);
        }

        var switchedAt = AccountSwitchFor(old, repo);
        if (!Nullable.Equals(old.PushedAt, repo.PushedAt))
        {
            OnPushed(repo, switchedAt);
        }
        else if (switchedAt is not null)
        {
            Reclassify(repo.Id, ClassifyCause.User, switchedAt);
        }
        else if (old != repo)
        {
            Reclassify(repo.Id, ClassifyCause.Discovery);
        }
    }

    /// <summary>
    /// 換了帳號之後類別變了（例如原本略過的協作 repo 變成自己的）：轉成監看是因為你的操作，回傳換帳號的時間當界線（spec §9.3「改設定」）。
    /// 類別只影響第 5 條（協作 repo），所以只算原本因為第 5 條略過的 repo；略過期間有 push（pushed_at 也變了）也一樣，
    /// 協作 repo 的略過不會因為 push 解除。其他略過原因（例如 push 解除的第 6 條）照原本的原因決定界線。
    /// </summary>
    /// <remarks>要在重新分類之前呼叫：看的是 <c>_classes</c> 裡原本的分類。</remarks>
    private DateTimeOffset? AccountSwitchFor(RepoInfo old, RepoInfo repo) =>
        _accountSwitchBaseline is { } switchedAt
        && old.Category != repo.Category
        && _classes.TryGetValue(repo.Id, out var previous)
        && previous.Reason == SkipReason.Collaborator
            ? switchedAt
            : null;

    /// <summary>
    /// 執行中第一次出現的 repo 是因為你的哪個操作才看得到：打開「包含協作 repo」（協作 repo）、換了帳號；
    /// 執行中重建資料庫之後重新看到的 repo 沿用重建前的界線（<see cref="_recoveryBaselines"/>）。有多個時取最晚的。
    /// </summary>
    private DateTimeOffset? OperationBaselineFor(RepoInfo repo)
    {
        var collaborator = repo.Category == RepoCategory.Collaborator ? _collaboratorListBaseline : null;
        DateTimeOffset? rebuilt = _recoveryBaselines.TryGetValue(repo.Id, out var before) ? before : null;
        return new[] { collaborator, _accountSwitchBaseline, rebuilt }.Max();
    }

    /// <summary>
    /// pushed_at 變動（spec §5.3）：重新分類；監看中的 repo 立即排 E4，並排入 E3 與 E7。
    /// 被第 7、8 條略過的 repo（沒有 workflow、workflow 都已停用）也立即重查 E3：新推上的 workflow 不必等每小時一次的重查。
    /// </summary>
    /// <param name="switchedAt">
    /// 同時因為換帳號而轉換（<see cref="AccountSwitchFor"/>）時是換帳號的時間：以你的操作重新分類、界線是那個時間（spec §9.3「改設定」），
    /// 不是這次 push 的 pushed_at；push 該立即重查的目標照排。
    /// </param>
    private void OnPushed(RepoInfo repo, DateTimeOffset? switchedAt)
    {
        var wasMonitored = IsMonitored(repo.Id);
        if (switchedAt is { } operationAt)
        {
            Reclassify(repo.Id, ClassifyCause.User, operationAt);
        }
        else
        {
            Reclassify(repo.Id, ClassifyCause.Push);
        }

        var monitored = IsMonitored(repo.Id);

        if (IsWorkflowCandidate(repo.Id) && !IsHeld(repo.Id, TargetKind.Workflows))
        {
            _scheduler.ScheduleNow(new PollTarget(TargetKind.Workflows, repo.Id), RequestPriority.Maintenance);
            if (!monitored && switchedAt is null)
            {
                // 這次 E3 讓它轉成監看時，是因為這次 push（界線是 pushed_at，spec §9.3）。
                // 因為換帳號才轉換的，重新分類時已經記下換帳號的時間當界線（Reclassify），E3 之後沿用。
                _pushTriggeredWorkflows.Add(repo.Id);
            }
        }

        if (!wasMonitored || !monitored)
        {
            // 從略過轉成監看時，Reclassify 已經排好 E3 與 E4。
            return;
        }

        if (StateOf(repo.Id).WorkflowsTotal is not null && !IsHeld(repo.Id, TargetKind.Runs))
        {
            _scheduler.ScheduleNow(new PollTarget(TargetKind.Runs, repo.Id), RequestPriority.PushedRepo);
        }

        if (!_prStopped.Contains(repo.Id) && !IsHeld(repo.Id, TargetKind.PullRequests))
        {
            _scheduler.ScheduleNow(new PollTarget(TargetKind.PullRequests, repo.Id), RequestPriority.PullRequests);
        }
    }

    /// <summary>完整重抓的一頁（spec §5.3）：只跟 Link 的 next，304 時用存下的 next 與項目 id（spec §4.1 分頁）。</summary>
    private async Task<int> ProcessRepoListAsync(CancellationToken ct)
    {
        var round = _round ??= StartRound();
        var url = round.CurrentUrl;
        var (result, requests) = await CallAsync(c => _api.GetRepoPageAsync(url, c), repoId: null, ct).ConfigureAwait(false);
        var list = round.OnCollaboratorList ? round.ListB : round.ListA;
        string? next = null;

        switch (result.Outcome)
        {
            case ApiOutcome.Ok:
                list.AddRange(result.Value!.Repos);
                round.AddCanPush(result.Value.CanPush);
                if (result.CacheEntry is { } entry)
                {
                    round.Entries.Add(entry);
                }

                next = result.CacheEntry?.NextUrl;
                break;

            case ApiOutcome.NotModified:
                var ids = result.CacheEntry?.ItemIds;
                if (ids is null || ids.Any(id => !_repos.ContainsKey(id)))
                {
                    // 304 卻對不上本地資料：這一輪不算完整，下次改送不帶 ETag 的請求重抓。
                    round.Complete = false;
                    if (result.CacheEntry is { } stale)
                    {
                        _cache.PutCache(stale with { ETag = null });
                    }
                }
                else
                {
                    list.AddRange(ids.Select(id => _repos[id]));
                    next = result.CacheEntry!.NextUrl;
                }

                break;

            default:
                // 分頁中途失敗：這一輪不算數（spec §5.4），已抓到的分頁照常寫入。
                round.Complete = false;
                FinishRound(round);
                HandleFailure(RepoListTarget, OutcomeOf(result));
                return requests;
        }

        if (round.Complete && next is not null && round.Visited.Add(next))
        {
            round.CurrentUrl = next;
            _scheduler.ScheduleNow(RepoListTarget, RequestPriority.Maintenance);
            return requests;
        }

        if (round.Complete && !round.OnCollaboratorList && round.IncludeCollaborators)
        {
            round.OnCollaboratorList = true;
            round.FetchedCollaborators = true;
            round.CurrentUrl = GitHubApi.RepoListUrl(collaborator: true, probe: false);
            round.Visited.Add(round.CurrentUrl);
            _scheduler.ScheduleNow(RepoListTarget, RequestPriority.Maintenance);
            return requests;
        }

        FinishRound(round);
        Reschedule(RepoListTarget, WithPollInterval(NormalInterval(RepoListTarget), result.PollInterval));
        return requests;
    }

    private RepoListRound StartRound()
    {
        var round = new RepoListRound(NeedsCollaboratorList() || _collaboratorListOnce);
        round.Visited.Add(round.CurrentUrl);
        return round;
    }

    /// <summary>
    /// 一輪結束：寫入（資料與 ETag 同一個交易）、改名、消失（連續兩輪才移除）、新 repo、重新分類（spec §5.2、§5.4）。
    /// </summary>
    private void FinishRound(RepoListRound round)
    {
        _round = null;
        var listB = round.FetchedCollaborators ? round.ListB : null;
        var merged = RepoDiscovery.Merge(_account?.UserId ?? UnknownUserId, round.ListA, listB);

        // 沒抓清單 B 的那一輪，協作 repo 不參與「消失」的判斷（handoff Task 11）。
        var known = _repos.Values.Where(repo => listB is not null || repo.Category != RepoCategory.Collaborator).ToList();
        var previousMissing = _states.Values.Where(state => state.MissingRounds > 0).ToDictionary(state => state.RepoId, state => state.MissingRounds);
        var evaluation = RepoDiscovery.EvaluateListing(known, merged, round.Complete, previousMissing);

        if (merged.Count > 0 || round.Entries.Count > 0)
        {
            var permissions = PermissionsUpdate(round.CanPush);
            _cache.UpsertRepos(merged, round.Entries, permissions);
            RememberPushPermissions(permissions);
        }

        foreach (var repo in merged)
        {
            ApplyListedRepo(repo);
        }

        foreach (var repo in known)
        {
            var missing = evaluation.MissingRounds.GetValueOrDefault(repo.Id);
            if (_states.ContainsKey(repo.Id) || missing > 0)
            {
                SaveState(StateOf(repo.Id) with { MissingRounds = missing });
            }
        }

        foreach (var id in evaluation.RemoveIds)
        {
            RemoveRepo(id);
        }

        // 抓完一輪完整的清單 B：之後才第一次出現的協作 repo 是新的 repo id，界線回到程式啟動時間（spec §9.3）。
        if (round.FetchedCollaborators && round.Complete)
        {
            _collaboratorListBaseline = null;
            _collaboratorListOnce = false;
        }

        // 換帳號之後抓完一輪完整清單（換帳號時放棄了進行中的那一輪，所以這一輪一定是換了之後才開始的）：同上。
        // 重建資料庫時也放棄了進行中的那一輪：重建前就有的 repo 這一輪都重新看過了。
        if (round.Complete)
        {
            _accountSwitchBaseline = null;
            _recoveryBaselines.Clear();
            _repoListFetched = true;
            _targetRetries.Remove(RepoListTarget);
            ForgetRerunsOfUnknownRepos();
        }

        ResolvePending404(merged.Select(repo => repo.Id).ToHashSet());

        // 每次完整重抓後都重新分類（spec §5.2）。
        foreach (var id in _repos.Keys.ToList())
        {
            Reclassify(id, ClassifyCause.Discovery);
        }

        PublishRepoLookup();
    }

    /// <summary>
    /// 完整重抓後仍在、名稱沒變，E4／E3 卻回 404：標示可能沒有 Actions 權限，改成每 NotFoundRetryInterval 重試（spec §5.4）。
    /// 改名的已經在 HandleRename 用新名稱重送。
    /// </summary>
    /// <remarks>
    /// 「仍在」要這一輪真的看到這個 id（<paramref name="listed"/>）。沒看到時（可能被刪除，要連續兩輪不見才算；或分頁中途失敗、還不知道在不在）
    /// 不寫權限提示，只照 NotFoundRetryInterval 重試：下一次仍 404 時會再排一次完整重抓確認，不會每一步都重送。
    /// 重試由接著的重新分類（EnsureRepoTargets → EnsureRetry）從 404 那次送出起算排入；
    /// 這段期間被靜音、略過或暫停的 repo 不會排，恢復時再照同一個規則排回去。
    /// </remarks>
    /// <param name="listed">這一輪清單裡看到的 repo id（改名的已經以新名稱合併進來）。</param>
    private void ResolvePending404(IReadOnlySet<long> listed)
    {
        foreach (var (repoId, kind) in _pending404.ToList())
        {
            _pending404.Remove((repoId, kind));
            if (!_repos.ContainsKey(repoId))
            {
                continue;
            }

            SetProblem(repoId, kind, listed.Contains(repoId) ? (Func<AppLanguage, string>)EngineText.ActionsNotFound : null, Tuning.NotFoundRetryInterval);
        }
    }

    /// <summary>收到 repo 請求的 301 或 404：排入一次完整重抓（spec §5.3）；已經在重抓中就等這一輪的結果。</summary>
    private void RequestRepoList()
    {
        if (_round is null && _current != RepoListTarget)
        {
            _scheduler.ScheduleNow(RepoListTarget, RequestPriority.Maintenance);
        }
    }

    /// <summary>改名（spec §5.4）：刪掉舊網址的快取，所有網址用新名稱重組並立即重送。</summary>
    private void HandleRename(long repoId, string oldFullName)
    {
        foreach (var prefix in RepoCachePrefixes(oldFullName))
        {
            _cache.DeleteCacheForPrefix(prefix);
        }

        foreach (var kind in new[] { TargetKind.Runs, TargetKind.Workflows, TargetKind.StaleCheck })
        {
            _pending404.Remove((repoId, kind));
            ClearProblem(repoId, kind);
        }

        if (!IsMonitored(repoId))
        {
            return;
        }

        _scheduler.ScheduleNow(new PollTarget(TargetKind.Workflows, repoId), RequestPriority.Maintenance);
        if (StateOf(repoId).WorkflowsTotal is not null)
        {
            _scheduler.ScheduleNow(new PollTarget(TargetKind.Runs, repoId), RequestPriority.PushedRepo);
        }

        if (!_prStopped.Contains(repoId))
        {
            _scheduler.ScheduleNow(new PollTarget(TargetKind.PullRequests, repoId), RequestPriority.PullRequests);
        }
    }

    /// <summary>連續兩輪不見：移出監看、從主清單移除並刪除本地 run 紀錄（spec §5.4）。靜音與強制納入保留在 DB。</summary>
    private void RemoveRepo(long repoId)
    {
        _cache.DeleteRepo(repoId);
        ForgetPushPermission(repoId);
        ForgetRepoPostActions(repoId);
        _scheduler.RemoveRepo(repoId);
        _repos.Remove(repoId);
        _states.Remove(repoId);
        _classes.Remove(repoId);
        _data.Remove(repoId);
        _latestPage.Remove(repoId);
        _prStopped.Remove(repoId);
        _firstFetched.Remove(repoId);
        _pending404.RemoveWhere(pending => pending.RepoId == repoId);
        foreach (var key in _problems.Keys.Where(key => key.RepoId == repoId).ToList())
        {
            _problems.Remove(key);
        }

        foreach (var key in _samples.Keys.Where(key => key.RepoId == repoId).ToList())
        {
            _samples.Remove(key);
        }

        _unknownWorkflowsRequested.RemoveWhere(key => key.RepoId == repoId);
        foreach (var target in _lastSent.Keys.Where(target => target.RepoId == repoId).ToList())
        {
            _lastSent.Remove(target);
        }

        foreach (var target in _lastDelay.Keys.Where(target => target.RepoId == repoId).ToList())
        {
            _lastDelay.Remove(target);
        }

        _errorBackoff.RemoveWhere(target => target.RepoId == repoId);
        _unanswered.RemoveWhere(target => target.RepoId == repoId);
        _lastRepoSuccess.Remove(repoId);
        foreach (var key in _seededAt.Keys.Where(key => key.RepoId == repoId).ToList())
        {
            _seededAt.Remove(key);
        }

        ForgetPullRequestSeeds(repoId);
        _pendingCompletions.RemoveAll(pending => pending.Observation.Run.RepoId == repoId);
        _pushTriggeredWorkflows.Remove(repoId);
        _monitoredSince.Remove(repoId);
    }

    private void ForgetPullRequestSeeds(long repoId)
    {
        foreach (var key in _prSeedScheduledAt.Keys.Where(key => key.RepoId == repoId).ToList())
        {
            _prSeedScheduledAt.Remove(key);
        }
    }

    // ── E3 ────────────────────────────────────────────────────────────

    /// <summary>
    /// workflow 清單（E3），也是 stale repo 的首次檢查（Q7）：寫入後重新分類；stale 檢查有 workflow 時再用 E4 看本地有沒有 90 天內的 run。
    /// </summary>
    private async Task<int> ProcessWorkflowsAsync(PollTarget target, CancellationToken ct)
    {
        if (!TryGetRepo(target, out var repo))
        {
            return 0;
        }

        var (result, requests) = await CallAsync(c => _api.GetWorkflowsAsync(repo, c), repo.Id, ct).ConfigureAwait(false);
        if (result.MovedTo is not null)
        {
            RequestRepoList();
        }

        switch (result.Outcome)
        {
            case ApiOutcome.Ok:
                _cache.ReplaceWorkflows(repo.Id, result.Value!, result.CacheEntry);
                Invalidate(repo.Id);
                break;

            case ApiOutcome.NotModified:
                break;

            case ApiOutcome.NotFound:
                // §5.4 只適用於 E4 與 E3：不立刻判定刪除，先重抓清單確認。
                _pending404.Add((repo.Id, target.Kind));
                RequestRepoList();
                return requests;

            default:
                HandleFailure(target, OutcomeOf(result));
                return requests;
        }

        var firstCheck = StateOf(repo.Id).WorkflowsTotal is null;
        var workflows = Data(repo.Id).Workflows;
        SaveState(StateOf(repo.Id) with
        {
            WorkflowsTotal = workflows.Count,
            WorkflowsActive = workflows.Count(workflow => workflow.IsActive),
            WorkflowsCheckedAt = _time.GetUtcNow(),
        });
        MarkRepoSuccess(repo.Id);
        ClearProblem(repo.Id, target.Kind);

        // pushed_at 改變而立即重查的這一次讓 repo 轉成監看：界線是那次的 pushed_at（spec §9.3）。
        var pushTriggered = target.Kind == TargetKind.Workflows && _pushTriggeredWorkflows.Remove(repo.Id);
        Reclassify(repo.Id, pushTriggered ? ClassifyCause.Push : ClassifyCause.Discovery);
        if (target.Kind == TargetKind.StaleCheck)
        {
            // 有 workflow 才 E4（同主要輪詢的網址）、只做一次：上面的重新分類已經由 EnsureRepoTargets 排好。
            return requests;
        }

        if (IsWorkflowCandidate(repo.Id))
        {
            Reschedule(target, WithPollInterval(NormalInterval(target), result.PollInterval));
        }

        if (IsMonitored(repo.Id))
        {
            EnsureSeeds(repo);
            if (firstCheck && _refreshMode == RefreshMode.Paused)
            {
                RefreshAfterFirstWorkflowCheck(repo);
            }
        }

        return requests;
    }

    /// <summary>
    /// 暫停模式下第一次查到 workflow 清單、監看中的 repo：接著送一次 E4 與 E7，這次刷新才拿得到 runs 與 PR（spec §6.2、§6.6）。
    /// 暫停時不排任何週期性輪詢，這次 E3 只會來自全量刷新（F5 等）或你的操作，是排 E4 之前的前置檢查（有 workflow 才 E4，spec §5.3）；
    /// E4／E7 回來之後照暫停模式的規則不再排。等錯誤重試的目標不提前。
    /// </summary>
    private void RefreshAfterFirstWorkflowCheck(RepoInfo repo)
    {
        if (!IsHeld(repo.Id, TargetKind.Runs))
        {
            _scheduler.ScheduleNow(new PollTarget(TargetKind.Runs, repo.Id), RunsPriority(repo));
        }

        if (!_prStopped.Contains(repo.Id) && !IsHeld(repo.Id, TargetKind.PullRequests))
        {
            _scheduler.ScheduleNow(new PollTarget(TargetKind.PullRequests, repo.Id), RequestPriority.PullRequests);
        }
    }

    // ── E4、E6 ────────────────────────────────────────────────────────

    /// <summary>主要輪詢（E4 第 1 頁）。</summary>
    private async Task<int> ProcessRunsAsync(PollTarget target, CancellationToken ct)
    {
        if (!TryGetRepo(target, out var repo))
        {
            return 0;
        }

        var (result, requests) = await CallAsync(c => _api.GetRunsAsync(repo, c), repo.Id, ct).ConfigureAwait(false);
        if (result.MovedTo is not null)
        {
            // 301：已照 Location 重送這一次；排入完整重抓，用 id 對照新名稱（spec §4.1、§5.4）。
            RequestRepoList();
        }

        switch (result.Outcome)
        {
            case ApiOutcome.Ok:
                ApplyRuns(repo, result.Value!, isLatestPage: true, result.CacheEntry);
                break;

            case ApiOutcome.NotModified:
                if (result.CacheEntry?.ItemIds is { } ids)
                {
                    _latestPage[repo.Id] = ids;
                }

                break;

            case ApiOutcome.NotFound:
                _pending404.Add((repo.Id, TargetKind.Runs));
                RequestRepoList();
                return requests;

            default:
                HandleFailure(target, OutcomeOf(result));
                return requests;
        }

        MarkRepoSuccess(repo.Id);
        ClearProblem(repo.Id, TargetKind.Runs);
        _firstFetched.Add(repo.Id);

        // stale repo 的首次檢查也走這裡：有 90 天內的 run 就轉成監看（Q7）。
        Reclassify(repo.Id, ClassifyCause.Discovery);
        if (!IsMonitored(repo.Id))
        {
            return requests;
        }

        Reschedule(target, RunsInterval(repo, result.PollInterval));
        EnsureRunDetails(repo);
        EnsureSeeds(repo);
        return requests;
    }

    /// <summary>
    /// 單筆 run（E6）：完成或回 404 就停止；404 只標成已不存在，不觸發 §5.4。有操作後追蹤時（第二階段 spec §6.2）
    /// 每次回應（200、304）先判斷結束條件、再累計回應與判斷放棄，沒結束就照追蹤的間隔重排，已完成的 run 也一樣。
    /// </summary>
    private async Task<int> ProcessRunDetailAsync(PollTarget target, CancellationToken ct)
    {
        if (!TryGetRepo(target, out var repo) || target.ItemId is not { } runId)
        {
            return 0;
        }

        var (result, requests) = await CallAsync(c => _api.GetRunAsync(repo, runId, c), repo.Id, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ApiOutcome.Ok:
                ApplyRuns(repo, [result.Value!], isLatestPage: false, result.CacheEntry);
                MarkRepoSuccess(repo.Id);
                ClearProblem(repo.Id, TargetKind.RunDetail);
                break;

            case ApiOutcome.NotModified:
                MarkRepoSuccess(repo.Id);
                ClearProblem(repo.Id, TargetKind.RunDetail);

                // 304：本地的資料就是伺服器目前的樣子。重新啟動後讀回的待確認重跑，本地可能早已是新的 attempt。
                ObservePostActions(Data(repo.Id).Runs.Where(run => run.Id == runId));
                break;

            case ApiOutcome.NotFound:
                // 過了保留期或被刪掉：標成已不存在並移出追蹤，保留本地紀錄（spec §4.2 E6）。
                // 待確認的操作先清除（第二階段 spec §6.2 第 5 點），取消標記時才不會照它的間隔再排一次 E6。
                ForgetPostActions(runId);
                _cache.MarkRunGone(runId);
                if (_watched.Contains(runId))
                {
                    SetWatched(runId, watched: false);
                }

                Invalidate(repo.Id);
                return requests;

            default:
                HandleFailure(target, OutcomeOf(result));
                return requests;
        }

        OnRunDetailAnswered(runId);

        // 已完成（或回到第 1 頁）而且沒有待確認的操作：不再是 E6 的對象，停止。
        if (IsMonitored(repo.Id) && RunDetailTargets(repo).Contains(runId))
        {
            Reschedule(target, WithPollInterval(RunDetailInterval(repo, runId), result.PollInterval));
        }

        return requests;
    }

    // ── E7 ────────────────────────────────────────────────────────────

    /// <summary>開啟中 PR（E7）：403／404 只停這個 repo 的 PR 追蹤，E4 照常（spec §4.2 E7）。</summary>
    private async Task<int> ProcessPullRequestsAsync(PollTarget target, CancellationToken ct)
    {
        if (!TryGetRepo(target, out var repo))
        {
            return 0;
        }

        var (result, requests) = await CallAsync(c => _api.GetOpenPullRequestsAsync(repo, c), repo.Id, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ApiOutcome.Ok:
                _cache.ReplacePullRequests(repo.Id, result.Value!, result.CacheEntry);
                Invalidate(repo.Id);
                break;

            case ApiOutcome.NotModified:
                break;

            case ApiOutcome.Forbidden:
            case ApiOutcome.NotFound:
                _prStopped.Add(repo.Id);
                SetProblem(repo.Id, TargetKind.PullRequests, EngineText.PullRequestsUnavailable, Tuning.PermissionRetryInterval);
                _scheduler.RemoveWhere(t => t.RepoId == repo.Id && t.Kind == TargetKind.PullRequestSeed);
                ForgetPullRequestSeeds(repo.Id);

                // PR 鍵變成「未知」：等這次 E7 的完成觀察照沒有 PR 清單判斷。
                ReleaseAwaitingPullRequests(repo.Id);
                Reschedule(target, IntervalPolicy.Periodic(Tuning.PermissionRetryInterval, _refreshMode, Stretch(), _locked, followsRefreshMode: false));
                return requests;

            default:
                HandleFailure(target, OutcomeOf(result));
                return requests;
        }

        _prStopped.Remove(repo.Id);
        ClearProblem(repo.Id, TargetKind.PullRequests);
        MarkRepoSuccess(repo.Id);
        ReleaseAwaitingPullRequests(repo.Id);

        if (IsMonitored(repo.Id))
        {
            EnsurePullRequestSeeds(repo);
            Reschedule(target, WithPollInterval(NormalInterval(target), result.PollInterval));
            EnsureSeeds(repo);
        }

        return requests;
    }

    /// <summary>
    /// E7 回 200 或 304 之後（本地的 PR 清單就是目前的清單）挑出要用 head_sha 補抓的 PR（spec §7.2），每個 head sha 只抓一次。
    /// </summary>
    /// <remarks>
    /// 304 也要挑：排入的補抓被暫停、靜音或重新啟動移出排程之後，E7 帶 ETag 只會回 304，只看 200 就再也排不回去。
    /// 已經抓過的 sha 由 pr_seed 表擋下（重啟後也一樣）；等權限錯誤重試的補抓不提前。
    /// </remarks>
    private void EnsurePullRequestSeeds(RepoInfo repo)
    {
        foreach (var pr in TrackedRunSelector.PullRequestSeedCandidates(TrackingInput(repo, Data(repo.Id))))
        {
            var seed = new PollTarget(TargetKind.PullRequestSeed, repo.Id, Sha: pr.HeadSha);
            if (_scheduler.Contains(seed) || _current == seed || _cache.PrSeedChecked(repo.Id, pr.HeadSha))
            {
                continue;
            }

            if (IsHeld(repo.Id, TargetKind.PullRequestSeed) && _lastSent.ContainsKey(seed))
            {
                EnsureRetry(seed);
                continue;
            }

            // 5xx 等錯誤退避中的補抓：照退避排回去，不立即重送（spec §6.4）。
            if (RestoreBackoff(seed))
            {
                continue;
            }

            _scheduler.ScheduleNow(seed, RequestPriority.PullRequests);
            _prSeedScheduledAt.TryAdd((repo.Id, pr.HeadSha), _time.GetUtcNow());
        }
    }

    // ── 補抓 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 預設分支補抓（E5）：只留 head_repository.id == repo.id 且不是 pull_request 開頭事件的 run（spec §4.2 E5）；
    /// 查不到決定性結論時 SeedRecheck 後再查。補抓回來的結果照 §9.3 的時間界線判定：界線之前完成的只記 suppressed（spec §7.2）。
    /// </summary>
    private async Task<int> ProcessDefaultBranchSeedAsync(PollTarget target, CancellationToken ct)
    {
        if (!TryGetRepo(target, out var repo) || target.ItemId is not { } workflowId)
        {
            return 0;
        }

        var (result, requests) = await CallAsync(c => _api.GetDefaultBranchRunsAsync(repo, workflowId, c), repo.Id, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ApiOutcome.Ok:
                var runs = result.Value!
                    .Where(run => run.HeadRepoId == repo.Id && !TrackedRunSelector.IsPullRequestEvent(run.Event))
                    .ToList();
                ApplyRuns(repo, runs, isLatestPage: false, result.CacheEntry);
                break;

            case ApiOutcome.NotModified:
                break;

            default:
                HandleFailure(target, OutcomeOf(result));
                return requests;
        }

        MarkRepoSuccess(repo.Id);
        ClearProblem(repo.Id, TargetKind.DefaultBranchSeed);
        _seededAt[(repo.Id, workflowId)] = _time.GetTimestamp();
        if (IsMonitored(repo.Id) && DefaultBranchSeedCandidates(repo).Contains(workflowId))
        {
            Reschedule(target, WithPollInterval(NormalInterval(target), result.PollInterval));
        }

        return requests;
    }

    /// <summary>
    /// PR 分支補抓（E4 加 head_sha）：每個 head sha 只抓一次。本地原本沒有的失敗 run（pull_request 事件以外），時間界線不早於補抓排入的時間：
    /// 在那之前就完成的，是因為你開了 PR 才變成追蹤中，不發通知（spec §9.1「追蹤範圍本身的變動不會發通知」、handoff Task 12 Q12）；
    /// 排入之後才完成的照常判定。pull_request 事件的 run、成功（N2 不限追蹤範圍）與本地已經記過的 run 照 §9.3 的界線（spec §7.2）。
    /// </summary>
    private async Task<int> ProcessPullRequestSeedAsync(PollTarget target, CancellationToken ct)
    {
        if (!TryGetRepo(target, out var repo) || string.IsNullOrWhiteSpace(target.Sha))
        {
            return 0;
        }

        var sha = target.Sha;
        var (result, requests) = await CallAsync(c => _api.GetRunsForShaAsync(repo, sha, c), repo.Id, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ApiOutcome.Ok:
                var scheduledAt = _prSeedScheduledAt.TryGetValue((repo.Id, sha), out var at) ? at : _time.GetUtcNow();
                ApplyRuns(repo, result.Value!, isLatestPage: false, result.CacheEntry, newRunBaseline: scheduledAt);
                break;

            case ApiOutcome.NotModified:
                break;

            default:
                HandleFailure(target, OutcomeOf(result));
                return requests;
        }

        _cache.MarkPrSeedChecked(repo.Id, sha, _time.GetUtcNow());
        _prSeedScheduledAt.Remove((repo.Id, sha));
        MarkRepoSuccess(repo.Id);
        ClearProblem(repo.Id, TargetKind.PullRequestSeed);
        return requests;
    }

    // ── run 的寫入與完成觀察 ──────────────────────────────────────────

    /// <summary>
    /// 寫入一批 run（資料與快取項同一個交易），再做完成觀察（spec §9.1）：通知、ETA 樣本、取消「完成時通知我」。
    /// </summary>
    /// <param name="newRunBaseline">
    /// PR 分支補抓：本地原本沒有的失敗 run（pull_request 事件以外），時間界線不早於這個時間（補抓排入的時間）。
    /// 其他 run 與其他來源（含 E5）照 §9.3 的界線。
    /// </param>
    /// <remarks>
    /// 完成觀察在寫入之前判斷、寫入之後才規劃通知：寫入之後 attempt 已經是完成，之後的回應（304 或 200）都看不出這次完成，
    /// 所以這批觀察先放進 <c>_pendingCompletions</c>，規劃到一半失敗時留著，下一次處理這個 repo 時再試，不會永久漏掉。
    /// </remarks>
    private void ApplyRuns(RepoInfo repo, IReadOnlyList<RunInfo> page, bool isLatestPage, HttpCacheEntry? cacheEntry,
        DateTimeOffset? newRunBaseline = null)
    {
        // 同一個 run 在一頁裡出現兩次（分頁期間資料變動）時，留 attempt 較新、再來是 updated_at 較晚的那一份。
        var runs = page
            .GroupBy(run => run.Id)
            .Select(group => group.OrderByDescending(run => run.RunAttempt).ThenByDescending(run => run.UpdatedAt).First())
            .ToList();
        var before = Data(repo.Id);
        var knownIds = before.Runs.Select(run => run.Id).ToHashSet();
        var previous = runs.ToDictionary(run => run.Id, run => LatestAttempt(before, run.Id));
        foreach (var run in runs)
        {
            LogUnknownState(run);
        }

        var completions = runs.Where(run => NotificationPlanner.IsNewCompletion(previous[run.Id], run)).ToList();
        _cache.SaveRunsPage(repo.Id, runs, isLatestPage, cacheEntry);
        if (isLatestPage)
        {
            _latestPage[repo.Id] = runs.Select(run => run.Id).ToList();
        }

        Invalidate(repo.Id);

        // 操作後追蹤的結束條件（第二階段 spec §6.2、§8.1）：E4、E5、E6 哪一個先看到都算。
        var notifyOnCompletion = ObservePostActions(runs);
        var data = Data(repo.Id);
        var input = TrackingInput(repo, data);

        // E4 出現本地沒有的 workflow_id：先當成 active，並立即排 E3（spec §7.2）。同一個 id 只觸發一次，
        // E3 回來仍沒有它（例如 workflow 檔已刪除）時不會每輪都重抓。
        var unknown = TrackedRunSelector.UnknownWorkflowIds(input).Where(id => _unknownWorkflowsRequested.Add((repo.Id, id))).ToList();
        if (unknown.Count > 0 && IsMonitored(repo.Id) && StateOf(repo.Id).WorkflowsTotal is not null && !IsHeld(repo.Id, TargetKind.Workflows))
        {
            _scheduler.ScheduleNow(new PollTarget(TargetKind.Workflows, repo.Id), RequestPriority.Maintenance);
        }

        // 出現新的 pull_request* 事件 run：立即重查 PR 清單（spec §6.1）。
        if (runs.Any(run => !knownIds.Contains(run.Id) && TrackedRunSelector.IsPullRequestEvent(run.Event)))
        {
            RequestPullRequestList(repo.Id);
        }

        if (completions.Count == 0)
        {
            return;
        }

        // 追蹤與標記在完成觀察的那一刻判斷（spec §9.1），界線也在這時候決定。
        var keys = TrackedRunSelector.Evaluate(input, _time.GetUtcNow());
        var baseline = Baseline(repo.Id);
        foreach (var run in completions)
        {
            var state = RunStateMapper.Map(run, Language);
            var tracked = IsTrackedRun(keys, run.Id);

            // 重跑在一次查詢間隔內就跑完：這次完成觀察直接當成「完成時通知我」，照 N3 通知（第二階段 spec §8.1 第 3 步）。
            var observation = new CompletionObservation(
                run with { WorkflowName = WorkflowDisplayName(run, data.Workflows) },
                state,
                repo.FullName,
                tracked,
                _watched.Contains(run.Id) || notifyOnCompletion.Contains(run.Id));

            // 補抓排入時間的下限只用在「因為開了 PR 才變成追蹤中」的失敗（N1，spec §9.1）：pull_request 事件的 run
            // 是 PR 開了之後才產生的，不是追蹤範圍的變動；N2 不限追蹤範圍，兩者都只照 §9.3 的界線（spec §7.2）。
            var scopeChange = previous[run.Id] is null && state.Category == RunCategory.Failure
                && !TrackedRunSelector.IsPullRequestEvent(run.Event);
            var runBaseline = scopeChange && newRunBaseline is { } floor && floor > baseline ? floor : baseline;
            var awaiting = AwaitsPullRequestList(repo, run, state, tracked, data);
            _pendingCompletions.Add(new PendingCompletion(observation, runBaseline, awaiting, RecheckTracking: awaiting));
            if (awaiting)
            {
                RequestPullRequestList(repo.Id);
            }
        }

        PlanPendingCompletions(repo.Id);
    }

    private static bool IsTrackedRun(IReadOnlyList<KeyEvaluation> keys, long runId) =>
        keys.Any(key => key.Latest.Id == runId || key.LastDecisiveGroupRunIds.Contains(runId));

    /// <summary>立即重查這個 repo 的 PR 清單（spec §6.1、Q25）；PR 追蹤停用、不在監看或正在等錯誤重試時不排。</summary>
    private void RequestPullRequestList(long repoId)
    {
        var target = new PollTarget(TargetKind.PullRequests, repoId);
        if (IsMonitored(repoId) && !_prStopped.Contains(repoId) && !IsHeld(repoId, TargetKind.PullRequests) && _current != target)
        {
            _scheduler.ScheduleNow(target, RequestPriority.PullRequests);
        }
    }

    /// <summary>
    /// 失敗的 pull_request 事件 run 第一次被看到時，本地的 PR 清單還沒有它的分支：這筆 run 是因為 PR 才產生的，
    /// 完成時 PR 一定已經開著，只是本地清單還沒更新（spec §6.1 這時會立即重查）。先等這次 E7 回來再判斷是否追蹤中，
    /// 否則 N1 會永久漏發（spec §9.1：之後 attempt 已經是完成，不會再被觀察到）。
    /// </summary>
    private bool AwaitsPullRequestList(RepoInfo repo, RunInfo run, RunState state, bool tracked, RepoData data) =>
        !tracked
        && state.Category == RunCategory.Failure
        && TrackedRunSelector.IsPullRequestEvent(run.Event)
        && run.HeadBranch is not null
        && IsMonitored(repo.Id)
        && !_prStopped.Contains(repo.Id)
        && !data.PullRequests.Any(pr => pr.HeadRepoId == run.HeadRepoId && string.Equals(pr.HeadRef, run.HeadBranch, StringComparison.Ordinal));

    /// <summary>這個 repo 的 E7 有了確定的結果（200、304、403／404）：等它的完成觀察可以規劃了。</summary>
    private void ReleaseAwaitingPullRequests(long repoId)
    {
        for (var index = 0; index < _pendingCompletions.Count; index++)
        {
            var pending = _pendingCompletions[index];
            if (pending.AwaitingPullRequests && pending.Observation.Run.RepoId == repoId)
            {
                _pendingCompletions[index] = pending with { AwaitingPullRequests = false };
            }
        }
    }

    /// <summary>
    /// 做完這個 repo 排著的完成觀察：ETA 樣本、通知（spec §9.1～§9.3）、取消「完成時通知我」。
    /// 一筆一筆做、做完才移除：某一筆失敗時前面的不受影響，失敗的那筆留到下一次處理這個 repo 時再試。
    /// 重試不會重複通知：已經寫進 notified 的由去重鍵擋下（spec §9.2）。
    /// </summary>
    /// <remarks>
    /// 等 E7 的觀察（<see cref="AwaitsPullRequestList"/>）留到 E7 有結果才做；E7 已經不在排程裡（暫停移除、不再監看、PR 追蹤停用）時
    /// 不再等，照現有資料判斷。等過的觀察用當下的追蹤鍵重新判斷是否追蹤中，時間界線仍是觀察當下決定的那一個。
    /// </remarks>
    private void PlanPendingCompletions(long repoId)
    {
        var pullRequestsComing = PullRequestListComing(repoId);
        for (var index = 0; index < _pendingCompletions.Count;)
        {
            var pending = _pendingCompletions[index];
            if (pending.Observation.Run.RepoId != repoId || (pending.AwaitingPullRequests && pullRequestsComing))
            {
                index++;
                continue;
            }

            PlanCompletion(pending.RecheckTracking ? WithCurrentTracking(pending) : pending);
            _pendingCompletions.RemoveAt(index);
        }
    }

    /// <summary>這個 repo 的 E7 還會送（已排入或正在處理）。</summary>
    private bool PullRequestListComing(long repoId)
    {
        var target = new PollTarget(TargetKind.PullRequests, repoId);
        return IsMonitored(repoId) && !_prStopped.Contains(repoId) && (_current == target || _scheduler.Contains(target));
    }

    private PendingCompletion WithCurrentTracking(PendingCompletion pending)
    {
        var observation = pending.Observation;
        if (!_repos.TryGetValue(observation.Run.RepoId, out var repo))
        {
            return pending;
        }

        var tracked = IsTrackedRun(EvaluateKeys(repo), observation.Run.Id);
        return pending with { Observation = observation with { IsTracked = tracked } };
    }

    private void PlanCompletion(PendingCompletion pending)
    {
        var observation = pending.Observation;
        var run = observation.Run;

        // 重跑過的成功不存樣本（第二階段 spec §8.2、D13）：只重跑失敗的 job 時耗時通常比完整執行短。
        if (observation.State.Category == RunCategory.Success && run.RunAttempt == FirstAttempt
            && DurationEstimator.CompletedDuration(run) is { } duration)
        {
            _cache.AddDurationSample(run.Id, run.RunAttempt, run.RepoId, run.WorkflowId, duration, run.UpdatedAt);
            _samples.Remove((run.RepoId, run.WorkflowId));
        }

        var settings = _settings;
        var options = new NotificationOptions(settings.NotifyTrackedFailure, settings.NotifyMyRunSuccess, settings.NotifyWatchedRunDone);
        var planned = _planner.Plan([observation], _account?.UserId, options, _ => pending.Baseline, _time.GetUtcNow());
        if (planned.Count > 0)
        {
            _toasts.Add(planned);
            _toastPendingSince ??= _time.GetTimestamp();
        }

        // 「完成時通知我」：完成後自動取消標記（spec §9.1 N3）。
        if (observation.IsWatched)
        {
            SetWatched(run.Id, watched: false);
        }
    }

    private static RunAttemptRecord? LatestAttempt(RepoData data, long runId) =>
        data.Attempts.TryGetValue(runId, out var attempts) && attempts.Count > 0 ? attempts.MaxBy(attempt => attempt.Attempt) : null;

    /// <summary>沒見過的 status／conclusion 寫一次 error.log（每個值只記一次，spec §7.1）。</summary>
    private void LogUnknownState(RunInfo run)
    {
        var state = RunStateMapper.Map(run);
        if (state.Display == DisplayState.Unknown && state.RawValue is { } raw && _unknownValuesLogged.Add(raw))
        {
            _deps.ErrorLog.WriteMessage("不認得的 run 狀態", $"status={run.Status}, conclusion={run.Conclusion}");
        }
    }

    /// <summary>workflow 名稱的退回規則（spec §10.5）：run.name → E3 的名稱 → path 的檔名。</summary>
    private static string WorkflowDisplayName(RunInfo run, IReadOnlyList<WorkflowInfo> workflows)
    {
        if (!string.IsNullOrWhiteSpace(run.WorkflowName))
        {
            return run.WorkflowName;
        }

        var fromList = workflows.FirstOrDefault(workflow => workflow.Id == run.WorkflowId)?.Name;
        return !string.IsNullOrWhiteSpace(fromList) ? fromList : Path.GetFileName(run.Path);
    }

    private long FutureTimestamp(TimeSpan delay) =>
        _time.GetTimestamp() + (long)(delay.TotalSeconds * _time.TimestampFrequency);
}
