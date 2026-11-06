using System.Runtime.InteropServices;
using IsItGreenYet.Core;
using IsItGreenYet.Core.Models;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Imaging = System.Drawing.Imaging;
using Media = System.Windows.Media;

namespace IsItGreenYet.App.Services;

/// <summary>
/// 系統匣圖示用的顏色，全部取自 Design.xaml 既有的 token（spec §16.1：不新增任何顏色；§16.2 的對應）。
/// </summary>
internal sealed record TrayIconPalette(
    Drawing.Color Failure,
    Drawing.Color Pending,
    Drawing.Color Success,
    Drawing.Color NoData,
    Drawing.Color Glyph,
    Drawing.Color BadgeBackground,
    Drawing.Color BadgeMark)
{
    private const string DangerKey = "Brush.Danger";
    private const string WarningKey = "Brush.Warning";
    private const string AccentKey = "Brush.Accent";
    private const string MutedKey = "Brush.Text.Muted";
    private const string AccentForegroundKey = "Brush.Accent.Foreground";
    private const string WindowBackgroundKey = "Brush.Window.Background";

    /// <summary>
    /// 從 WPF 資源取色：失敗 Danger、進行中 Warning、全部成功 Accent、無資料 Text Muted（spec §7.3），
    /// 形狀用 Accent Foreground（spec §12），角標是 Window Background 底加 Warning 色「!」（spec §7.3）。
    /// </summary>
    /// <param name="findResource">例如 <c>Application.TryFindResource</c>；找不到時回 null。</param>
    /// <exception cref="InvalidOperationException">找不到其中一個色彩資源，或它不是單色筆刷。</exception>
    public static TrayIconPalette FromResources(Func<object, object?> findResource)
    {
        ArgumentNullException.ThrowIfNull(findResource);

        return new TrayIconPalette(
            Failure: ColorOf(DangerKey),
            Pending: ColorOf(WarningKey),
            Success: ColorOf(AccentKey),
            NoData: ColorOf(MutedKey),
            Glyph: ColorOf(AccentForegroundKey),
            BadgeBackground: ColorOf(WindowBackgroundKey),
            BadgeMark: ColorOf(WarningKey));

        Drawing.Color ColorOf(string key) =>
            findResource(key) is Media.SolidColorBrush { Color: var color }
                ? Drawing.Color.FromArgb(color.A, color.R, color.G, color.B)
                : throw new InvalidOperationException($"找不到系統匣圖示要用的色彩資源 {key}。");
    }

    public Drawing.Color Fill(OverallState state) => state switch
    {
        OverallState.Failure => Failure,
        OverallState.Pending => Pending,
        OverallState.Success => Success,
        _ => NoData,
    };
}

/// <summary>
/// 系統匣的狀態圖示（spec §12、§7.3、§16.2），沿用 SAM 的做法：GDI+ 畫 <see cref="Tuning.TrayIconSize"/> 見方的點陣圖、
/// GetHicon 後 Clone、立即 DestroyIcon。
/// </summary>
/// <remarks>
/// <para>
/// 圓角方塊填狀態色，上面用 Accent Foreground 畫形狀：失敗 ✕、進行中或排隊三點、全部成功 ✓、無資料短橫線。
/// 形狀一律用線段與圓畫，不用文字，也不只靠顏色區分。有連線問題時，右下角疊加深色小圓加 Warning 色「!」。
/// </para>
/// <para>
/// 以下座標都在邊長 <see cref="Tuning.TrayIconSize"/>（32）的格線上，筆畫與間距是設計選擇：
/// 系統匣在 100% 縮放時把圖示縮成 16×16、150% 時縮成 24×24，筆畫 4 縮小後約 2 px／3 px，三個點之間還留得下空隙。
/// 不做動畫（spec §12、Q21）。
/// </para>
/// </remarks>
internal static class TrayIconFactory
{
    private const int IconSize = Tuning.TrayIconSize;

