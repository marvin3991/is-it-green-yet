using System.Globalization;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Engine;

/// <summary>步驟專用的狀態與存取 DB 的小工具。</summary>
public sealed partial class MonitorEngine
{
    /// <summary>探測 B（協作 repo）用 ItemId 和探測 A 區分。</summary>
    private const long CollaboratorListItem = 1;

    private static readonly PollTarget ProbeATarget = new(TargetKind.Probe);
    private static readonly PollTarget ProbeBTarget = new(TargetKind.Probe, ItemId: CollaboratorListItem);
    private static readonly PollTarget RepoListTarget = new(TargetKind.RepoList);
    private static readonly PollTarget OfflineProbeTarget = new(TargetKind.OfflineProbe);

    // ── repo 與資料（步驟專用）──────────────────────────────────────────
    private readonly Dictionary<long, RepoInfo> _repos = [];
    private readonly Dictionary<long, RepoState> _states = [];
    private readonly Dictionary<long, RepoOverride> _overrides = [];
    private readonly Dictionary<long, RepoClassification> _classes = [];
    private readonly Dictionary<long, RepoData> _data = [];

    /// <summary>每個 repo 最近一次 E4 第 1 頁的 run id（啟動時取自快取項的 item_ids）。</summary>
    private readonly Dictionary<long, IReadOnlyList<long>> _latestPage = [];

    /// <summary>E7 回 403／404 而停掉 PR 追蹤的 repo（spec §4.2 E7）。</summary>
    private readonly HashSet<long> _prStopped = [];

    /// <summary>E4／E3 回 404、等完整重抓確認的目標（spec §5.4）。</summary>
    private readonly HashSet<(long RepoId, TargetKind Kind)> _pending404 = [];

    /// <summary>各目標的問題標示（§5.2 第 9 條），組成 repos.problem_note；只記這次執行判定的問題。</summary>
    private readonly Dictionary<(long RepoId, TargetKind Kind), Problem> _problems = [];

    /// <summary>
    /// push 探測（不屬於任何 repo）回非限流的 403 或 404 之後的重試間隔（spec §6.1 最後一列、§6.4 第 3、4 列）；成功時移除。
    /// repo 的目標記在 <see cref="_problems"/>。完整重抓的重試間隔和 RepoListRefresh 相同，照上一次送出起算就不會提前，不另外記。
    /// </summary>
    private readonly Dictionary<PollTarget, TimeSpan> _targetRetries = [];

    private readonly HashSet<long> _watched = [];

    /// <summary>本次執行中第一次 E4 已成功的 repo（載入進度）。</summary>
    private readonly HashSet<long> _firstFetched = [];

    /// <summary>每個目標上一次送出的單調時間戳（鎖定下限、改變刷新模式時重排用）。</summary>
    private readonly Dictionary<PollTarget, long> _lastSent = [];

    /// <summary>每個目標上一次排定的間隔（錯誤退避的「上次間隔」）。</summary>
    private readonly Dictionary<PollTarget, TimeSpan> _lastDelay = [];

    /// <summary>正在 5xx、422、JSON 錯誤等的退避中（上一次排定的間隔是退避排的，spec §6.4）；成功一次、照正常間隔重排時移除。</summary>
    private readonly HashSet<PollTarget> _errorBackoff = [];

    /// <summary>
    /// 最近一次送出沒有拿到答案的目標（5xx、連線失敗、限流、處理時丟例外等）；再送一次時移除。403、404 是有答案的錯誤，有自己的錯誤狀態（<see cref="IsHeld(long, TargetKind)"/>），不算。
    /// 第一次載入用（<see cref="IsLaterWorkflowsRefresh"/>）。
    /// </summary>
    private readonly HashSet<PollTarget> _unanswered = [];

    /// <summary>每個 repo 這次執行最近一次成功回應（含 304）的單調時間戳；「無法更新」用它判斷（spec §7.3）。</summary>
    private readonly Dictionary<long, long> _lastRepoSuccess = [];

    /// <summary>預設分支補抓（E5）上一次送出的時間；SeedRecheck 內不重複補抓。</summary>
    private readonly Dictionary<(long RepoId, long WorkflowId), long> _seededAt = [];

    private readonly Dictionary<(long RepoId, long WorkflowId), IReadOnlyList<TimeSpan>> _samples = [];

    /// <summary>沒見過的 status／conclusion 每個值只記一次 error.log（spec §7.1）。</summary>
    private readonly HashSet<string> _unknownValuesLogged = new(StringComparer.Ordinal);

    private readonly List<QuotaSample> _quotaSamples = [];

