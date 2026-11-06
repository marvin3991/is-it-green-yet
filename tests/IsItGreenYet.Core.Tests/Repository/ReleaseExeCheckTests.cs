using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IsItGreenYet.Core.Tests.Repository;

/// <summary>
/// tools/check-release-exes.ps1 的中止條件：用合成的單一檔案 bundle 跑真的腳本。
/// 合成的檔案沒有版本資訊，所以每個案例都會多兩個版本欄位的錯，這裡只看要測的那一項。
/// 真的 exe 通過的情況由 CI 的發行檢查與 release.yml 跑。
/// </summary>
public class ReleaseExeCheckTests : IDisposable
{
    /// <summary>從測試的輸出資料夾往上找 repo 根目錄的依據（方案檔）。</summary>
    private const string RepoMarker = "IsItGreenYet.slnx";

    private const string Script = "tools/check-release-exes.ps1";
    private const string PublishScript = "tools/publish.ps1";
    private const string NoticesFile = "THIRD-PARTY-NOTICES.md";
    private const char YamlCommentPrefix = '#';
    private const string Shell = "pwsh";
    private const string AppExe = "IsItGreenYet.exe";
    private const string CliExe = "IsItGreenYet-cli.exe";
    private const string SqliteFile = "e_sqlite3.dll";
    private const string FailureAnnotation = "::error::";
    private const int FailureExitCode = 1;

    /// <summary>腳本要啟動 pwsh 並讀兩個合成檔；CI 的 runner 比本機慢，抓寬一點。</summary>
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(2);

