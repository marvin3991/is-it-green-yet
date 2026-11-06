using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using IsItGreenYet.App.Interop;

namespace IsItGreenYet.App.Themes;

/// <summary>
/// WPF 內建的右鍵選單（TextBox、PasswordBox 的「剪下／複製／貼上」，捲軸的「捲動這裡…」）改用程式的深色選單樣式。
/// </summary>
/// <remarks>
/// <para>
/// 這些選單由 WPF 自己建立，沒有程式指定的樣式，原本是系統主題的淺色外觀。
/// 捲軸的選單是一般的 ContextMenu，Themes/Controls.xaml 的隱含樣式（TargetType 是 ContextMenu）就套得上；
/// TextBox、PasswordBox 的選單是 WPF 內部的子類別，隱含樣式只認完全相同的型別，所以啟動時用那個型別當鍵，再登記一次同一個樣式。
/// 隱含樣式在選單建立時就套上，popup 畫出的第一個畫面就是深色（2026-10-26 測試殼確認）。
/// </para>
/// <para>
/// 找不到那個內部型別時（以後的 .NET 改了名稱或做法），改在選單的 Opened 事件補套：那時已經畫過第一個畫面，
/// 第一次開會先閃一下淺色的選單，之後同型別的選單建立時就套上。
/// </para>
/// <para>
/// 不用自訂的選單取代它們：TextBox 的行為（唯讀時只在選取範圍上才開、Shift+F10 開在插入點旁）維持 WPF 原本的做法。
/// </para>
/// </remarks>
public static class BuiltinMenus
{
    /// <summary>TextBox、PasswordBox 右鍵選單的型別（PresentationFramework 的內部類別，.NET 10 的名稱）。</summary>
    private const string EditorMenuTypeName = "System.Windows.Documents.TextEditorContextMenu+EditorContextMenu";

    private static bool s_registered;

    /// <summary>程式啟動時（App.OnStartup，顯示任何視窗之前）呼叫一次；重複呼叫不會重複登記。</summary>
    public static void Register(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (s_registered)
        {
            return;
        }

        s_registered = true;
        if (app.TryFindResource(typeof(ContextMenu)) is Style style
            && typeof(TextBoxBase).Assembly.GetType(EditorMenuTypeName) is { } editorMenuType
            && typeof(ContextMenu).IsAssignableFrom(editorMenuType))
        {
            app.Resources[editorMenuType] = style;
        }

        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnOpened));
    }

    /// <summary>還是沒有樣式的選單（Style 是 null，只有系統主題的外觀）：補套隱含樣式，並以它的型別登記，之後建立時就套上。</summary>
    private static void OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { Style: null } menu
            || !ReferenceEquals(e.OriginalSource, menu)
            || Application.Current is not { } app
            || app.TryFindResource(typeof(ContextMenu)) is not Style style)
        {
            return;
        }

        app.Resources[menu.GetType()] = style;
        menu.Style = style;

        // DWM 的圓角與框線色在 popup 接上視窗時套用（DwmFrame），這時視窗已經接上了，用新樣式補套一次。
        DwmFrame.Refresh(menu);
    }
}