    /// <summary>PR 分支補抓第一次排入的時間（UTC），當成補抓回來的新 run 的時間界線下限（spec §9.1、§9.3）。</summary>
    private readonly Dictionary<(long RepoId, string HeadSha), DateTimeOffset> _prSeedScheduledAt = [];

    /// <summary>run 已寫入 DB、還沒做完的完成觀察（通知、ETA 樣本、取消標記）；做完才移除，失敗時之後重試。</summary>
    private readonly List<PendingCompletion> _pendingCompletions = [];

    /// <summary>pushed_at 改變而立即重查 workflow 清單的略過中 repo：這次 E3 讓它轉成監看時，界線是那次的 pushed_at（spec §9.3）。</summary>
    private readonly HashSet<long> _pushTriggeredWorkflows = [];

    /// <summary>
    /// 這次執行中才開始監看的 repo（取消靜音、新出現的 repo、改設定等）開始監看的單調時間戳；「無法更新」不早於這個時間起算（spec §7.3）。
    /// 啟動時就在監看的 repo 沒有這一筆，從程式啟動時間起算。
    /// </summary>
    private readonly Dictionary<long, long> _monitoredSince = [];

    // ── 排程與流程（步驟專用）──────────────────────────────────────────

    /// <summary>
    /// 程式啟動時間：首輪時間界線的起點（spec §9.3）。執行中重建資料庫也不改（spec §9.3 最後一點、§14.3）；
    /// 重建前已經通知過的結果由 <see cref="SessionNotifiedStore"/> 擋下，不重發。
    /// </summary>
    private readonly DateTimeOffset _epochUtc;

    /// <summary>程式啟動（建立引擎）時的單調時間戳：「無法更新」最早從這裡起算（spec §7.3），剛啟動時不會全部算進去。</summary>
    private readonly long _startedAtTimestamp;

    /// <summary>
    /// 打開「包含協作 repo」的時間：之後抓清單 B 時第一次看到的協作 repo 是因為你改了設定才開始監看，
    /// 界線是這個時間（spec §9.3）。抓完一輪完整的清單 B、或關掉設定時清除。
    /// </summary>
    private DateTimeOffset? _collaboratorListBaseline;

    /// <summary>
    /// 這次執行已經抓完至少一輪完整的 repo 清單（快照的 <see cref="MonitorSnapshot.RepoListFetched"/>）。
    /// 執行中重建資料庫時歸零：重建後要重新抓完一輪才算。
    /// </summary>
    private bool _repoListFetched;

    /// <summary>
    /// 從空的資料庫開始（新的或剛重建的），第一次載入還沒完成：手上沒有上一次的結果，看得到的都是這次才抓的，可能只是一部分（<see cref="Summarize"/>）。
    /// 資料庫裡有上一次的清單時不算：重新啟動的總狀態照舊（spec §6.2）。完成一次就清掉（<see cref="FirstLoadDone"/>），常駐程式之後才出現的 repo 不算。
    /// </summary>
    private bool _firstLoadPending;

    /// <summary>開啟 repo 管理頁要求的「清單 B 抓一次」（spec §5.1）；抓完一輪完整的清單 B 才清掉。</summary>
    private bool _collaboratorListOnce;

    /// <summary>
    /// 這次執行中改用另一個帳號的時間：之後第一次看到的 repo 是因為你的操作才看得到，界線是這個時間（spec §9.3「改設定」）。
    /// 抓完一輪完整清單時清除。
    /// </summary>
    private DateTimeOffset? _accountSwitchBaseline;

    /// <summary>
    /// 執行中重建資料庫之前各 repo 的時間界線（只記晚於程式啟動時間的）：一般是原本的界線（例如取消靜音的時間），
    /// 靜音中的（靜音設定會跟著遺失）是重建的時間。重建後重新看到這些 repo 時沿用，界線不會因為重建而提早，
    /// 重建後不會一次跳出一堆舊通知（spec §9.3 最後一點、§14.3）；略過原因不會遺失的 repo 也不會因此延後而漏發。抓完一輪完整清單時清除。
    /// </summary>
    private readonly Dictionary<long, DateTimeOffset> _recoveryBaselines = [];

    /// <summary>
    /// 輪詢收到 401、換到新 token 時節流器不放行而延後的那一次重送所用的 token（spec §13.2 第 1 點的「重送一次」）。
    /// 用它送出的輪詢請求收到 HTTP 回應就清除；收到的是 401 時直接進入未登入，不再重取（第 2 點）。
    /// </summary>
    private string? _deferredRetryToken;

    private RepoListRound? _round;
    private bool _scheduleSeeded;
    private bool _needsVerify;

