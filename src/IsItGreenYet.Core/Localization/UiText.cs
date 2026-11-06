using System.Globalization;

namespace IsItGreenYet.Core.Localization;

/// <summary>
/// 取畫面文字的入口：<see cref="Of"/> 依語言給整張字串表，<see cref="Current"/> 是共用的目前語言。
/// </summary>
/// <remarks>
/// <para>
/// 語言一律明確傳入或讀 <see cref="Current"/>，不看執行緒的 CurrentUICulture（Engine 跑在背景執行緒）。
/// App 在 UI 執行緒設定 <see cref="Current"/>（啟動時讀設定、設定頁儲存時切換）；Engine 用自己設定裡的語言明確傳入，不讀這裡。
/// Core 各單元沒有語言參數的多載讀 <see cref="Current"/>，給 App 用，也讓既有的測試照原本的寫法在繁體中文下跑。
/// </para>
/// <para>數字一律用 InvariantCulture 格式化（例如 <c>4870/5000</c> 不加千分位），兩種語言相同。</para>
/// </remarks>
public static class UiText
{
    private static int s_current = (int)AppLanguages.Default;

    /// <summary>共用的目前語言；可以從任何執行緒讀寫。不認得的值當成預設值。</summary>
    public static AppLanguage Current
    {
        get => (AppLanguage)Volatile.Read(ref s_current);
        set => Volatile.Write(ref s_current, (int)AppLanguages.Normalize(value));
    }

    /// <summary>這個語言的字串表。</summary>
    public static TextTable Of(AppLanguage language) =>
        AppLanguages.Normalize(language) == AppLanguage.English ? TextTables.English : TextTables.TraditionalChinese;

    /// <summary>字串表裡的格式字串（<c>{0}</c>…）填入參數；數字用 InvariantCulture。</summary>
    public static string Format(string format, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, format, args);

    /// <summary>
    /// 依數量選單數或複數的格式字串再填入參數（英文的 1 run／2 runs；中文兩種寫法相同）。
    /// <paramref name="count"/> 填在 <c>{0}</c>，其他參數接在後面。
    /// </summary>
    public static string Plural(long count, string one, string other, params object?[] more) =>
        Format(count == 1 ? one : other, [count, .. more]);

    /// <summary>
    /// spec §10.2「快取資料庫損毀已復原」，App 啟動時與監看引擎共用。<paramref name="backupFileName"/> 是改名保留的檔名（不含資料夾）；
    /// 這次執行沒有留下任何備份（主檔已經不在，沒有東西可以改名保留）時是 null，不能顯示空的檔名。
    /// </summary>
    public static string DatabaseRebuilt(string? backupFileName, AppLanguage language) => backupFileName is null
        ? Of(language).BannerDatabaseRebuiltNoBackup
        : Format(Of(language).BannerDatabaseRecovered, backupFileName);
}
