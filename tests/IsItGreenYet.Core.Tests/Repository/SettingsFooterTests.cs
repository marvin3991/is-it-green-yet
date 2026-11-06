using System.Xml.Linq;

namespace IsItGreenYet.Core.Tests.Repository;

/// <summary>
/// 設定頁最下方（spec §11.2）：只有文字連結，沒有 GitHub 圖示；分成兩列，文字連結的鍵盤焦點要看得出來；
/// 拿掉圖示之後，第三方聲明與 README 的非官方聲明還在；授權入口開的對話框可以縮放、選取、捲動，按 Esc 關閉。
/// 測試專案不參考 WPF 的 App 專案，所以讀 repo 裡的原始檔（同 ThirdPartyNoticesTests 找 repo 根目錄的做法）。
/// </summary>
public class SettingsFooterTests
{
    /// <summary>從測試的輸出資料夾往上找 repo 根目錄的依據（方案檔）。</summary>
    private const string RepoMarker = "IsItGreenYet.slnx";

    private const string SettingsWindowFile = "src/IsItGreenYet.App/Views/SettingsWindow.xaml";
    private const string SettingsWindowCodeFile = "src/IsItGreenYet.App/Views/SettingsWindow.xaml.cs";
    private const string NoticesDialogFile = "src/IsItGreenYet.App/Views/NoticesDialog.xaml";
    private const string NoticesDialogCodeFile = "src/IsItGreenYet.App/Views/NoticesDialog.xaml.cs";
    private const string NoticesFile = "THIRD-PARTY-NOTICES.md";
    private const string EnglishReadme = "README.md";
    private const string ChineseReadme = "README.zh-TW.md";

    private const string ProjectLinkClick = "OnProjectLinkClick";
    private const string NoticesLinkClick = "OnNoticesClick";
    private const string LinkFocusVisualKey = "Style.FocusVisual.Link";
    private const string LinkMarginKey = "Margin.Gap.Left";
    private const string SecondRowMarginKey = "Margin.Gap.Top";
    private const string ProjectLinkName = "ProjectLinkText";
    private const string VersionName = "VersionText";
    private const string NoticesLinkName = "NoticesLinkText";

    private const string OpenNoticesDialog = "new NoticesDialog(EmbeddedNotices.ReadAll())";
    private const string DialogStyleKey = "Style.Window.Dialog.Resizable";
    private const string MessageStyleKey = "Style.TextBox.Message";
    private const string MessageKeysCall = "DialogMessageKeys.Handle(";
    private const string ShowModal = "dialog.ShowDialog()";
    private const string SetMessageText = "MessageText.Text = text";
    private const string True = "True";

    /// <summary>圖示留下的識別字：XAML 的資源、元素名稱，程式碼的處理函式，以及聲明裡的名稱。</summary>
    private const string MarkResourceWord = "GitHubMark";
    private const string MarkElementWord = "ProjectMark";
    private const string MarkName = "GitHub mark";
    private const string MarkNickname = "Invertocat";