    /// <summary>
    /// 等驗證的 E1 只送一次：未登入時按「重新檢查」拿回的仍是被拒的那一把 token（spec §13.2 第 3 點「手動按才送的單一請求」）；
    /// 非 401 的失敗不退避重送。和 <c>_needsVerify</c> 一起設定、一起清除。
    /// </summary>
    private bool _verifyOnce;

    /// <summary>
    /// 已登入（Ok）而且 token 沒變時按「重新檢查」：用 E1 驗證這一次（spec §13.2 第 3 點），不擋其他輪詢；
    /// 非 401 的失敗不自動重送，限流時等暫停結束再送。
    /// </summary>
    private bool _recheckPending;

    private long? _verifyAt;
    private TimeSpan _verifyDelay;
    private bool _startupRateLimitPending;
    private long? _lastCleanupAt;
    private long _lastAuthRecheckAt;
    private TimeSpan _offlineProbeDelay = Tuning.OfflineProbeInitial;
    private volatile bool _pauseNoted;
    private DateTimeOffset? _lastSuccessUtc;
    private RateLimitSnapshot? _rateLimit;
    private long? _toastPendingSince;
    private bool _deprecatedNoticed;
    private bool _ssoNoticed;

    /// <summary>repo 重新分類的原因，決定首輪時間界線 T0（spec §9.3）。</summary>
    private enum ClassifyCause
    {
        /// <summary>完整重抓、探測、workflow 清單、stale 首次檢查：T0 是程式啟動時間。</summary>
        Discovery,

        /// <summary>新的 push 讓 repo 從略過轉成監看：T0 是觸發的 pushed_at。</summary>
        Push,

        /// <summary>你的操作（取消靜音、強制納入、改設定）：T0 是操作時間。</summary>
        User,
    }

    /// <summary>E4 出現本地沒有的 workflow_id 時已經為它排過 E3 的組合；同一個 id 不重複觸發。</summary>
    private readonly HashSet<(long RepoId, long WorkflowId)> _unknownWorkflowsRequested = [];

    private DiscoveryOptions Options => new(_settings.IncludeForks, _settings.IncludeCollaboratorRepos);

    private RepoOverride OverrideOf(long repoId) => _overrides.GetValueOrDefault(repoId) ?? RepoOverride.None;

    private bool IsMonitored(long repoId) => _classes.TryGetValue(repoId, out var cls) && cls.Monitored;

    private bool NeedsCollaboratorList() =>
        RepoDiscovery.NeedsCollaboratorList(Options, _repos.Values.Select(repo => (repo, OverrideOf(repo.Id))));

    /// <summary>DB 讀出來的狀態一律放進記憶體；之後的寫入同時寫 DB 與記憶體。</summary>
    private void LoadFromDatabase()
    {
        _repos.Clear();
        _states.Clear();
        _overrides.Clear();
        _classes.Clear();
        _data.Clear();
        _latestPage.Clear();
        _watched.Clear();

        // 先清掉：後面讀 DB 失敗時不留著舊的值（例如執行中重建資料庫之前的）。
        _pushPermissions = null;

        _account = _cache.GetAccount();

        // push 權限只讀目前帳號的資料列（第二階段 spec §4.1）；還不知道帳號時全部不確定，驗證帳號時再讀。
        if (_account is { } account)
        {
            _pushPermissions = new PushPermissions(account.UserId, _cache.GetRepoPermissions(account.UserId));
        }

        foreach (var repo in _cache.GetRepos())
        {
            _repos[repo.Id] = repo;
        }

        _firstLoadPending = _repos.Count == 0;

        foreach (var (id, state) in _cache.GetRepoStates())
        {
            _states[id] = state;
        }

        // 「最後更新」是最近一次成功的回應（spec §7.4）：啟動時先取 DB 記下的各 repo 最近一次成功，以離線狀態啟動時橫幅也寫得出時間（spec §15）。
        // 執行中重建時保留記憶體裡的值（空白的資料庫沒有紀錄）。
        _lastSuccessUtc ??= _states.Values.Max(state => state.LastSuccessAt);

        foreach (var (id, value) in _cache.GetOverrides())
        {
            _overrides[id] = value;
        }

        _watched.UnionWith(_cache.GetWatched());

        foreach (var repo in _repos.Values)
        {
            // 「有 ETag 就有資料」：快取項的 item_ids 就是上一次第 1 頁的 run（spec §14.3）。
            _latestPage[repo.Id] = _cache.Get(RunsCacheKey(repo))?.ItemIds ?? [];
            _classes[repo.Id] = Classify(repo);
        }

        // 待確認的重跑接著追（第二階段 spec §6.2 第 6 點）：要在分類之後，才知道哪些 repo 在監看中、可以排 E6。
        RestorePendingReruns();
        PublishRepoLookup();

        // 命令列刷新的序號接著上一次執行的（第二階段 spec §10.5），讀回之後才開始寫狀態表。
        // 執行中重建時保留記憶體裡的值（空白的資料庫沒有紀錄），正在等的命令列才等得到。
        if (!_statusLoaded)
        {
            LoadStatusSequences();
        }
    }

