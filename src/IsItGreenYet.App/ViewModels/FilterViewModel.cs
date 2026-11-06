using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using IsItGreenYet.App.Localization;
using IsItGreenYet.Core.Filtering;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.App.ViewModels;

/// <summary>篩選列的 6 個多選欄位（spec §10.4），順序即畫面上的順序。</summary>
internal enum FilterField
{
    Repo,
    Workflow,
    Event,
    Status,
    Branch,
    Actor,
}

/// <summary>多選下拉裡的一個值。</summary>
internal sealed class FilterOptionViewModel : INotifyPropertyChanged
{
    private readonly Action _changed;
    private bool _isChecked;
    private string _text;

    public FilterOptionViewModel(string value, string text, bool isChecked, Action changed)
    {
        Value = value;
        _text = text;
        _isChecked = isChecked;
        _changed = changed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>比對用的原始值（repo full_name、workflow 名稱、event、DisplayState 名稱、分支、actor）。</summary>
    public string Value { get; }

    /// <summary>畫面上的文字；Status 用 §7.1 的顯示狀態（依目前的語言），其他就是原始值。</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }

            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            _changed();
        }
    }

    /// <summary>載入偏好或清除篩選時用：只改值、不觸發篩選（由呼叫端統一通知一次）。</summary>
    internal void SetCheckedSilently(bool value)
    {
        if (_isChecked == value)
        {
            return;
        }

        _isChecked = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
    }

    public override string ToString() => Text;
}

/// <summary>一個多選欄位：選項依目前清單動態產生，已勾選、但清單裡已經沒有的值仍保留顯示（spec §10.4）。</summary>
internal sealed class FilterFieldViewModel : INotifyPropertyChanged
{
    private const string SelectedTag = "Selected";

    private readonly Comparison<FilterOptionViewModel> _order;
    private readonly Func<string, string> _textOf;
    private readonly Func<string> _nameOf;
    private int _selectedCount;

    /// <param name="name">欄位名稱（依目前的語言）。</param>
    /// <param name="textOf">選項的文字（依目前的語言）。</param>
    public FilterFieldViewModel(FilterField field, Func<string> name, Comparison<FilterOptionViewModel> order, Func<string, string> textOf)
    {
        Field = field;
        _nameOf = name;
        _order = order;
        _textOf = textOf;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>勾選改變（使用者點選項時）。</summary>
    public event Action? SelectionChanged;

    public FilterField Field { get; }

    /// <summary>欄位名稱（spec §10.4 原文：Repo、Workflow、Event、Status、Branch、Actor；兩種語言相同，仍從字串表讀）。</summary>
    public string Name => _nameOf();

    /// <summary>UI Automation 用的穩定識別碼。</summary>
    public string AutomationId => "Filter" + Field;

    public ObservableCollection<FilterOptionViewModel> Options { get; } = [];

    /// <summary>按鈕文字：有勾選時加上數量，例如「Repo（2）」（英文 <c>Repo (2)</c>）。</summary>
    public string Label => _selectedCount == 0
        ? Name
        : Loc.Format(Loc.Texts.FilterWithCount, Name, _selectedCount);

    /// <summary>有勾選任何值時用 Chip 的選取外觀（Style.ToggleButton.Filter 的 Tag 合約）。</summary>
    public string? TagValue => _selectedCount == 0 ? null : SelectedTag;

    public bool HasOptions => Options.Count > 0;

    public bool HasSelection => _selectedCount > 0;

    /// <summary>ItemsControl 的項目報讀名稱用 ToString；不覆寫會唸出類別全名。</summary>
    public override string ToString() => Label;

    public IReadOnlyList<string> SelectedValues => [.. Options.Where(option => option.IsChecked).Select(option => option.Value)];

    /// <summary>載入偏好：這些值先以「已勾選」顯示，之後的快照再補上其他選項。</summary>
    public void Load(IEnumerable<string> selected) =>
        Sync(selected.Distinct(StringComparer.Ordinal).Select(value => (value, true)));

    /// <summary>依目前清單更新選項（保留勾選狀態與已勾選但不在清單中的值）。缺值（null、空白）不成為選項。</summary>
    public void UpdateAvailable(IEnumerable<string?> available)
    {
        var current = Options.Where(option => option.IsChecked).Select(option => option.Value).ToHashSet(StringComparer.Ordinal);
        var values = available
            .OfType<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Concat(current)
            .Distinct(StringComparer.Ordinal)
            .Select(value => (value, current.Contains(value)));
        Sync(values);
    }

    /// <summary>換語言之後：欄位名稱、按鈕文字與選項文字重新讀；選項的順序依新的文字重排。</summary>
    public void RefreshLanguage()
    {
        foreach (var option in Options)
        {
            option.Text = _textOf(option.Value);
        }

        Sync(Options.Select(option => (option.Value, option.IsChecked)).ToList());
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Label));
    }

    /// <summary>全部取消勾選，不觸發篩選（由 FilterViewModel 統一通知）。</summary>
    public void ClearSilently()
    {
        foreach (var option in Options)
        {
            option.SetCheckedSilently(false);
        }

        RecountSelection();
    }

    /// <summary>
    /// 原地更新選項集合：沿用同值的選項實例，只做必要的新增、移除與移動，下拉開著時不會整份重建。
    /// </summary>
    private void Sync(IEnumerable<(string Value, bool IsChecked)> values)
    {
        var existing = Options.ToDictionary(option => option.Value, StringComparer.Ordinal);
        var target = new List<FilterOptionViewModel>();
        foreach (var (value, isChecked) in values)
        {
            if (!existing.TryGetValue(value, out var option))
            {
                option = new FilterOptionViewModel(value, _textOf(value), isChecked, OnOptionChanged);
            }

            target.Add(option);
        }

        target.Sort(_order);
        CollectionSync.Apply(Options, target);
        RecountSelection();
        OnPropertyChanged(nameof(HasOptions));
    }

    private void OnOptionChanged()
    {
        RecountSelection();
        SelectionChanged?.Invoke();
    }

    private void RecountSelection()
    {
        var count = Options.Count(option => option.IsChecked);
        if (count == _selectedCount)
        {
            return;
        }

        _selectedCount = count;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(TagValue));
        OnPropertyChanged(nameof(HasSelection));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 篩選列（spec §10.4）：6 個多選欄位（同欄位 OR、跨欄位 AND）、文字搜尋、只看需要注意。
