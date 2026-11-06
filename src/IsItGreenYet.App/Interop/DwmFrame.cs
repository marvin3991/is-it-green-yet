using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace IsItGreenYet.App.Interop;

/// <summary>DWM 畫的圓角（DWM_WINDOW_CORNER_PREFERENCE 的值）。</summary>
public enum DwmCorner
{
    /// <summary>不設定，維持系統預設。</summary>
    Default = 0,

    /// <summary>圓角：選單、提示視窗、錯誤對話框。2026-10-26 在 Windows 11（build 26300、100% 縮放）從截圖像素反推約 8 px。</summary>
    Round = 2,

    /// <summary>小圓角：滑鼠停留提示。同上反推約 4 px。</summary>
    RoundSmall = 3,
}

/// <summary>
/// 讓 DWM 畫 Windows 11 的圓角、原生陰影與框線色（DwmSetWindowAttribute）。選單、滑鼠停留提示、提示視窗、錯誤對話框與系統匣選單共用。
/// </summary>
/// <remarks>
/// <para>
/// 視窗或 popup 自己的範本畫方角加 1 px 框線，框線色和這裡設給 DWM 的相同：Windows 11 上 DWM 把四角裁成圓角、沿圓角畫框線與陰影，
/// 方角框線和 DWM 的框線疊在同一圈像素上，看不出差別（2026-10-26 測試殼放大比對）；不支援這些屬性的 Windows（例如 Windows 10）
/// 回傳錯誤碼、不丟例外，畫面就是方角加框線。
/// </para>
/// <para>
/// WPF 範本不能自己再畫圓角：分層（AllowsTransparency）的 popup 疊上 DWM 圓角時，WPF 的圓角會在邊角多出一圈框（同上實測）。
/// </para>
/// <para>
/// WPF 的選單、滑鼠停留提示在樣式裡設 <see cref="CornerProperty"/>：popup 每次開啟都會建立新的 HWND，接上時（SourceChanged）套用。
/// 框線色取控制項自己的 <see cref="Control.BorderBrush"/>（要是單色筆刷）；之後換了框線色（例如提示視窗換語氣）要呼叫 <see cref="Refresh"/>。
/// </para>
/// </remarks>
public static class DwmFrame
{
    public static readonly DependencyProperty CornerProperty = DependencyProperty.RegisterAttached(
        "Corner",
        typeof(DwmCorner),
        typeof(DwmFrame),
        new PropertyMetadata(DwmCorner.Default, OnCornerChanged));

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE：深色的視窗框架（App 只有深色主題）。</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE。</summary>
    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWA_BORDER_COLOR（COLORREF：0x00BBGGRR）。</summary>
    private const int DwmwaBorderColor = 34;

    /// <summary>上面三個屬性的值都是 32 位元整數。</summary>
    private const int AttributeSize = sizeof(int);

    /// <summary>BOOL 的 TRUE。</summary>
    private const int True = 1;

    private const int SOk = 0;

    /// <summary>COLORREF 裡綠、藍色版的位移（紅色在最低的位元組）。</summary>
    private const int GreenShift = 8;

    private const int BlueShift = 16;

    public static DwmCorner GetCorner(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (DwmCorner)element.GetValue(CornerProperty);
    }

    public static void SetCorner(DependencyObject element, DwmCorner value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(CornerProperty, value);
    }

    /// <summary>
    /// 對視窗設深色框架、圓角偏好與框線色。圓角與框線色都設成功（HRESULT 為 S_OK）才回 true；
    /// 失敗時（舊版 Windows）不丟例外，畫面維持範本自己畫的方角框線。
    /// </summary>
    /// <param name="border">框線色；null 時不設，用系統預設。</param>
    public static bool TryApply(IntPtr hwnd, DwmCorner corner, Color? border)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        // 深色框架只影響標題列與系統畫的邊；舊版 Windows 不認得這個屬性時沒有影響，不算進結果。
        SetAttribute(hwnd, DwmwaUseImmersiveDarkMode, True);
        var applied = SetAttribute(hwnd, DwmwaWindowCornerPreference, (int)corner);
        if (border is { } color)
        {
            applied &= SetAttribute(hwnd, DwmwaBorderColor, ToColorRef(color));
        }

        return applied;
    }

    /// <summary>元素已經接上 HWND 時，用目前的圓角設定與框線色重新套用一次（例如提示視窗換了語氣）。</summary>
    public static bool Refresh(Visual element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return PresentationSource.FromVisual(element) is HwndSource source && Apply(element, source);
    }

    private static void OnCornerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        // 先拿掉再加，同一個元素不會掛兩次。
        PresentationSource.RemoveSourceChangedHandler(element, OnSourceChanged);
        if ((DwmCorner)e.NewValue != DwmCorner.Default)
        {
            PresentationSource.AddSourceChangedHandler(element, OnSourceChanged);
        }
    }

    private static void OnSourceChanged(object sender, SourceChangedEventArgs e)
    {
        if (sender is DependencyObject element && e.NewSource is HwndSource source)
        {
            Apply(element, source);
        }
    }

    private static bool Apply(DependencyObject element, HwndSource source) =>
        TryApply(source.Handle, GetCorner(element), (element as Control)?.BorderBrush is SolidColorBrush brush ? brush.Color : null);

    private static int ToColorRef(Color color) => color.R | (color.G << GreenShift) | (color.B << BlueShift);

    private static bool SetAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(hwnd, attribute, ref value, AttributeSize) == SOk;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 沒有 DWM 的環境：維持範本自己畫的方角框線。
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