    /// <summary>和腳本相同的 bundle 標記（dotnet/runtime 的 HostWriter.cs）。</summary>
    private static readonly byte[] BundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];

    private const uint BundleMajorVersion = 6;
    private const uint BundleMinorVersion = 0;
    private const int HeaderOffsetBytes = 8;
    private const int HeaderExtraFieldsBytes = 40;
    private const byte AssemblyFileType = 1;

    /// <summary>假的 host 區段開頭，只是讓 bundle 的第一個檔不在檔案最前面。</summary>
    private static readonly byte[] HostFiller = Encoding.ASCII.GetBytes("MZ fake apphost for tests ");

    private static readonly string[] AppFiles =
    [
        SqliteFile, "IsItGreenYet.Core.dll", "IsItGreenYet.deps.json", "IsItGreenYet.dll", "IsItGreenYet.runtimeconfig.json",
        "Microsoft.Data.Sqlite.dll", "SQLitePCLRaw.batteries_v2.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll",
    ];

    private static readonly string[] CliFiles =
    [
        SqliteFile, "IsItGreenYet-cli.deps.json", "IsItGreenYet-cli.dll", "IsItGreenYet-cli.runtimeconfig.json", "IsItGreenYet.Core.dll",
        "Microsoft.Data.Sqlite.dll", "SQLitePCLRaw.batteries_v2.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll",
    ];

    private static readonly byte[] FakeSqlite = Encoding.ASCII.GetBytes("fake e_sqlite3.dll content");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "iigy-release-exe-check-" + Guid.NewGuid().ToString("N"));

    public ReleaseExeCheckTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void 找不到exe時失敗()
    {
        var result = Run();

        Assert.Equal(FailureExitCode, result.ExitCode);
        Assert.Contains($"找不到 {Path.Combine(_directory, AppExe)}", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void 不是單一檔案bundle時失敗()
    {
        File.WriteAllBytes(Path.Combine(_directory, AppExe), HostFiller);
        WriteBundle(CliExe, CliFiles);

        var result = Run();

        Assert.Equal(FailureExitCode, result.ExitCode);
        Assert.Contains($"{AppExe}：找不到單一檔案 bundle 的標記", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void 合格的bundle只剩版本資訊的錯()
    {
        WriteBundle(AppExe, AppFiles);
        WriteBundle(CliExe, CliFiles);

        var result = Run();

        Assert.Equal(FailureExitCode, result.ExitCode);
        // 兩個 exe 各兩個版本欄位的錯，少一個表示那一項檢查不見了。
        var expected = new[] { AppExe, CliExe }
            .SelectMany(exe => new[] { $"{exe} 的 LegalCopyright 是", $"{exe} 的 ProductVersion 是" })
            .ToList();
        var failures = FailureLines(result.Output);
        Assert.Equal(expected.Count, failures.Count);
        Assert.All(expected, prefix => Assert.Contains(failures, line => line.Contains(prefix, StringComparison.Ordinal)));
    }

    [Fact]
    public void 有license_information列名的檔案時失敗()
    {
        WriteBundle(AppExe, [.. AppFiles, "coreclr.dll"]);
        WriteBundle(CliExe, CliFiles);

        var result = Run();

        Assert.Equal(FailureExitCode, result.ExitCode);
        Assert.Contains($"{AppExe} 的 bundle 有 license-information-windows.md 列名的檔案 coreclr.dll", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void 有清單以外的檔案時失敗()
    {
        WriteBundle(AppExe, AppFiles);
        WriteBundle(CliExe, [.. CliFiles, "Extra.dll"]);

        var result = Run();

        Assert.Equal(FailureExitCode, result.ExitCode);
        Assert.Contains($"{CliExe} 的 bundle 有清單以外的檔案 Extra.dll", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void 缺檔時失敗()
    {
        WriteBundle(AppExe, AppFiles.Where(f => f != "SQLitePCLRaw.core.dll").ToArray());
        WriteBundle(CliExe, CliFiles);

        var result = Run();

        Assert.Equal(FailureExitCode, result.ExitCode);
        Assert.Contains($"{AppExe} 的 bundle 缺 SQLitePCLRaw.core.dll", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void e_sqlite3的雜湊不符時失敗()
    {
        WriteBundle(AppExe, AppFiles);
        WriteBundle(CliExe, CliFiles);

        var result = Run(expectedSqliteSha256: Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("another build"))).ToLowerInvariant());

        Assert.Equal(FailureExitCode, result.ExitCode);
        Assert.Contains($"{AppExe} 裡的 {SqliteFile} 和聲明檔記的不同", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WerRegister", false)]
    [InlineData("Watson", true)]
    [InlineData("coreclr.dll", false)]
    [InlineData("vcruntime140", true)]
    public void host區段有不該有的字時失敗(string word, bool utf16)
    {
        WriteBundle(AppExe, AppFiles, hostWord: utf16 ? Encoding.Unicode.GetBytes(word) : Encoding.ASCII.GetBytes(word));
        WriteBundle(CliExe, CliFiles);

        var result = Run();

        Assert.Equal(FailureExitCode, result.ExitCode);
        Assert.Contains($"{AppExe} 的 host 區段有 {word}", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void host區段的vcruntime140不分大小寫()
    {
        // 匯入表的寫法是大寫。
        WriteBundle(AppExe, AppFiles, hostWord: Encoding.ASCII.GetBytes("VCRUNTIME140.dll"));
        WriteBundle(CliExe, CliFiles);

        var result = Run();

        Assert.Equal(FailureExitCode, result.ExitCode);
        Assert.Contains($"{AppExe} 的 host 區段有 vcruntime140", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void host區段的Watson分大小寫()
    {
        // apphost 從 UCRT 匯入 _invoke_watson，這不是單一檔案 runtime 的痕跡。
        WriteBundle(AppExe, AppFiles, hostWord: Encoding.ASCII.GetBytes("_invoke_watson"));
        WriteBundle(CliExe, CliFiles);

        var result = Run();

        Assert.DoesNotContain("host 區段有 Watson", result.Output, StringComparison.Ordinal);
        Assert.Contains("Watson：ASCII 0 次", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void 大寫的期望雜湊視為相同()
    {
        WriteBundle(AppExe, AppFiles);
        WriteBundle(CliExe, CliFiles);

        var result = Run(expectedSqliteSha256: Convert.ToHexString(SHA256.HashData(FakeSqlite)));

        Assert.Contains($"{SqliteFile} 的 SHA-256", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("和聲明檔記的不同", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void 腳本預設的e_sqlite3雜湊和聲明檔記的相同()
    {
        // CI 與發版不帶參數，比對的是腳本的預設值，所以它要和聲明檔第 5 項記的是同一個。
        var script = File.ReadAllText(Path.Combine(RepoRoot(), Script));
        // SHA-256 的十六進位是 64 個字，和腳本參數的 ValidatePattern 相同。
        var match = Regex.Match(script, @"\$ExpectedSqliteSha256\s*=\s*'([0-9a-f]{64})'");
        Assert.True(match.Success, $"{Script} 找不到 -ExpectedSqliteSha256 的預設值");

        var notices = File.ReadAllText(Path.Combine(RepoRoot(), NoticesFile));
        Assert.Contains(match.Groups[1].Value, notices, StringComparison.Ordinal);
    }

    [Fact]
    public void 不該有的字只在bundle裡時不算()
    {
        // 內嵌的聲明檔本身就有這些字，所以只看 host 區段。
        WriteBundle(AppExe, AppFiles, bundledText: "WerRegister Watson coreclr.dll vcruntime140 VCRUNTIME140.dll");
        WriteBundle(CliExe, CliFiles);

        var result = Run();

        Assert.DoesNotContain("host 區段有", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/release.yml")]
    public void CI與發版在發行之後都會檢查exe(string workflow)
    {
        // 註解不算：步驟被註解掉時這裡要失敗。
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), workflow))
            .Where(line => !line.TrimStart().StartsWith(YamlCommentPrefix))
            .ToList();

        var lastPublish = lines.FindLastIndex(line => line.Contains(PublishScript, StringComparison.Ordinal));
        var lastCheck = lines.FindLastIndex(line => line.Contains(Script, StringComparison.Ordinal));

        Assert.True(lastPublish >= 0, $"{workflow} 沒有跑 {PublishScript}");
        Assert.True(lastCheck > lastPublish, $"{workflow} 要在 {PublishScript} 之後跑 {Script}");
    }

    private static List<string> FailureLines(string output) =>
        output.Split('\n').Where(line => line.StartsWith(FailureAnnotation, StringComparison.Ordinal)).ToList();

    private void WriteBundle(string exe, IReadOnlyList<string> files, byte[]? hostWord = null, string? bundledText = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        writer.Write(HostFiller);
        if (hostWord is not null)
        {
            writer.Write(hostWord);
        }

        var markerPosition = stream.Position;
        writer.Write(new byte[HeaderOffsetBytes]);
        writer.Write(BundleSignature);

        var entries = new List<(string Path, long Offset, long Size)>();
        foreach (var file in files)
        {
            var content = file == SqliteFile
                ? FakeSqlite
                : Encoding.UTF8.GetBytes(bundledText is null ? $"content of {file}" : $"content of {file} {bundledText}");
            entries.Add((file, stream.Position, content.Length));
            writer.Write(content);
        }

        var headerOffset = stream.Position;
        writer.Write(BundleMajorVersion);
        writer.Write(BundleMinorVersion);
        writer.Write(entries.Count);
        writer.Write("test-bundle-id");
        writer.Write(new byte[HeaderExtraFieldsBytes]);
        foreach (var (path, offset, size) in entries)
        {
            writer.Write(offset);
            writer.Write(size);
            writer.Write(0L);
            writer.Write(AssemblyFileType);
            writer.Write(path);
        }

        stream.Position = markerPosition;
        writer.Write(headerOffset);
        writer.Flush();

        File.WriteAllBytes(Path.Combine(_directory, exe), stream.ToArray());
    }

    private (int ExitCode, string Output) Run(string? expectedSqliteSha256 = null)
    {
        var start = new ProcessStartInfo(Shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(RepoRoot(), Script), "-Directory", _directory, "-NoEnvironmentInfo" })
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("-ExpectedSqliteSha256");
        start.ArgumentList.Add(expectedSqliteSha256 ?? Convert.ToHexString(SHA256.HashData(FakeSqlite)).ToLowerInvariant());

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ScriptTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Script} 超過 {ScriptTimeout} 沒有結束。");
        }

        return (process.ExitCode, output.Result + error.Result);
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
