using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using IsItGreenYet.App.Localization;
using IsItGreenYet.App.Services;
using IsItGreenYet.App.ViewModels;
using IsItGreenYet.Core.Engine;

namespace IsItGreenYet.App.Views;

/// <summary>
/// repo 管理頁（spec §11.1）。資料來自快照的 Repos，快照在 Engine 的背景執行緒發布，這裡只保留最新一份、轉回 UI 執行緒再套用。
/// 靜音與強制納入送 <see cref="EngineCommand.SetOverride"/>：Engine 寫入 DB、重新分類，下一份快照就反映在這裡與主清單（spec §11.1）。
/// </summary>
public partial class RepoManagerWindow : Window
{
    /// <summary>窄版的門檻（Controls.xaml），單位 DIP。</summary>
    private const string NameMinWidthKey = "Size.RepoRow.NameMinWidth";

    /// <summary>每一列（Style.ListBoxItem.Record）的內距與外框，算名稱欄寬度時扣掉（同主視窗）。</summary>
    private const string RowPaddingKey = "Padding.Record";

    private const string RowBorderKey = "Thickness.Border.Standard";

    private static readonly DependencyPropertyKey CompactRowsPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CompactRows), typeof(bool), typeof(RepoManagerWindow), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey RowMaxWidthPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(RowMaxWidth), typeof(double), typeof(RepoManagerWindow), new PropertyMetadata(double.PositiveInfinity));

    public static readonly DependencyProperty CompactRowsProperty = CompactRowsPropertyKey.DependencyProperty;

    public static readonly DependencyProperty RowMaxWidthProperty = RowMaxWidthPropertyKey.DependencyProperty;

    private readonly EngineFeed _engine;
    private readonly RepoManagerViewModel _viewModel = new();
    private readonly double _nameMinWidth;

    /// <summary>清單的 ScrollViewer（範本裡的，第一次用到時才找）。</summary>
    private ScrollViewer? _listScroll;

    /// <summary>背景執行緒送來、還沒套用的最新快照。</summary>
    private MonitorSnapshot? _incoming;

    /// <summary>已經排了一個「套用快照」到 UI 執行緒（0／1）；連續好幾份快照只套用最新的一份（同主視窗）。</summary>
    private int _applyScheduled;

    private bool _closed;

    internal RepoManagerWindow(EngineFeed engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));

        InitializeComponent();
        DataContext = _viewModel;
        _nameMinWidth = TryFindResource(NameMinWidthKey) is double nameMinWidth ? nameMinWidth : 0;

        // 先訂閱再讀最新的一份：兩者之間發布的快照會在之後套用，不會漏掉。
        _engine.SnapshotPublished += OnSnapshotPublished;
        if (_engine.LatestSnapshot is { } snapshot)
        {
            ApplySnapshot(snapshot);
        }

        // XAML 的固定文字由繫結即時更新；每一列、數量與空狀態的文字在這裡重算。
        Loc.Instance.LanguageChanged += _viewModel.RefreshLanguage;
    }

    /// <summary>
    /// 窄版：每一列的名稱與原因佔滿整列，類別、狀態與兩個按鈕換到下一行（Template.RepoRow 的觸發）。
    /// 名稱欄能用的寬度（<see cref="UpdateCompactRows"/>）比 Size.RepoRow.NameMinWidth 窄時為 true。
    /// 右邊四欄一律容得下目前語言最寬的標籤（XAML 的 ColumnSizer），所以結果只跟清單寬度與語言有關，切換之後不會又切回來。
    /// </summary>
    public bool CompactRows => (bool)GetValue(CompactRowsProperty);

    /// <summary>
    /// 每一列的最大寬度：清單的可見寬度，還沒量到之前不限制。
    /// 清單是虛擬化的：視窗一次縮窄很多時（例如 720 直接拉到 480），列變高、排不進快取範圍的列不會重新量，仍是舊的寬度，
    /// 清單又把每一列都排成最寬那一列的寬度，整頁的按鈕就被切掉（2026-10-26 測試殼：中文一次拉到 480，每一列寬 486、可見寬 435）。
    /// 可見寬度改變時更新它，每一列都依新的寬度重新量。
    /// </summary>
    public double RowMaxWidth => (double)GetValue(RowMaxWidthProperty);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 可縮放的無邊框視窗最大化時，限制在所在螢幕的工作區內（同主視窗，spec §10.1）。
        if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is { } source)
        {
            source.AddHook(Interop.WindowPlacement.FilterMessage);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _engine.SnapshotPublished -= OnSnapshotPublished;
        Loc.Instance.LanguageChanged -= _viewModel.RefreshLanguage;
        base.OnClosed(e);
    }

    private void OnSnapshotPublished(MonitorSnapshot snapshot)
    {
        Interlocked.Exchange(ref _incoming, snapshot);
        if (Interlocked.Exchange(ref _applyScheduled, 1) == 0)
        {
            // Dispatcher 已經關閉（程式正在結束）時不會丟例外，操作直接被放棄。
            Dispatcher.InvokeAsync(ApplyIncomingSnapshot, DispatcherPriority.Background);
        }
    }

    private void ApplyIncomingSnapshot()
    {
        Interlocked.Exchange(ref _applyScheduled, 0);
        if (Interlocked.Exchange(ref _incoming, null) is { } snapshot && !_closed)
        {
            ApplySnapshot(snapshot);
        }
    }

    /// <summary>套用失敗時不交給 DispatcherUnhandledException（每次都跳對話框）：寫 error.log、橫幅提示（同主視窗，spec §15）。</summary>
    private void ApplySnapshot(MonitorSnapshot snapshot)
    {
        try
        {
            _viewModel.Apply(snapshot);
        }
        catch (Exception ex)
        {
            _engine.ReportError("更新 repo 管理頁", ex);
        }
    }

    /// <summary>
    /// 「靜音／取消靜音」：保留強制納入值（spec §5.2 靜音是第 1 條，取消靜音後強制納入照常有效）。
    /// 以送出中、快照還沒反映的值為基礎（見 <see cref="RepoRowViewModel.ToggleMute"/>）。
    /// </summary>
    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            _engine.Post(new EngineCommand.SetOverride(row.RepoId, row.ToggleMute()));
        }
    }

    /// <summary>「強制納入／取消強制納入」：保留靜音值。不能強制納入時按鈕是停用的，這裡再檢查一次。</summary>
    private void OnForceClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { CanToggleForce: true } row)
        {
            _engine.Post(new EngineCommand.SetOverride(row.RepoId, row.ToggleForce()));
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>撐欄寬那一列（ColumnSizer）的第 0 欄寬度變了，也就是清單寬度或四欄的寬度（語言）變了：重新判斷窄版。</summary>
    private void OnSizerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateCompactRows();
        }
    }

    /// <summary>
    /// 每一列的名稱欄能用的寬度：清單 ScrollViewer 的內側寬度一律扣掉直向捲軸，再扣掉每一列的內距與外框、右邊四欄（ColumnSizer）。
    /// 只看清單的寬度與語言，不看清單畫出了哪幾列、也不看捲軸當下有沒有出現（同主視窗的寬度斷點，MainWindow.UpdateListWidth）：
    /// 寬版與窄版的列高不同，捲軸會跟著出現或消失，看實際的列寬時，在門檻附近會「切成窄版 → 捲軸出現或消失 → 名稱欄寬度變了 →
    /// 切回寬版」來回切換（2026-10-26 測試殼：中文 7 個 repo、視窗寬 550，同一次排版裡切了 7 次）。
    /// </summary>
    private void UpdateCompactRows()
    {
        _listScroll ??= MainWindow.FindDescendant<ScrollViewer>(RepoList);
        if (_listScroll is not { ActualWidth: > 0 } scroll)
        {
            return;
        }

        var nameWidth = scroll.ActualWidth - scroll.Padding.Left - scroll.Padding.Right
            - MainWindow.VerticalScrollBarWidth(scroll)
            - HorizontalOf(RowPaddingKey) - HorizontalOf(RowBorderKey)
            - ColumnSizer.ColumnDefinitions.Skip(1).Sum(column => column.ActualWidth);
        SetValue(CompactRowsPropertyKey, nameWidth < _nameMinWidth);
    }

    private double HorizontalOf(string thicknessKey) =>
        TryFindResource(thicknessKey) is Thickness thickness ? thickness.Left + thickness.Right : 0;

    /// <summary>清單的可見寬度變了（拉視窗、直向捲軸出現或消失）：每一列的最大寬度跟著改（見 <see cref="RowMaxWidth"/>）。</summary>
    private void OnRepoListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 && e.ViewportWidth > 0
            && e.OriginalSource is ScrollViewer { TemplatedParent: var owner } && ReferenceEquals(owner, RepoList))
        {
            SetValue(RowMaxWidthPropertyKey, e.ViewportWidth);
        }
    }

    /// <summary>Ctrl+F 到搜尋框；Esc 在搜尋框有文字時清空，否則關閉視窗（同 SAM 對話框的 Esc）。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape when Keyboard.Modifiers == ModifierKeys.None:
                if (SearchBox.IsKeyboardFocusWithin && !_viewModel.IsSearchEmpty)
                {
                    _viewModel.SearchText = string.Empty;
                }
                else
                {
                    Close();
                }

                e.Handled = true;
                break;
        }
    }

    private static RepoRowViewModel? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as RepoRowViewModel;
}
