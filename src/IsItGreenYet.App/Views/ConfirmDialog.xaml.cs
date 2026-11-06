using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using IsItGreenYet.Core.Localization;

namespace IsItGreenYet.App.Views;

/// <summary>
/// 寫入的確認對話框（第二階段 spec §9.2，見 ConfirmDialog.xaml）：<c>ShowDialog() == true</c> 代表確認；
/// 「不要」、Esc 都是 false。文字由呼叫端在按下選單項目的當下用 <see cref="WriteText.Confirm"/> 組好，開著時切換語言不更新。
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <summary>取消類的執行按鈕（Themes/Controls.xaml）。</summary>
    private const string DangerButtonStyleKey = "Style.Button.Danger";

    /// <summary>重跑類的執行按鈕（Themes/Controls.xaml）。</summary>
    private const string AccentButtonStyleKey = "Style.Button.Accent";

    /// <summary>訊息裡「是哪一筆」與說明之間空一行（同錯誤訊息的分段）。</summary>
    private const string ParagraphBreak = "\n\n";

    internal ConfirmDialog(ConfirmText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        InitializeComponent();
        Title = text.Title;
        TitleText.Text = text.Title;
        MessageText.Text = text.Target + ParagraphBreak + text.Body;

        // 訊息框的報讀名稱：錯誤對話框用「錯誤訊息」，這裡用對話框的問題本身。
        AutomationProperties.SetName(MessageText, text.Title);
        NoButton.Content = text.CancelLabel;
        ConfirmButton.Content = text.ConfirmLabel;
        ConfirmButton.Style = (Style)FindResource(text.Danger ? DangerButtonStyleKey : AccentButtonStyleKey);
    }

    /// <summary>沒有標題列：在空白處（標題、按鈕列的空白）按住左鍵可以拖曳（同錯誤對話框）。</summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!e.Handled && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>訊息超過最大高度時用鍵盤捲動；Ctrl+C 複製標題與整段訊息（<see cref="DialogMessageKeys"/>）。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e) => DialogMessageKeys.Handle(MessageText, Title, e);
}
