using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace IsItGreenYet.App.Interop;

/// <summary>
/// 主視窗的位置與最大化（spec §10.1）：
/// <list type="bullet">
/// <item>WindowStyle=None 的視窗最大化時會蓋住工作列，處理 WM_GETMINMAXINFO 把最大化範圍限制在所在螢幕的工作區；
/// 工作列自動隱藏時再在那一邊留一點空隙。</item>
/// <item>還原儲存的位置前，檢查這個矩形是否和任何一個螢幕的工作區相交；不相交就改到主螢幕置中。</item>
/// </list>
/// WinForms 的 Screen 回傳裝置像素，WPF 的位置是 DIP，兩者用 <see cref="SystemDpiToDevice"/> 換算。
/// </summary>
/// <remarks>
/// 位置要在建立 HWND 之前決定：WPF 建立視窗時會用「建立前」的 Left／Top 決定初始位置，
/// 在 SourceInitialized 裡才改的位置會被蓋掉（2026-10-25 實測：置中沒有生效）。
/// 這時還沒有視窗可以問 DPI，所以用系統 DPI：App 沒有宣告 per-monitor，是 system-DPI-aware，
/// WPF 的 DIP 與 Screen 的像素都以系統 DPI 換算（實測 GetWindowDpiAwarenessContext 為 system aware）。
/// </remarks>
internal static class WindowPlacement
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    /// <summary>SHAppBarMessage 的 ABM_GETAUTOHIDEBAREX：取得某個螢幕某一邊的自動隱藏 appbar。</summary>
    private const uint AbmGetAutoHideBarEx = 0x0000000B;

    private const uint AbeLeft = 0;
    private const uint AbeTop = 1;
    private const uint AbeRight = 2;
    private const uint AbeBottom = 3;

    /// <summary>
    /// 設計選擇：自動隱藏的工作列那一邊留 2 個裝置像素（至少要 1 px）。工作列自動隱藏時工作區等於整個螢幕，
    /// 無邊框視窗最大化會蓋滿螢幕，shell 會把它當成全螢幕視窗，自動隱藏的工作列就叫不出來（spec §10.1「不蓋住工作列」）。
    /// </summary>
    private const int AutoHideTaskbarReserve = 2;

    /// <summary>WPF 的 1 DIP = 1/96 英吋。</summary>
    private const double DipsPerInch = 96;

    /// <summary>置中時兩側各分一半。</summary>
    private const double Halves = 2;

    /// <summary>DIP → 裝置像素（系統 DPI）。</summary>
    public static Matrix SystemDpiToDevice()
    {
        var scale = GetDpiForSystem() / DipsPerInch;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return Matrix.Identity;
        }

        var matrix = Matrix.Identity;
        matrix.Scale(scale, scale);
        return matrix;
    }

    /// <summary>裝置像素 → DIP（系統 DPI）。</summary>
    public static Matrix SystemDpiFromDevice()
    {
        var matrix = SystemDpiToDevice();
        matrix.Invert();
        return matrix;
    }

    /// <summary>HwndSource 的訊息掛鉤：只改 MINMAXINFO 的最大化位置與大小，不標成已處理，WPF 照常套用 MinWidth／MinHeight。</summary>
    public static IntPtr FilterMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            LimitMaximizedToWorkArea(hwnd, lParam);
        }

        return IntPtr.Zero;
    }

    /// <summary>DIP 矩形換成裝置像素後，是否和任何一個螢幕的工作區相交。</summary>
    public static bool IntersectsAnyWorkArea(Rect bounds, Matrix toDevice)
    {
        if (bounds.IsEmpty || !IsFinite(bounds))
        {
            return false;
        }

        var device = Rect.Transform(bounds, toDevice);
        var rectangle = Drawing.Rectangle.FromLTRB(
            (int)Math.Floor(device.Left),
            (int)Math.Floor(device.Top),
            (int)Math.Ceiling(device.Right),
            (int)Math.Ceiling(device.Bottom));
        return Forms.Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(rectangle));
    }

    /// <summary>主螢幕工作區內置中的位置（DIP）；視窗比工作區大時縮到工作區大小，但不小於最小尺寸。</summary>
    public static Rect CenterOnPrimaryWorkArea(Size size, Size minimum, Matrix fromDevice)
    {
        var work = PrimaryWorkArea(fromDevice);
        var width = Math.Max(Math.Min(size.Width, work.Width), minimum.Width);
        var height = Math.Max(Math.Min(size.Height, work.Height), minimum.Height);
        return new Rect(
            work.Left + (work.Width - width) / Halves,
            work.Top + (work.Height - height) / Halves,
            width,
            height);
    }

    private static Rect PrimaryWorkArea(Matrix fromDevice)
    {
        if (Forms.Screen.PrimaryScreen is { } primary)
        {
            var area = primary.WorkingArea;
            return Rect.Transform(new Rect(area.X, area.Y, area.Width, area.Height), fromDevice);
        }

        // 理論上一定有主螢幕；拿不到時退回 WPF 的主螢幕工作區（已經是 DIP）。
        return SystemParameters.WorkArea;
    }

    private static bool IsFinite(Rect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height);

    /// <summary>最大化的位置與大小改成視窗所在螢幕的工作區（相對於該螢幕左上角），工作列才不會被蓋住。</summary>
    private static void LimitMaximizedToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var work = ReserveAutoHideTaskbars(info.Monitor, info.Work);
        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMax.MaxPosition.X = work.Left - info.Monitor.Left;
        minMax.MaxPosition.Y = work.Top - info.Monitor.Top;
        minMax.MaxSize.X = work.Right - work.Left;
        minMax.MaxSize.Y = work.Bottom - work.Top;
        Marshal.StructureToPtr(minMax, lParam, fDeleteOld: false);
    }

    /// <summary>
    /// 工作區貼齊螢幕邊緣、而那一邊有自動隱藏的工作列（或其他自動隱藏的 appbar）時，那一邊內縮 AutoHideTaskbarReserve。
    /// 一般（不自動隱藏）的工作列已經從工作區扣掉，不必處理。
    /// </summary>
    private static NativeRect ReserveAutoHideTaskbars(NativeRect monitor, NativeRect work)
    {
        if (work.Left == monitor.Left && HasAutoHideBar(AbeLeft, monitor))
        {
            work.Left += AutoHideTaskbarReserve;
        }

        if (work.Top == monitor.Top && HasAutoHideBar(AbeTop, monitor))
        {
            work.Top += AutoHideTaskbarReserve;
        }

        if (work.Right == monitor.Right && HasAutoHideBar(AbeRight, monitor))
        {
            work.Right -= AutoHideTaskbarReserve;
        }

        if (work.Bottom == monitor.Bottom && HasAutoHideBar(AbeBottom, monitor))
        {
            work.Bottom -= AutoHideTaskbarReserve;
        }

        return work;
    }

    private static bool HasAutoHideBar(uint edge, NativeRect monitor)
    {
        var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>(), Edge = edge, Bounds = monitor };
        return SHAppBarMessage(AbmGetAutoHideBarEx, ref data) != 0;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>ABM_GETAUTOHIDEBAREX 回傳那一邊自動隱藏 appbar 的 HWND，沒有則為 0。</summary>
    [DllImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    /// <summary>APPBARDATA。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public IntPtr Window;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRect Bounds;
        public IntPtr LParam;
    }
}
