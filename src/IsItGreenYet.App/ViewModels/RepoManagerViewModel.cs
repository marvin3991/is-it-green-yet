using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using IsItGreenYet.App.Localization;
using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.App.ViewModels;

/// <summary>
/// repo 管理頁的一列（spec §11.1）：repo 名稱、類別、狀態、原因、操作。
/// 以 repo id 為鍵沿用同一個實例，快照更新時只改內容。
/// 類別、狀態、按鈕（spec §11.1 原文）與不能強制納入的說明（spec 沒有給文字，用最短的說明）都用目前的語言；
/// 換語言時 <see cref="RepoManagerViewModel.RefreshLanguage"/> 呼叫 <see cref="RefreshLanguage"/>。
/// </summary>
internal sealed class RepoRowViewModel : INotifyPropertyChanged
{
    private const string WordSeparator = " ";

    private RepoStatusRow? _row;
    private string _fullName = string.Empty;
    private string _categoryText = string.Empty;
    private bool _isMonitored;
    private string _reasonText = string.Empty;
    private RepoOverride _override = RepoOverride.None;
    private bool _canForceInclude;
    private SkipReason _reason;

    /// <summary>
    /// 已經送給 Engine、快照還沒反映的值；null 表示沒有。SetOverride 是整筆覆寫，快照回來前在同一列再按另一顆按鈕時，
    /// 要以這個值為基礎，才不會蓋掉前一次的變更。快照的 Override 等於它時清掉。
    /// </summary>
    private RepoOverride? _pending;

    public RepoRowViewModel(long repoId)
    {
        RepoId = repoId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long RepoId { get; }

    public string FullName => _fullName;

    public string CategoryText => _categoryText;

    public bool IsMonitored => _isMonitored;

    public string StatusText => _isMonitored ? Loc.Texts.ReposMonitored : Loc.Texts.ReposSkipped;

    /// <summary>
    /// 略過時是 spec §5.2 的原因文字（RepoDiscovery.SkipReasonText）；監看中時是 §5.4、§6.4 的問題標示（problem_note），沒有就留白。
    /// </summary>
    public string ReasonText => _reasonText;

    public bool HasReason => _reasonText.Length > 0;

    public RepoOverride Override => _override;

    public string MuteButtonText => Effective.Muted ? Loc.Texts.ReposUnmute : Loc.Texts.ReposMute;

    /// <summary>已強制納入時一律顯示「取消強制納入」：命中第 1～3、7、8 條時 CanForceInclude 是 false，但殘留的強制納入要能取消（handoff Task 11）。</summary>
    public string ForceButtonText => Effective.ForceIncluded ? Loc.Texts.ReposUnforce : Loc.Texts.ReposForce;

    /// <summary>
    /// 已靜音的 repo 不需要強制納入（spec §5.2 第 1 條）：快照的 CanForceInclude 已經是 false；
    /// 剛送出「靜音」、快照還沒反映時也照這個判斷。
    /// </summary>
    public bool CanToggleForce => Effective.ForceIncluded || (_canForceInclude && !Effective.Muted);

    /// <summary>不能強制納入時說明原因（spec §11.1、Q30）；按鈕可以按時沒有 tooltip。</summary>
    public string? ForceTooltip => CanToggleForce ? null : NotForcibleReason();

    /// <summary>報讀名稱各段之間的分隔同主清單的列。</summary>
    public string AutomationName => string.Join(Loc.Texts.CommonSpeechSeparator,
        new[] { _fullName, _categoryText, StatusText, _reasonText }.Where(part => part.Length > 0));

    /// <summary>每一列都有同名的按鈕，報讀名稱加上 repo 名稱才分得出是哪一列的。</summary>
    public string MuteAutomationName => MuteButtonText + WordSeparator + _fullName;

    public string ForceAutomationName => ForceButtonText + WordSeparator + _fullName;

    /// <summary>按鈕顯示與下一次切換的基礎：有送出中的值就用它，否則用快照的值。</summary>
    private RepoOverride Effective => _pending ?? _override;

    /// <summary>「靜音／取消靜音」：只切換靜音、保留強制納入，記成送出中的值，回傳要送給 Engine 的值。</summary>
    public RepoOverride ToggleMute() => SetPending(Effective with { Muted = !Effective.Muted });

    /// <summary>「強制納入／取消強制納入」：只切換強制納入、保留靜音，記成送出中的值，回傳要送給 Engine 的值。</summary>
    public RepoOverride ToggleForce() => SetPending(Effective with { ForceIncluded = !Effective.ForceIncluded });

    /// <summary>搜尋比對 repo 的 full_name，不分大小寫。</summary>
    public bool Matches(string search) =>
        search.Length == 0 || _fullName.Contains(search, StringComparison.OrdinalIgnoreCase);

    public void Update(RepoStatusRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        _row = row;
        var repo = row.Repo;
        var text = Loc.Texts;
        var changed = SetField(ref _fullName, repo.FullName, nameof(FullName));

        // fork 不論出現在哪個清單都算 fork（spec §5.1）。
        changed |= SetField(ref _categoryText, repo.IsFork
            ? text.ReposCategoryFork
            : repo.Category switch
            {
                RepoCategory.Own => text.ReposCategoryOwn,
                RepoCategory.Organization => text.ReposCategoryOrganization,
                _ => text.ReposCategoryCollaborator,
            }, nameof(CategoryText));
        changed |= SetField(ref _isMonitored, row.Monitored, nameof(IsMonitored));

        // 問題標示（problem_note）由 Engine 依它設定的語言產生；略過原因依目前的語言。
        changed |= SetField(ref _reasonText, row.Monitored ? row.ProblemNote ?? string.Empty : RepoDiscovery.SkipReasonText(row.Reason), nameof(ReasonText));
        changed |= SetField(ref _reason, row.Reason, propertyName: null);
        changed |= SetField(ref _override, row.Override, nameof(Override));
        changed |= SetField(ref _canForceInclude, row.CanForceInclude, propertyName: null);

        // 送出的值已經反映在快照上：之後以快照為準。只反映了前一個指令的快照（後一個還在排隊）不清掉。
        if (_pending is { } pending && pending == row.Override)
        {
            _pending = null;
            changed = true;
        }

        // 快照每一步都會發布：內容沒變時不通知，畫面不必重新繫結。
        if (!changed)
        {
            return;
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasReason));
        OnPropertyChanged(nameof(AutomationName));
        OnButtonsChanged();
    }

