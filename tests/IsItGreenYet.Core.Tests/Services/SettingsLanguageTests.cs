using System.Globalization;
using System.Text;
using System.Text.Json;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Services;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Services;

/// <summary>
/// settings.json 的 <c>language</c>（<c>zh-Hant</c>／<c>en</c>）：既有使用者（有設定檔、沒有這個欄位）預設繁體中文，
/// 第一次執行（沒有設定檔）依 Windows 顯示語言；不認得的值退回預設。
/// </summary>
public class SettingsLanguageTests
{
    private static readonly CultureInfo Taiwan = CultureInfo.GetCultureInfo("zh-TW");
    private static readonly CultureInfo China = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly CultureInfo UnitedStates = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Japan = CultureInfo.GetCultureInfo("ja-JP");

    public static TheoryData<CultureInfo, AppLanguage> FirstRunCultures => new()
    {
        { Taiwan, AppLanguage.TraditionalChinese },
        { China, AppLanguage.TraditionalChinese },
        { UnitedStates, AppLanguage.English },
        { Japan, AppLanguage.English },
    };

    [Theory]
    [MemberData(nameof(FirstRunCultures))]
    public void 第一次執行依Windows顯示語言(CultureInfo display, AppLanguage expected)
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath, display);

        Assert.Equal(expected, service.Load().Language);
        Assert.Equal(expected, service.PeekLanguage());
        Assert.False(File.Exists(workspace.Paths.SettingsPath));
    }

    public static TheoryData<CultureInfo> DisplayCultures => [Taiwan, China, UnitedStates, Japan];

    [Theory]
    [MemberData(nameof(DisplayCultures))]
    public void 既有使用者的設定檔沒有語言欄位時是繁體中文(CultureInfo display)
    {
        using var workspace = new TempWorkspace();
        var service = Write(workspace, """{ "authMode": "pat" }""", display);

        var settings = service.Load();

        Assert.Equal(AppLanguage.TraditionalChinese, settings.Language);
        Assert.Equal(AuthMode.Pat, settings.AuthMode);
        Assert.Equal(AppLanguage.TraditionalChinese, service.PeekLanguage());
    }

    [Theory]
    [InlineData("\"en\"", AppLanguage.English)]
    [InlineData("\"zh-Hant\"", AppLanguage.TraditionalChinese)]
    [InlineData("\"EN\"", AppLanguage.TraditionalChinese)]
    [InlineData("\"en-US\"", AppLanguage.TraditionalChinese)]
    [InlineData("\"zh-TW\"", AppLanguage.TraditionalChinese)]
    [InlineData("\"fr\"", AppLanguage.TraditionalChinese)]
    [InlineData("\"\"", AppLanguage.TraditionalChinese)]
    [InlineData("1", AppLanguage.TraditionalChinese)]
    [InlineData("null", AppLanguage.TraditionalChinese)]
    [InlineData("{ \"a\": 1 }", AppLanguage.TraditionalChinese)]
    [InlineData("[\"en\"]", AppLanguage.TraditionalChinese)]
    public void 讀語言欄位不認得的值退回預設(string value, AppLanguage expected)
    {
        using var workspace = new TempWorkspace();
        // 英文的顯示語言：確認退回的是「既有使用者」的預設（繁體中文），不是第一次執行的規則。
        var service = Write(workspace, $$"""{ "language": {{value}}, "logTailLines": 300 }""", UnitedStates);

        var settings = service.Load();

        Assert.Equal(expected, settings.Language);
        // 一個欄位寫錯不影響其他欄位，也不算損毀。
        Assert.Equal(300, settings.LogTailLines);
        Assert.Null(service.LastRecoveryBackupPath);
        Assert.Equal(expected, service.PeekLanguage());
    }

    [Theory]
    [InlineData(AppLanguage.English, "en")]
    [InlineData(AppLanguage.TraditionalChinese, "zh-Hant")]
    [InlineData((AppLanguage)99, "zh-Hant")]
    public void 儲存的語言值(AppLanguage language, string json)
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath, UnitedStates);

        service.Save(new AppSettings { Language = language });

        using var document = JsonDocument.Parse(File.ReadAllText(workspace.Paths.SettingsPath, Encoding.UTF8));
        Assert.Equal(json, document.RootElement.GetProperty("language").GetString());
        Assert.Equal(AppLanguages.Normalize(language), service.Load().Language);
    }

    [Fact]
    public void 第一次執行的預設語言儲存後下次照樣讀到()
    {
        using var workspace = new TempWorkspace();
        var first = new SettingsService(workspace.Paths.SettingsPath, UnitedStates);
        first.Save(first.Load());

        // 之後 Windows 換成中文也不影響：設定檔已經存在，以設定檔為準。
        Assert.Equal(AppLanguage.English, new SettingsService(workspace.Paths.SettingsPath, Taiwan).Load().Language);
    }

    [Fact]
    public void 偷看語言不改動損毀的設定檔()
    {
        using var workspace = new TempWorkspace();
        var service = Write(workspace, "{ not json", UnitedStates);

        Assert.Equal(AppLanguage.TraditionalChinese, service.PeekLanguage());
        Assert.True(File.Exists(workspace.Paths.SettingsPath));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.bak"));

        // Load 照原本的規則改名保留、回到預設（既有使用者：繁體中文）。
        Assert.Equal(AppLanguage.TraditionalChinese, service.Load().Language);
        Assert.NotNull(service.LastRecoveryBackupPath);
    }

    /// <summary>
    /// 語言欄位正確、其他欄位的型別錯了：Load 整份反序列化失敗，視為損毀、改名保留、回到預設（繁體中文）。
    /// 偷看語言要得到同一個結果，否則最早期的錯誤訊息（建立資料夾、開啟快取資料庫失敗）是英文，之後整個 App 是中文。
    /// </summary>
    [Theory]
    [InlineData("""{ "language": "en", "alwaysOnTop": "yes" }""")]
    [InlineData("""{ "language": "en", "logTailLines": "300" }""")]
    [InlineData("""{ "language": "en", "window": 5 }""")]
    [InlineData("""{ "language": "en", "filters": { "repos": "octo-org/sample-app" } }""")]
    public void 偷看語言和Load一樣把其他欄位型別錯誤的設定檔當成損毀(string json)
    {
        using var workspace = new TempWorkspace();
        var service = Write(workspace, json, UnitedStates);

        Assert.Equal(AppLanguage.TraditionalChinese, service.PeekLanguage());
        Assert.True(File.Exists(workspace.Paths.SettingsPath));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.bak"));

        Assert.Equal(AppLanguage.TraditionalChinese, service.Load().Language);
        Assert.NotNull(service.LastRecoveryBackupPath);
    }

    [Fact]
    public void 不認得的列舉值在Normalized修正()
    {
        var settings = new AppSettings { Language = (AppLanguage)99 }.Normalized();

        Assert.Equal(AppLanguage.TraditionalChinese, settings.Language);
    }

    [Fact]
    public void 顯示語言不能是null()
    {
        using var workspace = new TempWorkspace();

        Assert.Throws<ArgumentNullException>(() => new SettingsService(workspace.Paths.SettingsPath, displayCulture: null!));
    }

    private static SettingsService Write(TempWorkspace workspace, string json, CultureInfo display)
    {
        File.WriteAllText(workspace.Paths.SettingsPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new SettingsService(workspace.Paths.SettingsPath, display);
    }
}
