using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using IsItGreenYet.App.Localization;
using IsItGreenYet.Core;
using IsItGreenYet.Core.Display;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Filtering;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.App.ViewModels;

/// <summary>快照裡以 run id 為鍵、附在每一列的資料。</summary>
/// <param name="Actions">四個寫入動作能不能用（第二階段 spec §7）；快照沒有這筆時是 null。</param>
/// <param name="Cancelling">取消中（第二階段 spec §6.3）；不在取消中是 null。</param>
/// <param name="WritesEnabled">寫入功能的開關；關閉時 run 選單不出現寫入的項目（第二階段 spec §9.1）。</param>
/// <param name="Sending">這筆 run 有寫入在送出中（App 端的標記，MainViewModel）：四個寫入項目都停用。</param>
internal sealed record RunRowExtras(
    bool IsWatched,
    EtaInfo? Eta,
    TimeSpan? CompletedDuration,
    IReadOnlyList<int> PullRequestNumbers,
    RunActions? Actions,
    CancellingInfo? Cancelling,
    bool WritesEnabled,
    bool Sending);

/// <summary>寬度不夠時依序隱藏的欄位：觸發者 → 分支 chip → 時間細節（spec §10.10）。</summary>
internal sealed record RowLayout(bool ShowActor, bool ShowBranch, bool ShowTimeDetail)
{
    public static RowLayout Full { get; } = new(ShowActor: true, ShowBranch: true, ShowTimeDetail: true);
}

/// <summary>
/// 主清單的一列（spec §10.5）。以 run id 為鍵沿用同一個實例，快照更新時只改內容（差異更新）。
/// 文字一律由 DisplayFormatter 產生：開始時間每 RelativeTextRefresh、進行中與排隊的時間每 ElapsedRefresh 重算（spec §10.8）。
/// 文字用目前的語言；換語言時 MainViewModel 呼叫 <see cref="RefreshLanguage"/>。
/// </summary>
internal sealed class RunRowViewModel : INotifyPropertyChanged
{
    /// <summary>第二行各段之間的分隔（同 DisplayFormatter 的「·」，U+00B7）。</summary>
    private const string Separator = " · ";

    /// <summary>fork 的分支顯示成 <c>owner:branch</c>（spec §10.5）。</summary>
    private const char ForkBranchSeparator = ':';

    private const char FullNameSeparator = '/';

    /// <summary>細進度條用 0～100（ProgressBar 的預設範圍），DurationEstimator 給的是 0～1。</summary>
    private const double PercentScale = 100;

    private const string OverrunTone = "Overrun";

    /// <summary>API 給的網址解析不了時用這個：一定不會通過 WebLinks.IsAllowed，所以不會被開啟（spec §10.7）。</summary>
    private static readonly Uri NotOpenable = new(string.Empty, UriKind.Relative);

    private readonly DisplayFormatter _formatter;

    private RunRow? _row;
    private RunRowExtras _extras = new(false, null, null, [], null, null, false, false);
    private RowLayout _layout;
    private DateTimeOffset? _startUtc;

    private RunState? _state;
    private string _title = string.Empty;
    private string? _titleTooltip;
    private string _repoName = string.Empty;
    private string _repoFullName = string.Empty;
    private string _workflowSegment = string.Empty;
    private string _triggerSeparator = string.Empty;
    private string _triggerLinkText = string.Empty;
    private string _triggerPlainText = string.Empty;
    private string? _triggerTooltip;
    private bool _hasTriggerLink;
    private string _actorText = string.Empty;
    private string _actorSegment = string.Empty;
    private string? _actorTooltip;
    private string? _metaTooltip;
    private string _attemptText = string.Empty;
    private string _branchText = string.Empty;
    private bool _showBranchChip;
    private bool _isWatched;
    private string _startText = string.Empty;
    private string? _startTooltip;
    private string _durationText = string.Empty;
    private string? _durationTooltip;
    private bool _showProgress;
    private double _progressPercent;
    private string? _progressTone;
    private bool _hasPullRequest;
    private string _automationName = string.Empty;