    /// <summary>換語言之後：類別與略過原因用新的語言重算，所有繫結的文字（狀態、按鈕、tooltip、報讀名稱）重新讀。</summary>
    public void RefreshLanguage()
    {
        if (_row is { } row)
        {
            Update(row);
        }

        OnPropertyChanged(string.Empty);
    }

    private RepoOverride SetPending(RepoOverride value)
    {
        _pending = value;
        OnButtonsChanged();
        return value;
    }

    private void OnButtonsChanged()
    {
        OnPropertyChanged(nameof(MuteButtonText));
        OnPropertyChanged(nameof(ForceButtonText));
        OnPropertyChanged(nameof(CanToggleForce));
        OnPropertyChanged(nameof(ForceTooltip));
        OnPropertyChanged(nameof(MuteAutomationName));
        OnPropertyChanged(nameof(ForceAutomationName));
    }

    /// <summary>略過但沒有原因（Engine 還沒分類完）時不說明，避免寫出不正確的原因。</summary>
    private string? NotForcibleReason()
    {
        var text = Loc.Texts;
        if (Effective.Muted)
        {
            return text.ReposMutedForceTooltip;
        }

        return _reason switch
        {
            // 剛送出「取消靜音」、快照還沒反映：還不知道新的分類，不說明。
            SkipReason.Muted => null,
            SkipReason.None => _isMonitored ? text.ReposMonitoredForceTooltip : null,

            // 封存、停用、沒有 workflow、workflow 都已停用（Q30）。
            _ => Loc.Format(text.ReposNotForcible, RepoDiscovery.SkipReasonText(_reason)),
        };
    }