    /// <summary>圓角方塊的圓角半徑（同 SAM 的 8）。</summary>
    private const float CornerRadius = 8f;

    private const float QuarterTurn = 90f;

    /// <summary>格線中心（✕、三點、短橫線都以這裡為中心）。</summary>
    private const float Center = IconSize / 2f;

    /// <summary>✕、✓、短橫線的筆畫粗細。</summary>
    private const float GlyphStroke = 4f;

    /// <summary>✕ 兩臂與短橫線的端點離中心各 5.5（短橫線加上兩端圓頭後全長 15）。</summary>
    private const float CrossHalf = 5.5f;

    private const float DashHalf = 5.5f;

    /// <summary>✓ 的三個點：左端、轉折、右端。</summary>
    private static readonly Drawing.PointF[] CheckPoints = [new(8.5f, 16.5f), new(13.5f, 21.5f), new(23.5f, 11.5f)];

    /// <summary>三點「…」：半徑 2.5、間距 7.5，點與點之間留 2.5 的空隙，縮到 16×16 時仍分得開。</summary>
    private const float DotRadius = 2.5f;

    private const float DotSpacing = 7.5f;

    /// <summary>角標的圓心與半徑：外圈貼齊右下角。</summary>
    private const float BadgeCenter = IconSize - BadgeRadius - BadgeRing;

    private const float BadgeRadius = 7f;

    /// <summary>角標外圈用狀態色描一圈：在深色工作列上也看得出角標的輪廓，也和形狀隔開。</summary>
    private const float BadgeRing = 1.5f;

    /// <summary>
    /// 有角標時形狀往左上移，讓出右下角：形狀不縮小、也幾乎不被角標蓋到（✕ 的右下端只少掉一點圓頭）。
    /// </summary>
    private const float BadgeGlyphShift = 5f;

    /// <summary>
    /// 「!」的直線（上端、下端）與下方的點。含圓頭後直線佔格線 18–24、點佔 26–29，上下都離圓邊 1.5（= BadgeRadius 7 − 圓心離角標中心 4 − 半徑 1.5）。
    /// 中間空 2 格（24–26）：100% 縮放縮成 16×16 時一格是半個像素，這 2 格剛好是一整列像素，
    /// 直線和點才不會糊成一條；150% 縮成 24×24 時也還有一列。
    /// </summary>
    private const float MarkStroke = 3f;

    private const float MarkTop = BadgeCenter - 4f;

    private const float MarkBottom = BadgeCenter - 1f;

    private const float MarkDotY = BadgeCenter + 4f;

