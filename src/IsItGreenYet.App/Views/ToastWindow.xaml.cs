using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IsItGreenYet.App.Interop;
using IsItGreenYet.Core;
using IsItGreenYet.Core.Display;
using IsItGreenYet.Core.Logging;
using IsItGreenYet.Core.Notifications;

namespace IsItGreenYet.App.Views;

/// <summary>
/// 螢幕右下角的通知提示（spec §9.4）。用自己的視窗而不是系統通知：系統氣泡會被專注輔助吃掉（同 SAM）。
/// 同一個視窗重複使用；提示還開著時收到新內容（Engine 已經合併成彙總）就換上新內容並重新計時。
/// </summary>
/// <remarks>
/// <para>停留時間是 <see cref="ToastContent.Duration"/>；滑鼠停在提示上時暫停計時，移開後繼續剩餘的時間。</para>
/// <para>
/// 不搶焦點：顯示時不啟用（ShowActivated=False），視窗另外加上 WS_EX_NOACTIVATE，
/// 點 ✕ 或點行也不會把焦點從你正在用的程式搶走。
/// </para>
/// <para>關掉（時間到、✕、點了開啟）時觸發 <see cref="Dismissed"/>，App 轉成 EngineCommand.ToastDismissed。</para>
/// </remarks>
public partial class ToastWindow : Window
{
    /// <summary>離工作區右下角的距離（同 SAM 的 16）。</summary>
    private const double ScreenMargin = 16;

    private const string FailureBrushKey = "Brush.Danger";
    private const string SuccessBrushKey = "Brush.Accent";
    private const string NeutralBrushKey = "Brush.Text.Muted";

    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;

    /// <summary>標頭的 App 圖示：和主視窗同一個 Assets/app.ico（組件名稱是 csproj 的 AssemblyName）。</summary>
    private static readonly Uri AppIconUri = new("pack://application:,,,/IsItGreenYet;component/Assets/app.ico", UriKind.Absolute);

    /// <summary>停留時間的合理範圍：失敗與成功兩種可調範圍的聯集（spec §9.4：2–120 秒）。</summary>
    private static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(Tuning.SuccessToastMinSeconds);

    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(Tuning.FailureToastMaxSeconds);

    private readonly ErrorLog _errorLog;
    private readonly Action _openMainWindow;
    private readonly Action<string> _launchUrl;
    private readonly TimeProvider _time;
    private readonly DispatcherTimer _hideTimer;

    private ToastContent? _content;

    /// <summary>還剩多少停留時間；計時中時從 <see cref="_countdownStartedAt"/> 起算。</summary>
    private TimeSpan _remaining;

    private long _countdownStartedAt;

    /// <summary>滑鼠在框上按下（還沒放開）：放開時才算點了空白處。</summary>
    private bool _framePressed;

    private bool _shuttingDown;

    /// <param name="errorLog">連結不是 https://github.com/、或瀏覽器開不起來時寫一筆（spec §15）。</param>
    /// <param name="openMainWindow">彙總點空白處、或單筆沒有可開的網址時呼叫（spec §9.4）。</param>
    /// <param name="launchUrl">把網址交給瀏覽器；預設用 ShellExecute。測試時換掉，不真的開瀏覽器。</param>
    /// <param name="time">停留計時用的單調時鐘；預設 <see cref="TimeProvider.System"/>。</param>
    internal ToastWindow(ErrorLog errorLog, Action openMainWindow, Action<string>? launchUrl = null, TimeProvider? time = null)
    {
        _errorLog = errorLog ?? throw new ArgumentNullException(nameof(errorLog));
        _openMainWindow = openMainWindow ?? throw new ArgumentNullException(nameof(openMainWindow));
        _launchUrl = launchUrl ?? LaunchInBrowser;
        _time = time ?? TimeProvider.System;

        // ✕ 的名稱與 tooltip（spec 只寫「右上角有 ✕ 可以關掉」，沒有給文字）在 XAML，換語言時即時更新。
        InitializeComponent();

        AppIcon.Source = LoadAppIcon();

        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += (_, _) => Dismiss();

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => PlaceAtCorner();
        MouseEnter += (_, _) => PauseCountdown();
        MouseLeave += (_, _) =>
        {
            // 在框上按下、移出去才放開：不算點了空白處。
            _framePressed = false;
            ResumeCountdown();
        };
    }

