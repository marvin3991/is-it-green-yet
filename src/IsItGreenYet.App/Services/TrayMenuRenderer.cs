using IsItGreenYet.App.Interop;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace IsItGreenYet.App.Services;

/// <summary>
/// 系統匣右鍵選單（WinForms）的外觀，全部取自 WPF 資源（Themes/Design.xaml、Themes/Controls.xaml），和主畫面的深色選單一致（spec §16.1：不新增顏色）。
/// </summary>
/// <param name="Background">選單底色（Brush.Surface）。</param>
/// <param name="Highlight">滑過或鍵盤選取的項目底色、分隔線（Brush.Surface.Hover）。</param>
/// <param name="Border">外框（Brush.Border）。</param>
/// <param name="Text">項目文字（Brush.Text.Primary）。</param>
/// <param name="DisabledText">停用的項目文字（Brush.Text.Muted）。</param>
/// <param name="ItemRadius">項目底色的圓角（Radius.Control，DIP）。</param>
/// <param name="Padding">選單外框與項目之間的距離（Padding.Menu，DIP；只用左、上、右、下四個整數值）。</param>
internal sealed record TrayMenuTheme(
    Drawing.Color Background,
    Drawing.Color Highlight,
    Drawing.Color Border,
    Drawing.Color Text,
    Drawing.Color DisabledText,
    double ItemRadius,
    System.Windows.Thickness Padding)
{
    private const string BackgroundKey = "Brush.Surface";
    private const string HighlightKey = "Brush.Surface.Hover";
    private const string BorderKey = "Brush.Border";
    private const string TextKey = "Brush.Text.Primary";
    private const string DisabledTextKey = "Brush.Text.Muted";
    private const string ItemRadiusKey = "Radius.Control";
    private const string PaddingKey = "Padding.Menu";

    /// <summary>從 WPF 資源取值。</summary>
    /// <param name="findResource">例如 <c>Application.TryFindResource</c>；找不到時回 null。</param>
    /// <exception cref="InvalidOperationException">找不到其中一個資源，或型別不對（同 <see cref="TrayIconPalette.FromResources"/>）。</exception>
    public static TrayMenuTheme FromResources(Func<object, object?> findResource)
    {
        ArgumentNullException.ThrowIfNull(findResource);

        return new TrayMenuTheme(
            Background: ColorOf(BackgroundKey),
            Highlight: ColorOf(HighlightKey),
            Border: ColorOf(BorderKey),
            Text: ColorOf(TextKey),
            DisabledText: ColorOf(DisabledTextKey),
            ItemRadius: findResource(ItemRadiusKey) is System.Windows.CornerRadius radius
                ? radius.TopLeft
                : throw Missing(ItemRadiusKey),
            Padding: findResource(PaddingKey) is System.Windows.Thickness padding ? padding : throw Missing(PaddingKey));

        Drawing.Color ColorOf(string key) =>
            findResource(key) is Media.SolidColorBrush { Color: var color }
                ? Drawing.Color.FromArgb(color.A, color.R, color.G, color.B)
                : throw Missing(key);

        static InvalidOperationException Missing(string key) => new($"找不到系統匣選單要用的資源 {key}。");
    }

    /// <summary>給 DWM 的框線色。</summary>
    public Media.Color BorderColor => Media.Color.FromArgb(Border.A, Border.R, Border.G, Border.B);
}

/// <summary>
/// 系統匣右鍵選單的 Windows 11 深色外觀：深色底、滑過的項目畫圓角底色、深色分隔線、1 px 方角框線；
/// 選單視窗的圓角與原生陰影交給 DWM（<see cref="Apply"/> 在選單建立 HWND 時設定）。
/// </summary>
/// <remarks>
/// <para>
/// 只換外觀：選單本身仍是 NotifyIcon 開的 WinForms ContextMenuStrip，在游標位置開啟、點外面或 Esc 關閉、鍵盤上下選擇與 Enter 都照 WinForms。
/// 不改用 WPF ContextMenu：要自己處理前景視窗（不先 SetForegroundWindow，點外面不會關）、主視窗隱藏時的 PlacementTarget、
/// 混合 DPI 多螢幕的座標換算，看起來的效果卻一樣。
/// </para>
/// <para>
/// 外框自己畫方角 1 px，和 DWM 的圓角框線同色，疊在同一圈像素上看不出差別；DWM 不支援時（例如 Windows 10）就是方角加框線。
/// 尺寸都是 DIP，畫的時候依選單的 DPI 換成像素（<see cref="Forms.Control.LogicalToDeviceUnits(int)"/>）。
/// </para>
/// </remarks>
internal class TrayMenuRenderer : Forms.ToolStripProfessionalRenderer
{
    /// <summary>
    /// 項目高度（DIP）：和主畫面 WPF 選單項目差不多高（Padding.MenuItem 上下各 7、框線各 1、14 DIP 的字一行約 16.4，
    /// 2026-10-26 測試殼量到 32.4，主視窗開了版面取整時是 33）。WinForms 原本是 24，加大的部分平分在項目上下的內距。
    /// </summary>
    internal const int ItemHeight = 32;