    private const float MarkDotRadius = 1.5f;

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>建立狀態圖示。呼叫端負責 Dispose。</summary>
    /// <param name="badge">有連線問題（未登入、全域暫停、離線、持續 5xx、API 版本錯誤，spec §7.3）時疊加角標。</param>
    public static Drawing.Icon Create(OverallState state, bool badge, TrayIconPalette palette)
    {
        using var bitmap = Render(state, badge, palette);

        // GetHicon 產生的是未受管理的控制代碼，Clone 出獨立副本後必須立刻銷毀，否則會漏 GDI 物件（同 SAM）。
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>畫出圖示的點陣圖（<see cref="Create"/> 與預覽共用）。呼叫端負責 Dispose。</summary>
    internal static Drawing.Bitmap Render(OverallState state, bool badge, TrayIconPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var bitmap = new Drawing.Bitmap(IconSize, IconSize, Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Drawing.Graphics.FromImage(bitmap);
            graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;

            // 像素中心在 (i + 0.5, i + 0.5)：格線座標 16 剛好是兩個像素的交界，形狀才會對稱。
            graphics.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
            graphics.Clear(Drawing.Color.Transparent);

            var fill = palette.Fill(state);
            using (var background = new Drawing.SolidBrush(fill))
            using (var square = RoundedSquare())
            {
                graphics.FillPath(background, square);
            }

            if (badge)
            {
                graphics.TranslateTransform(-BadgeGlyphShift, -BadgeGlyphShift);
                DrawGlyph(graphics, state, palette.Glyph);
                graphics.ResetTransform();
                DrawBadge(graphics, fill, palette);
            }
            else
            {
                DrawGlyph(graphics, state, palette.Glyph);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void DrawGlyph(Drawing.Graphics graphics, OverallState state, Drawing.Color color)
    {
        using var pen = RoundPen(color, GlyphStroke);
        using var brush = new Drawing.SolidBrush(color);
        switch (state)
        {
            case OverallState.Failure:
                graphics.DrawLine(pen, Center - CrossHalf, Center - CrossHalf, Center + CrossHalf, Center + CrossHalf);
                graphics.DrawLine(pen, Center + CrossHalf, Center - CrossHalf, Center - CrossHalf, Center + CrossHalf);
                break;
            case OverallState.Pending:
                foreach (var offset in (ReadOnlySpan<float>)[-DotSpacing, 0f, DotSpacing])
                {
                    FillCircle(graphics, brush, Center + offset, Center, DotRadius);
                }

                break;
            case OverallState.Success:
                graphics.DrawLines(pen, CheckPoints);
                break;
            default:
                graphics.DrawLine(pen, Center - DashHalf, Center, Center + DashHalf, Center);
                break;
        }
    }

    /// <summary>右下角的角標：先用狀態色描外圈（隔開底下的形狀），再畫深色圓與 Warning 色的「!」（直線＋點，不用文字）。</summary>
    private static void DrawBadge(Drawing.Graphics graphics, Drawing.Color ring, TrayIconPalette palette)
    {
        using (var ringBrush = new Drawing.SolidBrush(ring))
        {
            FillCircle(graphics, ringBrush, BadgeCenter, BadgeCenter, BadgeRadius + BadgeRing);
        }

        using (var background = new Drawing.SolidBrush(palette.BadgeBackground))
        {
            FillCircle(graphics, background, BadgeCenter, BadgeCenter, BadgeRadius);
        }

        using var markPen = RoundPen(palette.BadgeMark, MarkStroke);
        using var markBrush = new Drawing.SolidBrush(palette.BadgeMark);
        graphics.DrawLine(markPen, BadgeCenter, MarkTop, BadgeCenter, MarkBottom);
        FillCircle(graphics, markBrush, BadgeCenter, MarkDotY, MarkDotRadius);
    }

    private static Drawing.Pen RoundPen(Drawing.Color color, float width) => new(color, width)
    {
        StartCap = Drawing2D.LineCap.Round,
        EndCap = Drawing2D.LineCap.Round,
        LineJoin = Drawing2D.LineJoin.Round,
    };

    private static void FillCircle(Drawing.Graphics graphics, Drawing.Brush brush, float centerX, float centerY, float radius) =>
        graphics.FillEllipse(brush, centerX - radius, centerY - radius, radius * 2, radius * 2);

    /// <summary>填滿整張點陣圖的圓角方塊；四個角各畫四分之一圓弧（GDI+ 的角度從 3 點鐘方向順時針算）。</summary>
    private static Drawing2D.GraphicsPath RoundedSquare()
    {
        const float diameter = CornerRadius * 2;
        const float far = IconSize - diameter;
        var path = new Drawing2D.GraphicsPath();
        path.AddArc(0, 0, diameter, diameter, 2 * QuarterTurn, QuarterTurn);
        path.AddArc(far, 0, diameter, diameter, 3 * QuarterTurn, QuarterTurn);
        path.AddArc(far, far, diameter, diameter, 0, QuarterTurn);
        path.AddArc(0, far, diameter, diameter, QuarterTurn, QuarterTurn);
        path.CloseFigure();
        return path;
    }
}
