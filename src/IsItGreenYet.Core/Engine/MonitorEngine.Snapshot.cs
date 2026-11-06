using System.Collections.Frozen;
using System.Globalization;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.Display;
using IsItGreenYet.Core.Filtering;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Engine;

/// <summary>組不可變的快照（spec §7.3、§7.4、§10.2、§10.10）。</summary>
public sealed partial class MonitorEngine
{
    /// <summary>上一次發布的橫幅：內容沒變時沿用同一個實例，App 可以用參考判斷「是不是同一則」（例如記住使用者關掉的那一則）。</summary>
    private BannerInfo? _lastBanner;

    /// <summary>
    /// 一次性的提示（Deprecation、執行中重建資料庫、未預期例外）：發生時建立一次，之後的快照沿用同一個實例（換語言時才換成新的文字）。
    /// </summary>
    /// <param name="Window">發生時已經發布過幾次快照，比較哪一則比較新用。</param>
    /// <param name="Text">產生橫幅文字的方法（依語言）。</param>
    private sealed record Notice(BannerInfo Banner, long Window, Func<AppLanguage, string> Text);

    /// <returns>
    /// 快照，與只給寫入的再檢查用的 run 狀態查表（<see cref="_runStateLookup"/>）：鍵和 run_attempt 同快照的 Actions，
    /// 但用不算送出中、沒有共通條件的 context 組出，四個動作的 run 狀態類原因不會被共通代碼蓋掉（第二階段 spec §5.3 第 3.1 點）。
    /// </returns>
    private (MonitorSnapshot Snapshot, IReadOnlyDictionary<long, RunActions> RunStates) BuildSnapshot()
    {
        var now = _time.GetUtcNow();
        var language = Language;
        var rows = new List<RunRow>();
        var keys = new List<KeyEvaluation>();
        var stale = new HashSet<long>();
        var eta = new Dictionary<long, EtaInfo>();
        var durations = new Dictionary<long, TimeSpan>();
        var pullRequestNumbers = new Dictionary<long, IReadOnlyList<int>>();
        var actions = new Dictionary<long, RunActions>();
        var runStates = new Dictionary<long, RunActions>();
        var monitored = 0;
        var loaded = 0;

        foreach (var repo in _repos.Values.OrderBy(repo => repo.Id))
        {
            // 被靜音、被略過或已移出的 repo，它的 run 不顯示也不計數（spec §10.10）。
            if (!IsMonitored(repo.Id))
            {
                continue;
            }

            monitored++;
            if (_firstFetched.Contains(repo.Id))
            {
                loaded++;
            }

            if (IsStale(repo.Id))
            {
                stale.Add(repo.Id);
            }

            var data = Data(repo.Id);
            var repoKeys = TrackedRunSelector.Evaluate(TrackingInput(repo, data), now);
            keys.AddRange(repoKeys);

            // 主清單：第 1 頁的 run、各鍵的 latest 與 lastDecisive 組、被標記的、還在 E6 追蹤中的（四者聯集）。
            // 「E6 追蹤中」看的是 E6 的對象（本地未完成或等待、不在第 1 頁，同 EnsureRunDetails），不看排程：
            // 暫停自動更新時 E6 不排，清單內容仍不變（spec §6.2 暫停只停止週期性輪詢）。
            var page = _latestPage.GetValueOrDefault(repo.Id) ?? [];
            var shown = new HashSet<long>(page);
            var trackedFailureGroups = new HashSet<long>();
            foreach (var key in repoKeys)
            {
                shown.Add(key.Latest.Id);
                shown.UnionWith(key.LastDecisiveGroupRunIds);
                if (key.Failing)
                {
                    trackedFailureGroups.UnionWith(key.LastDecisiveGroupRunIds);
                }
            }

            shown.UnionWith(_watched);
            shown.UnionWith(RunDetailCandidates(data, page));

            var openPullRequests = _prStopped.Contains(repo.Id) ? null : data.PullRequests;

            // 寫入的共通條件只看 repo 與全域狀態（第二階段 spec §7.2），每個 repo 算一次。
            var globalBlock = GlobalBlock(repo.Id);
            foreach (var run in data.Runs.Where(run => shown.Contains(run.Id)))
            {
                var state = RunStateMapper.Map(run, language);
                var trackedFailure = trackedFailureGroups.Contains(run.Id) && DecidedFailure(run, data);
                rows.Add(new RunRow(run, repo, state, WorkflowDisplayName(run, data.Workflows), trackedFailure));

                var workflow = data.Workflows.FirstOrDefault(candidate => candidate.Id == run.WorkflowId);
                var context = ActionContextFor(run.Id, globalBlock);
                actions[run.Id] = ActionRules.Evaluate(run, workflow, now, context);
                runStates[run.Id] = ActionRules.Evaluate(run, workflow, now, context with { GlobalBlock = ActionBlock.None, InFlight = false });

                if (DurationEstimator.Estimate(run, Samples(repo.Id, run.WorkflowId), now) is { } estimate)
                {
                    eta[run.Id] = estimate;
                }

                if (DurationEstimator.CompletedDuration(run) is { } duration)
                {
                    durations[run.Id] = duration;
                }

                var numbers = PrNumberResolver.Resolve(run, openPullRequests, run.PullRequestNumbers);
                if (numbers.Count > 0)
                {
                    pullRequestNumbers[run.Id] = numbers;
                }
            }
        }

        // 排序：開始時間（缺值用 created_at）由新到舊，同時間再依 run id 由大到小（spec §10.10）。
        var ordered = rows
            .OrderByDescending(row => row.Run.RunStartedAt ?? row.Run.CreatedAt)
            .ThenByDescending(row => row.Run.Id)
            .ToList();

        var issues = Issues();
        var snapshot = new MonitorSnapshot(
            ordered.AsReadOnly(),
            Summarize(keys, stale, issues),
            _lastSuccessUtc,
            _rateLimit,
            RepoRows(),
            CurrentBanner(),
            new LoadingProgress(loaded, monitored),
            _auth.Status,
            _auth.Source,
            TrayIssueLine(issues, stale.Count),
            _offline.IsOffline,
            _refreshMode,
            _watched.ToFrozenSet(),
            eta.ToFrozenDictionary(),
            durations.ToFrozenDictionary(),
            pullRequestNumbers.ToFrozenDictionary())
        {
            RepoListFetched = _repoListFetched,
            Actions = actions.ToFrozenDictionary(),
            Cancelling = CancellingRuns(),
            WritesEnabled = _settings.AllowWriteActions,
        };
        return (snapshot, runStates.ToFrozenDictionary());
    }

