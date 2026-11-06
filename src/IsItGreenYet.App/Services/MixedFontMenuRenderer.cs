using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace IsItGreenYet.App.Services;

/// <summary>
/// 系統匣右鍵選單（WinForms）的文字照 <c>FontFamily.Default</c> 的字型清單逐字挑字型，和 WPF 的畫面一樣：
/// 中文與全形標點用微軟正黑體，英文、數字與符號用 Consolas。
/// </summary>
/// <remarks>
/// <para>
/// WinForms 的 Font 只能指定一個字型，缺字時由 GDI 自己找替代字型（不一定是清單裡的中文字型），
/// 所以這裡把每一項的文字切成幾段，每段用清單裡第一個有這些字的字型畫，各段的基線對齊。
/// 哪個字型有哪些字，用 WPF 讀同一個字型檔的字元表（<see cref="GlyphTypeface.CharacterToGlyphMap"/>）判斷，和 WPF 挑字型的結果一致。
/// </para>
/// <para>
/// 清單裡的字型由 WPF 解析，只用裝在這台電腦上的字型（字型檔是 file URI），照名稱建立 GDI+ 字型。
/// 程式不內嵌字型；清單裡如果出現組件資源裡的字型（pack URI），GDI 用不到，略過。
/// </para>
/// <para>
/// 選單本身的 Font 要設成 <see cref="PrimaryFont"/>（清單第一個字型）：WinForms 用它量每一項的寬度與高度。
/// 英文段量的就是實際畫的字型；中文不論落到哪個字型都是全形一個字寬，量出來的寬度和實際畫的一樣。
/// </para>
/// <para>含 &amp; 快速鍵、直書、不是靠左對齊、或整段都用第一個字型的文字，交給基底類別照常畫。</para>
/// <para>顏色、項目底色、分隔線與外框來自基底類別 <see cref="TrayMenuRenderer"/>（深色外觀）。</para>
/// </remarks>
internal sealed class MixedFontMenuRenderer : TrayMenuRenderer, IDisposable
{
    /// <summary>FontFamily.Default 的字型清單以逗號分隔（同 WPF 的寫法）。</summary>
    private const char FamilySeparator = ',';

    /// <summary>WPF 字型參考「位置#家族名稱」的分隔字元（例如 <c>file:///C:/Windows/Fonts/#Consolas</c>）；沒有位置時整段就是家族名稱。</summary>
    private const char LocationSeparator = '#';

    /// <summary>WinForms 選單文字的快速鍵標記；含這個字元的文字交給基底類別處理。</summary>
    private const char AccessKeyMarker = '&';

    /// <summary>每段文字自己量寬、自己定位：不加 GDI 的左右留白、不處理快速鍵、單行。</summary>
    private const Forms.TextFormatFlags RunFormat =
        Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.SingleLine;

    /// <summary>基底類別畫文字時的對齊方式：只處理靠左（Left 是 0，有這兩個位元就不是靠左）。</summary>
    private const Forms.TextFormatFlags NotLeftAligned = Forms.TextFormatFlags.HorizontalCenter | Forms.TextFormatFlags.Right;

    private static readonly Drawing.Size Unbounded = new(int.MaxValue, int.MaxValue);

    private readonly IReadOnlyList<FontSlot> _slots;

    private MixedFontMenuRenderer(IReadOnlyList<FontSlot> slots, TrayMenuTheme theme)
        : base(theme)
    {
        _slots = slots;
    }

    /// <summary>清單裡第一個用得到的字型（選單的 Font 用這個）。</summary>
    internal Drawing.Font PrimaryFont => _slots[0].Font;

