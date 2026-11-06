using System.Media;
using System.Windows;
using System.Windows.Input;

namespace IsItGreenYet.App.Views;

/// <summary>
/// 錯誤對話框：取代系統 MessageBox，外觀仿 Windows 11 的 ContentDialog（見 ErrorDialog.xaml）。
/// 由 <c>App.ShowError</c> 建立與顯示；顯示失敗時 App 退回系統 MessageBox。
/// </summary>
public partial class ErrorDialog : Window
{
    internal ErrorDialog(string title, string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;

        // 同系統 MessageBox 的錯誤圖示（MB_ICONERROR）播的音效。
        ContentRendered += (_, _) => SystemSounds.Hand.Play();
    }

    /// <summary>沒有標題列：在空白處（圖示、標題、按鈕列的空白）按住左鍵可以拖曳。訊息與按鈕自己處理滑鼠，不會走到這裡。</summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!e.Handled && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>訊息超過最大高度時用鍵盤捲動；Ctrl+C 複製標題與整段訊息（<see cref="DialogMessageKeys"/>）。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e) => DialogMessageKeys.Handle(MessageText, Title, e);
}