    /// <summary>提示關掉了（時間到、按 ✕、點了開啟）。隱藏中的提示不會再觸發。</summary>
    internal event Action? Dismissed;

    /// <summary>
    /// 顯示提示，或把開著的提示換成新內容（spec §9.4：提示還開著時又有新的，內容合併成彙總並重新計時）。
    /// 滑鼠正停在提示上時先不計時，移開才開始。
    /// </summary>
    internal void Present(ToastContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_shuttingDown)
        {
            return;
        }

        _content = content;
        _hideTimer.Stop();

        TitleText.Text = content.Title;
        LineList.ItemsSource = content.Lines;
        MoreText.Text = content.MoreText ?? string.Empty;
        MoreText.Visibility = string.IsNullOrEmpty(content.MoreText) ? Visibility.Collapsed : Visibility.Visible;
        if (TryFindResource(BrushKeyOf(content.Tone)) is Brush brush)
        {
            // 視窗自己的方角框線與 DWM 的圓角框線同色（HWND 還沒建立時，建立當下由 DwmFrame 套用）。
            BorderBrush = brush;
            DwmFrame.Refresh(this);
        }

        _remaining = Clamp(content.Duration);

        var wasVisible = IsVisible;
        if (!wasVisible)
        {
            // 先建立 HWND、量好大小、移到右下角，再顯示。不透明視窗不能像原本那樣用 Opacity = 0 先顯示再量：
            // 2026-10-26 測試殼實測，非分層視窗設 Opacity = 0 藏不住，會在預設位置閃出一個帶陰影的方塊。
            new WindowInteropHelper(this).EnsureHandle();
            UpdateLayout();
            MoveToCorner();
            Show();
        }
        else
        {
            UpdateLayout();
            PlaceAtCorner();
        }