    public RunRowViewModel(long runId, DisplayFormatter formatter, RowLayout layout)
    {
        RunId = runId;
        _formatter = formatter;
        _layout = layout;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long RunId { get; }

    public long RepoId => _row?.Repo.Id ?? 0;

    public RunState? State
    {
        get => _state;
        private set => SetField(ref _state, value);
    }

    public string Title { get => _title; private set => SetField(ref _title, value); }

    public string? TitleTooltip { get => _titleTooltip; private set => SetField(ref _titleTooltip, value); }

    public string RepoName { get => _repoName; private set => SetField(ref _repoName, value); }

    public string RepoFullName { get => _repoFullName; private set => SetField(ref _repoFullName, value); }

    public string WorkflowSegment { get => _workflowSegment; private set => SetField(ref _workflowSegment, value); }

    public string TriggerSeparator { get => _triggerSeparator; private set => SetField(ref _triggerSeparator, value); }

    /// <summary>可點的觸發說明（PR 編號、commit）；不可點時是空字串，文字改放 <see cref="TriggerPlainText"/>。</summary>
    public string TriggerLinkText { get => _triggerLinkText; private set => SetField(ref _triggerLinkText, value); }

    public string TriggerPlainText { get => _triggerPlainText; private set => SetField(ref _triggerPlainText, value); }

    public string? TriggerTooltip { get => _triggerTooltip; private set => SetField(ref _triggerTooltip, value); }

    public bool HasTriggerLink { get => _hasTriggerLink; private set => SetField(ref _hasTriggerLink, value); }

    /// <summary>「 · by alice」；觸發者被寬度斷點隱藏或 actor 缺值時是空字串。</summary>
    public string ActorSegment { get => _actorSegment; private set => SetField(ref _actorSegment, value); }

    public string? ActorTooltip { get => _actorTooltip; private set => SetField(ref _actorTooltip, value); }

    /// <summary>
    /// 第二行的全文（tooltip）：repo · workflow #n · 觸發說明 · by 觸發者 · 第 N 次嘗試 · 分支。
    /// 寬度不夠時第二行會截斷，觸發者與分支 chip 也可能被斷點隱藏（spec §10.10），所以一律列出全部，不看目前的斷點。
    /// </summary>
    public string? MetaTooltip { get => _metaTooltip; private set => SetField(ref _metaTooltip, value); }

    public string AttemptText { get => _attemptText; private set => SetField(ref _attemptText, value); }

    public bool HasAttempt => _attemptText.Length > 0;

    public string BranchText { get => _branchText; private set => SetField(ref _branchText, value); }

    public bool ShowBranchChip { get => _showBranchChip; private set => SetField(ref _showBranchChip, value); }

    public bool CanOpenBranch => BranchUri is not null;

    public bool IsWatched
    {
        get => _isWatched;
        private set
        {
            if (_isWatched == value)
            {
                return;
            }

            _isWatched = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WatchMenuAutomationName));
            UpdateAutomationName();
        }
    }

    /// <summary>「這筆完成時通知我」選單項目的報讀名稱：已標記時加上「已標記」（畫面上是勾號）。</summary>
    public string WatchMenuAutomationName => _isWatched ? Loc.Texts.MenuWatchChecked : Loc.Texts.MenuWatch;

    public string StartText { get => _startText; private set => SetField(ref _startText, value); }

    public string? StartTooltip { get => _startTooltip; private set => SetField(ref _startTooltip, value); }

    public bool ShowStartTime => _layout.ShowTimeDetail;

    public string DurationText { get => _durationText; private set => SetField(ref _durationText, value); }

    public string? DurationTooltip { get => _durationTooltip; private set => SetField(ref _durationTooltip, value); }

    public bool ShowProgress { get => _showProgress; private set => SetField(ref _showProgress, value); }

    public double ProgressPercent { get => _progressPercent; private set => SetField(ref _progressPercent, value); }

    /// <summary>超過預估時是 <c>Overrun</c>（進度條改用 Warning 色，spec §8.2）。</summary>
    public string? ProgressTone { get => _progressTone; private set => SetField(ref _progressTone, value); }

    /// <summary>「開啟 PR」只在有 PR 編號時出現（spec §10.9）。</summary>
    public bool HasPullRequest { get => _hasPullRequest; private set => SetField(ref _hasPullRequest, value); }

    /// <summary>「複製失敗 log」只在結論是失敗類別時出現（spec §10.9）。</summary>
    public bool CanCopyFailedLog => _state?.Category == RunCategory.Failure;

    /// <summary>「這筆完成時通知我」只在未完成或等待時出現（spec §10.9）。</summary>
    public bool CanWatch => _state?.Category is RunCategory.Pending or RunCategory.Waiting;

    // ── 寫入（第二階段 spec §9.1、§9.3）：每次 Update 都通知改變（OnWriteActionsChanged）────

    /// <summary>這一列最新的 run_attempt；按下寫入項目時記下，當成 expectedAttempt 送出（第二階段 spec §5.1、§9.2）。</summary>
    public int RunAttempt => _row?.Run.RunAttempt ?? 0;

    /// <summary>寫入功能開啟、而且快照有這筆的可操作性時，選單才出現分隔線與寫入的項目（第二階段 spec §9.1）。</summary>
    public bool ShowWriteActions => _extras.WritesEnabled && _extras.Actions is not null;

    public bool CanRerunFailed => BlockOf(_extras.Actions?.RerunFailedJobs) == ActionBlock.None;

    /// <summary>停用的原因（tooltip 與報讀說明）；可以用時是 null，不顯示空的 tooltip（同 repo 管理頁的強制納入按鈕）。</summary>
    public string? RerunFailedReason => ReasonOf(_extras.Actions?.RerunFailedJobs);

    public bool CanRerunAll => BlockOf(_extras.Actions?.RerunAll) == ActionBlock.None;

    public string? RerunAllReason => ReasonOf(_extras.Actions?.RerunAll);

    public bool CanCancel => BlockOf(_extras.Actions?.Cancel) == ActionBlock.None;

    public string? CancelReason => ReasonOf(_extras.Actions?.Cancel);

    /// <summary>「強制取消」只在第二階段 spec §6.4 的條件成立時出現（Engine 算好，<see cref="RunActions.ShowForceCancel"/>）。</summary>
    public bool ShowForceCancel => ShowWriteActions && _extras.Actions!.ShowForceCancel;

    public bool CanForceCancel => BlockOf(_extras.Actions?.ForceCancel) == ActionBlock.None;

    public string? ForceCancelReason => ReasonOf(_extras.Actions?.ForceCancel);

    /// <summary>取消中（第二階段 spec §6.3）：狀態與時間欄的文字換成「取消中」，圖示與篩選不變（§9.3）。</summary>
    public bool IsCancelling => _extras.Cancelling is not null;

    /// <summary>報讀軟體讀的列名稱：狀態、標題、repo、workflow、觸發、觸發者、時間，已標記時加上「完成時通知我」。</summary>
    public string AutomationName { get => _automationName; private set => SetField(ref _automationName, value); }

    public Uri RunUri { get; private set; } = NotOpenable;

    /// <summary>複製用的 run 網址（API 給的原字串）。</summary>
    public string RunUrlText { get; private set; } = string.Empty;

    public Uri RepoActionsUri { get; private set; } = NotOpenable;

    public Uri CommitUri { get; private set; } = NotOpenable;

    public Uri? PullRequestUri { get; private set; }

    public Uri? TriggerUri { get; private set; }

    public Uri? BranchUri { get; private set; }

    /// <summary>每秒要重算時間文字的列：進行中（已執行時間、ETA）與排隊中（排隊時間）。</summary>
    public bool IsTicking => _state?.Display is DisplayState.InProgress or DisplayState.Queued;

    /// <summary>用快照的內容更新這一列；開始時間只在值改變時重算，其餘交給計時器（spec §10.8）。</summary>
    public void Update(RunRow row, RunRowExtras extras, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(extras);

        _row = row;
        _extras = extras;
        var run = row.Run;
        var repo = row.Repo;

        var previousCategory = _state?.Category;

        // 顯示狀態的文字用目前的語言：換語言之後、Engine 的下一份快照到之前，也不會留著舊的語言。
        // 取消中只換文字（狀態圖示的名稱與 tooltip、列的報讀名稱），顯示狀態不變，圖示與篩選照原本的分類（第二階段 spec §9.3）。
        var state = RunStateMapper.InLanguage(row.State, UiText.Current);
        State = extras.Cancelling is not null ? state with { Label = WriteText.Cancelling(UiText.Current) } : state;
        if (previousCategory != row.State.Category)
        {
            OnPropertyChanged(nameof(CanCopyFailedLog));
            OnPropertyChanged(nameof(CanWatch));
        }

        OnWriteActionsChanged();

        Title = DisplayFormatter.SingleLine(run.DisplayTitle);
        TitleTooltip = string.IsNullOrWhiteSpace(run.DisplayTitle) ? null : run.DisplayTitle.Trim();

        // 自己的 repo 只顯示名稱，組織與協作的 repo 顯示 owner/name，避免同名 repo 混淆。
        RepoFullName = repo.FullName;
        RepoName = repo.Category == RepoCategory.Own && !string.IsNullOrEmpty(repo.Name) ? repo.Name : repo.FullName;
        RepoActionsUri = WebLinks.RepoActions(repo);

        var workflow = string.Create(CultureInfo.InvariantCulture, $"{row.WorkflowName} #{run.RunNumber}").Trim();
        WorkflowSegment = Separator + workflow;

        UpdateTrigger(run, repo, extras.PullRequestNumbers);
        UpdateActor(run);

        AttemptText = run.RunAttempt > 1
            ? Loc.Format(Loc.Texts.RowAttempt, run.RunAttempt)
            : string.Empty;
        OnPropertyChanged(nameof(HasAttempt));

        UpdateBranch(run, repo);
        UpdateMetaTooltip();
        IsWatched = extras.IsWatched;

        RunUrlText = run.HtmlUrl ?? string.Empty;
        RunUri = Uri.TryCreate(RunUrlText, UriKind.Absolute, out var runUri) ? runUri : NotOpenable;
        CommitUri = WebLinks.Commit(repo, run.HeadSha);
        PullRequestUri = extras.PullRequestNumbers.Count > 0 ? WebLinks.PullRequest(repo, extras.PullRequestNumbers[0]) : null;
        HasPullRequest = PullRequestUri is not null;

        var start = run.RunStartedAt ?? run.CreatedAt;
        if (_startUtc != start)
        {
            _startUtc = start;
            RefreshStartTime();
        }

        RefreshElapsed(nowUtc);
    }

    /// <summary>換語言之後：用最近一次的快照內容重算所有文字（含開始時間），報讀名稱與選單的報讀名稱也一起更新。</summary>
    public void RefreshLanguage(DateTimeOffset nowUtc)
    {
        if (_row is { } row)
        {
            Update(row, _extras, nowUtc);
        }

        RefreshStartTime();
        OnPropertyChanged(nameof(WatchMenuAutomationName));
    }

    /// <summary>寬度斷點改變（spec §10.10）。</summary>
    public void SetLayout(RowLayout layout, DateTimeOffset nowUtc)
    {
        if (layout == _layout)
        {
            return;
        }

        _layout = layout;
        OnPropertyChanged(nameof(ShowStartTime));
        if (_row is not null)
        {
            UpdateActor(_row.Run);
            UpdateBranch(_row.Run, _row.Repo);
            RefreshElapsed(nowUtc);
        }
    }

    /// <summary>開始時間的相對文字（剛剛／N 分鐘前／今天 HH:mm…）與 tooltip 的完整本地時間。</summary>
    public void RefreshStartTime()
    {
        if (_startUtc is not { } start)
        {
            return;
        }

        StartText = _formatter.StartTime(start);
        StartTooltip = _formatter.FullLocal(start);
        UpdateAutomationName();
    }

    /// <summary>
    /// 耗時；進行中改成已執行時間、ETA 與細進度條；排隊中顯示排隊時間（spec §8）。
    /// 取消中（第二階段 spec §9.3）：進行中的預估剩餘（含細進度條）換成「取消中」、已執行時間照常；排隊中與等待中的時間文字換成「取消中」。
    /// </summary>
    public void RefreshElapsed(DateTimeOffset nowUtc)
    {
        if (_row is not { } row)
        {
            return;
        }

        var run = row.Run;
        var showDetail = _layout.ShowTimeDetail;
        var cancelling = _extras.Cancelling is not null;
        var progress = (double?)null;
        var overrun = false;

        // 時間文字是不是從開始時間量出來的（run_started_at 缺值時 tooltip 才要註明）；換成「取消中」的不是。
        var timed = true;
        switch (row.State.Display)
        {
            case DisplayState.InProgress when cancelling:
                var elapsed = Project(run, _extras.Eta, nowUtc).Elapsed;
                DurationText = showDetail
                    ? Loc.Format(Loc.Texts.DurationElapsed, DisplayFormatter.Duration(elapsed)) + Separator + WriteText.Cancelling(UiText.Current)
                    : DisplayFormatter.Duration(elapsed);
                break;
            case DisplayState.InProgress:
                var eta = Project(run, _extras.Eta, nowUtc);
                DurationText = showDetail ? DisplayFormatter.Running(eta) : DisplayFormatter.Duration(eta.Elapsed);
                progress = eta.Progress;
                overrun = eta.Overrun is { } over && over > TimeSpan.Zero;
                break;

            // 取消中一定是未完成（Engine 看到已完成就清掉）：排隊中、等待中，以及不認得的未完成狀態。
            case not DisplayState.InProgress when cancelling:
                DurationText = WriteText.Cancelling(UiText.Current);
                timed = false;
                break;
            case DisplayState.Queued:
                // 排隊中不預估，只顯示排隊時間（spec §8.2）：從 run_started_at 算，缺值時退回 created_at（DurationEstimator.QueuedDuration）。
                DurationText = DisplayFormatter.Queued(DurationEstimator.QueuedDuration(run, nowUtc));
                break;
            default:
                DurationText = _extras.CompletedDuration is { } duration ? DisplayFormatter.Duration(duration) : string.Empty;
                break;
        }

        // run_started_at 缺值時耗時從 created_at 起算，在 tooltip 註明（spec §8.1）。
        DurationTooltip = timed && run.RunStartedAt is null && DurationText.Length > 0
            ? Loc.Texts.RowStartedFromCreated
            : null;

        ShowProgress = showDetail && progress is not null;
        ProgressPercent = (progress ?? 0) * PercentScale;
        ProgressTone = overrun ? OverrunTone : null;
        UpdateAutomationName();
    }

    /// <summary>
    /// 確認對話框上寫明是哪一筆（第二階段 spec §9.2）：repo、workflow、run 編號、第幾次嘗試與分支（同分支 chip，fork 是 owner:branch）。
    /// 按下選單項目的當下呼叫：之後列的容器可能被回收給別筆 run。
    /// </summary>
    public ConfirmTarget ToConfirmTarget() => new(
        _repoFullName,
        _row?.WorkflowName ?? string.Empty,
        _row?.Run.RunNumber ?? 0,
        RunAttempt,
        _branchText.Length > 0 ? _branchText : null);

    /// <summary>報讀軟體讀 ListBoxItem 的名稱時，預設會是類別名稱，改給有意義的文字（同 SAM）。</summary>
    public override string ToString() => AutomationName;

    /// <summary>
    /// 用快照裡的中位數重算這一刻的已執行時間與 ETA：把中位數當成 EtaMinSamples 筆相同的樣本交給 DurationEstimator，
    /// 中位數不變，判斷（剩餘、超過、進度）就和 Engine 用的是同一套規則。沒有中位數時只有已執行時間。
    /// </summary>
    private static EtaInfo Project(RunInfo run, EtaInfo? snapshotEta, DateTimeOffset nowUtc)
    {
        var samples = snapshotEta?.Median is { } median
            ? Enumerable.Repeat(median, Tuning.EtaMinSamples).ToArray()
            : [];
        return DurationEstimator.Estimate(run, samples, nowUtc)
            ?? new EtaInfo(snapshotEta?.Elapsed ?? TimeSpan.Zero, null, null, null, null);
    }

    private void UpdateTrigger(RunInfo run, RepoInfo repo, IReadOnlyList<int> pullRequestNumbers)
    {
        var trigger = DisplayFormatter.Trigger(run, pullRequestNumbers.Count > 0 ? pullRequestNumbers[0] : null);
        TriggerUri = trigger.LinkKind switch
        {
            TriggerLinkKind.PullRequest when trigger.PullRequestNumber is { } number => WebLinks.PullRequest(repo, number),
            TriggerLinkKind.Commit => WebLinks.Commit(repo, run.HeadSha),
            _ => null,
        };

        HasTriggerLink = TriggerUri is not null;
        TriggerSeparator = trigger.Text.Length > 0 ? Separator : string.Empty;
        TriggerLinkText = HasTriggerLink ? trigger.Text : string.Empty;
        TriggerPlainText = HasTriggerLink ? string.Empty : trigger.Text;

        // 有多個 PR 時 tooltip 列出全部（spec §10.6 第 2 點）；push 的 commit 顯示完整 sha。
        TriggerTooltip = trigger.LinkKind switch
        {
            TriggerLinkKind.PullRequest when pullRequestNumbers.Count > 1 => Loc.Format(
                Loc.Texts.RowPullRequests,
                string.Join(Loc.Texts.CommonListSeparator, pullRequestNumbers.Select(n => "#" + n.ToString(CultureInfo.InvariantCulture)))),
            TriggerLinkKind.Commit => run.HeadSha,
            _ => null,
        };
    }

    private void UpdateActor(RunInfo run)
    {
        _actorText = DisplayFormatter.ActorText(run);
        ActorSegment = _layout.ShowActor && _actorText.Length > 0 ? Separator + _actorText : string.Empty;

        // triggering_actor 不同時 tooltip 加「重跑者」（spec §10.5）；actor 缺值時整段留白。
        ActorTooltip = _actorText.Length > 0
            && !string.IsNullOrWhiteSpace(run.TriggeringActorLogin)
            && !string.Equals(run.TriggeringActorLogin, run.ActorLogin, StringComparison.Ordinal)
                ? Loc.Format(Loc.Texts.RowRerunBy, run.TriggeringActorLogin)
                : null;
    }

    /// <summary>分支 chip：來源 repo 和本 repo 不同（fork）時顯示 <c>owner:branch</c>；head_branch 為 null 時不顯示（spec §10.5）。</summary>
    private void UpdateBranch(RunInfo run, RepoInfo repo)
    {
        if (string.IsNullOrEmpty(run.HeadBranch))
        {
            BranchText = string.Empty;
            BranchUri = null;
        }
        else
        {
            var owner = run.HeadRepoId != repo.Id ? OwnerOf(run.HeadRepoFullName) : null;
            BranchText = owner is null ? run.HeadBranch : owner + ForkBranchSeparator + run.HeadBranch;
            BranchUri = WebLinks.Branch(run, repo);
        }

        ShowBranchChip = _layout.ShowBranch && BranchText.Length > 0;
        OnPropertyChanged(nameof(CanOpenBranch));
    }

    /// <summary>
    /// 第二行各段（含被斷點隱藏的觸發者與分支）用「 · 」接起來；文字和畫面上的一樣（repo 名稱同畫面的寫法）。
    /// 每一段都是空的時候不給 tooltip（同 <see cref="TitleTooltip"/>），不會出現空白的提示框。
    /// </summary>
    private void UpdateMetaTooltip()
    {
        var parts = new[]
        {
            _repoName,
            _workflowSegment.Length > 0 ? _workflowSegment[Separator.Length..] : null,
            _triggerLinkText.Length > 0 ? _triggerLinkText : _triggerPlainText,
            _actorText,
            _attemptText,
            _branchText,
        };
        var text = string.Join(Separator, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        MetaTooltip = text.Length > 0 ? text : null;
    }

    private static string? OwnerOf(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return null;
        }

        var index = fullName.IndexOf(FullNameSeparator, StringComparison.Ordinal);
        return index > 0 ? fullName[..index] : null;
    }

    private void UpdateAutomationName()
    {
        var parts = new[]
        {
            _state?.Label,
            _title,
            _repoFullName,
            _workflowSegment.Length > 0 ? _workflowSegment[Separator.Length..] : null,
            _triggerLinkText.Length > 0 ? _triggerLinkText : _triggerPlainText,
            _actorText,
            _attemptText,
            _branchText,
            _startText,
            _durationText,

            // 已標記時報讀軟體要知道：列上的小鈴鐺是 Path，沒有 automation peer，選單項目也不提供勾選狀態。
            _isWatched ? Loc.Texts.RowWatched : null,
        };
        AutomationName = string.Join(Loc.Texts.CommonSpeechSeparator, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>
    /// 寫入項目的出現、能不能用與原因都由快照與送出中標記算出來：每次 Update 都通知改變，不只在判定類別改變時
    /// （送出中、取消中、權限、離線等都可能在類別不變時改變；換語言時原因的文字也要重讀）。
    /// </summary>
    private void OnWriteActionsChanged()
    {
        OnPropertyChanged(nameof(RunAttempt));
        OnPropertyChanged(nameof(ShowWriteActions));
        OnPropertyChanged(nameof(CanRerunFailed));
        OnPropertyChanged(nameof(RerunFailedReason));
        OnPropertyChanged(nameof(CanRerunAll));
        OnPropertyChanged(nameof(RerunAllReason));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CancelReason));
        OnPropertyChanged(nameof(ShowForceCancel));
        OnPropertyChanged(nameof(CanForceCancel));
        OnPropertyChanged(nameof(ForceCancelReason));
        OnPropertyChanged(nameof(IsCancelling));
    }

    /// <summary>
    /// 一個動作現在的原因代碼：送出中的標記優先（四個項目都停用，原因「送出中」，第二階段 spec §9.4），其次是快照的可操作性。
    /// 快照沒有這筆的可操作性時是 null：不能用（項目也不會出現）。
    /// </summary>
    private ActionBlock? BlockOf(ActionBlock? published) =>
        published is null ? null : _extras.Sending ? ActionBlock.InFlight : published;

    /// <summary>停用的原因文字（目前的語言）；可以用時是 null。</summary>
    private string? ReasonOf(ActionBlock? published) =>
        BlockOf(published) is { } block && block != ActionBlock.None ? WriteText.Block(block, UiText.Current) : null;

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