    /// <summary>圓角半徑換成直徑。</summary>
    private const int DiameterPerRadius = 2;

    /// <summary>GDI+ 畫圓角的圓弧：每個角掃 90 度，起點角度（0 度朝右、順時針）左上 180、右上 270、右下 0、左下 90。</summary>
    private const float QuarterTurn = 90;

    private const float TopLeftStart = 180;

    private const float TopRightStart = 270;

    private const float BottomRightStart = 0;

    private const float BottomLeftStart = 90;

    /// <summary>分隔線畫在項目高度的一半、文字區置中在項目高度的一半；項目高度多出來的部分平分在上下。</summary>
    private const int Halves = 2;

    /// <summary>外框粗細（DIP，同 Thickness.Border.Standard 的 1）：項目和外框的距離從外框內側算起。</summary>
    private const int BorderThickness = 1;

    public TrayMenuRenderer(TrayMenuTheme theme)
    {
        Theme = theme ?? throw new ArgumentNullException(nameof(theme));
        RoundedEdges = false;
    }

    protected TrayMenuTheme Theme { get; }

    /// <summary>
    /// 套用到選單（字型設好、項目都加好之後呼叫）：顏色、項目高度、上下內距、不顯示圖示欄，以及建立 HWND 時讓 DWM 畫圓角、陰影與框線色。
    /// </summary>
    internal void Apply(Forms.ContextMenuStrip menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        // 選單沒有圖示也沒有勾選，拿掉左邊的圖示欄（WinForms 預設是淺灰色漸層）。
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.Renderer = this;
        menu.BackColor = Theme.Background;
        menu.ForeColor = Theme.Text;

        // 項目高度：多出來的部分平分在上下的內距（文字的左右位置由下拉選單自己決定，不理會項目的左右內距）。
        var height = menu.LogicalToDeviceUnits(ItemHeight);
        foreach (var item in menu.Items.OfType<Forms.ToolStripMenuItem>())
        {
            // 明確設定前景色：沒設時 WinForms 在滑過的項目改用系統的反白文字色。
            item.ForeColor = Theme.Text;
            var extra = height - item.GetPreferredSize(Drawing.Size.Empty).Height;
            if (extra > 0)
            {
                var padding = item.Padding;
                item.Padding = new Forms.Padding(padding.Left, padding.Top + (extra / Halves), padding.Right, padding.Bottom + extra - (extra / Halves));
            }
        }

        // 下拉選單不理會 Padding（ToolStripDropDownMenu 一律用自己算的值，上下各 2；2026-10-26 實測設了也不變），
        // 上下和外框的距離改用第一項的上邊距、最後一項的下邊距補到「外框 1 加 Padding.Menu」，和主畫面的選單一樣。
        if (menu.Items.Count > 0)
        {
            var first = menu.Items[0];
            var last = menu.Items[^1];
            var top = Math.Max(0, menu.LogicalToDeviceUnits(BorderThickness + RoundToInt(Theme.Padding.Top)) - menu.Padding.Top);
            var bottom = Math.Max(0, menu.LogicalToDeviceUnits(BorderThickness + RoundToInt(Theme.Padding.Bottom)) - menu.Padding.Bottom);
            first.Margin = new Forms.Padding(first.Margin.Left, first.Margin.Top + top, first.Margin.Right, first.Margin.Bottom);
            last.Margin = new Forms.Padding(last.Margin.Left, last.Margin.Top, last.Margin.Right, last.Margin.Bottom + bottom);
        }

        menu.HandleCreated += (_, _) => DwmFrame.TryApply(menu.Handle, DwmCorner.Round, Theme.BorderColor);
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var brush = new Drawing.SolidBrush(Theme.Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    /// <summary>外框：1 px 方角（DWM 的圓角框線疊在同一圈上）。</summary>
    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var pen = new Drawing.Pen(Theme.Border);
        var bounds = new Drawing.Rectangle(Drawing.Point.Empty, e.ToolStrip.Size);
        e.Graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
    }

    /// <summary>
    /// 滑過或鍵盤選取的項目畫圓角底色，左右和外框內側距離 Padding.Menu（同主畫面的選單）。停用的項目不畫。
    /// 下拉選單的項目一律從 x=0 排到右邊（不理會 Padding），所以內縮用選單的寬度自己算，不用項目的寬度。
    /// </summary>
    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Item is not { Selected: true, Enabled: true } item || (e.ToolStrip ?? item.Owner) is not { } owner)
        {
            return;
        }