    /// <param name="propertyName">要通知的屬性；null 表示這個欄位只影響衍生的屬性（由呼叫端一起通知）。</param>
    /// <returns>值有改變。</returns>
    private bool SetField<T>(ref T field, T value, string? propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        if (propertyName is not null)
        {
            OnPropertyChanged(propertyName);
        }

        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// repo 管理頁（spec §11.1）：列出所有看得到的 repo（MonitorSnapshot.Repos，清單 A＋B），頂端可篩「只看略過」與文字搜尋。
/// 只在 UI 執行緒使用；快照由視窗轉回 UI 執行緒後交給 <see cref="Apply"/>。
/// 空狀態（spec 沒有給文字，用最短的說明）與數量用目前的語言；換語言時視窗呼叫 <see cref="RefreshLanguage"/>。
/// </summary>
internal sealed class RepoManagerViewModel : INotifyPropertyChanged
{
    private const string Separator = " · ";

    private readonly Dictionary<long, RepoRowViewModel> _rowsById = [];
    private MonitorSnapshot? _snapshot;
    private string _searchText = string.Empty;
    private bool _skippedOnly;
    private string _countText = string.Empty;
    private string _emptyText = Loc.Texts.CommonLoading;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>畫面上的列（已套用搜尋與「只看略過」），順序同快照（依名稱）。</summary>
    public ObservableCollection<RepoRowViewModel> Rows { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            var text = value ?? string.Empty;
            if (_searchText == text)
            {
                return;
            }

            _searchText = text;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSearchEmpty));
            SyncVisible();
        }
    }

    public bool IsSearchEmpty => _searchText.Length == 0;

    public bool SkippedOnly
    {
        get => _skippedOnly;
        set
        {
            if (_skippedOnly == value)
            {
                return;
            }

            _skippedOnly = value;
            OnPropertyChanged();
            SyncVisible();
        }
    }

    /// <summary>「共 N 個 repo · 監看中 M · 略過 K」（不受搜尋與篩選影響）。</summary>
    public string CountText { get => _countText; private set => SetField(ref _countText, value); }

    public bool HasEmptyState => Rows.Count == 0;

    public string EmptyText { get => _emptyText; private set => SetField(ref _emptyText, value); }

    public RepoRowViewModel? Find(long repoId) => _rowsById.GetValueOrDefault(repoId);

    /// <summary>Engine 發布的新快照（已在 UI 執行緒上）。</summary>
    public void Apply(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _snapshot = snapshot;
        var seen = new HashSet<long>();
        foreach (var row in snapshot.Repos)
        {
            var id = row.Repo.Id;
            if (!seen.Add(id))
            {
                continue;
            }

            if (!_rowsById.TryGetValue(id, out var item))
            {
                item = new RepoRowViewModel(id);
                _rowsById[id] = item;
            }

            item.Update(row);
        }

        foreach (var id in _rowsById.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _rowsById.Remove(id);
        }

        UpdateCount();
        SyncVisible();
    }

    /// <summary>換語言之後：每一列、數量與空狀態的文字換成新的語言。</summary>
    public void RefreshLanguage()
    {
        foreach (var row in _rowsById.Values)
        {
            row.RefreshLanguage();
        }

        if (_snapshot is not null)
        {
            UpdateCount();
        }

        SyncVisible();
    }

    /// <summary>「共 N 個 repo · 監看中 M · 略過 K」（英文 <c>N repos · M monitored · K skipped</c>）。</summary>
    private void UpdateCount()
    {
        var text = Loc.Texts;
        var monitored = _rowsById.Values.Count(row => row.IsMonitored);
        CountText = string.Join(Separator,
            UiText.Plural(_rowsById.Count, text.ReposCountTotalOne, text.ReposCountTotalOther),
            Loc.Format(text.ReposCountMonitored, monitored),
            Loc.Format(text.ReposCountSkipped, _rowsById.Count - monitored));
    }

    /// <summary>依快照順序（Engine 已依名稱排序）套用搜尋與「只看略過」，原地更新畫面上的列。</summary>
    private void SyncVisible()
    {
        var search = _searchText.Trim();
        var target = new List<RepoRowViewModel>();
        var seen = new HashSet<long>();
        foreach (var row in _snapshot?.Repos ?? [])
        {
            if (seen.Add(row.Repo.Id)
                && _rowsById.TryGetValue(row.Repo.Id, out var item)
                && (!_skippedOnly || !item.IsMonitored)
                && item.Matches(search))
            {
                target.Add(item);
            }
        }

        if (!CollectionSync.SameSequence(Rows, target))
        {
            CollectionSync.Apply(Rows, target);
        }

        // 還沒有快照，或 repo 清單還沒抓完一輪而且一個都沒有：正在載入（同主畫面的判斷）。
        var text = Loc.Texts;
        EmptyText = _snapshot is null || (_snapshot.Repos.Count == 0 && !_snapshot.RepoListFetched)
            ? text.CommonLoading
            : _rowsById.Count == 0 ? text.ReposNoRepos : text.ReposNoMatch;
        OnPropertyChanged(nameof(HasEmptyState));
    }

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
