using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using IsItGreenYet.App.Localization;
using IsItGreenYet.Core;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Display;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Filtering;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.App.ViewModels;

/// <summary>清單是空的時候顯示哪一種說明（spec §10.11）。</summary>
internal enum EmptyStateKind
{
    None,
    NotLoggedIn,
    Offline,
    Loading,
    NoData,
    FilteredOut,
}

/// <summary>「自動更新」下拉的一個選項（spec §10.3）。文字用目前的語言。</summary>
internal sealed class RefreshModeOption(RefreshMode mode) : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RefreshMode Mode { get; } = mode;

    public string Text => Mode switch
    {
        RefreshMode.Seconds30 => Loc.Texts.RefreshSeconds30,
        RefreshMode.Minute1 => Loc.Texts.RefreshMinute1,
        RefreshMode.Minutes5 => Loc.Texts.RefreshMinutes5,
        RefreshMode.Paused => Loc.Texts.RefreshPaused,
        _ => Loc.Texts.RefreshAuto,
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>換語言之後：選單項目的文字重新讀。</summary>
    public void RefreshText() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));

    public override string ToString() => Text;
}

/// <summary>
/// 主畫面（spec §10）：橫幅、摘要列、自動更新、清單、空狀態、寬度斷點與延後重排。
/// 只在 UI 執行緒使用；快照由 MainWindow 轉回 UI 執行緒後交給 <see cref="Apply"/>。
/// 文字用目前的語言（spec §10.2、§10.11 原文在 Core 的字串表）；換語言時 MainWindow 呼叫 <see cref="RefreshLanguage"/>。
/// </summary>
internal sealed class MainViewModel : INotifyPropertyChanged
{
    private const string Separator = " · ";

    /// <summary>摘要列「最後更新」的格式（spec §10.3 範例 <c>HH:mm:ss</c>）；用 InvariantCulture，冒號才不會被文化設定換掉。</summary>
    private const string LastUpdatedFormat = "HH:mm:ss";

    private const string WarningTone = "Warning";
    private const string DangerTone = "Danger";

    private readonly TimeProvider _time;
    private readonly DisplayFormatter _formatter;
    private readonly Dictionary<long, RunRowViewModel> _rowsById = [];

    /// <summary>
    /// 使用者按 ✕ 關掉的一次性提示（<see cref="BannerInfo.NoticeId"/>）：換語言時 Engine 與 App 換掉文字、保留識別碼，
    /// 關掉的提示不會因為換語言又出現。沒有識別碼的橫幅（0）改用參考比對（Engine 內容沒變時沿用同一個實例）。
    /// </summary>
    private readonly HashSet<long> _dismissedNotices = [];

    private readonly HashSet<BannerInfo> _dismissed = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// 送出中的 run（第二階段 spec §5.3 第 2 點、§9.4）：run id → 寫入回傳時的快照序號（<see cref="WriteResult.SnapshotSequence"/>），
    /// 還沒回傳時是 null。收到序號比它大、而且那筆不是 InFlight 的快照才清掉；標記期間四個寫入項目都停用。
    /// </summary>
    private readonly Dictionary<long, long?> _sending = [];

    /// <summary>啟動時 DB 或設定檔損毀復原的提示：依語言產生（換語言時重新產生）。</summary>
    private readonly Func<AppLanguage, BannerInfo>? _startupWarningFactory;
    private BannerInfo? _startupWarning;
    private BannerInfo? _backgroundError;
    private BannerInfo? _currentBanner;
    private MonitorSnapshot? _snapshot;
    private RowLayout _layout = RowLayout.Full;
    private RefreshMode _refreshMode;
    private RefreshMode? _lastSnapshotRefreshMode;

    // ── 延後重排（spec §10.10）──────────────────────────────────────
    private bool _menuOpen;
    private bool _pointerOverButton;

    /// <summary>第一個被延後的新增／移除／重排發生的單調時間戳；沒有延後中的變更時為 null。</summary>
    private long? _pendingSince;

