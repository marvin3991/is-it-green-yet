using System.Text.RegularExpressions;

namespace IsItGreenYet.Core.Tests.Localization;

/// <summary>
/// App 的 XAML 畫面文字一律從字串表來（<c>{l:T 名稱}</c>，App 的 Localization/TExtension.cs），換語言時才會即時更新：
/// 用到的名稱都要在字串表裡（寫錯時畫面是空的、編譯也抓不到），也不能留下寫死的中文。
/// 測試專案不參考 WPF 的 App 專案，所以讀 repo 裡的 XAML 原始檔（同 ThirdPartyNoticesTests 找 repo 根目錄的做法）。
/// </summary>
public class AppXamlTextTests
{
    /// <summary>從測試的輸出資料夾往上找 repo 根目錄的依據（方案檔）。</summary>
    private const string RepoMarker = "IsItGreenYet.slnx";

    private const string AppFolder = "src/IsItGreenYet.App";
    private const string XamlPattern = "*.xaml";

    /// <summary>主畫面、設定頁、repo 管理頁、提示視窗與錯誤對話框都要用到字串表：少於這個數量代表掃描的路徑不對。</summary>
    private const int MinimumUsages = 60;

    private static readonly Regex XamlKey = new(@"\{l:T\s+([^}\s]+)\s*\}", RegexOptions.CultureInvariant);

    private static readonly Regex XmlComment = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    [Fact]
    public void XAML用到的名稱都在字串表裡()
    {
        var names = TextTableTests.Properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var used = 0;
        foreach (var file in XamlFiles())
        {
            foreach (Match match in XamlKey.Matches(File.ReadAllText(file)))
            {
                used++;
                Assert.True(names.Contains(match.Groups[1].Value), $"{Path.GetFileName(file)} 用到字串表沒有的「{match.Groups[1].Value}」");
            }
        }

        Assert.True(used >= MinimumUsages, $"只找到 {used} 個");
    }

    [Fact]
    public void XAML沒有寫死的中文()
    {
        // 註解可以寫中文；屬性值與元素內容的畫面文字一律從字串表來。
        foreach (var file in XamlFiles())
        {
            var withoutComments = XmlComment.Replace(File.ReadAllText(file), string.Empty);
            var line = withoutComments.Split('\n').FirstOrDefault(text => text.Any(TextTableTests.IsCjk));
            Assert.True(line is null, $"{Path.GetFileName(file)}：{line?.Trim()}");
        }
    }

    private static string[] XamlFiles()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), AppFolder), XamlPattern, SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(files);
        return files;
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, RepoMarker)))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("找不到 repo 根目錄（" + RepoMarker + "）。");
    }
}