/// 篩選偏好（不含搜尋文字）存進 settings.json；篩選只影響清單，不影響摘要列與系統匣。
/// </summary>
internal sealed class FilterViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<FilterField, FilterFieldViewModel> _fields;

    /// <summary>settings.json 裡認不得的 Status 名稱：不顯示、不參與篩選，但存檔時原樣保留。</summary>
    private readonly List<string> _unparsedStatuses = [];

    private string _searchText = string.Empty;
    private bool _attentionOnly;

    public FilterViewModel(FilterPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        Fields =
        [
            TextField(FilterField.Repo, () => Loc.Texts.FilterRepo),
            TextField(FilterField.Workflow, () => Loc.Texts.FilterWorkflow),
            TextField(FilterField.Event, () => Loc.Texts.FilterEvent),
            new FilterFieldViewModel(FilterField.Status, () => Loc.Texts.FilterStatus, CompareStatus, StatusText),
            TextField(FilterField.Branch, () => Loc.Texts.FilterBranch),
            TextField(FilterField.Actor, () => Loc.Texts.FilterActor),
        ];
        _fields = Fields.ToDictionary(field => field.Field);

        _fields[FilterField.Repo].Load(preferences.Repos ?? []);
        _fields[FilterField.Workflow].Load(preferences.Workflows ?? []);
        _fields[FilterField.Event].Load(preferences.Events ?? []);
        _fields[FilterField.Branch].Load(preferences.Branches ?? []);
        _fields[FilterField.Actor].Load(preferences.Actors ?? []);

        var statuses = new List<string>();
        foreach (var name in preferences.Statuses ?? [])
        {
            if (TryParseStatus(name, out _))
            {
                statuses.Add(name);
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                _unparsedStatuses.Add(name);
            }
        }

        _fields[FilterField.Status].Load(statuses);
        _attentionOnly = preferences.AttentionOnly;

        foreach (var field in Fields)
        {
            field.SelectionChanged += () => RaiseChanged(persist: true);
            field.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FilterFieldViewModel.HasSelection))
                {
                    OnPropertyChanged(nameof(HasActiveFilter));
                }
            };
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>篩選條件改變。參數為 true 時是要存進 settings.json 的偏好（搜尋文字不保存，spec §10.4）。</summary>
    public event Action<bool>? Changed;

    public IReadOnlyList<FilterFieldViewModel> Fields { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            value ??= string.Empty;
            if (_searchText == value)
            {
                return;
            }

            _searchText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSearchEmpty));
            OnPropertyChanged(nameof(HasActiveFilter));
            RaiseChanged(persist: false);
        }
    }

    public bool IsSearchEmpty => _searchText.Length == 0;

    /// <summary>只看需要注意：追蹤中的失敗，加上所有未完成與等待核准的 run（Q13）。</summary>
    public bool AttentionOnly
    {
        get => _attentionOnly;
        set
        {
            if (_attentionOnly == value)
            {
                return;
            }

            _attentionOnly = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasActiveFilter));
            RaiseChanged(persist: true);
        }
    }

    /// <summary>任何篩選或搜尋在作用中（決定「清除篩選」按鈕要不要出現）。</summary>
    public bool HasActiveFilter =>
        _attentionOnly || !string.IsNullOrWhiteSpace(_searchText) || Fields.Any(item => item.HasSelection);

    public RunFilterCriteria ToCriteria()
    {
        var statuses = new HashSet<DisplayState>();
        foreach (var name in _fields[FilterField.Status].SelectedValues)
        {
            if (TryParseStatus(name, out var state))
            {
                statuses.Add(state);
            }
        }

        return new RunFilterCriteria(
            Selected(FilterField.Repo),
            Selected(FilterField.Workflow),
            Selected(FilterField.Event),
            statuses,
            Selected(FilterField.Branch),
            Selected(FilterField.Actor),
            _searchText,
            _attentionOnly);
    }

    public FilterPreferences ToPreferences() => new()
    {
        Repos = [.. _fields[FilterField.Repo].SelectedValues],
        Workflows = [.. _fields[FilterField.Workflow].SelectedValues],
        Events = [.. _fields[FilterField.Event].SelectedValues],
        Statuses = [.. _fields[FilterField.Status].SelectedValues, .. _unparsedStatuses],
        Branches = [.. _fields[FilterField.Branch].SelectedValues],
        Actors = [.. _fields[FilterField.Actor].SelectedValues],
        AttentionOnly = _attentionOnly,
    };

    /// <summary>選項依目前主清單（未篩選）動態產生（spec §10.4）。</summary>
    public void UpdateOptions(IReadOnlyList<RunRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        _fields[FilterField.Repo].UpdateAvailable(rows.Select(row => row.Repo.FullName));
        _fields[FilterField.Workflow].UpdateAvailable(rows.Select(row => row.WorkflowName));
        _fields[FilterField.Event].UpdateAvailable(rows.Select(row => row.Run.Event));
        _fields[FilterField.Status].UpdateAvailable(rows.Select(row => row.State.Display.ToString()));
        _fields[FilterField.Branch].UpdateAvailable(rows.Select(row => row.Run.HeadBranch));
        _fields[FilterField.Actor].UpdateAvailable(rows.Select(row => row.Run.ActorLogin));
    }

    /// <summary>換語言之後：各欄位的名稱、數量與 Status 選項的文字換成新的語言（勾選與篩選條件不變）。</summary>
    public void RefreshLanguage()
    {
        foreach (var field in Fields)
        {
            field.RefreshLanguage();
        }
    }

    /// <summary>「清除篩選」：6 個欄位、只看需要注意與搜尋文字一起清掉，只通知一次。</summary>
    public void ClearAll()
    {
        foreach (var field in Fields)
        {
            field.ClearSilently();
        }

        _attentionOnly = false;
        _searchText = string.Empty;
        OnPropertyChanged(nameof(AttentionOnly));
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(IsSearchEmpty));
        OnPropertyChanged(nameof(HasActiveFilter));
        RaiseChanged(persist: true);
    }

    private static FilterFieldViewModel TextField(FilterField field, Func<string> name) =>
        new(field, name, CompareText, value => value);

    /// <summary>不分大小寫排序；只差大小寫的兩個值再用 Ordinal 分出固定順序。</summary>
    private static int CompareText(FilterOptionViewModel x, FilterOptionViewModel y)
    {
        var result = StringComparer.OrdinalIgnoreCase.Compare(x.Text, y.Text);
        return result != 0 ? result : StringComparer.Ordinal.Compare(x.Text, y.Text);
    }

    /// <summary>Status 依 §7.1 的顯示順序（DisplayState 的宣告順序）。</summary>
    private static int CompareStatus(FilterOptionViewModel x, FilterOptionViewModel y)
    {
        var xKnown = TryParseStatus(x.Value, out var xState);
        var yKnown = TryParseStatus(y.Value, out var yState);
        return xKnown && yKnown ? xState.CompareTo(yState) : CompareText(x, y);
    }

    /// <summary>
    /// Status 用 §7.1 的顯示狀態（依目前的語言），不含括號裡的細分：例如「失敗（逾時）」一律是「失敗」
    /// （細分由原字串決定，這裡沒有原字串，所以就是主要的字）。
    /// </summary>
    private static string StatusText(string name) =>
        TryParseStatus(name, out var state) ? RunStateMapper.Label(state, rawValue: null, UiText.Current) : name;

    /// <summary>只認 DisplayState 的名稱（分大小寫，同設定檔的列舉規則）；數字字串不算。</summary>
    private static bool TryParseStatus(string? name, out DisplayState state)
    {
        state = default;
        return !string.IsNullOrWhiteSpace(name)
            && !char.IsAsciiDigit(name[0])
            && Enum.TryParse(name, ignoreCase: false, out state)
            && Enum.IsDefined(state);
    }

    private IReadOnlySet<string> Selected(FilterField field) =>
        _fields[field].SelectedValues.ToHashSet(StringComparer.Ordinal);

    private void RaiseChanged(bool persist) => Changed?.Invoke(persist);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