    private bool _needsElapsedTick;
    private string _summaryText = string.Empty;
    private IReadOnlyList<string> _summaryParts = [];
    private string _bannerText = string.Empty;
    private string? _bannerTone;
    private BannerKind? _bannerKind;
    private EmptyStateKind _emptyState = EmptyStateKind.Loading;
    private string _emptyTitle = string.Empty;
    private string _emptyDetail = string.Empty;

    public MainViewModel(TimeProvider time, FilterViewModel filter, Func<AppLanguage, BannerInfo>? startupWarning, RefreshMode refreshMode)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        Filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _formatter = new DisplayFormatter(time);
        _startupWarningFactory = startupWarning;
        _startupWarning = startupWarning?.Invoke(UiText.Current);

        RefreshModes =
        [
            new RefreshModeOption(RefreshMode.Auto),
            new RefreshModeOption(RefreshMode.Seconds30),
            new RefreshModeOption(RefreshMode.Minute1),
            new RefreshModeOption(RefreshMode.Minutes5),
            new RefreshModeOption(RefreshMode.Paused),
        ];
        ShowRefreshMode(refreshMode);

        Filter.Changed += _ => SyncVisible(userInitiated: true);
        UpdateBanner();
        UpdateEmptyState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>畫面上的列（已套用篩選），順序同快照（開始時間新到舊，spec §10.10）。</summary>
    public ObservableCollection<RunRowViewModel> Rows { get; } = [];

    public FilterViewModel Filter { get; }

    public IReadOnlyList<RefreshModeOption> RefreshModes { get; }

    public RefreshMode RefreshMode => _refreshMode;

    /// <summary>下拉按鈕上的文字，例如「自動更新：自動」（Auto-refresh: Auto）。</summary>
    public string RefreshModeText => Loc.Format(Loc.Texts.MainRefreshButton, RefreshModes.First(option => option.Mode == _refreshMode).Text);

    /// <summary><c>N 筆執行 · X 進行中 · Y 失敗 · 最後更新 HH:mm:ss · API 剩餘 a/b [· N 個 repo 無法更新]</c>（spec §10.3、§7.4）。</summary>
    public string SummaryText { get => _summaryText; private set => SetField(ref _summaryText, value); }

    /// <summary>
    /// 摘要列拆成一段一段（除了最後一段，每段結尾帶分隔「 · 」，接起來就是 <see cref="SummaryText"/>）：
    /// 畫面用 WrapPanel 排，寬度不夠時整段換到下一行，不在段落中間斷開、也不截斷。
    /// </summary>
    public IReadOnlyList<string> SummaryParts { get => _summaryParts; private set => SetField(ref _summaryParts, value); }

    // ── 橫幅（spec §10.2）──────────────────────────────────────────

    public bool HasBanner => _bannerKind is not null;

    public string BannerText { get => _bannerText; private set => SetField(ref _bannerText, value); }

    /// <summary>Style.Banner 的 Tag：Warning 或 Danger。</summary>
    public string? BannerTone { get => _bannerTone; private set => SetField(ref _bannerTone, value); }

    /// <summary>
    /// 有 ✕ 的橫幅：spec §10.2 表格的動作欄列出「關閉」的那幾種，加上快取資料庫損毀已復原。
    /// 後者在 §10.2 只列「開啟 repo 管理」，但它是一次性的啟動提示、又排在「未預期錯誤」之前：
    /// 不能關閉的話，這次執行之後的背景例外一則都提示不出來（§15）。所以同樣可以關閉，「開啟 repo 管理」照留。
    /// </summary>
    public bool BannerCanDismiss => _bannerKind is BannerKind.Deprecated
        or BannerKind.DatabaseRecovered or BannerKind.SettingsRecovered or BannerKind.UnexpectedError;

    public bool BannerShowRecheck => _bannerKind == BannerKind.Unauthorized;

    public bool BannerShowOpenSettings => _bannerKind == BannerKind.Unauthorized;

    public bool BannerShowRetry => _bannerKind is BannerKind.Offline or BannerKind.ServerErrors;

    public bool BannerShowOpenRepoManager => _bannerKind == BannerKind.DatabaseRecovered;

    public bool BannerShowOpenFolder => _bannerKind == BannerKind.UnexpectedError;

    // ── 空狀態（spec §10.11）───────────────────────────────────────