    /// <summary>
    /// E4 主要輪詢的快取鍵，和 GitHubApi.GetRunsAsync 組的網址相同（完整網址當鍵，Task 9 的約定）。
    /// </summary>
    private static string RunsCacheKey(RepoInfo repo) =>
        string.Create(CultureInfo.InvariantCulture, $"{Tuning.ApiBaseUrl}{GitHubClient.RepoPath(repo.FullName)}/actions/runs?per_page={Tuning.RunsPageSize}");

    /// <summary>repo 改名後要刪掉的舊網址快取前綴：完整網址與相對路徑兩種寫法（spec §5.4）。</summary>
    private static IEnumerable<string> RepoCachePrefixes(string fullName)
    {
        var path = GitHubClient.RepoPath(fullName) + "/";
        return [Tuning.ApiBaseUrl + path, path];
    }

    private void PublishRepoLookup() => _repoLookup = new Dictionary<long, RepoInfo>(_repos);

    private RepoState StateOf(long repoId) =>
        _states.GetValueOrDefault(repoId) ?? new RepoState(repoId, null, null, null, null, null, null, 0, null);

    private void SaveState(RepoState state)
    {
        if (_states.TryGetValue(state.RepoId, out var existing) && existing == state)
        {
            return;
        }

        _states[state.RepoId] = state;
        _cache.SaveRepoState(state);
    }

    /// <summary>這個 repo 在 DB 裡的 run、attempt、workflow、PR；寫入後呼叫 <see cref="Invalidate"/> 重新讀取。</summary>
    private RepoData Data(long repoId)
    {
        if (!_data.TryGetValue(repoId, out var data))
        {
            data = new RepoData(_cache.GetRuns(repoId), _cache.GetAttempts(repoId), _cache.GetWorkflows(repoId), _cache.GetPullRequests(repoId));
            _data[repoId] = data;
        }

        return data;
    }

    private void Invalidate(long repoId) => _data.Remove(repoId);

    private IReadOnlyList<TimeSpan> Samples(long repoId, long workflowId)
    {
        if (!_samples.TryGetValue((repoId, workflowId), out var samples))
        {
            samples = _cache.GetDurationSamples(repoId, workflowId);
            _samples[(repoId, workflowId)] = samples;
        }

        return samples;
    }

    private RepoTrackingInput TrackingInput(RepoInfo repo, RepoData data) =>
        new(repo, data.Workflows, data.Runs, data.Attempts, _prStopped.Contains(repo.Id) ? null : data.PullRequests);

    private IReadOnlyList<KeyEvaluation> EvaluateKeys(RepoInfo repo) =>
        TrackedRunSelector.Evaluate(TrackingInput(repo, Data(repo.Id)), _time.GetUtcNow());

    private RepoClassification Classify(RepoInfo repo)
    {
        var data = Data(repo.Id);
        var state = StateOf(repo.Id);
        DateTimeOffset? latestRun = data.Runs.Count > 0 ? data.Runs.Max(run => run.CreatedAt) : null;
        WorkflowCounts? counts = state.WorkflowsTotal is { } total ? new WorkflowCounts(total, state.WorkflowsActive ?? 0) : null;
        return RepoDiscovery.Classify(repo, OverrideOf(repo.Id), Options, latestRun, counts, _time.GetUtcNow());
    }

    /// <summary>
    /// 這個 repo 的首輪時間界線 T0（spec §9.3）：不早於程式啟動時間。
    /// DB 裡記的是上一次執行設定的界線，比這次啟動早時以啟動時間為準。
    /// </summary>
    private DateTimeOffset Baseline(long repoId)
    {
        var start = _epochUtc;
        return StateOf(repoId).BaselineT0 is { } stored && stored > start ? stored : start;
    }

    /// <param name="note">
    /// repo 管理頁顯示的問題（依語言產生文字，換語言時重建）；null 時只照 <paramref name="retryInterval"/> 等重試、不顯示（例如 404 還沒確認原因）。
    /// </param>
    /// <param name="retryInterval">這個目標之後多久重試一次：PermissionRetryInterval 或 NotFoundRetryInterval（spec §6.1）。</param>
    private void SetProblem(long repoId, TargetKind kind, Func<AppLanguage, string>? note, TimeSpan retryInterval)
    {
        _problems[(repoId, kind)] = new Problem(note, retryInterval);
        SaveProblemNote(repoId);
    }

