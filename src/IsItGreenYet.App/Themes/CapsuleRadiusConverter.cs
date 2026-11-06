using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IsItGreenYet.App.Themes;

/// <summary>
/// 膠囊形（兩端半圓、中間直線）的圓角半徑：寬、高較短那一邊的一半。
/// </summary>
/// <remarks>
/// <para>
/// 不能寫死一個很大的半徑（原本的 Radius.Chip＝999）：Border 的圓角超過邊長時會依比例縮小，
/// 寬 124、高 28 的 chip 會變成水平半徑 62、垂直半徑 14 的橢圓角，整個 chip 看起來是橢圓；
/// Rectangle 的 RadiusX／RadiusY 則是各自限制在寬、高的一半，結果一樣是橢圓（2026-10-26 量測）。
/// </para>
/// <para>
/// 用法：MultiBinding 依序綁 ActualWidth、ActualHeight（RelativeSource Self）。
/// 目標是 <see cref="CornerRadius"/>（Border）時回傳四角相同的 CornerRadius，目標是 double（Rectangle 的 RadiusX／RadiusY）時回傳半徑。
/// 尺寸還沒算出來（0 或不是有效數字）時回傳 0。
/// </para>
/// </remarks>
public sealed class CapsuleRadiusConverter : IMultiValueConverter
{
    /// <summary>半徑是較短那一邊的一半。</summary>
    private const double Halves = 2;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var radius = values is [double width, double height] && double.IsFinite(width) && double.IsFinite(height)
            ? Math.Max(0, Math.Min(width, height) / Halves)
            : 0;
        return targetType == typeof(CornerRadius) ? new CornerRadius(radius) : radius;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