    /// <summary>第三方聲明的非官方聲明；顯示在設定頁的授權對話框裡。</summary>
    private const string UnofficialNotice = "不是 GitHub 的官方產品，也和 GitHub, Inc. 沒有關聯";
    private const string EnglishUnofficialNotice = "This project isn't an official GitHub product and isn't affiliated with GitHub, Inc.";
    private const string ChineseUnofficialNotice = "本專案不是 GitHub 官方產品，與 GitHub, Inc. 無關";

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData(ProjectLinkClick)]
    [InlineData(NoticesLinkClick)]
    public void 文字連結的鍵盤焦點看得出來(string click)
    {
        // Style.Hyperlink.Repo 把焦點框設成 null（主畫面清單要這樣），要在這裡蓋掉。
        Assert.Equal(StaticResource(LinkFocusVisualKey), Link(click).Attribute("FocusVisualStyle")?.Value);
    }

    [Theory]
    [InlineData(ProjectLinkName)]
    [InlineData(VersionName)]
    [InlineData(NoticesLinkName)]
    public void 兩列的文字左邊都留了間距(string name)
    {
        // 焦點框比文字左右各寬 4（Margin.FocusVisual.Link）；連結在捲動區的最左邊時，超出的部分會被捲動區裁掉。
        // 版本沒有焦點框，留一樣的間距才和第一列對齊。
        Assert.Equal(StaticResource(LinkMarginKey), Named(name).Attribute("Margin")?.Value);
    }

    [Fact]
    public void 最下方分兩列_專案連結一列_版本與入口一列()
    {
        var document = SettingsWindow();
        var project = Named(document, ProjectLinkName);
        var version = Named(document, VersionName);
        var notices = Named(document, NoticesLinkName);

        // 排成一列時，英文介面在最小寬度會換行
        var secondRow = version.Parent!;
        Assert.Same(secondRow, notices.Parent);
        Assert.Same(project.Parent, secondRow.Parent);
        Assert.Equal(Presentation + "WrapPanel", secondRow.Name);
        Assert.Equal(StaticResource(SecondRowMarginKey), secondRow.Attribute("Margin")?.Value);
        Assert.True(project.IsBefore(secondRow), "專案連結在第一列");
        Assert.True(version.IsBefore(notices), "第二列先版本再入口");
    }

    [Fact]
    public void 入口開啟授權對話框()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), SettingsWindowCodeFile));

        Assert.Contains(OpenNoticesDialog, code, StringComparison.Ordinal);
        // 要是強制回應：Esc 靠 IsCancel 關閉，Show 開的視窗按 Esc 不會關（2026-11-05 實測）
        Assert.Contains(ShowModal, code, StringComparison.Ordinal);
    }

    [Fact]
    public void 授權對話框可以縮放_內文可以選取捲動_Esc能關()
    {
        var dialog = XDocument.Load(Path.Combine(RepoRoot(), NoticesDialogFile));
        var window = dialog.Root!;

        Assert.Equal(StaticResource(DialogStyleKey), window.Attribute("Style")?.Value);
        Assert.Null(window.Attribute("ResizeMode"));

        var message = dialog.Descendants(Presentation + "TextBox").Single();
        Assert.Equal(StaticResource(MessageStyleKey), message.Attribute("Style")?.Value);
        Assert.Null(message.Attribute("Visibility"));
        Assert.NotNull(window.Attribute("PreviewKeyDown"));

        var code = File.ReadAllText(Path.Combine(RepoRoot(), NoticesDialogCodeFile));
        Assert.Contains(SetMessageText, code, StringComparison.Ordinal);
        Assert.Contains(MessageKeysCall, code, StringComparison.Ordinal);

        // 同一個視窗有兩顆 IsCancel 時，WPF 把 Esc 當成存取鍵輪流移動焦點，不會按下任何一顆
        var cancel = dialog.Descendants(Presentation + "Button").Where(button => button.Attribute("IsCancel")?.Value == True).ToList();
        Assert.Single(cancel);
        Assert.Equal(True, cancel[0].Attribute("IsDefault")?.Value);
    }

    [Fact]
    public void 設定頁沒有GitHub圖示()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), SettingsWindowFile));
        var code = File.ReadAllText(Path.Combine(RepoRoot(), SettingsWindowCodeFile));

        Assert.DoesNotContain(MarkResourceWord, xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkElementWord, xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkElementWord, code, StringComparison.Ordinal);
    }

    [Fact]
    public void 第三方聲明拿掉圖示但保留非官方聲明()
    {
        var notices = File.ReadAllText(Path.Combine(RepoRoot(), NoticesFile));

        Assert.Contains(UnofficialNotice, notices, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkName, notices, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkNickname, notices, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkResourceWord, notices, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EnglishReadme, EnglishUnofficialNotice)]
    [InlineData(ChineseReadme, ChineseUnofficialNotice)]
    public void README拿掉圖示但保留非官方聲明(string readme, string notice)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), readme));

        Assert.Contains(notice, text, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkName, text, StringComparison.Ordinal);
    }

    private static XElement Named(string name) => Named(SettingsWindow(), name);

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element => element.Attribute(Xaml + "Name")?.Value == name);

    private static XElement Link(string click) =>
        SettingsWindow().Descendants(Presentation + "Hyperlink").Single(element => element.Attribute("Click")?.Value == click);

    private static string StaticResource(string key) => $"{{StaticResource {key}}}";

    private static XDocument SettingsWindow() => XDocument.Load(Path.Combine(RepoRoot(), SettingsWindowFile));

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, RepoMarker)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"從 {AppContext.BaseDirectory} 往上找不到 {RepoMarker}。");
    }
}