        var left = owner.LogicalToDeviceUnits(BorderThickness + RoundToInt(Theme.Padding.Left));
        var right = owner.LogicalToDeviceUnits(BorderThickness + RoundToInt(Theme.Padding.Right));
        var bounds = new Drawing.Rectangle(left - item.Bounds.Left, 0, owner.Width - left - right, item.Height);
        var radius = owner.LogicalToDeviceUnits(RoundToInt(Theme.ItemRadius));
        using var path = RoundedRectangle(bounds, radius);
        using var brush = new Drawing.SolidBrush(Theme.Highlight);
        var smoothing = e.Graphics.SmoothingMode;
        var pixelOffset = e.Graphics.PixelOffsetMode;

        // 圓角反鋸齒；PixelOffsetMode.Half 讓直線邊對齊像素邊界，左右上下的直邊才不會畫成半透明的一列。
        e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = Drawing2D.PixelOffsetMode.Half;
        e.Graphics.FillPath(brush, path);
        e.Graphics.PixelOffsetMode = pixelOffset;
        e.Graphics.SmoothingMode = smoothing;
    }

    /// <summary>分隔線：項目寬度內的一條水平線，顏色同滑過的底色（Windows 11 選單的淡分隔線）。</summary>
    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var y = e.Item.Height / Halves;
        using var pen = new Drawing.Pen(Theme.Highlight);
        e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
    }

    /// <summary>
    /// 文字區先垂直置中在項目裡（<see cref="CenterTextVertically"/>）。
    /// 停用的項目文字用 Text.Muted：基底類別一律換成 SystemColors.GrayText（ToolStripRenderer），所以橫書時自己畫，畫法同基底類別。
    /// </summary>
    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        CenterTextVertically(e);
        if (e.Item is { Enabled: false } && e.TextDirection == Forms.ToolStripTextDirection.Horizontal)
        {
            Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, Theme.DisabledText, e.TextFormat);
            return;
        }

        base.OnRenderItemText(e);
    }

    /// <summary>
    /// 把橫書的文字區（<see cref="Forms.ToolStripItemTextRenderEventArgs.TextRectangle"/>）移到項目高度的正中間。
    /// <see cref="Apply"/> 用上下內距把項目撐高到 <see cref="ItemHeight"/>，但下拉選單算文字區時不算這些內距，仍照撐高前的高度擺在項目上方
    /// （2026-10-26 實測：項目高 32，文字區 Y=2、高 19，文字上方留 4 px、下方留 13 px）。只改上下位置；重複呼叫的結果相同。
    /// </summary>
    protected static void CenterTextVertically(Forms.ToolStripItemTextRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.TextDirection != Forms.ToolStripTextDirection.Horizontal || e.Item is not { } item)
        {
            return;
        }

        var bounds = e.TextRectangle;
        e.TextRectangle = bounds with { Y = (item.Height - bounds.Height) / Halves };
    }

    private static int RoundToInt(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static Drawing2D.GraphicsPath RoundedRectangle(Drawing.Rectangle bounds, int radius)
    {
        var path = new Drawing2D.GraphicsPath();
        var diameter = radius * DiameterPerRadius;
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, TopLeftStart, QuarterTurn);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, TopRightStart, QuarterTurn);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, BottomRightStart, QuarterTurn);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, BottomLeftStart, QuarterTurn);
        path.CloseFigure();
        return path;
    }
}