    /// <summary>
    /// 依 WPF 的字型清單（例如 <c>Consolas, Microsoft JhengHei UI</c>，相對位置以
    /// <see cref="System.Windows.Media.FontFamily.BaseUri"/> 為準）建立；清單裡沒有一個字型用得到（這台電腦沒裝）時回 null，
    /// 呼叫端維持選單原本的字型。
    /// </summary>
    /// <param name="emSizeInPoints">字級（點）；用選單原本的字級，只換字型。</param>
    /// <param name="theme">選單的深色外觀（<see cref="TrayMenuRenderer"/>）。</param>
    internal static MixedFontMenuRenderer? TryCreate(System.Windows.Media.FontFamily family, float emSizeInPoints, TrayMenuTheme theme)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(theme);
        var slots = new List<FontSlot>();
        foreach (var name in family.Source.Split(FamilySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // 只用裝在這台電腦上的字型（file URI）；組件資源裡的字型 GDI 用不到。
            if (!TryGetGlyphTypeface(family.BaseUri, name, out var glyphTypeface) || !glyphTypeface.FontUri.IsFile)
            {
                continue;
            }

            if (TryCreateInstalledFont(name[(name.LastIndexOf(LocationSeparator) + 1)..], emSizeInPoints) is { } font)
            {
                slots.Add(new FontSlot(glyphTypeface.CharacterToGlyphMap, font));
            }
        }

        return slots.Count > 0 ? new MixedFontMenuRenderer(slots, theme) : null;
    }

    public void Dispose()
    {
        foreach (var slot in _slots)
        {
            slot.Font.Dispose();
        }
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // 分段畫與交給基底類別畫都在置中後的文字區裡（基底類別再呼叫一次結果相同）。
        CenterTextVertically(e);
        var runs = e.TextDirection == Forms.ToolStripTextDirection.Horizontal
            && (e.TextFormat & NotLeftAligned) == 0
            && e.Text is { Length: > 0 } text
            && !text.Contains(AccessKeyMarker, StringComparison.Ordinal)
                ? SplitRuns(text)
                : null;
        if (runs is null || (runs.Count == 1 && runs[0].Slot == _slots[0]) || !TryDrawRuns(e, runs))
        {
            base.OnRenderItemText(e);
        }
    }

    /// <summary>
    /// 依序畫每一段：起點和基底類別一樣（文字區左邊加上 GDI 的左留白），整行在文字區裡垂直置中，各段的基線對齊。
    /// 取不到字型的行距資料時回 false，交給基底類別畫。
    /// </summary>
    private bool TryDrawRuns(Forms.ToolStripItemTextRenderEventArgs e, IReadOnlyList<TextRun> runs)
    {
        var g = e.Graphics;
        var metrics = new List<(TextRun Run, int Width, int Ascent)>(runs.Count);
        var ascent = 0;
        var descent = 0;
        foreach (var run in runs)
        {
            if (!TryGetAscentDescent(g, run.Slot.Font, out var runAscent, out var runDescent))
            {
                return false;
            }

            ascent = Math.Max(ascent, runAscent);
            descent = Math.Max(descent, runDescent);
            metrics.Add((run, Forms.TextRenderer.MeasureText(g, run.Text, run.Slot.Font, Unbounded, RunFormat).Width, runAscent));
        }

        // 基底類別用 e.TextFormat（有 GDI 的左右留白）畫在文字區裡：留白是同一段文字加與不加留白的寬度差的一半。
        var font = e.TextFont ?? runs[0].Slot.Font;
        var padded = Forms.TextRenderer.MeasureText(g, runs[0].Text, font, Unbounded, e.TextFormat & ~Forms.TextFormatFlags.NoPadding);
        var tight = Forms.TextRenderer.MeasureText(g, runs[0].Text, font, Unbounded, e.TextFormat | Forms.TextFormatFlags.NoPadding);
        var leftPadding = (padded.Width - tight.Width) / 2;

        var bounds = e.TextRectangle;
        var lineTop = bounds.Top + ((bounds.Height - (ascent + descent)) / 2);
        var baseline = lineTop + ascent;
        var x = bounds.Left + leftPadding;
        var color = TextColorOf(e);
        foreach (var (run, width, runAscent) in metrics)
        {
            Forms.TextRenderer.DrawText(g, run.Text, run.Slot.Font, new Drawing.Point(x, baseline - runAscent), color, RunFormat);
            x += width;
        }

        return true;
    }

    /// <summary>
    /// 文字顏色照基底類別的規則：選取（滑過）中的選單項目用項目自己的前景色（ToolStripProfessionalRenderer），
    /// 停用時用 Text.Muted（<see cref="TrayMenuRenderer"/>；WinForms 原本是 SystemColors.GrayText）。
    /// </summary>
    private Drawing.Color TextColorOf(Forms.ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is { Enabled: false })
        {
            return Theme.DisabledText;
        }

