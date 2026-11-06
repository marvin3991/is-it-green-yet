using System.Windows;
using System.Windows.Controls;

namespace IsItGreenYet.App.Themes;

/// <summary>
/// 選單項目的樣式：MenuItem（含 WPF 內部的子類別）套 <see cref="MenuItemStyle"/>，分隔線不套（由 WPF 套 MenuItem.SeparatorStyleKey 的樣式）。
/// </summary>
/// <remarks>
/// WPF 內建的捲軸選單有分隔線。選單直接設 ItemContainerStyle 時，WPF 在產生項目時會把 MenuItem 的樣式也套到分隔線上而丟例外
/// （2026-10-26 測試殼確認：「無法將適用於類型 'MenuItem' 的樣式套用到類型 'Separator'」），所以改用這個選擇器。
/// </remarks>
public sealed class MenuItemStyleSelector : StyleSelector
{
    /// <summary>MenuItem 用的樣式。</summary>
    public Style? MenuItemStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) =>
        container is MenuItem ? MenuItemStyle : null;
}