    /// <summary>
    /// 沿用 <paramref name="last"/> 的內容（run、計數、repo 清單與它抓完了沒有、每一列的可操作性與取消中），只換上目前的橫幅、角標、認證、離線、
    /// 自動更新模式與寫入功能的開關：這些只看記憶體，不讀 DB（改名重建失敗或重建後仍損毀、還在等重試時用）。
    /// 「抓完了沒有」不能換成記憶體裡的值：清單已經抓回來、沿用的 repo 清單還是空的時，畫面會當成抓回 0 筆，說沒有資料。
    /// </summary>
    private MonitorSnapshot CarryOver(MonitorSnapshot last)
    {
        var issues = Issues();
        return last with
        {
            Summary = last.Summary with { Issues = issues },
            Banner = CurrentBanner(),
            Auth = _auth.Status,
            TokenSource = _auth.Source,
            TrayIssueLine = TrayIssueLine(issues, last.Summary.StaleRepoCount),
            Offline = _offline.IsOffline,
            RefreshMode = _refreshMode,
            WritesEnabled = _settings.AllowWriteActions,
        };
    }

    /// <summary>
    /// 只用記憶體組的快照（不讀 DB）：改名重建失敗或重建後仍損毀、還在等重試，又還沒有上一份快照可以沿用時用。
    /// run、ETA、耗時、PR 編號、可操作性與取消中留空；repo 清單、計數、橫幅、角標、認證、離線、自動更新模式與寫入功能的開關照常（spec §15）。
    /// </summary>
    private MonitorSnapshot MemorySnapshot()
    {
        var monitored = _repos.Keys.Where(IsMonitored).ToList();
        var stale = monitored.Where(IsStale).ToHashSet();
        var issues = Issues();
        return new MonitorSnapshot(
            Array.Empty<RunRow>(),
            StatusAggregator.Aggregate([], stale, issues),
            _lastSuccessUtc,
            _rateLimit,
            RepoRows(),
            CurrentBanner(),
            new LoadingProgress(monitored.Count(_firstFetched.Contains), monitored.Count),
            _auth.Status,
            _auth.Source,
            TrayIssueLine(issues, stale.Count),
            _offline.IsOffline,
            _refreshMode,
            _watched.ToFrozenSet(),
            FrozenDictionary<long, EtaInfo>.Empty,
            FrozenDictionary<long, TimeSpan>.Empty,
            FrozenDictionary<long, IReadOnlyList<int>>.Empty)
        {
            RepoListFetched = _repoListFetched,
            WritesEnabled = _settings.AllowWriteActions,
        };
    }