    public EmptyStateKind EmptyState => _emptyState;

    public bool HasEmptyState => _emptyState != EmptyStateKind.None;

    public string EmptyTitle { get => _emptyTitle; private set => SetField(ref _emptyTitle, value); }

    public string EmptyDetail { get => _emptyDetail; private set => SetField(ref _emptyDetail, value); }

    public bool HasEmptyDetail => _emptyDetail.Length > 0;

    public bool EmptyShowRecheck => _emptyState == EmptyStateKind.NotLoggedIn;

    public bool EmptyShowOpenSettings => _emptyState == EmptyStateKind.NotLoggedIn;

    public bool EmptyShowRetry => _emptyState == EmptyStateKind.Offline;

    public bool EmptyShowOpenRepoManager => _emptyState == EmptyStateKind.NoData;

    public bool EmptyShowClearFilters => _emptyState == EmptyStateKind.FilteredOut;

    /// <summary>目前的寬度斷點（spec §10.10）。</summary>
    public RowLayout Layout => _layout;

    /// <summary>
    /// 每 ElapsedRefresh 的計時器要不要跑：畫面上有進行中或排隊中的列（時間文字每秒變），
    /// 或有延後中的新增／移除／重排（要在 ReorderFreezeMax 時強制套用）。都沒有時視窗停掉計時器。
    /// </summary>
    public bool NeedsElapsedTick { get => _needsElapsedTick; private set => SetField(ref _needsElapsedTick, value); }

    /// <summary>Engine 發布的新快照（已在 UI 執行緒上）。</summary>
    public void Apply(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _snapshot = snapshot;
        ReleaseSending(snapshot);
        UpdateRows(snapshot);
        Filter.UpdateOptions(snapshot.Rows);
        SyncVisible(userInitiated: false);
        UpdateSummary(snapshot);

        // 「自動更新」以使用者剛選的為準；只有 Engine 那邊真的改變時（例如系統匣切換）才跟著快照。
        if (_lastSnapshotRefreshMode != snapshot.RefreshMode)
        {
            _lastSnapshotRefreshMode = snapshot.RefreshMode;
            ShowRefreshMode(snapshot.RefreshMode);
        }

        UpdateBanner();
        UpdateEmptyState();
    }

    /// <summary>App 殼回報的背景錯誤（AppHost.BackgroundErrorRaised）：每次都是新的一則。</summary>
    public void SetBackgroundError(BannerInfo banner)
    {
        _backgroundError = banner ?? throw new ArgumentNullException(nameof(banner));
        UpdateBanner();
    }

    /// <summary>使用者按了橫幅的 ✕：記住這一則（識別碼，沒有識別碼時用參考），之後不再顯示，改顯示下一則。</summary>
    public void DismissBanner()
    {
        if (_currentBanner is not null && BannerCanDismiss)
        {
            if (_currentBanner.NoticeId != 0)
            {
                _dismissedNotices.Add(_currentBanner.NoticeId);
            }
            else
            {
                _dismissed.Add(_currentBanner);
            }

            UpdateBanner();
        }
    }

    /// <summary>
    /// 換語言之後（<see cref="Loc.LanguageChanged"/>）：程式算出來的文字全部用新的語言重算——「自動更新」、摘要列、空狀態、
    /// 篩選選項、每一列，以及 App 自己的橫幅（啟動提示、背景錯誤）。Engine 的橫幅與列的顯示狀態在 Engine 處理完 SettingsChanged 後的下一份快照換成新的語言；
    /// 列的顯示狀態這裡先依目前的語言重新產生，不等快照。
    /// </summary>
    public void RefreshLanguage()
    {
        foreach (var option in RefreshModes)
        {
            option.RefreshText();
        }

        OnPropertyChanged(nameof(RefreshModeText));
        Filter.RefreshLanguage();

        var now = _time.GetUtcNow();
        foreach (var row in AllRows())
        {
            row.RefreshLanguage(now);
        }

        var language = UiText.Current;
        _startupWarning = _startupWarningFactory?.Invoke(language);
        if (_backgroundError is { } background)
        {
            _backgroundError = background with { Text = UiText.Of(language).CommonUnexpectedError };
        }

        if (_snapshot is not null)
        {
            UpdateSummary(_snapshot);
        }

        UpdateBanner();
        UpdateEmptyState();
    }

