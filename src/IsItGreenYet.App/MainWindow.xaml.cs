using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using IsItGreenYet.App.Localization;
using IsItGreenYet.App.Services;
using IsItGreenYet.App.ViewModels;
using IsItGreenYet.App.Views;
using IsItGreenYet.Core;
using IsItGreenYet.Core.Display;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using Forms = System.Windows.Forms;

namespace IsItGreenYet.App;

/// <summary>
/// 主畫面（spec §10）。快照在 Engine 的背景執行緒發布，這裡只保留最新一份、轉回 UI 執行緒再交給 <see cref="MainViewModel"/>。
/// 和 App 殼的合約：<c>internal MainWindow(AppHost)</c>、<see cref="BringToFront"/>、SourceInitialized 由 App 訂閱。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>設計選擇：訊息條停留的時間（同 SAM 主視窗的訊息條）。</summary>
    private static readonly TimeSpan SnackbarDuration = TimeSpan.FromSeconds(4);

    private const string AccentBrushKey = "Brush.Accent";
    private const string RunMenuKey = "Menu.Run";
    private const string RowPaddingKey = "Padding.Record";
    private const string RowBorderKey = "Thickness.Border.Standard";
    private const string VerticalScrollBarPart = "PART_VerticalScrollBar";

    /// <summary>WinForms Clipboard.SetDataObject 自己的重試次數與間隔：0 表示只試一次，重試由 TrySetClipboardAsync 做。</summary>
    private const int FrameworkClipboardRetries = 0;

    /// <summary>只試一次時框架不會等待，間隔給 0。</summary>
    private const int FrameworkClipboardRetryDelayMs = 0;

    private readonly AppHost _host;
    private readonly FilterViewModel _filter;
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly DispatcherTimer _relativeTimer;
    private readonly DispatcherTimer _snackbarTimer;

    /// <summary>程式結束時取消進行中的使用者操作（複製失敗 log、寫入）。</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>背景執行緒送來、還沒套用的最新快照。</summary>
    private MonitorSnapshot? _incoming;

    /// <summary>已經排了一個「套用快照」到 UI 執行緒（0／1）；連續好幾份快照只套用最新的一份。</summary>
    private int _applyScheduled;

    /// <summary>視窗隱藏期間收到的最新快照：顯示時才套用，收在系統匣時不做任何畫面更新。</summary>
    private MonitorSnapshot? _hiddenSnapshot;

    private ScrollViewer? _listScroll;
    private bool _exiting;
    private bool _closed;
    private bool _detached;
    private bool _trayAvailable;

    /// <summary>最小化之前是不是最大化：最小化中結束時，還原時要回到最大化。</summary>
    private bool _restoreToMaximized;

    /// <summary>儲存的狀態是最大化、還沒套用：等 HWND 在還原後的一般位置建好（OnSourceInitialized）才切成最大化。</summary>
    private bool _maximizeWhenSourceReady;

    internal MainWindow(AppHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Navigator = new WindowNavigator(host);
        _filter = new FilterViewModel(host.Settings.Filters);
        _viewModel = new MainViewModel(TimeProvider.System, _filter, host.StartupWarning, host.Settings.RefreshMode);

        InitializeComponent();
        DataContext = _viewModel;

        ApplyAlwaysOnTop(host.Settings.AlwaysOnTop);
        RestorePlacement();
        UpdateCloseButton();

        // 進行中的已執行時間每 ElapsedRefresh、相對時間每 RelativeTextRefresh 重算（spec §10.8）。
        // 兩個計時器都只在需要時跑（UpdateTimers）：收在系統匣時不喚醒 UI 執行緒。
        _elapsedTimer = new DispatcherTimer { Interval = Tuning.ElapsedRefresh };
        _elapsedTimer.Tick += (_, _) => OnTimerTick(_viewModel.OnElapsedTick);
        _relativeTimer = new DispatcherTimer { Interval = Tuning.RelativeTextRefresh };
        _relativeTimer.Tick += (_, _) => OnTimerTick(_viewModel.OnRelativeTick);
        _snackbarTimer = new DispatcherTimer { Interval = SnackbarDuration };
        _snackbarTimer.Tick += (_, _) =>
        {
            _snackbarTimer.Stop();
            Snackbar.Visibility = Visibility.Collapsed;
        };

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _filter.Changed += OnFilterChanged;
        _host.BackgroundErrorRaised += OnBackgroundErrorRaised;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        _host.Engine.SnapshotPublished += OnSnapshotPublished;
        IsVisibleChanged += OnIsVisibleChanged;
        StateChanged += OnStateChanged;
        Dispatcher.ShutdownStarted += OnDispatcherShutdownStarted;
    }

    /// <summary>開 repo 管理頁、設定頁與資料夾的入口；系統匣選單（Task 16）也用這個。</summary>
    internal WindowNavigator Navigator { get; }

    /// <summary>
    /// 系統匣圖示是否可用（Task 16 建立 NotifyIcon 成功後設成 true）。
    /// 不可用時 ✕ 直接結束程式，而不是收到看不到的系統匣（spec §10.1、§12）。
    /// </summary>
    internal bool IsTrayAvailable
    {
        get => _trayAvailable;
        set
        {
            _trayAvailable = value;
            UpdateCloseButton();
        }
    }

    /// <summary>把視窗叫回前景，供重複啟動（與之後的系統匣）使用（同 SAM）。</summary>
    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _restoreToMaximized ? WindowState.Maximized : WindowState.Normal;
        }

        // 置頂關閉時，單純 Activate 不一定能搶到前景，先短暫置頂再還原（同 SAM）。
        var wasTopmost = Topmost;
        Topmost = true;
        Activate();
        Topmost = wasTopmost || _host.Settings.AlwaysOnTop;
    }

    /// <summary>全量刷新（F5、橫幅與空狀態的「立即重試」，之後系統匣的「立即刷新」，spec §6.6）。</summary>
    internal void RefreshNow() => _host.Engine.Post(new EngineCommand.RefreshAll());

    /// <summary>
    /// 「自動更新」的選擇（spec §10.3）：交給 Engine、存進 settings.json；選「暫停」時記住前一個模式（previousRefreshMode）。
    /// </summary>
    internal void SetRefreshMode(RefreshMode mode)
    {
        var settings = _host.Settings;
        if (mode == RefreshMode.Paused && settings.RefreshMode != RefreshMode.Paused)
        {
            settings.PreviousRefreshMode = settings.RefreshMode;
        }

        var changed = settings.RefreshMode != mode;
        settings.RefreshMode = mode;
        _viewModel.ShowRefreshMode(mode);
        if (!changed)
        {
            return;
        }

        _host.Engine.Post(new EngineCommand.SetRefreshMode(mode));
        TrySaveSettings();
    }

    /// <summary>系統匣的「暫停自動更新／恢復自動更新」：暫停時設成 Paused，恢復時回到 previousRefreshMode（spec §10.3）。</summary>
    internal void ToggleAutoRefreshPaused() =>
        SetRefreshMode(_host.Settings.RefreshMode == RefreshMode.Paused ? _host.Settings.PreviousRefreshMode : RefreshMode.Paused);

    /// <summary>真正結束程式（之後系統匣的「結束」用這個）：存好視窗位置再關閉。</summary>
    internal void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    /// <summary>
    /// 設定頁儲存之後即時套用：「視窗置頂」（spec §11.2）、依「關閉時收到系統匣」更新 ✕ 的說明，以及介面語言。
    /// 關閉時本來就每次重新讀 MinimizeToTray，行為不變。換了語言時 <see cref="Loc.Apply"/> 通知所有畫面（包括這個視窗與系統匣）重算文字。
    /// </summary>
    internal void ApplySavedSettings()
    {
        ApplyAlwaysOnTop(_host.Settings.AlwaysOnTop);
        UpdateCloseButton();
        Loc.Instance.Apply(_host.Settings.Language);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // WindowStyle=None 的視窗最大化時要自己限制在工作區內，否則會蓋住工作列（spec §10.1）。
        // 這時 RootVisual 還沒接上，PresentationSource.FromVisual 會是 null，要用 HWND 取 HwndSource。
        if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is { } source)
        {
            source.AddHook(Interop.WindowPlacement.FilterMessage);
        }

        // 掛鉤接上之後才切成最大化（見 RestorePlacement）：HWND 已經在一般位置，最大化發生在那個位置所在的螢幕。
        // App 一律先 EnsureHandle 才 Show，這時視窗還沒顯示，只改 WindowState；Show 時（含 --tray 之後叫出來）才以最大化出現。
        if (_maximizeWhenSourceReady)
        {
            _maximizeWhenSourceReady = false;
            WindowState = WindowState.Maximized;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
        if (!_exiting && _host.Settings.MinimizeToTray && _trayAvailable)
        {
            // 關閉鈕預設是收到系統匣，不是結束程式（同 SAM）。
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
        if (!e.Cancel)
        {
            Application.Current.Shutdown();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        Detach();
        base.OnClosed(e);
    }

    // ── 快照 ────────────────────────────────────────────────────────────

    /// <summary>在 Engine 的背景執行緒上觸發：只記下最新的快照，排一次到 UI 執行緒套用。</summary>
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
        if (Interlocked.Exchange(ref _incoming, null) is not { } snapshot)
        {
            return;
        }

        if (!IsVisible)
        {
            _hiddenSnapshot = snapshot;
            return;
        }

        _hiddenSnapshot = null;
        ApplySnapshot(snapshot);
    }

    /// <summary>
    /// 套用一份快照。這裡每秒都會跑，失敗時不能交給 DispatcherUnhandledException（每次都跳對話框）：
    /// 改成寫 error.log，同一種例外 10 分鐘內只用橫幅提示一次（spec §15）。
    /// </summary>
    private void ApplySnapshot(MonitorSnapshot snapshot)
    {
        try
        {
            _viewModel.Apply(snapshot);
        }
        catch (Exception ex)
        {
            _host.ReportBackgroundError("更新主畫面", ex);
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            if (_hiddenSnapshot is { } snapshot)
            {
                _hiddenSnapshot = null;
                ApplySnapshot(snapshot);
            }

            // 收在系統匣期間計時器停著，回來時先補成現在的時間文字。
            _viewModel.OnRelativeTick();
            _viewModel.OnElapsedTick();
        }

        UpdateTimers();
    }

    private void OnTimerTick(Action tick)
    {
        if (IsVisible)
        {
            tick();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.NeedsElapsedTick))
        {
            UpdateTimers();
        }
    }

    /// <summary>
    /// 視窗看得到時才跑計時器；每秒的計時器另外只在有進行中／排隊中的列或延後中的重排時跑（MainViewModel.NeedsElapsedTick）。
    /// 最小化時視窗仍算看得到（工作列上），照常跑。
    /// </summary>
    private void UpdateTimers()
    {
        if (_detached)
        {
            return;
        }

        var visible = IsVisible;
        _relativeTimer.IsEnabled = visible;
        _elapsedTimer.IsEnabled = visible && _viewModel.NeedsElapsedTick;
    }

    private void OnBackgroundErrorRaised(BannerInfo banner) => _viewModel.SetBackgroundError(banner);

    /// <summary>換語言：XAML 的固定文字由繫結更新；這裡重算程式設定的文字（置頂與 ✕ 的說明、ViewModel 的文字）。</summary>
    private void OnLanguageChanged()
    {
        ApplyAlwaysOnTop(_host.Settings.AlwaysOnTop);
        UpdateCloseButton();
        _viewModel.RefreshLanguage();
    }

    private void OnFilterChanged(bool persist)
    {
        if (!persist)
        {
            return;
        }

        // 篩選偏好（不含搜尋文字）存進 settings.json（spec §10.4）。
        _host.Settings.Filters = _filter.ToPreferences();
        TrySaveSettings();
    }

    // ── 鍵盤（spec §10.12）─────────────────────────────────────────────

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.F5 when modifiers == ModifierKeys.None:
                RefreshNow();
                e.Handled = true;
                break;
            case Key.F when modifiers == ModifierKeys.Control:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape when SearchBox.IsKeyboardFocusWithin:
                // 搜尋框有焦點時清空；其他時候不做事（不收起視窗，避免誤觸）。
                _filter.SearchText = string.Empty;
                e.Handled = true;
                break;
        }
    }

    /// <summary>Enter 開啟選取列的 run；Shift+F10／選單鍵由 ListBoxItem 的 ContextMenu 處理。</summary>
    private void OnRunListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter
            && Keyboard.Modifiers == ModifierKeys.None
            && e.OriginalSource is ListBoxItem { DataContext: RunRowViewModel row })
        {
            OpenLink(row.RunUri);
            e.Handled = true;
        }
    }

    // ── 清單的寬度斷點與延後重排（spec §10.10）───────────────────────────

    private void OnRunListLoaded(object sender, RoutedEventArgs e)
    {
        if (_listScroll is null && FindDescendant<ScrollViewer>(RunList) is { } scroll)
        {
            _listScroll = scroll;
            _listScroll.ScrollChanged += (_, args) =>
            {
                // 檢視區寬度變了（例如清單第一次排版完成）就重算；捲軸出現或消失算出來的寬度不變，不會切換斷點。
                if (args.ViewportWidthChange != 0)
                {
                    UpdateListWidth();
                }
            };
        }

        UpdateListWidth();
    }

    private void OnRunListSizeChanged(object sender, SizeChangedEventArgs e) => UpdateListWidth();

    /// <summary>
    /// 比較的是清單的可用寬度（扣掉邊距與捲軸，spec §10.10），再扣掉每一列的內距與外框
    /// （Style.ListBoxItem.Record 的 Padding.Record、Thickness.Border.Standard），就是列內容實際能用的寬度。
    /// 捲軸寬度一律扣掉、不看捲軸當下有沒有出現：否則在斷點附近會「隱藏欄位 → 列變矮 → 捲軸消失 → 變寬 →
    /// 顯示欄位 → 列變高 → 捲軸出現」不停來回切換。ViewportWidth 在捲軸出現時已經扣掉捲軸，沒出現時這裡自己扣。
    /// </summary>
    private void UpdateListWidth()
    {
        double width;
        if (_listScroll is { ViewportWidth: > 0 } scroll)
        {
            width = scroll.ViewportWidth;
            if (scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible)
            {
                width -= VerticalScrollBarWidth(scroll);
            }
        }
        else
        {
            width = RunList.ActualWidth - SystemParameters.VerticalScrollBarWidth;
        }

        width -= HorizontalOf(RowPaddingKey) + HorizontalOf(RowBorderKey);
        _viewModel.SetListWidth(width);
    }

    private double HorizontalOf(string thicknessKey) =>
        TryFindResource(thicknessKey) is Thickness thickness ? thickness.Left + thickness.Right : 0;

    /// <summary>
    /// 直向捲軸出現時占的寬度。出現中用實際寬度；沒出現時用它出現後會拿到的寬度（Width 與 MinWidth 取大、不超過 MaxWidth）。
    /// 不能直接用 Size.ScrollBar.Thin：系統主題的 ScrollBar 樣式設了 MinWidth，細捲軸實際占的是系統捲軸寬
    /// （2026-10-25 實測 17 DIP，等於 SystemParameters.VerticalScrollBarWidth，不是 6）。
    /// repo 管理頁的窄版判斷也用它（同樣一律扣掉捲軸）。
    /// </summary>
    internal static double VerticalScrollBarWidth(ScrollViewer scroll)
    {
        if (scroll.Template?.FindName(VerticalScrollBarPart, scroll) is not ScrollBar bar)
        {
            return SystemParameters.VerticalScrollBarWidth;
        }

        var margin = bar.Margin.Left + bar.Margin.Right;
        if (scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible && bar.ActualWidth > 0)
        {
            return bar.ActualWidth + margin;
        }

        var width = Math.Min(Math.Max(double.IsNaN(bar.Width) ? 0 : bar.Width, bar.MinWidth), bar.MaxWidth);
        return (width > 0 ? width : SystemParameters.VerticalScrollBarWidth) + margin;
    }

    private void OnRunListPreviewMouseMove(object sender, MouseEventArgs e) =>
        _viewModel.SetPointerOverButton(IsOverRowButton(e.OriginalSource as DependencyObject));

    private void OnRunListMouseLeave(object sender, MouseEventArgs e) => _viewModel.SetPointerOverButton(false);

    /// <summary>
    /// 滑鼠在某一列的按鈕或連結（repo、PR、commit、分支 chip、⋯）上：往上找到按鈕或連結之後，還要走得到那一列（ListBoxItem）才算。
    /// 清單範本裡的捲軸也是 RunList 的子孫（軌道上下兩段是 RepeatButton），但不在任何一列裡，一路走到 RunList 就不算。
    /// </summary>
    private bool IsOverRowButton(DependencyObject? element)
    {
        var overButton = false;
        for (var current = element; current is not null && !ReferenceEquals(current, RunList); current = ParentOf(current))
        {
            switch (current)
            {
                case ButtonBase or Hyperlink:
                    overButton = true;
                    break;
                case ListBoxItem:
                    return overButton;
            }
        }

        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    private void OnRunMenuOpened(object sender, RoutedEventArgs e) => _viewModel.SetMenuOpen(true);

    private void OnRunMenuClosed(object sender, RoutedEventArgs e) => _viewModel.SetMenuOpen(false);

    // ── 列上的連結與選單（spec §10.5、§10.9）────────────────────────────

    private void OnRepoLinkClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            OpenLink(row.RepoActionsUri);
        }
    }

    private void OnTriggerLinkClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { TriggerUri: { } uri })
        {
            OpenLink(uri);
        }
    }

    private void OnBranchClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { BranchUri: { } uri })
        {
            OpenLink(uri);
        }
    }

    /// <summary>⋯ 按鈕開的是和右鍵同一個選單，位置在按鈕下方。</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || RowOf(sender) is not { } row || TryFindResource(RunMenuKey) is not ContextMenu menu)
        {
            return;
        }

        RunList.SelectedItem = row;
        menu.DataContext = row;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>右鍵、Shift+F10 或選單鍵開 run 選單之前：選單綁定這一列（見 Menu.Run 的說明），選單項目都作用在這一筆。</summary>
    private void OnRunItemContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: RunRowViewModel row, ContextMenu: { } menu })
        {
            // 容器已經和資料脫鉤（例如正在移除的列）：不開選單，免得選單沒有對應的 run。
            e.Handled = true;
            return;
        }

        menu.DataContext = row;
    }

    private void OnOpenRunClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            OpenLink(row.RunUri);
        }
    }

    private void OnOpenPullRequestClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { PullRequestUri: { } uri })
        {
            OpenLink(uri);
        }
    }

    private void OnOpenCommitClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            OpenLink(row.CommitUri);
        }
    }

    private async void OnCopyRunUrlClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        try
        {
            ShowMessage(await TrySetClipboardAsync(row.RunUrlText) ? Loc.Texts.MessageRunUrlCopied : Loc.Texts.MessageClipboardBusy);
        }
        catch (Exception ex)
        {
            ReportUnexpected("複製 run 網址", ex);
        }
    }

    /// <summary>複製失敗 log（spec §10.9）：Engine 取 job log 尾端，這裡只負責放進剪貼簿與顯示結果。</summary>
    private async void OnCopyFailedLogClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        try
        {
            ShowMessage(Loc.Texts.MessageFetchingLogs);
            var result = await _host.Engine.CopyFailedLogAsync(row.RepoId, row.RunId, _lifetime.Token);
            if (result.Success && result.Text is { } text && !await TrySetClipboardAsync(text))
            {
                ShowMessage(Loc.Texts.MessageClipboardBusy);
                return;
            }

            ShowMessage(result.Message);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // 程式正在結束，Engine 已經停止。
        }
        catch (Exception ex)
        {
            ReportUnexpected("複製失敗 log", ex);
        }
    }

    /// <summary>「這筆完成時通知我」：再點一次取消（spec §10.9）。畫面在下一份快照更新。</summary>
    private void OnToggleWatchClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            _host.Engine.Post(new EngineCommand.SetWatched(row.RunId, !row.IsWatched));
        }
    }

    /// <summary>「靜音此 repo」：寫入 repo_overrides，Engine 下一步就從清單與計數移除（spec §10.9）。保留原本的強制納入值。</summary>
    private void OnMuteRepoClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        var current = _viewModel.OverrideOf(row.RepoId);
        _host.Engine.Post(new EngineCommand.SetOverride(row.RepoId, current with { Muted = true }));
        ShowMessage(Loc.Format(Loc.Texts.MessageRepoMuted, row.RepoFullName));
    }

    // ── 寫入：重跑與取消（第二階段 spec §9.1～§9.4）──────────────────────

    private void OnRerunFailedJobsClick(object sender, RoutedEventArgs e) => SendWrite(sender, WriteAction.RerunFailedJobs);

    private void OnRerunAllClick(object sender, RoutedEventArgs e) => SendWrite(sender, WriteAction.RerunAll);

    private void OnCancelRunClick(object sender, RoutedEventArgs e) => SendWrite(sender, WriteAction.Cancel);

    private void OnForceCancelClick(object sender, RoutedEventArgs e) => SendWrite(sender, WriteAction.ForceCancel);

    /// <summary>
    /// 四個寫入項目共用的流程：確認 → 標成送出中 →「正在送出…」→ 交給 Engine → 顯示結果（第二階段 spec §9.2、§9.4）。
    /// 已受理與未送出用訊息條，被拒與結果不確定用錯誤對話框；程式正在結束時不顯示（同複製失敗 log）。
    /// </summary>
    private async void SendWrite(object sender, WriteAction action)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        // 按下的當下就記下要送的值與對話框的文字（第二階段 spec §9.2）：選單一關，延後的重排就會套用、列的容器可能被回收給別筆 run；
        // 對話框開著時這筆 run 也可能被別人重跑，Engine 用記下的 run_attempt 比對（expectedAttempt）。
        var repoId = row.RepoId;
        var runId = row.RunId;
        var attempt = row.RunAttempt;
        var confirm = WriteText.Confirm(action, row.ToConfirmTarget(), Loc.Instance.Language);
        var sending = false;
        try
        {
            if (new ConfirmDialog(confirm) { Owner = this }.ShowDialog() != true || _lifetime.IsCancellationRequested)
            {
                return;
            }

            _viewModel.BeginSending(runId);
            sending = true;
            ShowMessage(WriteText.Sending(Loc.Instance.Language));
            var result = await _host.Engine.SendWriteAsync(action, repoId, runId, attempt, _lifetime.Token);
            sending = false;
            _viewModel.EndSending(runId, result.SnapshotSequence);

            if (_lifetime.IsCancellationRequested || WriteText.Result(result, Loc.Instance.Language) is not { } shown)
            {
                return;
            }

            if (shown.AsDialog)
            {
                ((App)Application.Current).ShowError(shown.Text);
            }
            else
            {
                ShowMessage(shown.Text);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // 程式正在結束，Engine 已經停止：不顯示。
            if (sending)
            {
                _viewModel.CancelSending(runId);
            }
        }
        catch (Exception ex)
        {
            if (sending)
            {
                _viewModel.CancelSending(runId);
            }

            ReportUnexpected("送出寫入", ex);
        }
    }

    private static RunRowViewModel? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as RunRowViewModel
        ?? (sender as FrameworkContentElement)?.DataContext as RunRowViewModel;

    // ── 篩選列、摘要列（spec §10.3、§10.4）─────────────────────────────

    /// <summary>
    /// 篩選與「自動更新」的下拉：按鈕按下時把按鈕本身的 ContextMenu 開在按鈕下方，選單關閉時按鈕回到未按下。
    /// 掛在 Checked／Unchecked 而不是 Click：UI Automation 的 Toggle（報讀軟體的主要動作）只切換 IsChecked、不觸發 Click，
    /// 這樣滑鼠、鍵盤與 Toggle 都走同一條路。
    /// </summary>
    private void OnDropdownChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { ContextMenu: { } menu } toggle)
        {
            return;
        }

        menu.PlacementTarget = toggle;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>選單開著時再切換一次（例如 UI Automation 的 Toggle）：關掉選單。</summary>
    private void OnDropdownUnchecked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { ContextMenu: { IsOpen: true } menu })
        {
            menu.IsOpen = false;
        }
    }

    private void OnDropdownClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { PlacementTarget: ToggleButton toggle })
        {
            toggle.IsChecked = false;
        }
    }

    private void OnRefreshModeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RefreshModeOption option)
        {
            SetRefreshMode(option.Mode);
        }
    }

    private void OnClearFiltersClick(object sender, RoutedEventArgs e) => _filter.ClearAll();

    // ── 標題列、橫幅、空狀態的按鈕 ─────────────────────────────────────

    private void OnTogglePinClick(object sender, RoutedEventArgs e)
    {
        ApplyAlwaysOnTop(!_host.Settings.AlwaysOnTop);
        TrySaveSettings();
    }

    private void OnRepoManagerClick(object sender, RoutedEventArgs e) => Navigator.OpenRepoManager(this);

    private void OnSettingsClick(object sender, RoutedEventArgs e) => Navigator.OpenSettings(this);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnRecheckAuthClick(object sender, RoutedEventArgs e) => _host.Engine.Post(new EngineCommand.RecheckAuth());

    private void OnRetryClick(object sender, RoutedEventArgs e) => RefreshNow();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (!Navigator.OpenDataFolder())
        {
            ShowMessage(Loc.Texts.CommonFolderFailed);
        }
    }

    private void OnDismissBannerClick(object sender, RoutedEventArgs e) => _viewModel.DismissBanner();

    private void ApplyAlwaysOnTop(bool enabled)
    {
        Topmost = enabled;
        _host.Settings.AlwaysOnTop = enabled;

        // 同 SAM：置頂中圖示用強調色；關閉時清掉本地值，讓樣式的預設前景色重新生效。
        if (enabled && TryFindResource(AccentBrushKey) is Brush accent)
        {
            PinButton.Foreground = accent;
        }
        else
        {
            PinButton.ClearValue(ForegroundProperty);
        }

        var text = enabled ? Loc.Texts.MainUnpin : Loc.Texts.MainPin;
        PinButton.ToolTip = text;
        System.Windows.Automation.AutomationProperties.SetName(PinButton, text);
    }

    private void UpdateCloseButton()
    {
        var text = _host.Settings.MinimizeToTray && _trayAvailable ? Loc.Texts.MainCloseToTray : Loc.Texts.MainExit;
        CloseButton.ToolTip = text;
        System.Windows.Automation.AutomationProperties.SetName(CloseButton, text);
    }

    // ── 連結與剪貼簿 ───────────────────────────────────────────────────

    /// <summary>只開 https://github.com/ 的網址（spec §10.7）；不符合就不開並寫 error.log。交給瀏覽器時用 AbsoluteUri 保留編碼。</summary>
    private void OpenLink(Uri uri)
    {
        if (!WebLinks.IsAllowed(uri))
        {
            _host.ErrorLog.WriteMessage("開啟連結", $"網址不是 https://github.com/，不開啟：{uri.OriginalString}");
            ShowMessage(Loc.Texts.MessageLinkBlocked);
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            _host.ErrorLog.Write("開啟連結失敗", ex);
            ShowMessage(Loc.Texts.MessageBrowserFailed);
        }
    }

    /// <summary>
    /// 剪貼簿被其他程式佔用時重試 ClipboardRetries 次，每次間隔 ClipboardRetryDelay（spec §10.9）。
    /// 每次呼叫都要框架只試一次、不自己重試：WPF 的 Clipboard.SetText 內部會在 UI 執行緒上用 Thread.Sleep 再重試 10 次、每次 100 毫秒，
    /// 疊上這裡的重試會卡住畫面約 4 秒（2026-10-26 實測 4.7 秒）。WinForms 的 SetDataObject 有可以指定重試次數的多載；
    /// 間隔用 Task.Delay，等待時畫面照常更新。
    /// </summary>
    private async Task<bool> TrySetClipboardAsync(string text)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Forms.Clipboard.SetDataObject(
                    new Forms.DataObject(Forms.DataFormats.UnicodeText, text),
                    copy: true,
                    retryTimes: FrameworkClipboardRetries,
                    retryDelay: FrameworkClipboardRetryDelayMs);
                return true;
            }
            catch (ExternalException ex)
            {
                if (attempt >= Tuning.ClipboardRetries)
                {
                    _host.ErrorLog.Write("複製到剪貼簿失敗", ex);
                    return false;
                }
            }

            await Task.Delay(Tuning.ClipboardRetryDelay);
        }
    }

    // ── 視窗位置（spec §10.1）─────────────────────────────────────────

    /// <summary>
    /// 還原儲存的位置、大小與最大化（spec §10.1）。位置與大小要在建立 HWND 之前決定（見 Interop.WindowPlacement 的說明），
    /// 最大化則要等 HWND 建好才切（OnSourceInitialized）。
    /// 儲存的矩形和任何一個螢幕的工作區都不相交（或從沒存過）時，改到主螢幕置中。
    /// </summary>
    private void RestorePlacement()
    {
        var saved = _host.Settings.Window;
        Width = Math.Max(saved.Width, MinWidth);
        Height = Math.Max(saved.Height, MinHeight);

        var bounds = saved.Left is { } left && saved.Top is { } top ? new Rect(left, top, Width, Height) : Rect.Empty;
        if (!Interop.WindowPlacement.IntersectsAnyWorkArea(bounds, Interop.WindowPlacement.SystemDpiToDevice()))
        {
            bounds = Interop.WindowPlacement.CenterOnPrimaryWorkArea(
                new Size(Width, Height),
                new Size(MinWidth, MinHeight),
                Interop.WindowPlacement.SystemDpiFromDevice());
        }

        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        // 這裡只記下要最大化，等 HWND 在上面的一般位置建好才切（OnSourceInitialized）。
        // 建立 HWND 之前就設 Maximized，WPF 會不理 Left／Top，一律在主螢幕最大化（2026-10-26 實測，副螢幕的位置也一樣）。
        if (saved.Maximized)
        {
            _restoreToMaximized = true;
            _maximizeWhenSourceReady = true;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _restoreToMaximized = WindowState == WindowState.Maximized;
        }
    }

    /// <summary>記住位置、大小與是否最大化（spec §10.1）。從沒顯示過（例如開機只在系統匣）時不覆蓋舊值。</summary>
    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal && ActualWidth > 0
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (bounds.IsEmpty
            || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
        {
            return;
        }

        var placement = _host.Settings.Window;
        placement.Left = bounds.Left;
        placement.Top = bounds.Top;
        placement.Width = Math.Max(bounds.Width, Tuning.WindowMinWidth);
        placement.Height = Math.Max(bounds.Height, Tuning.WindowMinHeight);
        placement.Maximized = WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && _restoreToMaximized);
        TrySaveSettings();
    }

    private void TrySaveSettings()
    {
        try
        {
            _host.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.ErrorLog.Write("儲存設定失敗", ex);
            ShowMessage(Loc.Texts.MessageSettingsSaveFailed);
        }
    }

    // ── 結束 ────────────────────────────────────────────────────────────

    /// <summary>程式由別處結束（例如之後系統匣的「結束」直接 Shutdown）時，視窗可能沒有經過 Closing，在這裡補存位置。</summary>
    private void OnDispatcherShutdownStarted(object? sender, EventArgs e)
    {
        if (!_closed)
        {
            SavePlacement();
        }

        Detach();
    }

    private void Detach()
    {
        _detached = true;
        _elapsedTimer.Stop();
        _relativeTimer.Stop();
        _snackbarTimer.Stop();
        _host.Engine.SnapshotPublished -= OnSnapshotPublished;
        _host.BackgroundErrorRaised -= OnBackgroundErrorRaised;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _filter.Changed -= OnFilterChanged;
        Dispatcher.ShutdownStarted -= OnDispatcherShutdownStarted;
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }
    }

    // ── 訊息條 ──────────────────────────────────────────────────────────

    private void ShowMessage(string text)
    {
        SnackbarText.Text = text;
        Snackbar.Visibility = Visibility.Visible;
        _snackbarTimer.Stop();
        _snackbarTimer.Start();
    }

    private void ReportUnexpected(string context, Exception exception)
    {
        _host.ErrorLog.Write(context, exception);

        // spec 沒有給文字的訊息用最短的說明。
        ShowMessage(Loc.Texts.CommonUnexpectedError);
    }

    /// <summary>深度優先找第一個 <typeparamref name="T"/> 的子孫；repo 管理頁也用它找清單的 ScrollViewer。</summary>
    internal static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