    /// <summary>
    /// 彙總（spec §7.3）。從空的資料庫開始、第一次載入還沒完成時（<see cref="_firstLoadPending"/>），
    /// 看得到的全部成功不代表全部成功，總狀態是無資料（spec §6.6）。失敗與進行中照常：看得到的就是真的。
    /// </summary>
    private StatusSummary Summarize(IReadOnlyCollection<KeyEvaluation> keys, IReadOnlySet<long> stale, ConnectionIssue issues)
    {
        if (_firstLoadPending && FirstLoadDone())
        {
            _firstLoadPending = false;
        }

        var summary = StatusAggregator.Aggregate(keys, stale, issues);
        return summary.State == OverallState.Success && _firstLoadPending ? summary with { State = OverallState.NoData } : summary;
    }

    /// <summary>
    /// 第一次載入完成：清單抓完一輪、每個監看中的 repo 都查過一次，也沒有第一次都還沒送過的 repo 目標
    /// （補抓與 stale 首次檢查排在 E4 後面，失敗可能只有它們看得到）。
    /// 等錯誤重試的 repo 不等：有問題標示，否則權限不足的 repo 會讓總狀態一直是無資料。
    /// 一直查不到的最多等 StaleDataThreshold，從開始監看這個 repo 起算。清單一直抓不完就一直不算完成：清單確實不完整。
    /// </summary>
    private bool FirstLoadDone() =>
        _repoListFetched
        && !_repos.Keys.Any(id => IsMonitored(id) && !_firstFetched.Contains(id)
            && !IsHeld(id, TargetKind.Workflows) && !IsHeld(id, TargetKind.Runs)
            && _time.GetElapsedTime(MonitoringSince(id)) <= Tuning.StaleDataThreshold)
        && !_scheduler.Scheduled.Any(target => target.RepoId is not null && !_lastSent.ContainsKey(target) && !IsLaterWorkflowsRefresh(target));

    /// <summary>
    /// stale 首次檢查已經查過 E3（同一個網址，WorkflowsCheckedAt 有值）的 repo，排在之後的 workflow 清單是下一次定期重查（<see cref="EnsureRepoTargets"/>），
    /// 不是第一次都還沒送過：等它只是多灰一個 WorkflowsRefresh。有新的 push 等原因馬上要送的不算：新的 workflow 的失敗可能只有接著的補抓看得到。
    /// 鎖定會把馬上要送的目標往後延最多 LockedMinInterval（<see cref="ApplyLockedFloor"/>），所以離到期不超過這個時間的也不算；定期重查快到期時多等的也不超過這個時間。
    /// 這個 repo 有目標沒有拿到答案（<see cref="_unanswered"/>）時也不算：送過但回 5xx 或連線失敗的補抓，失敗可能只有它看得到，也沒有問題標示。最多等到這個定期重查送出。
    /// 只看 workflow 清單：其他目標第一次也可能排在未來（額度低時的 E6），到期時間在未來不代表送過。
    /// </summary>
    private bool IsLaterWorkflowsRefresh(PollTarget target) =>
        target is { Kind: TargetKind.Workflows, RepoId: { } repoId }
        && StateOf(repoId).WorkflowsCheckedAt is not null
        && _scheduler.TimeUntilDue(target) > Tuning.LockedMinInterval
        && !_unanswered.Any(other => other.RepoId == repoId);

