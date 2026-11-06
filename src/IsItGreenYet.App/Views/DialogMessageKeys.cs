using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IsItGreenYet.App.Views;

/// <summary>
/// 唯讀訊息對話框（錯誤對話框、授權與第三方聲明）共用的按鍵，由對話框的 PreviewKeyDown 呼叫：
/// 用鍵盤捲動訊息（<see cref="TryScroll"/>），以及 Ctrl+C 複製標題加整段訊息。
/// 訊息用 Style.TextBox.Message（不是 Tab 停駐點，Tab 只在按鈕之間移動，同系統 MessageBox）。
/// </summary>
internal static class DialogMessageKeys
{
    /// <summary>訊息（Style.TextBox.Message）範本裡負責捲動的 ScrollViewer 名稱（TextBox 規定的 PART_ContentHost）。</summary>
    private const string MessageScrollerPart = "PART_ContentHost";

    /// <summary>
    /// 先試著捲動訊息；不是捲動鍵時處理 Ctrl+C：焦點在訊息、又有選取文字時，交給訊息照常複製選取的部分；
    /// 其他情況複製 <paramref name="title"/> 與整段訊息（同系統 MessageBox 的 Ctrl+C）。
    /// 訊息失去焦點後選取範圍還在、但畫面上看不到（WPF 不畫非使用中的選取），這時使用者以為沒有選取，所以也複製整段。
    /// </summary>
    internal static void Handle(TextBox message, string title, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(e);
        if (TryScroll(message, e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control || (message.IsKeyboardFocused && message.SelectionLength > 0))
        {
            return;
        }

        e.Handled = true;
        try
        {
            Clipboard.SetText(title + Environment.NewLine + message.Text);
        }
        catch (ExternalException)
        {
            // 剪貼簿被別的程式佔用：對話框裡不再疊另一個錯誤；稍後再按一次即可。
        }
    }

    /// <summary>
    /// 在對話框層級用鍵盤捲動訊息，不論焦點在按鈕還是訊息：↑／↓ 捲一行、PageUp／PageDown 捲一頁、Home／End（含 Ctrl）到頭尾。
    /// 訊息不是 Tab 停駐點，開啟時焦點也在按鈕，只用鍵盤的人原本捲不到超出最大高度的部分。
    /// 訊息裡的插入點不顯示，交給訊息自己處理時 ↑／↓ 移動看不到的插入點、到邊緣才捲動，所以焦點在訊息時也在這裡處理；
    /// 加 Shift 的鍵（延伸選取）照常交給訊息。訊息不需要捲動時回 false，按鍵照常交給焦點所在的元素。
    /// </summary>
    private static bool TryScroll(TextBox message, Key key, ModifierKeys modifiers)
    {
        if (message.Template?.FindName(MessageScrollerPart, message) is not ScrollViewer { ScrollableHeight: > 0 } scroller)
        {
            return false;
        }

        Action? scroll = (key, modifiers) switch
        {
            (Key.Up, ModifierKeys.None) => scroller.LineUp,
            (Key.Down, ModifierKeys.None) => scroller.LineDown,
            (Key.PageUp, ModifierKeys.None) => scroller.PageUp,
            (Key.PageDown, ModifierKeys.None) => scroller.PageDown,
            (Key.Home, ModifierKeys.None or ModifierKeys.Control) => scroller.ScrollToTop,
            (Key.End, ModifierKeys.None or ModifierKeys.Control) => scroller.ScrollToBottom,
            _ => null,
        };
        scroll?.Invoke();
        return scroll is not null;
    }
}