        return e.Item is Forms.ToolStripMenuItem item && (item.Selected || item.Pressed) ? item.ForeColor : e.TextColor;
    }

    /// <summary>把文字切成段：每個字用清單裡第一個有這個字的字型，相鄰同字型的字併成一段；每個字型都沒有的字用第一個字型（GDI 自己找替代字型）。</summary>
    private List<TextRun> SplitRuns(string text)
    {
        var runs = new List<TextRun>();
        var builder = new StringBuilder();
        FontSlot? current = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var slot = _slots.FirstOrDefault(s => s.Characters.ContainsKey(rune.Value)) ?? _slots[0];
            if (current is not null && slot != current)
            {
                runs.Add(new TextRun(builder.ToString(), current));
                builder.Clear();
            }

            current = slot;
            builder.Append(rune.ToString());
        }

        if (current is not null)
        {
            runs.Add(new TextRun(builder.ToString(), current));
        }

        return runs;
    }

    /// <summary>
    /// WPF 讀得到這個字型時，取它的 Regular 字型檔（選單只用 Regular）；
    /// 字型檔壞掉、讀不到、或相對位置沒有基準可以解析時，當成沒有這個字型。
    /// </summary>
    private static bool TryGetGlyphTypeface(Uri? baseUri, string name, [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        try
        {
            var typeface = new Typeface(new System.Windows.Media.FontFamily(baseUri, name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out glyphTypeface))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or InvalidOperationException)
        {
            // 當成沒有這個字型，改用清單裡的下一個。
        }

        glyphTypeface = null;
        return false;
    }

    /// <summary>
    /// 裝在這台電腦上的字型：照名稱建立。
    /// 字型不存在時 GDI+ 不丟例外，而是換成替代字型（Microsoft Sans Serif）：名稱不同就表示 GDI+ 找不到，當成沒有這個字型。
    /// </summary>
    private static Drawing.Font? TryCreateInstalledFont(string familyName, float emSizeInPoints)
    {
        var font = new Drawing.Font(familyName, emSizeInPoints, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Point);
        if (string.Equals(font.Name, familyName, StringComparison.OrdinalIgnoreCase))
        {
            return font;
        }

        font.Dispose();
        return null;
    }

    /// <summary>GDI 畫這個字型時的上升與下降高度（像素，TEXTMETRIC 的 tmAscent／tmDescent），TextRenderer 就是依這兩個值擺基線。</summary>
    private static bool TryGetAscentDescent(Drawing.Graphics graphics, Drawing.Font font, out int ascent, out int descent)
    {
        ascent = 0;
        descent = 0;
        var hdc = graphics.GetHdc();
        var hfont = font.ToHfont();
        try
        {
            var previous = SelectObject(hdc, hfont);
            try
            {
                if (!GetTextMetrics(hdc, out var metrics))
                {
                    return false;
                }

                ascent = metrics.Ascent;
                descent = metrics.Descent;
                return true;
            }
            finally
            {
                SelectObject(hdc, previous);
            }
        }
        finally
        {
            DeleteObject(hfont);
            graphics.ReleaseHdc(hdc);
        }
    }

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll", EntryPoint = "GetTextMetricsW", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTextMetrics(IntPtr hdc, out TextMetric metrics);

    /// <summary>一個字型與它有哪些字。</summary>
    private sealed record FontSlot(IDictionary<int, ushort> Characters, Drawing.Font Font);

    /// <summary>同一個字型的一段文字。</summary>
    private sealed record TextRun(string Text, FontSlot Slot);

    /// <summary>Win32 的 TEXTMETRICW。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TextMetric
    {
        public int Height;
        public int Ascent;
        public int Descent;
        public int InternalLeading;
        public int ExternalLeading;
        public int AveCharWidth;
        public int MaxCharWidth;
        public int Weight;
        public int Overhang;
        public int DigitizedAspectX;
        public int DigitizedAspectY;
        public char FirstChar;
        public char LastChar;
        public char DefaultChar;
        public char BreakChar;
        public byte Italic;
        public byte Underlined;
        public byte StruckOut;
        public byte PitchAndFamily;
        public byte CharacterSet;
    }
}