    /// <summary>run 的決定性結論是失敗（由新到舊看 attempt，第一個成功或失敗，Q9）。</summary>
    private static bool DecidedFailure(RunInfo run, RepoData data)
    {
        var recorded = data.Attempts.GetValueOrDefault(run.Id) ?? [];
        var categories = recorded
            .Where(attempt => attempt.Attempt != run.RunAttempt)
            .Select(attempt => (attempt.Attempt, RunStateMapper.Map(attempt).Category))
            .Append((run.RunAttempt, RunStateMapper.Map(run).Category))
            .OrderByDescending(pair => pair.Item1);

        foreach (var (_, category) in categories)
        {
            if (category is RunCategory.Success or RunCategory.Failure)
            {
                return category == RunCategory.Failure;
            }
        }

        return false;
    }

    /// <summary>repo 管理頁：所有看得到的 repo（spec §11.1），依名稱排序。</summary>
    private IReadOnlyList<RepoStatusRow> RepoRows() =>
        _repos.Values
            .OrderBy(repo => repo.FullName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(repo => repo.Id)
            .Select(repo =>
            {
                var cls = _classes.GetValueOrDefault(repo.Id);
                return new RepoStatusRow(
                    repo,
                    cls?.Monitored ?? false,
                    cls?.Reason ?? SkipReason.None,
                    cls?.CanForceInclude ?? false,
                    OverrideOf(repo.Id),
                    StateOf(repo.Id).ProblemNote);
            })
            .ToList()
            .AsReadOnly();

    /// <summary>全域的連線問題（角標，spec §7.3）。</summary>
    private ConnectionIssue Issues()
    {
        var issues = ConnectionIssue.None;
        if (_auth.Status == AuthStatus.Unauthorized)
        {
            issues |= ConnectionIssue.Unauthorized;
        }

        if (_throttle.IsPaused)
        {
            issues |= ConnectionIssue.RateLimited;
        }

        if (_offline.IsOffline)
        {
            issues |= ConnectionIssue.Offline;
        }

        if (_serverErrors.IsActive)
        {
            issues |= ConnectionIssue.ServerErrors;
        }

        if (_versionError)
        {
            issues |= ConnectionIssue.VersionError;
        }

        return issues;
    }

    /// <summary>系統匣 tooltip 的原因行（spec §7.4、§12）：角標的原因與「N 個 repo 無法更新」；都沒有回 null。</summary>
    private string? TrayIssueLine(ConnectionIssue issues, int staleRepos)
    {
        var language = Language;
        var parts = new List<string>();
        if (issues.HasFlag(ConnectionIssue.Unauthorized))
        {
            parts.Add(EngineText.TrayUnauthorized(language));
        }

        if (issues.HasFlag(ConnectionIssue.RateLimited) && _throttle.PauseRemaining is { } pause)
        {
            parts.Add(EngineText.TrayRateLimited(LocalTime(_time.GetUtcNow() + pause), language));
        }

        if (issues.HasFlag(ConnectionIssue.Offline))
        {
            parts.Add(EngineText.TrayOffline(language));
        }

        if (issues.HasFlag(ConnectionIssue.ServerErrors))
        {
            parts.Add(EngineText.TrayServerErrors(language));
        }

        if (issues.HasFlag(ConnectionIssue.VersionError))
        {
            parts.Add(EngineText.TrayVersionError(language));
        }

        if (staleRepos > 0)
        {
            parts.Add(EngineText.StaleRepos(staleRepos, language));
        }

        return parts.Count == 0 ? null : string.Join(EngineText.Separator, parts);
    }

    /// <summary>依嚴重度只給最重要的一則（spec §10.2，順序同 BannerKind）。</summary>
    /// <remarks>
    /// 啟動時 DatabaseInitializer.Ensure 的復原結果與設定檔損毀的橫幅由 App 依啟動結果顯示：Engine 拿不到那兩個結果。
    /// 執行中才發現的資料庫損毀由 Engine 復原並提示。
    /// </remarks>
    private BannerInfo? CurrentBanner()
    {
        var banner = SelectBanner();

        // 每次快照重新組的橫幅（未登入、離線、5xx、版本錯誤、限流）內容沒變時沿用上一次的實例；
        // 一次性提示本來就是發生時建立的實例，照原樣發布：10 分鐘後再發生的例外是新的一則，不能因為文字相同就沿用舊的（spec §10.2）。
        if (banner is not null && banner == _lastBanner && !IsNotice(banner.Kind))
        {
            return _lastBanner;
        }

        _lastBanner = banner;
        return banner;
    }

    private static bool IsNotice(BannerKind kind) =>
        kind is BannerKind.Deprecated or BannerKind.DatabaseRecovered or BannerKind.UnexpectedError;

    /// <summary>記下一則新的一次性提示：新的 <see cref="BannerInfo.NoticeId"/>，文字用目前設定的語言，換語言時用 <paramref name="text"/> 重建。</summary>
    /// <remarks>
    /// 文字在鎖裡才依語言組（同 <see cref="PauseAll"/>）：使用者操作的工作呼叫 <see cref="ReportUnexpected"/> 時，
    /// Engine 執行緒可能正在換語言。在鎖外先讀語言的話，可能讀到舊的語言、等 <see cref="ApplyLanguage"/> 在鎖裡重建完才存進去，
    /// 這一則就一直是舊的語言（直到下一次換語言）。在鎖裡讀時，不是在重建之前存進去（會被重建），就是在重建之後讀到新的語言。
    /// </remarks>
    private void SetNotice(ref Notice? notice, BannerKind kind, Func<AppLanguage, string> text)
    {
        lock (_bannerGate)
        {
            var banner = new BannerInfo(kind, text(Language), Until: null) { NoticeId = BannerInfo.NextNoticeId() };
            notice = new Notice(banner, _publishWindow, text);
        }
    }

    private BannerInfo? SelectBanner()
    {
        var language = Language;
        if (_auth.Status == AuthStatus.Unauthorized)
        {
            var text = _auth.Source == TokenSource.GhEnvironment
                ? EngineText.UnauthorizedFromEnvironment(language)
                : EngineText.Unauthorized(language);
            return new BannerInfo(BannerKind.Unauthorized, text, Until: null);
        }

        lock (_bannerGate)
        {
            if (_throttle.IsPaused && _pauseBanner is { } pause)
            {
                return pause;
            }
        }

        if (_offline.IsOffline)
        {
            return new BannerInfo(BannerKind.Offline, EngineText.Offline(LastUpdatedText(EngineText.LongTimeFormat), language), Until: null);
        }

        if (_serverErrors.IsActive)
        {
            return new BannerInfo(BannerKind.ServerErrors, EngineText.ServerErrors(LastUpdatedText(EngineText.ShortTimeFormat), language), Until: null);
        }

        if (_versionError)
        {
            return new BannerInfo(BannerKind.VersionError, EngineText.VersionError(language), Until: null);
        }

        lock (_bannerGate)
        {
            // 一次性提示都可以關閉（spec §10.2），但 Engine 不知道哪一則被關掉了：較新的優先，
            // 免得較早的一則（例如 Deprecation）永遠蓋住之後的提示；同一次快照之前一起發生的才依嚴重度。
            return new[] { _deprecatedNotice, _databaseNotice, _unexpectedNotice }
                .OfType<Notice>()
                .OrderByDescending(notice => notice.Window)
                .ThenBy(notice => notice.Banner.Kind)
                .FirstOrDefault()?.Banner;
        }
    }

    private string? LastUpdatedText(string format) =>
        _lastSuccessUtc is { } utc
            ? TimeZoneInfo.ConvertTime(utc, _time.LocalTimeZone).ToString(format, CultureInfo.InvariantCulture)
            : null;
}