    /// <remarks>
    /// problem_note 存在 DB、_problems 只在記憶體：上一次執行留下的標示，在這次執行還沒判定任何問題時，
    /// 這個 repo 第一次成功就清掉；仍有問題的目標下一次失敗會再標示（spec §5.2 第 9 條標示的是目前的問題）。
    /// </remarks>
    private void ClearProblem(long repoId, TargetKind kind)
    {
        var removed = _problems.Remove((repoId, kind));
        var leftover = StateOf(repoId).ProblemNote is not null && !_problems.Keys.Any(key => key.RepoId == repoId);
        if (removed || leftover)
        {
            SaveProblemNote(repoId);
        }
    }

    private bool HasProblem(long repoId, TargetKind kind) => _problems.ContainsKey((repoId, kind));

    /// <summary>同一個 repo 的多個問題依目標種類排列，用字串表的 ProblemSeparator（中文「；」、英文「. 」）接起來，以目前設定的語言寫進 problem_note。</summary>
    private void SaveProblemNote(long repoId)
    {
        if (!_repos.ContainsKey(repoId))
        {
            return;
        }

        var language = Language;
        var notes = _problems
            .Where(pair => pair.Key.RepoId == repoId)
            .OrderBy(pair => pair.Key.Kind)
            .Select(pair => pair.Value.Note?.Invoke(language))
            .OfType<string>()
            .ToList();
        SaveState(StateOf(repoId) with { ProblemNote = notes.Count == 0 ? null : string.Join(EngineText.ProblemSeparator(language), notes) });
    }

    /// <summary>
    /// 換語言（<see cref="EngineCommand.SettingsChanged"/>）：記住的文字用新的語言重建，之後發布的快照就是新的語言。
    /// 全域暫停的橫幅與一次性提示保留原本的 <see cref="BannerInfo.NoticeId"/> 與恢復時間，只換文字；
    /// 這次執行判定的問題標示重新寫進 problem_note。上一次執行留下、這次還沒重新判定的 problem_note 不知道是哪一種問題，
    /// 維持寫入時的語言，到這個 repo 下一次成功時清掉或下一次失敗時重寫。
    /// </summary>
    private void ApplyLanguage()
    {
        var language = Language;
        lock (_bannerGate)
        {
            if (_pauseBanner is { Until: { } until } pause && _pauseText is { } pauseText)
            {
                _pauseBanner = pause with { Text = pauseText(language, until) };
            }

            _deprecatedNotice = InLanguage(_deprecatedNotice, language);
            _databaseNotice = InLanguage(_databaseNotice, language);
            _unexpectedNotice = InLanguage(_unexpectedNotice, language);
        }

        foreach (var repoId in _problems.Keys.Select(key => key.RepoId).Distinct().ToList())
        {
            SaveProblemNote(repoId);
        }
    }

    private static Notice? InLanguage(Notice? notice, AppLanguage language) =>
        notice is null ? null : notice with { Banner = notice.Banner with { Text = notice.Text(language) } };

    /// <summary>
    /// 這個目標正在等錯誤狀態的重試（E4／E3 的 404 等完整重抓、權限錯誤與補抓的 404 每小時重試）：全量刷新在重試間隔內不提前送它。
    /// </summary>
    private bool IsHeld(long repoId, TargetKind kind) => _pending404.Contains((repoId, kind)) || HasProblem(repoId, kind);

    /// <summary>同 <see cref="IsHeld(long, TargetKind)"/>；不屬於任何 repo 的目標（探測）看 <see cref="_targetRetries"/>。</summary>
    private bool IsHeld(PollTarget target) =>
        target.RepoId is { } repoId ? IsHeld(repoId, target.Kind) : _targetRetries.ContainsKey(target);

    /// <summary>這個目標在等錯誤重試時的重試間隔（PermissionRetryInterval／NotFoundRetryInterval，spec §6.1）。</summary>
    private bool TryGetRetryInterval(PollTarget target, out TimeSpan interval)
    {
        if (target.RepoId is not { } repoId)
        {
            return _targetRetries.TryGetValue(target, out interval);
        }

        if (_problems.TryGetValue((repoId, target.Kind), out var problem))
        {
            interval = problem.RetryInterval;
            return true;
        }

        interval = TimeSpan.Zero;
        return false;
    }

    /// <summary>
    /// repo 有成功的回應（含 304）：記下單調時間戳（判斷無法更新用，spec §7.3），並更新 DB 的 last_success_at（跨重啟的紀錄）。
    /// 先記在記憶體：寫 DB 失敗時，這次成功的回應仍算數。
    /// </summary>
    private void MarkRepoSuccess(long repoId)
    {
        _lastRepoSuccess[repoId] = _time.GetTimestamp();
        SaveState(StateOf(repoId) with { LastSuccessAt = _time.GetUtcNow() });
    }

