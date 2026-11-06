using System.ComponentModel;
using System.Globalization;
using IsItGreenYet.Core.Localization;

namespace IsItGreenYet.App.Localization;

/// <summary>
/// App 的介面語言：XAML 繫結的字串來源（<see cref="TExtension"/>），以及程式碼取字串的入口（<see cref="Texts"/>）。
/// 只在 UI 執行緒使用。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Apply"/> 換語言時：設定共用的目前語言（<see cref="UiText.Current"/>，Core 沒有語言參數的多載讀它）、
/// 把 UI 執行緒的 CurrentUICulture 換成對應的文化（WPF 內建的文字跟著它找資源），
/// 通知 <see cref="Table"/> 改變（所有 <c>{l:T 名稱}</c> 繫結即時更新），最後觸發 <see cref="LanguageChanged"/>，
/// 讓程式碼算出來的文字（ViewModel、系統匣選單與 tooltip、視窗的 tooltip）重算。
/// </para>
/// <para>
/// WPF 內建的輸入框右鍵選單（剪下／複製／貼上）的文字由 WPF 第一次用到時依 CurrentUICulture 載入、之後沿用，
/// 執行中切換不保證跟著換，重新啟動後一定是設定的語言（design-notes 的「語言」一節）。
/// 繁體中文的這些文字在 WPF 的 zh-Hant 衛星資源。目前只發相依框架版，所以來自目標電腦的 .NET Desktop Runtime
/// （只裝 Runtime 時在不在還沒驗證，見手動測試 M27）。
/// </para>
/// </remarks>
internal sealed class Loc : INotifyPropertyChanged
{
    private Loc()
    {
    }

    public static Loc Instance { get; } = new();

    /// <summary>程式碼用的目前語言的字串表。</summary>
    public static TextTable Texts => UiText.Of(UiText.Current);

    /// <summary>XAML 繫結的路徑起點（<c>Table.名稱</c>）；換語言時通知改變。</summary>
    public TextTable Table => Texts;

    public AppLanguage Language => UiText.Current;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>換語言之後（XAML 繫結已經更新）觸發，讓程式碼算出來的文字重算。</summary>
    public event Action? LanguageChanged;

    /// <summary>字串表的格式字串填入參數（數字用 InvariantCulture）。</summary>
    public static string Format(string format, params object?[] args) => UiText.Format(format, args);

    /// <summary>換成 <paramref name="language"/>；和目前的語言相同時只確保 UI 執行緒的文化正確，不觸發事件。</summary>
    public void Apply(AppLanguage language)
    {
        language = AppLanguages.Normalize(language);
        CultureInfo.CurrentUICulture = AppLanguages.UiCulture(language);
        if (UiText.Current == language)
        {
            return;
        }

        UiText.Current = language;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Table)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));

        // 逐一通知：其中一個處理丟例外，其他照樣重算（例如系統匣失敗，主畫面仍要換語言），全部做完才往外丟。
        var failures = new List<Exception>();
        foreach (var handler in LanguageChanged?.GetInvocationList().Cast<Action>() ?? [])
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(failures);
        }
    }
}
