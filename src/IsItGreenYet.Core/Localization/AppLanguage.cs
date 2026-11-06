using System.Globalization;

namespace IsItGreenYet.Core.Localization;

/// <summary>
/// 介面語言（設定頁的「語言／Language」）。settings.json 的值是 <c>zh-Hant</c>／<c>en</c>（<see cref="AppLanguages"/>）。
/// 列舉的預設值（0）是繁體中文：設定檔已經存在、但沒有這個欄位的既有使用者維持原本的中文介面。
/// </summary>
public enum AppLanguage
{
    TraditionalChinese,
    English,
}

/// <summary>語言的設定值、預設值與對應的文化。</summary>
public static class AppLanguages
{
    /// <summary>settings.json 裡繁體中文的值（BCP 47 的書寫系統標記，不綁地區）。</summary>
    public const string TraditionalChineseCode = "zh-Hant";

    /// <summary>settings.json 裡英文的值。</summary>
    public const string EnglishCode = "en";

    /// <summary>Windows 顯示語言是中文時，ISO 639-1 的語言代碼（zh-TW、zh-CN、zh-Hant-HK 等都是 zh）。</summary>
    private const string ChineseIsoCode = "zh";

    /// <summary>設定介面語言時，UI 執行緒用的文化：WPF 內建的文字（例如輸入框的「剪下／複製／貼上」）跟著它找資源。</summary>
    private const string TraditionalChineseCulture = "zh-TW";

    private const string EnglishCulture = "en-US";

    /// <summary>既有使用者（settings.json 已經存在）與不認得的值的預設。</summary>
    public const AppLanguage Default = AppLanguage.TraditionalChinese;

    /// <summary>
    /// 第一次執行（沒有 settings.json）時的預設：Windows 顯示語言是任何一種中文（zh-*）用繁體中文，其他用英文。
    /// </summary>
    public static AppLanguage FromDisplayCulture(CultureInfo? culture) =>
        culture is not null && string.Equals(culture.TwoLetterISOLanguageName, ChineseIsoCode, StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.TraditionalChinese
            : AppLanguage.English;

    /// <summary>不認得的列舉值（例如程式裡強制轉型的數字）回預設值。</summary>
    public static AppLanguage Normalize(AppLanguage language) => Enum.IsDefined(language) ? language : Default;

    /// <summary>UI 執行緒的 CurrentUICulture 要設成的文化。</summary>
    public static CultureInfo UiCulture(AppLanguage language) =>
        CultureInfo.GetCultureInfo(Normalize(language) == AppLanguage.English ? EnglishCulture : TraditionalChineseCulture);
}