    /// <summary>
    /// 超過 StaleDataThreshold 沒有成功回應的監看中 repo（spec §7.3）。「沒有成功回應」是本機經過的時間，用單調時鐘，
    /// 系統時間被往回或往前調都不影響（spec §3.1、§15）。起點取程式啟動、這次執行開始監看、這次執行最近一次成功三者中最晚的：
    /// 剛啟動或剛開始監看、還沒有機會更新的 repo 不算「無法更新」。
    /// </summary>
    private bool IsStale(long repoId)
    {
        var since = MonitoringSince(repoId);
        if (_lastRepoSuccess.TryGetValue(repoId, out var success) && success > since)
        {
            since = success;
        }

        return _time.GetElapsedTime(since) > Tuning.StaleDataThreshold;
    }

    /// <summary>這個 repo 這次執行開始有機會更新的時間：程式啟動與這次執行開始監看取較晚的（單調時鐘）。</summary>
    private long MonitoringSince(long repoId) =>
        _monitoredSince.TryGetValue(repoId, out var monitored) && monitored > _startedAtTimestamp ? monitored : _startedAtTimestamp;

    /// <summary>
    /// Engine 用到的設定值的副本。AppSettings 是可變的類別，App 可能改完同一個物件再送 SettingsChanged，
    /// 所以比對「哪些設定變了」要用自己留下的副本。
    /// </summary>
    private sealed record EngineSettings(
        AppLanguage Language,
        bool IncludeForks,
        bool IncludeCollaboratorRepos,
        RefreshMode RefreshMode,
        AuthMode AuthMode,
        bool IgnoreGhTokenEnv,
        bool NotifyTrackedFailure,
        bool NotifyMyRunSuccess,
        bool NotifyWatchedRunDone,
        int FailureToastSeconds,
        int SuccessToastSeconds,
        int LogTailLines,
        bool AllowWriteActions)
    {
        /// <summary>範圍外的值（App 沒有先 Normalized 時）改用預設值，同 AppSettings.Normalized 的規則。</summary>
        public static EngineSettings From(AppSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            return new EngineSettings(
                AppLanguages.Normalize(settings.Language),
                settings.IncludeForks,
                settings.IncludeCollaboratorRepos,
                Enum.IsDefined(settings.RefreshMode) ? settings.RefreshMode : RefreshMode.Auto,
                settings.AuthMode,
                settings.IgnoreGhTokenEnv,
                settings.NotifyTrackedFailure,
                settings.NotifyMyRunSuccess,
                settings.NotifyWatchedRunDone,
                InRange(settings.FailureToastSeconds, Tuning.FailureToastMinSeconds, Tuning.FailureToastMaxSeconds, Tuning.FailureToastSeconds),
                InRange(settings.SuccessToastSeconds, Tuning.SuccessToastMinSeconds, Tuning.SuccessToastMaxSeconds, Tuning.SuccessToastSeconds),
                InRange(settings.LogTailLines, Tuning.LogTailLinesMin, Tuning.LogTailLinesMax, Tuning.LogTailLines),
                settings.AllowWriteActions);
        }

        private static int InRange(int value, int min, int max, int fallback) => value >= min && value <= max ? value : fallback;
    }

