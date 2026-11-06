using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace IsItGreenYet.App.Themes;

/// <summary>
/// 選單（Style.ContextMenu）的項目面板：排法同 StackPanel，量測時順便決定要不要留勾選欄。
/// </summary>
/// <remarks>
/// <para>
/// Windows 11 的選單只有在有項目可以勾選時，才在文字左邊留勾選欄。量測時看可見的項目，沒有任何一項會顯示勾，
/// 就把選單的 <see cref="HasCheckColumnProperty"/> 設成 false（往下繼承給項目，Style.MenuItem 收掉勾選欄）。
/// 「會顯示勾」是可勾選（IsCheckable）、已勾選，或 IsChecked 有綁定（run 選單的「這筆完成時通知我」、自動更新的選項）。
/// </para>
/// <para>
/// 在量測時決定，而不是在選單的 Opened 事件：popup 建立視窗時就同步量測並畫出第一個畫面，Opened 在那之後才觸發，
/// 那時再改，第一個畫面會是錯的寬度（2026-10-26 測試殼確認）。項目顯示或隱藏時面板會重新量測，跟著重算。
/// </para>
/// </remarks>
public sealed class MenuItemsPanel : StackPanel
{
    /// <summary>選單裡有沒有項目會顯示勾（設在選單上，繼承給項目）；預設 true，保留勾選欄。</summary>
    public static readonly DependencyProperty HasCheckColumnProperty = DependencyProperty.RegisterAttached(
        "HasCheckColumn",
        typeof(bool),
        typeof(MenuItemsPanel),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetHasCheckColumn(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(HasCheckColumnProperty);
    }

    public static void SetHasCheckColumn(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(HasCheckColumnProperty, value);
    }

    /// <summary>先決定勾選欄（項目隨之改版面），再照 StackPanel 量測項目。</summary>
    protected override Size MeasureOverride(Size constraint)
    {
        if (ItemsControl.GetItemsOwner(this) is { } menu)
        {
            var hasCheckColumn = InternalChildren.OfType<MenuItem>().Any(CanShowCheck);
            if (GetHasCheckColumn(menu) != hasCheckColumn)
            {
                SetHasCheckColumn(menu, hasCheckColumn);
            }
        }

        return base.MeasureOverride(constraint);
    }

    private static bool CanShowCheck(MenuItem item) =>
        item.Visibility == Visibility.Visible
        && (item.IsCheckable || item.IsChecked || BindingOperations.IsDataBound(item, MenuItem.IsCheckedProperty));
}