    /// <summary>「自動更新」下拉顯示的模式。</summary>
    public void ShowRefreshMode(RefreshMode mode)
    {
        _refreshMode = Enum.IsDefined(mode) ? mode : RefreshMode.Auto;
        foreach (var option in RefreshModes)
        {
            option.IsSelected = option.Mode == _refreshMode;
        }

        OnPropertyChanged(nameof(RefreshMode));
        OnPropertyChanged(nameof(RefreshModeText));
    }

    /// <summary>清單可用寬度（扣掉邊距與捲軸）改變：依序隱藏觸發者、分支 chip、時間細節（spec §10.10）。</summary>
    public void SetListWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            return;
        }

        var layout = new RowLayout(
            ShowActor: width >= Tuning.HideActorBelow,
            ShowBranch: width >= Tuning.HideBranchBelow,
            ShowTimeDetail: width >= Tuning.HideTimeDetailBelow);
        if (layout == _layout)
        {
            return;
        }

        _layout = layout;
        var now = _time.GetUtcNow();
        foreach (var row in AllRows())
        {
            row.SetLayout(layout, now);
        }

        OnPropertyChanged(nameof(Layout));
    }

    /// <summary>run 的右鍵選單（或 ⋯ 選單）開著：新增、移除、重排延後（spec §10.10）。</summary>
    public void SetMenuOpen(bool open)
    {
        _menuOpen = open;
        ResumeIfUnfrozen();
    }

    /// <summary>滑鼠停在某列的按鈕或連結上：新增、移除、重排延後（spec §10.10）。</summary>
    public void SetPointerOverButton(bool over)
    {
        if (_pointerOverButton == over)
        {
            return;
        }

        _pointerOverButton = over;
        ResumeIfUnfrozen();
    }

    /// <summary>每 ElapsedRefresh：進行中與排隊中的時間文字；延後的重排超過 ReorderFreezeMax 時強制套用。</summary>
    public void OnElapsedTick()
    {
        var now = _time.GetUtcNow();
        foreach (var row in Rows)
        {
            if (row.IsTicking)
            {
                row.RefreshElapsed(now);
            }
        }

        if (_pendingSince is not null)
        {
            SyncVisible(userInitiated: false);
        }
    }

    /// <summary>每 RelativeTextRefresh：重算開始時間的相對文字，不重排清單（spec §10.8）。</summary>
    public void OnRelativeTick()
    {
        foreach (var row in AllRows())
        {
            row.RefreshStartTime();
        }

        if (_snapshot is not null)
        {
            UpdateSummary(_snapshot);
        }
    }

    /// <summary>repo 目前的靜音與強制納入設定（靜音此 repo 時保留強制納入的值）。</summary>
    public RepoOverride OverrideOf(long repoId) =>
        _snapshot?.Repos.FirstOrDefault(repo => repo.Repo.Id == repoId)?.Override ?? RepoOverride.None;

    /// <summary>確認了寫入、送出之前：這筆 run 標成送出中，四個寫入項目立即停用（第二階段 spec §9.4）。</summary>
    public void BeginSending(long runId)
    {
        _sending[runId] = null;
        RefreshRow(runId);
    }

    /// <summary>
    /// 寫入回傳：記下回傳當下的快照序號，等收到比它新、而且那筆不是 InFlight 的快照才清掉標記。
    /// 回傳到這裡之前已經套用了比它新的快照時，用那一份立即判斷，不必再等下一份。
    /// </summary>
    public void EndSending(long runId, long snapshotSequence)
    {
        if (!_sending.ContainsKey(runId))
        {
            return;
        }

        _sending[runId] = snapshotSequence;
        if (_snapshot is { } snapshot && IsReleasedBy(snapshot, snapshotSequence, runId))
        {
            _sending.Remove(runId);
            RefreshRow(runId);
        }
    }

    /// <summary>寫入沒有結果（程式正在結束、未預期的例外）：直接清掉標記。</summary>
    public void CancelSending(long runId)
    {
        if (_sending.Remove(runId))
        {
            RefreshRow(runId);
        }
    }

    private bool IsFrozen => _menuOpen || _pointerOverButton;

    private IEnumerable<RunRowViewModel> AllRows() => _rowsById.Values.Union(Rows);

    /// <summary>以 run id 為鍵更新每一列的內容；不在快照裡的列從索引移除（畫面上的列等下一次同步才移除）。</summary>
    private void UpdateRows(MonitorSnapshot snapshot)
    {
        var now = _time.GetUtcNow();
        var seen = new HashSet<long>();
        foreach (var row in snapshot.Rows)
        {
            var id = row.Run.Id;
            if (!seen.Add(id))
            {
                continue;
            }

            if (!_rowsById.TryGetValue(id, out var item))
            {
                item = new RunRowViewModel(id, _formatter, _layout);
                _rowsById[id] = item;
            }

            item.Update(row, ExtrasFor(snapshot, id), now);
        }

        foreach (var id in _rowsById.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _rowsById.Remove(id);
        }
    }

    private RunRowExtras ExtrasFor(MonitorSnapshot snapshot, long id) => new(
        snapshot.WatchedRunIds.Contains(id),
        snapshot.Eta.GetValueOrDefault(id),
        snapshot.CompletedDurations.TryGetValue(id, out var duration) ? duration : null,
        snapshot.PullRequestNumbers.GetValueOrDefault(id) ?? [],
        snapshot.Actions.GetValueOrDefault(id),
        snapshot.Cancelling.GetValueOrDefault(id),
        snapshot.WritesEnabled,
        _sending.ContainsKey(id));

    /// <summary>送出中的標記改變時，用目前的快照只更新那一列（不必等下一份快照）。</summary>
    private void RefreshRow(long runId)
    {
        if (_snapshot is not { } snapshot
            || !_rowsById.TryGetValue(runId, out var item)
            || snapshot.Rows.FirstOrDefault(row => row.Run.Id == runId) is not { } row)
        {
            return;
        }

        item.Update(row, ExtrasFor(snapshot, runId), _time.GetUtcNow());
    }

    /// <summary>新的快照套用前：已經回傳、而且這份快照已經反映結果的送出中標記清掉（第二階段 spec §5.3 第 2 點）。</summary>
    private void ReleaseSending(MonitorSnapshot snapshot)
    {
        foreach (var (runId, returned) in _sending.ToList())
        {
            if (returned is { } sequence && IsReleasedBy(snapshot, sequence, runId))
            {
                _sending.Remove(runId);
            }
        }
    }

    /// <summary>
    /// 這份快照是寫入回傳之後發布的（序號比回傳時的大），而且這筆 run 已經不是送出中：Engine 在套用了這筆結果的那一步發布快照之後
    /// 才從送出中移除，在那之前的快照查表還是舊的。快照裡已經沒有這筆 run 時也算。
    /// </summary>
    private static bool IsReleasedBy(MonitorSnapshot snapshot, long returnedSequence, long runId) =>
        snapshot.Sequence > returnedSequence
        && !(snapshot.Actions.TryGetValue(runId, out var actions)
            && (actions.RerunFailedJobs == ActionBlock.InFlight || actions.RerunAll == ActionBlock.InFlight
                || actions.Cancel == ActionBlock.InFlight || actions.ForceCancel == ActionBlock.InFlight));

    /// <summary>同步畫面上的列，再更新 <see cref="NeedsElapsedTick"/>（列的狀態與延後中的變更都可能改變）。</summary>
    private void SyncVisible(bool userInitiated)
    {
        SyncVisibleRows(userInitiated);
        NeedsElapsedTick = _pendingSince is not null || Rows.Any(row => row.IsTicking);
    }

    /// <summary>
    /// 把畫面上的列改成「快照套用篩選後」的順序。輪詢帶來的新增、移除、重排在選單開著或滑鼠停在按鈕上時延後，
    /// 最多延後 ReorderFreezeMax；使用者自己改篩選時立即套用。
    /// </summary>
    private void SyncVisibleRows(bool userInitiated)
    {
        if (_snapshot is null)
        {
            return;
        }

        var target = new List<RunRowViewModel>();
        var seen = new HashSet<long>();
        foreach (var row in RunFilter.Apply(_snapshot.Rows, Filter.ToCriteria()))
        {
            if (seen.Add(row.Run.Id) && _rowsById.TryGetValue(row.Run.Id, out var item))
            {
                target.Add(item);
            }
        }

        if (CollectionSync.SameSequence(Rows, target))
        {
            _pendingSince = null;
            return;
        }

        if (!userInitiated && IsFrozen)
        {
            var now = _time.GetTimestamp();
            _pendingSince ??= now;
            if (_time.GetElapsedTime(_pendingSince.Value, now) < Tuning.ReorderFreezeMax)
            {
                return;
            }
        }

        _pendingSince = null;
        foreach (var inserted in CollectionSync.Apply(Rows, target))
        {
            // 被篩選掉的列不跟著計時器重算，重新出現時先把相對時間補成現在的值。
            inserted.RefreshStartTime();
            inserted.RefreshElapsed(_time.GetUtcNow());
        }

        UpdateEmptyState();
    }

    private void ResumeIfUnfrozen()
    {
        if (!IsFrozen && _pendingSince is not null)
        {
            SyncVisible(userInitiated: false);
        }
    }

    private void UpdateSummary(MonitorSnapshot snapshot)
    {
        var text = Loc.Texts;
        var parts = new List<string>
        {
            UiText.Plural(snapshot.Rows.Count, text.SummaryRunsOne, text.SummaryRunsOther),
            Loc.Format(text.SummaryInProgress, snapshot.Summary.PendingCount),
            Loc.Format(text.SummaryFailed, snapshot.Summary.FailingCount),
            snapshot.LastUpdatedUtc is { } updated
                ? Loc.Format(text.CommonLastUpdated, TimeZoneInfo.ConvertTime(updated, _time.LocalTimeZone).ToString(LastUpdatedFormat, CultureInfo.InvariantCulture))
                : text.CommonNeverUpdated,
        };

        // 額度和 gh 等工具共用，還沒有任何 core 回應時不顯示（spec §7.4）。
        if (snapshot.RateLimit is { Remaining: { } remaining, Limit: { } limit })
        {
            parts.Add(Loc.Format(text.SummaryApiRemaining, remaining, limit));
        }

        if (snapshot.Summary.StaleRepoCount > 0)
        {
            parts.Add(UiText.Plural(snapshot.Summary.StaleRepoCount, text.StaleReposOne, text.StaleReposOther));
        }

        var summary = string.Join(Separator, parts);
        if (summary != _summaryText)
        {
            // 內容沒變時沿用同一份清單，每 RelativeTextRefresh 重算時畫面不必重建每一段。
            SummaryParts = parts.Select((part, index) => index < parts.Count - 1 ? part + Separator : part).ToList();
        }

        SummaryText = summary;
    }

    /// <summary>
    /// 依嚴重度只顯示最重要的一則（BannerKind 的宣告順序就是 spec §10.2 的順序）。
    /// 來源：Engine 快照、啟動時 DB／設定檔的復原提示、App 殼回報的背景錯誤；使用者關掉的不再顯示。
    /// </summary>
    private void UpdateBanner()
    {
        var banner = new[] { _snapshot?.Banner, _startupWarning, _backgroundError }
            .OfType<BannerInfo>()
            .Where(candidate => !IsDismissed(candidate))
            .OrderBy(candidate => candidate.Kind)
            .FirstOrDefault();

        _currentBanner = banner;
        _bannerKind = banner?.Kind;
        BannerText = banner?.Text ?? string.Empty;
        BannerTone = banner?.Kind is BannerKind.VersionError or BannerKind.UnexpectedError ? DangerTone : WarningTone;

        OnPropertyChanged(nameof(HasBanner));
        OnPropertyChanged(nameof(BannerCanDismiss));
        OnPropertyChanged(nameof(BannerShowRecheck));
        OnPropertyChanged(nameof(BannerShowOpenSettings));
        OnPropertyChanged(nameof(BannerShowRetry));
        OnPropertyChanged(nameof(BannerShowOpenRepoManager));
        OnPropertyChanged(nameof(BannerShowOpenFolder));
    }

    private bool IsDismissed(BannerInfo banner) =>
        banner.NoticeId != 0 ? _dismissedNotices.Contains(banner.NoticeId) : _dismissed.Contains(banner);

    /// <summary>
    /// 清單是空的時候顯示哪一種說明（spec §10.11）。依序：未登入、離線且沒有資料、正在載入、沒有資料；
    /// 主清單有資料、但篩選後一筆都沒有時是「全部被篩選掉」。有快取資料時照常顯示清單（spec §15「既有資料不清空」）。
    /// </summary>
    private void UpdateEmptyState()
    {
        var snapshot = _snapshot;
        EmptyStateKind kind;
        if (Rows.Count > 0)
        {
            kind = EmptyStateKind.None;
        }
        else if (snapshot is null)
        {
            kind = EmptyStateKind.Loading;
        }
        else if (snapshot.Rows.Count > 0)
        {
            kind = EmptyStateKind.FilteredOut;
        }
        else if (snapshot.Auth == AuthStatus.Unauthorized)
        {
            kind = EmptyStateKind.NotLoggedIn;
        }
        else if (snapshot.Offline)
        {
            kind = EmptyStateKind.Offline;
        }
        else if (IsLoading(snapshot))
        {
            kind = EmptyStateKind.Loading;
        }
        else
        {
            kind = EmptyStateKind.NoData;
        }

        var text = Loc.Texts;
        (EmptyTitle, EmptyDetail) = kind switch
        {
            EmptyStateKind.NotLoggedIn => (text.EmptyNotLoggedIn,
                snapshot?.TokenSource == TokenSource.GhEnvironment ? text.CommonUnauthorizedFromEnvironment : text.EmptyUnauthorizedHint),
            EmptyStateKind.Offline => (text.EmptyOffline, string.Empty),
            EmptyStateKind.Loading => (LoadingText(snapshot, text), string.Empty),
            // 監看中 0 個 repo 時再加一句（spec §10.11）。
            EmptyStateKind.NoData => (text.EmptyNoData,
                snapshot?.Loading.Total == 0 ? text.EmptyNoDataHint : string.Empty),
            EmptyStateKind.FilteredOut => (text.EmptyFilteredOut, string.Empty),
            _ => (string.Empty, string.Empty),
        };

        if (kind == _emptyState)
        {
            OnPropertyChanged(nameof(HasEmptyDetail));
            return;
        }

        _emptyState = kind;
        OnPropertyChanged(nameof(EmptyState));
        OnPropertyChanged(nameof(HasEmptyState));
        OnPropertyChanged(nameof(HasEmptyDetail));
        OnPropertyChanged(nameof(EmptyShowRecheck));
        OnPropertyChanged(nameof(EmptyShowOpenSettings));
        OnPropertyChanged(nameof(EmptyShowRetry));
        OnPropertyChanged(nameof(EmptyShowOpenRepoManager));
        OnPropertyChanged(nameof(EmptyShowClearFilters));
    }

    /// <summary>
    /// 還在驗證 token、還沒拿到 repo 清單，或還有監看中的 repo 沒完成第一次抓取。
    /// 看得到 0 個 repo 時用 <see cref="MonitorSnapshot.RepoListFetched"/> 分辨「清單還沒抓」與「抓回 0 筆」：
    /// 抓回 0 筆（例如沒選任何 repo 的 fine-grained PAT）就顯示 spec §10.11 的「沒有資料」與「開啟 repo 管理」。
    /// </summary>
    private static bool IsLoading(MonitorSnapshot snapshot) =>
        snapshot.Auth == AuthStatus.Unknown
        || (snapshot.Repos.Count == 0 && !snapshot.RepoListFetched)
        || snapshot.Loading.Completed < snapshot.Loading.Total;

    /// <summary>「正在載入（已完成 x／y 個 repo）」；還不知道有幾個 repo 時只寫「正在載入」。</summary>
    private static string LoadingText(MonitorSnapshot? snapshot, TextTable text) =>
        snapshot is { Loading.Total: > 0 } known
            ? Loc.Format(text.EmptyLoadingProgress, known.Loading.Completed, known.Loading.Total)
            : text.CommonLoading;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
