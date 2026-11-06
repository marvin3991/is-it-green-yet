using System.Text;

namespace IsItGreenYet.Core.Tests.Repository;

/// <summary>
/// 內嵌在 Core 的 LICENSE 與第三方聲明（App 的授權對話框、命令列的 <c>notices</c> 共用）：內容要和 repo 的檔案相同，
/// 讀不到時丟 <see cref="IOException"/>，不能回傳殘缺的內容。
/// </summary>
public class EmbeddedNoticesTests
{
    /// <summary>從測試的輸出資料夾往上找 repo 根目錄的依據（方案檔）。</summary>
    private const string RepoMarker = "IsItGreenYet.slnx";

    private const string LicenseFile = "LICENSE";
    private const string NoticesFile = "THIRD-PARTY-NOTICES.md";

    private const string ResourceThatDoesNotExist = "IsItGreenYet.Core.Legal.NOPE";

    /// <summary>不是合法 UTF-8 的位元組（0xFF 不會出現在 UTF-8 裡）。</summary>
    private const byte InvalidUtf8Byte = 0xFF;

    [Fact]
    public void 內嵌的LICENSE等於repo的檔案()
    {
        var expected = File.ReadAllText(Path.Combine(RepoRoot(), LicenseFile));

        Assert.Equal(WithLf(expected), WithLf(EmbeddedNotices.ReadLicense()));
    }

    [Fact]
    public void 內嵌的第三方聲明等於repo的檔案()
    {
        var expected = File.ReadAllText(Path.Combine(RepoRoot(), NoticesFile));

        Assert.Equal(WithLf(expected), WithLf(EmbeddedNotices.ReadThirdPartyNotices()));
    }

    [Fact]
    public void 資源名稱是固定的()
    {
        var names = typeof(EmbeddedNotices).Assembly.GetManifestResourceNames();

        Assert.Contains(EmbeddedNotices.LicenseResource, names);
        Assert.Contains(EmbeddedNotices.ThirdPartyNoticesResource, names);
    }

    [Fact]
    public void 全文依序是LICENSE_分隔線_第三方聲明()
    {
        var all = EmbeddedNotices.ReadAll();
        var license = EmbeddedNotices.ReadLicense();
        var notices = EmbeddedNotices.ReadThirdPartyNotices();

        Assert.StartsWith(license, all, StringComparison.Ordinal);
        Assert.EndsWith(notices, all, StringComparison.Ordinal);
        Assert.Contains(EmbeddedNotices.Divider, all[license.Length..^notices.Length], StringComparison.Ordinal);
    }

    [Fact]
    public void 全文的換行一律是目前平台的換行()
    {
        var all = EmbeddedNotices.ReadAll();

        Assert.Equal(all.Split(Environment.NewLine).Length, all.Split('\n').Length);
        Assert.Equal(all, all.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", Environment.NewLine, StringComparison.Ordinal));
    }

    [Fact]
    public void 找不到資源時丟IOException()
    {
        var error = Assert.Throws<IOException>(() => EmbeddedNotices.ReadFrom(typeof(EmbeddedNotices).Assembly, ResourceThatDoesNotExist));

        Assert.Contains(ResourceThatDoesNotExist, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 內容不是UTF8時丟IOException()
    {
        using var stream = new MemoryStream([InvalidUtf8Byte]);

        Assert.Throws<IOException>(() => EmbeddedNotices.Decode(stream, LicenseFile));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public void 內容是空的時丟IOException(string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        Assert.Throws<IOException>(() => EmbeddedNotices.Decode(stream, LicenseFile));
    }

    [Fact]
    public void 開頭的BOM不算內容()
    {
        using var stream = new MemoryStream([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(LicenseFile)]);

        Assert.Equal(LicenseFile, EmbeddedNotices.Decode(stream, LicenseFile));
    }

    private static string WithLf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

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