        // 剛顯示時一律開始計時：游標剛好在那裡、還沒移動時不會有 MouseEnter，IsMouseOver 也可能還是隱藏前的舊值。
        if (!wasVisible || !IsMouseOver)
        {
            StartCountdown();
        }
    }

    /// <summary>程式結束時真的關掉視窗（平常的關閉只是隱藏，好重複使用）。</summary>
    internal void Shutdown()
    {
        _shuttingDown = true;
        _hideTimer.Stop();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 平常的關閉（例如別的程式送 WM_CLOSE）改成隱藏：關掉的視窗不能再 Show，下一則提示就出不來。
        if (!_shuttingDown)
        {
            e.Cancel = true;
            Dismiss();
            return;
        }

        base.OnClosing(e);
    }

    private static string BrushKeyOf(ToastTone tone) => tone switch
    {
        ToastTone.Failure => FailureBrushKey,
        ToastTone.Success => SuccessBrushKey,
        _ => NeutralBrushKey,
    };

    private static TimeSpan Clamp(TimeSpan duration) =>
        duration < MinDuration ? MinDuration : duration > MaxDuration ? MaxDuration : duration;

    private static void LaunchInBrowser(string url)
    {
        using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    /// 標頭的 App 圖示：app.ico 有 16、20、24、32… 好幾格，挑寬度不小於實際顯示像素的最小一格（100% 縮放用 16、150% 用 24）。
    /// Image 直接用 .ico 時只取第一格（16），150% 時放大會糊。讀不到時寫 error.log、不顯示圖示，提示照常。
    /// </summary>
    private ImageSource? LoadAppIcon()
    {
        // App 是 system DPI aware（見 Interop.WindowPlacement 的說明），提示一律以系統 DPI 顯示。
        var pixels = AppIcon.Width * VisualTreeHelper.GetDpi(this).DpiScaleX;
        try
        {
            var frames = BitmapDecoder.Create(AppIconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            return frames.Where(frame => frame.PixelWidth >= pixels).MinBy(frame => frame.PixelWidth)
                ?? frames.MaxBy(frame => frame.PixelWidth);
        }
        catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException)
        {
            _errorLog.Write("讀取提示視窗的 App 圖示", ex);
            return null;
        }
    }

    // 延伸樣式只有 32 位元，用 GetWindowLongW／SetWindowLongW：32 與 64 位元的 user32 都有這兩個進入點。
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate);
    }

    /// <summary>顯示中的提示內容變了、大小跟著變時，重新對齊右下角。隱藏時不動（下次顯示前 <see cref="Present"/> 會先對齊）。</summary>
    private void PlaceAtCorner()
    {
        if (IsVisible)
        {
            MoveToCorner();
        }
    }

    /// <summary>主螢幕工作區右下角（spec §9.4，同 SAM）。</summary>
    private void MoveToCorner()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - ScreenMargin;
        Top = workArea.Bottom - ActualHeight - ScreenMargin;
    }

    // ── 停留計時 ────────────────────────────────────────────────────────

    private void StartCountdown()
    {
        _countdownStartedAt = _time.GetTimestamp();
        _hideTimer.Interval = _remaining;
        _hideTimer.Start();
    }

    /// <summary>滑鼠移進提示：停止計時，記下剩下的時間。</summary>
    private void PauseCountdown()
    {
        if (!_hideTimer.IsEnabled)
        {
            return;
        }

        _hideTimer.Stop();
        var left = _remaining - _time.GetElapsedTime(_countdownStartedAt);
        _remaining = left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>滑鼠移開：繼續剩下的時間（spec §9.4）。</summary>
    private void ResumeCountdown()
    {
        if (IsVisible && !_hideTimer.IsEnabled)
        {
            StartCountdown();
        }
    }

    private void Dismiss()
    {
        _hideTimer.Stop();
        _framePressed = false;
        if (!IsVisible)
        {
            return;
        }

        Hide();
        _content = null;
        LineList.ItemsSource = null;
        Dismissed?.Invoke();
    }

    // ── 點擊（spec §9.4）────────────────────────────────────────────────

    private void OnCloseClick(object sender, RoutedEventArgs e) => Dismiss();

    /// <summary>彙總的某一行（或單筆的內文）：開那筆 run。</summary>
    private void OnLineClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToastLine line && TryOpenLink(line.Url))
        {
            Dismiss();
        }
    }

    private void OnFrameMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _framePressed = true;

    /// <summary>點空白處：單筆開啟該 run；彙總（或單筆的網址不合法）開主視窗。行與 ✕ 是按鈕，已經自己處理掉了。</summary>
    private void OnFrameMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_framePressed || _content is not { } content)
        {
            return;
        }

        _framePressed = false;
        if (content.OpenUrl is { } url)
        {
            if (TryOpenLink(url))
            {
                Dismiss();
            }

            return;
        }

        _openMainWindow();
        Dismiss();
    }

    /// <summary>
    /// 只開 https://github.com/ 的網址（spec §10.7、§15）；不符合就不開、寫 error.log，提示留著。
    /// 交給瀏覽器時用 AbsoluteUri，保留分支名等的編碼。
    /// </summary>
    private bool TryOpenLink(Uri url)
    {
        if (!WebLinks.IsAllowed(url))
        {
            _errorLog.WriteMessage("開啟連結", $"網址不是 https://github.com/，不開啟：{url.OriginalString}");
            return false;
        }

        try
        {
            _launchUrl(url.AbsoluteUri);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            _errorLog.Write("開啟連結失敗", ex);
            return false;
        }
    }
}