    /// <summary>
    /// 執行中重建資料庫之後：記憶體裡的資料一律以新的（空白的）資料庫為準，像冷啟動一樣重新抓（spec §14.3「快取丟了只會重抓」）。
    /// 帳號沿用記憶體裡已驗證的值；spec §14.3 只說靜音與強制納入會遺失，「完成時通知我」的標記也寫回新的資料庫。
    /// 時間界線仍是程式啟動時間（spec §9.3 最後一點、§14.3）：損毀當下才完成、寫不進 DB 的結果，重新抓回來時照常判定；
    /// 重建前已經通知過的由 <see cref="SessionNotifiedStore"/> 擋下，notified 清空了也不重發。各 repo 晚於程式啟動時間的界線
    /// 與重建前靜音中的 repo（重建的時間）記在 <see cref="_recoveryBaselines"/>，不因重建而提早。
    /// </summary>
    private void ResetAfterDatabaseRecovery()
    {
        var rebuiltAt = _time.GetUtcNow();
        foreach (var repoId in _repos.Keys)
        {
            // 只有靜音會因重建遺失（spec §14.3、§15；強制納入遺失只會讓 repo 轉成略過）：靜音中的 repo 重建後是因為重建才開始監看，
            // 界線是重建的時間。其他 repo（監看中，或因為第 6～8 條等不會遺失的原因略過）沿用原本的界線，
            // 沒有晚於程式啟動時間的界線就不記，之後轉成監看時照常以程式啟動時間為界線（spec §9.3），不漏發。
            var baseline = OverrideOf(repoId).Muted ? rebuiltAt : Baseline(repoId);
            if (baseline > _epochUtc)
            {
                _recoveryBaselines[repoId] = baseline;
            }
        }

        _scheduler.RemoveWhere(target => target.Kind != TargetKind.OfflineProbe);
        _round = null;
        _prStopped.Clear();
        _pending404.Clear();
        _problems.Clear();
        _targetRetries.Clear();
        _firstFetched.Clear();
        _lastSent.Clear();
        _lastDelay.Clear();
        _errorBackoff.Clear();
        _unanswered.Clear();
        _lastRepoSuccess.Clear();
        _seededAt.Clear();
        _samples.Clear();
        _unknownWorkflowsRequested.Clear();
        _prSeedScheduledAt.Clear();
        _pendingCompletions.Clear();
        _pushTriggeredWorkflows.Clear();
        _monitoredSince.Clear();
        _collaboratorListBaseline = null;
        _repoListFetched = false;
        ForgetStatusWrite();

        // 重讀 DB（LoadFromDatabase）以新的資料庫為準：先記下記憶體裡的帳號與標記，寫回失敗、讀不回來或重讀到一半失敗時都在重讀之後補回。
        var account = _account;
        var watched = _watched.ToList();
        WriteBackAccount(account);
        WriteBackWatched(watched);

        // 待確認的重跑也寫回（第二階段 spec §6.2）：之後重新啟動才接得上。取消中只在記憶體，留著等 repo 重新抓回來。
        WriteBackPendingReruns();
        ReloadFromDatabase();
        _account ??= account;
        _watched.UnionWith(watched);
        if (_scheduleSeeded && _auth.Status == AuthStatus.Ok)
        {
            SeedInitialSchedule();
        }
    }

    /// <summary>
    /// 執行中重建資料庫（<see cref="ResetAfterDatabaseRecovery"/>）：記憶體裡已驗證的帳號寫回新的資料庫。
    /// 寫回失敗只回報（同 <see cref="WriteBackPendingReruns"/>）：記憶體裡的帳號照常沿用，後面的寫回、重讀與重新排程照常做，監看不會因此停下。
    /// </summary>
    private void WriteBackAccount(AccountInfo? account)
    {
        if (account is null)
        {
            return;
        }

        try
        {
            _cache.SaveAccount(account);
        }
        catch (Exception ex)
        {
            ReportUnexpected("寫回帳號", ex);
        }
    }

    /// <summary>
    /// 執行中重建資料庫（<see cref="ResetAfterDatabaseRecovery"/>）：「完成時通知我」的標記寫回新的資料庫，重新啟動後才接得上。
    /// 寫回失敗只回報（同 <see cref="WriteBackPendingReruns"/>）：記憶體裡的標記照常保留，後面的寫回、重讀與重新排程照常做，監看不會因此停下。
    /// </summary>
    private void WriteBackWatched(IEnumerable<long> runIds)
    {
        try
        {
            foreach (var runId in runIds)
            {
                _cache.SetWatched(runId, watched: true);
            }
        }
        catch (Exception ex)
        {
            ReportUnexpected("寫回完成時通知我的標記", ex);
        }
    }

    /// <summary>
    /// 執行中重建資料庫（<see cref="ResetAfterDatabaseRecovery"/>）：重讀新的資料庫（<see cref="LoadFromDatabase"/>）。
    /// 重讀失敗只回報（同 <see cref="WriteBackPendingReruns"/>）：新的資料庫本來就是空的，讀到一半的狀態照留（push 權限不確定，處理下一次 repo 清單時再讀），
    /// 後面補回帳號與標記、重新排程照常做，監看不會因此停下。讀到損毀時 <see cref="ReportUnexpected"/> 記下來，下一次 <see cref="RecoverDatabaseIfNeeded"/> 再改名重建一次（spec §15）：
    /// 重建在步驟結尾時是下一個步驟開頭；重建在步驟開頭時是同一步結尾，這一步不送請求（<see cref="StepAsync"/>）。
    /// 連續重建已達 <see cref="RebuildsBeforeBackoff"/> 次時，下一次改排退避，等到重試的時間才再改名重建。
    /// </summary>
    private void ReloadFromDatabase()
    {
        try
        {
            LoadFromDatabase();
        }
        catch (Exception ex)
        {
            ReportUnexpected("重讀快取資料庫", ex);
        }
    }

