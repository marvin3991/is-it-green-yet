using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using IsItGreenYet.App.Localization;
using IsItGreenYet.App.Services;
using IsItGreenYet.App.ViewModels;
using IsItGreenYet.Core;
using IsItGreenYet.Core.Display;
using IsItGreenYet.Core.Engine;

namespace IsItGreenYet.App.Views;

/// <summary>
/// 設定頁（spec §11.2）。按「儲存」才生效；「取消」、✕ 或 Esc 關閉時什麼都不存（同 SAM）。
/// PAT 只在按「儲存」的那一刻從 PasswordBox 讀出來交給 <see cref="SettingsViewModel.SaveAsync"/>，讀完立即清空 PasswordBox；
/// 驗證失敗也一樣要重新貼上（spec §13.3、§13.4：token 只在記憶體、用完就丟）。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly EngineFeed _engine;
    private readonly Func<bool> _openDataFolder;

    /// <summary>視窗關閉時取消進行中的檢查連線與 PAT 驗證。</summary>
    private readonly CancellationTokenSource _lifetime = new();

    private MonitorSnapshot? _incoming;
    private int _applyScheduled;
    private bool _closed;

    /// <param name="openDataFolder">開啟資料夾；開不起來時回 false（已寫 error.log）。</param>
    /// <param name="dataFolder">資料夾的路徑（spec §11.2：<c>%LOCALAPPDATA%\IsItGreenYet</c>）。</param>
    internal SettingsWindow(SettingsViewModel viewModel, EngineFeed engine, Func<bool> openDataFolder, string dataFolder)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _openDataFolder = openDataFolder ?? throw new ArgumentNullException(nameof(openDataFolder));

        InitializeComponent();
        DataContext = _viewModel;
        DataPathText.Text = dataFolder;

        // 登入狀態與 token 來源跟著快照更新（例如「檢查連線」或儲存後 Engine 重新驗證）。
        _engine.SnapshotPublished += OnSnapshotPublished;

        // XAML 的固定文字由繫結即時更新；ViewModel 算出來的文字在這裡重算。
        Loc.Instance.LanguageChanged += _viewModel.RefreshLanguage;
    }

    /// <summary>按「儲存」而且全部成功；WindowNavigator 用它決定要不要即時套用到主視窗。</summary>
    internal bool Saved { get; private set; }

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
        _lifetime.Cancel();
        PatBox.Clear();
        base.OnClosed(e);
    }

    private void OnSnapshotPublished(MonitorSnapshot snapshot)
    {
        Interlocked.Exchange(ref _incoming, snapshot);
        if (Interlocked.Exchange(ref _applyScheduled, 1) == 0)
        {
            Dispatcher.InvokeAsync(ApplyIncomingSnapshot, DispatcherPriority.Background);
        }
    }

    private void ApplyIncomingSnapshot()
    {
        Interlocked.Exchange(ref _applyScheduled, 0);
        if (Interlocked.Exchange(ref _incoming, null) is not { } snapshot || _closed)
        {
            return;
        }

        try
        {
            _viewModel.ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _engine.ReportError("更新設定頁", ex);
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanSave)
        {
            return;
        }

        // 驗證 PAT 期間內容整區停用，鍵盤焦點會被移到視窗本身；沒存成功時要還給按下「儲存」前的欄位
        // （例如在 PAT 輸入框按 Enter 收到 401 之後，可以直接貼上下一把）。
        var focusedBefore = Keyboard.FocusedElement as UIElement;

        // 讀出來就清空：之後不論成功或失敗，PasswordBox 裡都不再留著 PAT。
        var patInput = PatBox.Password;
        PatBox.Clear();
        try
        {
            if (await _viewModel.SaveAsync(patInput, _lifetime.Token) && !_closed)
            {
                Saved = true;
                Close();
                return;
            }
        }
        catch (Exception ex)
        {
            _engine.ReportError("儲存設定", ex);

            // spec 沒有給文字（同主視窗的訊息）。
            _viewModel.ShowError(Loc.Texts.CommonUnexpectedError);
        }

        RestoreFocus(focusedBefore);
    }

    /// <summary>
    /// 把焦點還給按下「儲存」前的欄位：只限這個視窗裡、目前可用的元素。
    /// 視窗不在前景時不動，免得搶走使用者正在用的視窗：停用期間邏輯焦點沒有離開原欄位，切回來時 WPF 會把鍵盤焦點交給它。
    /// 只在焦點還停在視窗本身（停用期間被移過去的）時才還；使用者在驗證期間自己移到「取消」或 ✕ 等可用的元素，
    /// 就不搶回來，免得按 Enter 時觸發的是預設的「儲存」而不是使用者想按的那一個。
    /// </summary>
    private void RestoreFocus(UIElement? element)
    {
        if (_closed || !IsActive || element is null || !element.IsEnabled || !element.IsVisible || !element.Focusable
            || !ReferenceEquals(GetWindow(element), this))
        {
            return;
        }

        var current = Keyboard.FocusedElement;
        var userMovedFocus = current is UIElement { IsEnabled: true } other
            && !ReferenceEquals(other, this)
            && !ReferenceEquals(other, element);
        if (userMovedFocus)
        {
            return;
        }

        element.Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async void OnCheckConnectionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.CheckConnectionAsync(_lifetime.Token);
        }
        catch (Exception ex)
        {
            _engine.ReportError("檢查連線", ex);
        }
    }

    /// <summary>「清除」：清空輸入框，儲存時再從 Windows 認證管理員刪除（「取消」保持原樣）。</summary>
    private void OnClearPatClick(object sender, RoutedEventArgs e)
    {
        PatBox.Clear();
        _viewModel.RequestClearPat();
    }

    /// <summary>「清除」之後又貼上新的 PAT：以新的為準，不再刪除。只看長度，不把內容讀成字串（SecurePassword 的複本用完就釋放）。</summary>
    private void OnPatChanged(object sender, RoutedEventArgs e)
    {
        using var secure = PatBox.SecurePassword;
        if (secure.Length > 0)
        {
            _viewModel.CancelClearPat();
        }
    }

    /// <summary>改回 gh CLI 時輸入框停用，裡面還沒存的 PAT 也一併清掉。</summary>
    private void OnGhCliChecked(object sender, RoutedEventArgs e) => PatBox?.Clear();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (!_openDataFolder())
        {
            _viewModel.ShowError(Loc.Texts.CommonFolderFailed);
        }
    }

    private void OnProjectLinkClick(object sender, RoutedEventArgs e) => OpenProjectRepo();

    /// <summary>用預設瀏覽器開專案的 repo 頁；開不起來時設定頁底部顯示錯誤，視窗留著。</summary>
    private void OpenProjectRepo()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(WebLinks.ProjectRepo.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            _viewModel.ReportLinkFailed(ex);
        }
    }

    /// <summary>「授權與第三方聲明」：以設定頁為 owner 開啟對話框（關閉後回到設定頁，不影響尚未儲存的變更）。讀不到內容時在設定頁顯示錯誤、寫 error.log。</summary>
    private void OnNoticesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new NoticesDialog(EmbeddedNotices.ReadAll()) { Owner = this };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            _engine.ReportError("顯示授權與第三方聲明", ex);
            _viewModel.ShowError(Loc.Texts.SettingsNoticesFailed);
        }
    }

    /// <summary>Esc 等於「取消」（spec §11.2），不論焦點在哪個欄位。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Close();
        }
    }
}
