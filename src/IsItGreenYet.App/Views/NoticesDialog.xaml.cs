using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace IsItGreenYet.App.Views;

/// <summary>
/// 授權與第三方聲明：顯示內嵌的 LICENSE 與 THIRD-PARTY-NOTICES.md 全文（見 NoticesDialog.xaml）。
/// 由設定頁最下方的入口開啟；關閉後回到設定頁，不影響尚未儲存的變更。
/// </summary>
public partial class NoticesDialog : Window
{
    internal NoticesDialog(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        InitializeComponent();
        MessageText.Text = text;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 可縮放的無邊框視窗最大化時，限制在所在螢幕的工作區內（同設定頁）。
        if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is { } source)
        {
            source.AddHook(Interop.WindowPlacement.FilterMessage);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>用鍵盤捲動內文；Ctrl+C 複製標題與全文（<see cref="DialogMessageKeys"/>，同錯誤對話框）。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e) => DialogMessageKeys.Handle(MessageText, Title, e);
}
