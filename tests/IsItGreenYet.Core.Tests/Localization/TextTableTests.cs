using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using IsItGreenYet.Core.Localization;

namespace IsItGreenYet.Core.Tests.Localization;

/// <summary>
/// 兩種語言的字串表（<see cref="TextTable"/>）：項目完全一致、沒有空值、格式字串的參數一致、英文沒有漏翻。
/// App 的 XAML 用到的名稱見 AppXamlTextTests。
/// </summary>
public class TextTableTests
{
    /// <summary>單數與複數一組的名稱結尾（UiText.Plural）。</summary>
    private const string OneSuffix = "One";
    private const string OtherSuffix = "Other";

    /// <summary>DateTimeOffset 自訂格式的項目（不是 string.Format 的格式字串）。</summary>
    private static readonly string[] DateFormatProperties = [nameof(TextTable.TimeThisYearFormat), nameof(TextTable.TimeOlderFormat)];

    /// <summary>英文表裡可以有中文的項目：語言選項一律用自己的語言顯示，標題附上另一種語言，切錯語言時也找得到。</summary>
    private static readonly string[] EnglishMayContainChinese =
        [nameof(TextTable.SettingsLanguage), nameof(TextTable.SettingsLanguageTraditionalChinese)];

    /// <summary>格式字串的參數：<c>{0}</c>、<c>{1:00}</c>…（<c>{{</c> 是跳脫的大括號，不算）。</summary>
    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.CultureInvariant);

    internal static readonly PropertyInfo[] Properties = typeof(TextTable)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.PropertyType == typeof(string))
        .ToArray();

    public static TheoryData<AppLanguage> Languages => [AppLanguage.TraditionalChinese, AppLanguage.English];

    [Fact]
    public void 字串表的每一項都是字串而且兩種語言的項目完全一致()
    {
        // 所有公開屬性都是字串（不會有漏掉檢查的其他型別）。
        Assert.Equal(typeof(TextTable).GetProperties(BindingFlags.Public | BindingFlags.Instance).Length, Properties.Length);
        Assert.NotEmpty(Properties);

        var chinese = Values(AppLanguage.TraditionalChinese);
        var english = Values(AppLanguage.English);
        Assert.Equal(chinese.Keys.Order(StringComparer.Ordinal), english.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void 沒有空值(AppLanguage language)
    {
        var empty = Values(language).Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).ToList();

        Assert.Empty(empty);
    }

    [Fact]
    public void 兩種語言的格式字串參數相同()
    {
        var chinese = Values(AppLanguage.TraditionalChinese);
        var english = Values(AppLanguage.English);

        var mismatched = chinese.Keys
            .Where(key => !Placeholders(chinese[key]).SetEquals(Placeholders(english[key])))
            .ToList();

        Assert.Empty(mismatched);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void 格式字串都填得進參數(AppLanguage language)
    {
        foreach (var (key, value) in Values(language))
        {
            if (DateFormatProperties.Contains(key))
            {
                continue;
            }

            // 參數個數取最大的編號 + 1，填入會用到的樣本值；格式不合法時 string.Format 丟 FormatException。
            var count = Placeholders(value).Select(index => index + 1).DefaultIfEmpty(0).Max();
            var args = Enumerable.Range(0, count).Select(index => (object)("arg" + index)).ToArray();
            var formatted = UiText.Format(value, args);
            for (var index = 0; index < count; index++)
            {
                Assert.Contains("arg" + index, formatted, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void 日期格式是合法的自訂格式(AppLanguage language)
    {
        var sample = new DateTimeOffset(2025, 12, 31, 23, 50, 0, TimeSpan.Zero);
        var text = UiText.Of(language);

        Assert.False(string.IsNullOrWhiteSpace(sample.ToString(text.TimeThisYearFormat, CultureInfo.InvariantCulture)));
        Assert.False(string.IsNullOrWhiteSpace(sample.ToString(text.TimeOlderFormat, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void 單數與複數成對出現()
    {
        var names = Properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var name in names.Where(name => name.EndsWith(OneSuffix, StringComparison.Ordinal)))
        {
            Assert.Contains(name[..^OneSuffix.Length] + OtherSuffix, names);
        }

        foreach (var name in names.Where(name => name.EndsWith(OtherSuffix, StringComparison.Ordinal)))
        {
            Assert.Contains(name[..^OtherSuffix.Length] + OneSuffix, names);
        }
    }

    [Fact]
    public void 英文表沒有漏翻的中文()
    {
        var untranslated = Values(AppLanguage.English)
            .Where(pair => !EnglishMayContainChinese.Contains(pair.Key) && pair.Value.Any(IsCjk))
            .Select(pair => pair.Key)
            .ToList();

        Assert.Empty(untranslated);
    }

    [Fact]
    public void 語言選項用自己的語言顯示()
    {
        foreach (var language in new[] { AppLanguage.TraditionalChinese, AppLanguage.English })
        {
            var text = UiText.Of(language);
            Assert.Equal("English", text.SettingsLanguageEnglish);
            Assert.Equal("繁體中文", text.SettingsLanguageTraditionalChinese);
        }
    }

    [Fact]
    public void 繁體中文是原本的文字()
    {
        // 抽幾項 spec 原文（§7.1、§7.4、§10.2、§10.11），確認搬進字串表時沒有改寫。
        var text = UiText.Of(AppLanguage.TraditionalChinese);

        Assert.Equal("失敗（逾時）", text.RunTimedOut);
        Assert.Equal("{0} 進行中 · {1} 失敗 · {2}", text.TrayCounts);
        Assert.Equal("尚未登入 GitHub。請在終端機執行 gh auth login，或到設定貼上 PAT。", text.BannerUnauthorized);
        Assert.Equal("目前的篩選條件沒有符合的執行。", text.EmptyFilteredOut);
    }

    [Fact]
    public void 設定頁引用的選單名稱和run選單一致()
    {
        // 設定頁「…『這筆完成時通知我』的 run 完成時通知」引用 run 選單的項目名稱，畫面上要找得到同一個名稱。
        Assert.Contains("「" + UiText.Of(AppLanguage.TraditionalChinese).MenuWatch + "」", UiText.Of(AppLanguage.TraditionalChinese).SettingsNotifyWatched, StringComparison.Ordinal);
        Assert.Contains("\"" + UiText.Of(AppLanguage.English).MenuWatch + "\"", UiText.Of(AppLanguage.English).SettingsNotifyWatched, StringComparison.Ordinal);
    }

    [Fact]
    public void 寫入設定的說明引用的選單名稱和run選單一致()
    {
        // 設定頁「寫入操作」的說明（第二階段 spec §9.5）引用 run 選單的寫入項目，畫面上要找得到同一個名稱。
        Func<TextTable, string>[] writeMenuItems =
            [text => text.MenuRerunFailedJobs, text => text.MenuRerunAll, text => text.MenuCancelRun, text => text.MenuForceCancel];
        var chinese = UiText.Of(AppLanguage.TraditionalChinese);
        var english = UiText.Of(AppLanguage.English);

        foreach (var item in writeMenuItems)
        {
            Assert.Contains("「" + item(chinese) + "」", chinese.SettingsAllowWriteActionsHint, StringComparison.Ordinal);
            Assert.Contains("\"" + item(english) + "\"", english.SettingsAllowWriteActionsHint, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void PAT與寫入設定的說明寫出GitHub的權限名稱(AppLanguage language)
    {
        // 第二階段 spec §9.5：fine-grained PAT 要另外開的權限，照 GitHub 畫面上的名稱寫（不翻譯）。
        const string permission = "Actions: Read and write";
        var text = UiText.Of(language);

        Assert.Contains(permission, text.SettingsPatHintDefault, StringComparison.Ordinal);
        Assert.Contains(permission, text.SettingsAllowWriteActionsHint, StringComparison.Ordinal);
    }

    [Fact]
    public void 英文的數量與分項寫法()
    {
        var text = UiText.Of(AppLanguage.English);

        // 只有 1 個 repo 時也通（審查回報：原本是「0 of 1 repos done」）。
        Assert.Equal("Loading repos (0 of 1 done)", UiText.Format(text.EmptyLoadingProgress, 0, 1));

        // 多個問題各自成句，項目裡沒有分號。
        var notes = string.Join(text.ProblemSeparator, text.ProblemPullRequestsUnavailable, UiText.Format(text.ProblemInsufficientPermissionNeeds, "actions=read"));
        Assert.Equal("Missing the Pull requests permission, or not accessible. Insufficient permissions (requires actions=read)", notes);
        Assert.Equal(
            "Can't be force included: No workflows, or Actions is disabled (can't tell which)",
            UiText.Format(text.ReposNotForcible, text.SkipNoWorkflows));
    }

    [Fact]
    public void 不認得的語言用繁體中文的字串表()
    {
        Assert.Same(UiText.Of(AppLanguage.TraditionalChinese), UiText.Of((AppLanguage)99));
        Assert.Equal(AppLanguages.Default, AppLanguages.Normalize((AppLanguage)(-1)));
    }

    [Fact]
    public void 單數複數依數量選格式()
    {
        var text = UiText.Of(AppLanguage.English);

        Assert.Equal("1 run", UiText.Plural(1, text.SummaryRunsOne, text.SummaryRunsOther));
        Assert.Equal("0 runs", UiText.Plural(0, text.SummaryRunsOne, text.SummaryRunsOther));
        Assert.Equal("2 runs", UiText.Plural(2, text.SummaryRunsOne, text.SummaryRunsOther));
        Assert.Equal("1 repo visible (3 private)", UiText.Plural(1, text.ActionVisibleReposOne, text.ActionVisibleReposOther, 3));
    }

    [Fact]
    public void 數字用InvariantCulture不加千分位()
    {
        var text = UiText.Of(AppLanguage.English);

        Assert.Equal("API 4870/5000 left", UiText.Format(text.SummaryApiRemaining, 4870, 5000));
        Assert.Equal("12345 runs", UiText.Plural(12345, text.SummaryRunsOne, text.SummaryRunsOther));
    }

    [Theory]
    [InlineData("zh-TW", AppLanguage.TraditionalChinese)]
    [InlineData("zh-CN", AppLanguage.TraditionalChinese)]
    [InlineData("zh-HK", AppLanguage.TraditionalChinese)]
    [InlineData("zh-Hans", AppLanguage.TraditionalChinese)]
    [InlineData("zh-Hant", AppLanguage.TraditionalChinese)]
    [InlineData("zh", AppLanguage.TraditionalChinese)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("en-GB", AppLanguage.English)]
    [InlineData("ja-JP", AppLanguage.English)]
    [InlineData("ko-KR", AppLanguage.English)]
    [InlineData("", AppLanguage.English)]
    public void 第一次執行的預設語言依Windows顯示語言(string culture, AppLanguage expected)
    {
        Assert.Equal(expected, AppLanguages.FromDisplayCulture(CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void 沒有顯示語言時用英文()
    {
        Assert.Equal(AppLanguage.English, AppLanguages.FromDisplayCulture(null));
    }

    [Fact]
    public void UI執行緒的文化對應語言()
    {
        Assert.Equal("zh-TW", AppLanguages.UiCulture(AppLanguage.TraditionalChinese).Name);
        Assert.Equal("en-US", AppLanguages.UiCulture(AppLanguage.English).Name);
        Assert.Equal("zh-TW", AppLanguages.UiCulture((AppLanguage)99).Name);
    }

    private static Dictionary<string, string> Values(AppLanguage language)
    {
        var table = UiText.Of(language);
        return Properties.ToDictionary(property => property.Name, property => (string?)property.GetValue(table) ?? string.Empty, StringComparer.Ordinal);
    }

    private static HashSet<int> Placeholders(string value) =>
        Placeholder.Matches(value).Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();

    /// <summary>中日韓文字與全形標點（同 App 的字型說明：這些字落到微軟正黑體）：CJK 符號、擴充 A、基本區、全形與半形。</summary>
    internal static bool IsCjk(char c) =>
        c is >= (char)0x3000 and <= (char)0x303F or >= (char)0x3400 and <= (char)0x4DBF or >= (char)0x4E00 and <= (char)0x9FFF
            or >= (char)0xFF00 and <= (char)0xFFEF;
}
