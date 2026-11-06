using System.Xml.Linq;

namespace IsItGreenYet.Core.Tests.Repository;

/// <summary>
/// 字型只用名稱參照 Windows 內建的字型，不隨程式散布（repo 根目錄 THIRD-PARTY-NOTICES.md 的「字型」一節）：
/// App 專案裡不能有字型檔，專案檔也不能引用字型檔。要內嵌字型時，得先在第三方聲明補上它的授權，再改這裡的測試。
/// </summary>
/// <remarks>
/// 專案檔只檢查明確寫出副檔名的項目（含用分號列出的多個項目）；用萬用字元（例如 <c>..\fonts\*.*</c>）引用 App 資料夾外的檔案時擋不到。
/// </remarks>
public class ThirdPartyNoticesTests
{
    /// <summary>從測試的輸出資料夾往上找 repo 根目錄的依據（方案檔）。</summary>
    private const string RepoMarker = "IsItGreenYet.slnx";

    private const string AppFolder = "src/IsItGreenYet.App";
    private const string AppProject = "src/IsItGreenYet.App/IsItGreenYet.App.csproj";

    /// <summary>MSBuild 項目的 Include／Update 可以用分號列出多個檔案。</summary>
    private const char ItemSeparator = ';';

    /// <summary>建置輸出（不在版控裡）：不檢查。</summary>
    private static readonly string[] BuildOutputFolders = ["bin", "obj"];

    /// <summary>字型檔的副檔名（TrueType、OpenType、字型集合、網頁字型）。</summary>
    private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc", ".otc", ".woff", ".woff2"];

    [Fact]
    public void App專案的資料夾裡沒有字型檔()
    {
        var app = Path.Combine(RepoRoot(), AppFolder);

        var fonts = Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(app, path))
            .Where(IsFontFile)
            .Select(path => Path.GetRelativePath(app, path))
            .ToList();

        Assert.Empty(fonts);
    }

    [Fact]
    public void App專案檔沒有引用字型檔()
    {
        var project = XDocument.Load(Path.Combine(RepoRoot(), AppProject));

        var references = project.Descendants()
            .SelectMany(element => new[] { element.Attribute("Include"), element.Attribute("Update") })
            .OfType<XAttribute>()
            .SelectMany(attribute => attribute.Value.Split(ItemSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(IsFontFile)
            .ToList();

        Assert.Empty(references);
    }

    private static bool IsFontFile(string path) =>
        FontExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsBuildOutput(string app, string path)
    {
        var first = Path.GetRelativePath(app, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return BuildOutputFolders.Contains(first, StringComparer.OrdinalIgnoreCase);
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

        throw new InvalidOperationException($"從 {AppContext.BaseDirectory} 往上找不到 {RepoMarker}。");
    }
}
