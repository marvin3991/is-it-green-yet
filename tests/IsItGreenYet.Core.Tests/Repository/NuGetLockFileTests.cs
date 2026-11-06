using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace IsItGreenYet.Core.Tests.Repository;

/// <summary>
/// NuGet lock 檔的設定：為什麼要這樣設定見 Directory.Build.props 與 docs/development.md 的「鎖版本」。
/// lock 檔不見這件事這裡看不到（測試之前的還原會重新產生一份），由 tools/check-lock-files.ps1 檢查。
/// </summary>
public class NuGetLockFileTests
{
    /// <summary>從測試的輸出資料夾往上找 repo 根目錄的依據（方案檔）。</summary>
    private const string RepoMarker = "IsItGreenYet.slnx";

    private const string PropsFile = "Directory.Build.props";
    private const string PublishScript = "tools/publish.ps1";
    private const string CoreProject = "src/IsItGreenYet.Core/IsItGreenYet.Core.csproj";
    private const string CiWorkflow = ".github/workflows/ci.yml";
    private const string ReleaseWorkflow = ".github/workflows/release.yml";
    private const string LockCheckScript = "tools/check-lock-files.ps1";
    private const string LockFileName = "packages.lock.json";

    /// <summary>版本跟著 SDK 走的隱含套件，留在 lock 檔的話 SDK 一更新發行就會失敗。</summary>
    private const string SdkBoundPackage = "Microsoft.NET.ILLink.Tasks";

    private const string LockTargetsProperty = "dependencies";
    private const char RuntimeTargetSeparator = '/';

    /// <summary>MSBuild 屬性可以用分號列出多個值。</summary>
    private const char PropertySeparator = ';';

    private const char YamlCommentPrefix = '#';

    [Fact]
    public void lock檔包含publish用的平台()
    {
        var runtime = PublishRuntimeIdentifier();
        var projects = SolutionProjects();

        Assert.NotEmpty(projects);
        foreach (var project in projects)
        {
            var targets = LockTargets(project);
            Assert.Contains(targets, t => t.EndsWith(RuntimeTargetSeparator + runtime, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void lock檔沒有跟著SDK走的套件()
    {
        foreach (var project in SolutionProjects())
        {
            Assert.DoesNotContain(SdkBoundPackage, File.ReadAllText(LockFilePath(project)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 設定檔開了lock檔與CI的鎖定模式()
    {
        Assert.Equal("true", PropsValue("RestorePackagesWithLockFile").Value);

        var lockedMode = PropsValue("RestoreLockedMode");
        Assert.Equal("true", lockedMode.Value);
        Assert.Equal("'$(CI)' == 'true'", lockedMode.Attribute("Condition")?.Value);
    }

    [Fact]
    public void 設定檔的平台與單一檔案分析器符合發行腳本()
    {
        var runtimes = PropsValue("RuntimeIdentifiers").Value.Split(PropertySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Contains(PublishRuntimeIdentifier(), runtimes);
        Assert.Equal("false", PropsValue("EnableSingleFileAnalyzer").Value);

        var runtimePack = PropsValue("EnableRuntimePackDownload");
        Assert.Equal("false", runtimePack.Value);
        Assert.Equal("'$(SelfContained)' != 'true'", runtimePack.Attribute("Condition")?.Value);

        Assert.Contains("PublishSingleFile=true", File.ReadAllText(Path.Combine(RepoRoot(), PublishScript)), StringComparison.Ordinal);
    }

    [Fact]
    public void Core維持和平台無關()
    {
        // RuntimeIdentifiers 只是為了 lock 檔；少了這個設定，發行時 Core 會被建成 x64 專用，exe 的內容就變了。
        var value = XDocument.Load(Path.Combine(RepoRoot(), CoreProject)).Descendants("IsRidAgnostic").SingleOrDefault();

        Assert.Equal("true", value?.Value);
    }

    [Theory]
    [InlineData(CiWorkflow)]
    [InlineData(ReleaseWorkflow)]
    public void CI與發版在發行之後都會檢查lock檔(string workflow)
    {
        // 註解不算：步驟被註解掉時這裡要失敗。
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), workflow))
            .Where(line => !line.TrimStart().StartsWith(YamlCommentPrefix))
            .ToList();

        var lastPublish = lines.FindLastIndex(line => line.Contains(PublishScript, StringComparison.Ordinal));
        var lastCheck = lines.FindLastIndex(line => line.Contains(LockCheckScript, StringComparison.Ordinal));

        Assert.True(lastPublish >= 0, $"{workflow} 沒有跑 {PublishScript}");
        Assert.True(lastCheck > lastPublish, $"{workflow} 要在 {PublishScript} 之後跑 {LockCheckScript}");
    }

    [Theory]
    [InlineData("src/IsItGreenYet.Core/bin/Release")]
    [InlineData("src/IsItGreenYet.Core/obj/Release")]
    public void 發行前會刪Core舊的Release輸出(string staleOutput)
    {
        // Core 和一般 Release 建置共用資料夾；沒刪的話，先建過 Release 時 exe 裡的 Core.dll 會帶本機的 pdb 路徑。
        Assert.Contains(staleOutput, File.ReadAllText(Path.Combine(RepoRoot(), PublishScript)), StringComparison.Ordinal);
    }

    private static List<string> SolutionProjects() =>
        XDocument.Load(Path.Combine(RepoRoot(), RepoMarker))
            .Descendants("Project")
            .Select(p => (string?)p.Attribute("Path"))
            .OfType<string>()
            .ToList();

    private static string LockFilePath(string project) =>
        Path.Combine(RepoRoot(), Path.GetDirectoryName(project.Replace('\\', Path.DirectorySeparatorChar))!, LockFileName);

    private static List<string> LockTargets(string project)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LockFilePath(project)));
        return document.RootElement.GetProperty(LockTargetsProperty).EnumerateObject().Select(t => t.Name).ToList();
    }

    private static XElement PropsValue(string name)
    {
        var element = XDocument.Load(Path.Combine(RepoRoot(), PropsFile)).Descendants(name).SingleOrDefault();
        Assert.NotNull(element);
        return element;
    }

    private static string PublishRuntimeIdentifier()
    {
        var match = Regex.Match(File.ReadAllText(Path.Combine(RepoRoot(), PublishScript)), @"\$runtimeIdentifier = '([^']+)'");
        Assert.True(match.Success, $"{PublishScript} 找不到 $runtimeIdentifier");
        return match.Groups[1].Value;
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