    /// <param name="Note">產生 repo 管理頁顯示的問題的方法（依語言，換語言時重建）；null 時只等重試、不顯示。</param>
    /// <param name="RetryInterval">這個目標之後多久重試一次。</param>
    private sealed record Problem(Func<AppLanguage, string>? Note, TimeSpan RetryInterval);

    /// <summary>
    /// 通知去重（spec §9.2）：cache.db 的 notified 表，加上這次執行寫過的去重鍵。執行中重建資料庫後 notified 是空的，
    /// 界線仍是程式啟動時間（spec §9.3），重建前已經通知或記過 suppressed 的完成觀察靠記憶體裡的這份紀錄擋下，不重發。
    /// 只在步驟裡使用（NotificationPlanner 由 PlanCompletion 呼叫），不必加鎖。
    /// </summary>
    private sealed class SessionNotifiedStore(INotifiedStore store) : INotifiedStore
    {
        private readonly HashSet<(long RunId, int Attempt, string Conclusion)> _written = [];

        public bool Contains(long runId, int runAttempt, string conclusion) =>
            _written.Contains((runId, runAttempt, conclusion)) || store.Contains(runId, runAttempt, conclusion);

        public void Add(long runId, int runAttempt, string conclusion, NotificationKind? kind, bool suppressed, DateTimeOffset at)
        {
            // 先寫 DB：寫入失敗時這一筆留到之後重試（PlanPendingCompletions），不能先記成已經處理過。
            store.Add(runId, runAttempt, conclusion, kind, suppressed, at);
            _written.Add((runId, runAttempt, conclusion));
        }
    }

    /// <summary>一次完成觀察與它的時間界線（觀察當下就決定，spec §9.1「在完成觀察的那一刻判斷」）。</summary>
    /// <param name="AwaitingPullRequests">等這個 repo 的 E7 回來才規劃（失敗的 pull_request 事件 run，本地 PR 清單還沒有它的分支）。</param>
    /// <param name="RecheckTracking">規劃時用當下的追蹤鍵重新判斷是否追蹤中（等過 E7 的觀察）。</param>
    private sealed record PendingCompletion(
        CompletionObservation Observation,
        DateTimeOffset Baseline,
        bool AwaitingPullRequests,
        bool RecheckTracking);

    private sealed record RepoData(
        IReadOnlyList<RunInfo> Runs,
        IReadOnlyDictionary<long, IReadOnlyList<RunAttemptRecord>> Attempts,
        IReadOnlyList<WorkflowInfo> Workflows,
        IReadOnlyList<PullRequestInfo> PullRequests);

    /// <summary>一個 core 額度樣本（spec §6.4 低額度拉長：取 QuotaSampleWindow 內、同一個 reset 視窗的最小 remaining）。</summary>
    private sealed record QuotaSample(long At, int Remaining, int Limit, long? Reset);

    /// <summary>一輪完整重抓（清單 A 全部分頁，需要時再抓清單 B），一頁一個步驟（spec §5.3）。</summary>
    private sealed class RepoListRound(bool includeCollaborators)
    {
        /// <summary>這一輪要接著抓清單 B；開啟 repo 管理頁時可以在清單 A 抓完之前補上（spec §5.1）。</summary>
        public bool IncludeCollaborators { get; set; } = includeCollaborators;

        public bool OnCollaboratorList { get; set; }

        public bool FetchedCollaborators { get; set; }

        public string CurrentUrl { get; set; } = GitHubApi.RepoListUrl(collaborator: false, probe: false);

        public HashSet<string> Visited { get; } = new(StringComparer.Ordinal);

        public List<RepoInfo> ListA { get; } = [];

        public List<RepoInfo> ListB { get; } = [];

        public List<HttpCacheEntry> Entries { get; } = [];

        /// <summary>
        /// 這一輪回 200 的分頁裡每個 repo 的 permissions.push（第二階段 spec §4.1），一輪結束時和 repo 同一個交易寫入。
        /// 回 304 的分頁沒有內容，裡面的 repo 不在這裡（不確定，保留資料庫原本的值）。
        /// </summary>
        public Dictionary<long, bool?> CanPush { get; } = [];

        /// <summary>所有分頁都成功；false 時不計消失（spec §5.4）。</summary>
        public bool Complete { get; set; } = true;

        /// <summary>
        /// 合併一頁的 push 權限。同一個 repo 在這一輪出現兩次（分頁途中排序變動、清單 A 與 B 都有）時，
        /// 不確定（null）不蓋掉這一輪已經讀到的值。
        /// </summary>
        public void AddCanPush(IReadOnlyDictionary<long, bool?> page)
        {
            foreach (var (repoId, canPush) in page)
            {
                if (canPush is not null || !CanPush.ContainsKey(repoId))
                {
                    CanPush[repoId] = canPush;
                }
            }
        }
    }
}
