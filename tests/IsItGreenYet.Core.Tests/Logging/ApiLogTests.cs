using System.Text;
using IsItGreenYet.Core.Logging;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Logging;

public class ApiLogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 2, 7, 33, 123, TimeSpan.Zero);

    private static readonly Uri RunsUrl = new("https://api.github.com/repos/octo-org/sample-app/actions/runs?per_page=30");

    [Fact]
    public void api_log格式()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace);

        log.Write(new ApiLogEntry("GET", RunsUrl, 304, "core", 4870, 5000, TimeSpan.FromMilliseconds(182), null, null));

        Assert.Equal(
            ["2026-10-24T10:07:33.123+08:00\tGET\t/repos/octo-org/sample-app/actions/runs?per_page=30\t304\tcore 4870/5000\t182ms"],
            ReadLines(workspace));
    }

    [Fact]
    public void 轉址與retry_after附在行尾()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace);
        var elapsed = TimeSpan.FromMilliseconds(40);

        log.Write(new ApiLogEntry("GET", RunsUrl, 301, "core", 4869, 5000, elapsed,
            new Uri("https://api.github.com/repositories/42/actions/runs?per_page=30"), null));
        log.Write(new ApiLogEntry("GET", new Uri("https://api.github.com/repos/octo-org/sample-app/actions/jobs/7/logs"), 302,
            "core", 4868, 5000, elapsed, new Uri("https://pipelines.actions.githubusercontent.com/x/y?sig=abc"), null));
        log.Write(new ApiLogEntry("GET", RunsUrl, 403, "core", 4867, 5000, elapsed, null, 60));

        Assert.Equal(
            [
                "2026-10-24T10:07:33.123+08:00\tGET\t/repos/octo-org/sample-app/actions/runs?per_page=30\t301\tcore 4869/5000\t40ms\t→ /repositories/42/actions/runs?per_page=30",
                "2026-10-24T10:07:33.123+08:00\tGET\t/repos/octo-org/sample-app/actions/jobs/7/logs\t302\tcore 4868/5000\t40ms\t→ pipelines.actions.githubusercontent.com/x/y?***",
                "2026-10-24T10:07:33.123+08:00\tGET\t/repos/octo-org/sample-app/actions/runs?per_page=30\t403\tcore 4867/5000\t40ms\tretry-after=60",
            ],
            ReadLines(workspace));
    }

    [Fact]
    public void 轉址目標是省略scheme的相對網址時也只留主機路徑()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace);

        log.Write(new ApiLogEntry("GET", new Uri("https://api.github.com/repos/octo-org/sample-app/actions/jobs/7/logs"), 302,
            "core", 4868, 5000, TimeSpan.FromMilliseconds(40),
            new Uri("//pipelines.actions.githubusercontent.com/x/y?sig=abc", UriKind.Relative), null));

        Assert.Equal(
            ["2026-10-24T10:07:33.123+08:00\tGET\t/repos/octo-org/sample-app/actions/jobs/7/logs\t302\tcore 4868/5000\t40ms\t→ pipelines.actions.githubusercontent.com/x/y?***"],
            ReadLines(workspace));
    }

    [Fact]
    public void 簽名下載主機回相對轉址時依請求網址解析並只留主機路徑()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace);

        log.Write(new ApiLogEntry("GET", new Uri("https://pipelines.actions.githubusercontent.com/x/y?sig=abc"), 302,
            null, null, null, TimeSpan.FromMilliseconds(5), new Uri("/z/w?sig=abc", UriKind.Relative), null));

        Assert.Equal(
            ["2026-10-24T10:07:33.123+08:00\tGET\tpipelines.actions.githubusercontent.com/x/y?***\t302\t-\t5ms\t→ pipelines.actions.githubusercontent.com/z/w?***"],
            ReadLines(workspace));
    }

    [Theory]
    [InlineData("https://pipelines.actions.githubusercontent.com/x/y?sig=abc", "?sig=abc", "pipelines.actions.githubusercontent.com/x/y?***")]
    [InlineData("https://pipelines.actions.githubusercontent.com/x/y?sig=abc", "w?sig=abc", "pipelines.actions.githubusercontent.com/x/w?***")]
    [InlineData("https://pipelines.actions.githubusercontent.com/x/y?sig=abc", "/z/w", "pipelines.actions.githubusercontent.com/z/w")]
    [InlineData("https://api.github.com/repos/octo-org/sample-app/actions/runs", "runs?page=2", "/repos/octo-org/sample-app/actions/runs?page=2")]
    [InlineData("/repos/octo-org/sample-app/actions/runs", "runs?page=2", "/repos/octo-org/sample-app/actions/runs?page=2")]
    [InlineData("/repos/octo-org/sample-app/actions/runs", "/repositories/42/actions/runs", "/repositories/42/actions/runs")]
    public void 相對的轉址目標依請求網址解析主機與路徑(string request, string location, string expected)
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace);

        log.Write(new ApiLogEntry("GET", ParseUri(request), 302, null, null, null, TimeSpan.Zero,
            new Uri(location, UriKind.Relative), null));

        Assert.EndsWith("\t→ " + expected, ReadLines(workspace).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void 簽名下載網址寫成主機與路徑()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace);

        log.Write(new ApiLogEntry("GET", new Uri("https://pipelines.actions.githubusercontent.com/x/y?sig=abc"), 200,
            null, null, null, TimeSpan.FromMilliseconds(350), null, null));

        Assert.Equal(
            ["2026-10-24T10:07:33.123+08:00\tGET\tpipelines.actions.githubusercontent.com/x/y?***\t200\t-\t350ms"],
            ReadLines(workspace));
    }

    [Theory]
    [InlineData(null, 4870, 5000, "4870/5000")]
    [InlineData("core", null, null, "core")]
    [InlineData("search", 29, null, "search 29/-")]
    [InlineData(null, null, null, "-")]
    public void 額度欄位缺值時以減號補位(string? resource, int? remaining, int? limit, string expected)
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace);

        log.Write(new ApiLogEntry("GET", RunsUrl, 200, resource, remaining, limit, TimeSpan.FromMilliseconds(1), null, null));

        var fields = ReadLines(workspace).Single().Split('\t');
        Assert.Equal(6, fields.Length);
        Assert.Equal(expected, fields[4]);
    }

    [Fact]
    public void 耗時取整毫秒且負值當成0()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace);

        log.Write(new ApiLogEntry("GET", RunsUrl, 200, "core", 1, 2, TimeSpan.FromTicks(1_829_000), null, null));
        log.Write(new ApiLogEntry("GET", RunsUrl, 200, "core", 1, 2, TimeSpan.FromMilliseconds(-5), null, null));

        var lines = ReadLines(workspace);
        Assert.EndsWith("\t182ms", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("\t0ms", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void 時間依TimeProvider的本地時區()
    {
        using var workspace = new TempWorkspace();
        var log = new ApiLog(workspace.Paths.LogsDirectory, new SecretRedactor(), new ManualTimeProvider(Now));

        log.Write(new ApiLogEntry("GET", RunsUrl, 200, "core", 1, 2, TimeSpan.Zero, null, null));

        Assert.StartsWith("2026-10-24T02:07:33.123+00:00\t", ReadLines(workspace).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void 註記以井號開頭()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace);

        log.Note("paused until 2026-10-24T10:12:00+08:00");

        Assert.Equal(["# paused until 2026-10-24T10:12:00+08:00"], ReadLines(workspace));
    }

    [Fact]
    public void 註記的換行改成空白維持一行()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace);

        log.Note("resumed\r\nfirst\nsecond\rthird");

        Assert.Equal(["# resumed first second third"], ReadLines(workspace));
    }

    [Fact]
    public void 每一行都經過遮罩()
    {
        using var workspace = new TempWorkspace();
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(Fixture.FakeToken);
        var log = new ApiLog(workspace.Paths.LogsDirectory, redactor, new ManualTimeProvider(Now, ManualTimeProvider.Taipei));

        log.Write(new ApiLogEntry("GET", new Uri($"https://api.github.com/user?access_token={Fixture.FakeToken}"), 200,
            "core", 1, 2, TimeSpan.Zero, null, null));
        log.Note($"token {Fixture.FakeToken}");

        var content = File.ReadAllText(ApiLogPath(workspace));
        Assert.DoesNotContain(Fixture.FakeToken, content, StringComparison.Ordinal);
        Assert.Contains("/user?access_token=***", content, StringComparison.Ordinal);
        Assert.Contains("# token ***", content, StringComparison.Ordinal);
    }

    [Fact]
    public void api_log超過大小輪替並保留3份()
    {
        using var workspace = new TempWorkspace();
        var directory = workspace.Paths.LogsDirectory;
        File.WriteAllBytes(ApiLogPath(workspace), Filler(Tuning.ApiLogMaxBytes + 1));
        File.WriteAllText(Path.Combine(directory, "api.1.log"), "one", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "api.2.log"), "two", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "api.3.log"), "three", Encoding.UTF8);
        var log = CreateLog(workspace);

        log.Write(new ApiLogEntry("GET", RunsUrl, 200, "core", 1, 2, TimeSpan.Zero, null, null));

        Assert.Single(ReadLines(workspace));
        Assert.Equal(Tuning.ApiLogMaxBytes + 1, new FileInfo(Path.Combine(directory, "api.1.log")).Length);
        Assert.Equal("one", File.ReadAllText(Path.Combine(directory, "api.2.log")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(directory, "api.3.log")));
        Assert.False(File.Exists(Path.Combine(directory, "api.4.log")));
        Assert.Equal(
            ["api.1.log", "api.2.log", "api.3.log", "api.log"],
            Directory.GetFiles(directory).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void 輪替時api_log被其他程式開著不會蓋掉舊檔且照樣寫入()
    {
        using var workspace = new TempWorkspace();
        var directory = workspace.Paths.LogsDirectory;
        var path = ApiLogPath(workspace);
        File.WriteAllBytes(path, Filler(Tuning.ApiLogMaxBytes + 1));
        File.WriteAllText(Path.Combine(directory, "api.1.log"), "one", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "api.2.log"), "two", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "api.3.log"), "three", Encoding.UTF8);
        var log = CreateLog(workspace);

        // 模擬沒給 FILE_SHARE_DELETE 的程式開著 api.log：改名會失敗，但仍可附加。
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            log.Note("a");
            log.Note("b");
        }

        Assert.Equal("one", File.ReadAllText(Path.Combine(directory, "api.1.log")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(directory, "api.2.log")));
        Assert.Equal("three", File.ReadAllText(Path.Combine(directory, "api.3.log")));
        Assert.EndsWith("# a" + Environment.NewLine + "# b" + Environment.NewLine, File.ReadAllText(path, Encoding.UTF8));

        // 檔案關掉後的下一次寫入照常輪替。
        log.Note("c");

        Assert.Equal(["# c"], ReadLines(workspace));
        Assert.True(new FileInfo(Path.Combine(directory, "api.1.log")).Length > Tuning.ApiLogMaxBytes);
        Assert.Equal("one", File.ReadAllText(Path.Combine(directory, "api.2.log")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(directory, "api.3.log")));
        Assert.Equal(
            ["api.1.log", "api.2.log", "api.3.log", "api.log"],
            Directory.GetFiles(directory).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void 推移舊檔到一半失敗時放回api_log且舊檔都不少()
    {
        using var workspace = new TempWorkspace();
        var directory = workspace.Paths.LogsDirectory;
        var path = ApiLogPath(workspace);
        File.WriteAllBytes(path, Filler(Tuning.ApiLogMaxBytes + 1));
        File.WriteAllText(Path.Combine(directory, "api.1.log"), "one", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "api.2.log"), "two", Encoding.UTF8);
        File.WriteAllText(Path.Combine(directory, "api.3.log"), "three", Encoding.UTF8);
        var log = CreateLog(workspace);

        // api.2.log 被開著，推移 2 → 3 時失敗。
        using (new FileStream(Path.Combine(directory, "api.2.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            log.Note("a");
        }

        Assert.Equal("one", File.ReadAllText(Path.Combine(directory, "api.1.log")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(directory, "api.2.log")));
        Assert.Equal("three", File.ReadAllText(Path.Combine(directory, "api.3.log")));
        Assert.False(File.Exists(Path.Combine(directory, "api.rotating.log")));
        Assert.True(new FileInfo(path).Length > Tuning.ApiLogMaxBytes);
        Assert.EndsWith("# a" + Environment.NewLine, File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void 第一次輪替時沒有舊檔也能進行()
    {
        using var workspace = new TempWorkspace();
        var directory = workspace.Paths.LogsDirectory;
        File.WriteAllBytes(ApiLogPath(workspace), Filler(Tuning.ApiLogMaxBytes + 1));
        var log = CreateLog(workspace);

        log.Note("first");

        Assert.Equal(["# first"], ReadLines(workspace));
        Assert.Equal(Tuning.ApiLogMaxBytes + 1, new FileInfo(Path.Combine(directory, "api.1.log")).Length);
        Assert.False(File.Exists(Path.Combine(directory, "api.2.log")));
    }

    [Fact]
    public void 剛好等於上限不輪替()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllBytes(ApiLogPath(workspace), Filler(Tuning.ApiLogMaxBytes));
        var log = CreateLog(workspace);

        log.Note("still here");

        Assert.False(File.Exists(Path.Combine(workspace.Paths.LogsDirectory, "api.1.log")));
        Assert.True(new FileInfo(ApiLogPath(workspace)).Length > Tuning.ApiLogMaxBytes);
    }

    [Fact]
    public void 連續輪替後最多保留目前加3份舊檔()
    {
        using var workspace = new TempWorkspace();
        var directory = workspace.Paths.LogsDirectory;
        var log = CreateLog(workspace);

        for (var round = 1; round <= 5; round++)
        {
            File.WriteAllBytes(ApiLogPath(workspace), Filler(Tuning.ApiLogMaxBytes + round));
            log.Note($"round {round}");
        }

        Assert.Equal(1 + Tuning.ApiLogKeepFiles, Directory.GetFiles(directory).Length);
        Assert.Equal(["# round 5"], ReadLines(workspace));
        // api.1.log 是最新被換下來的那份（第 5 輪寫入前的內容）。
        Assert.Equal(Tuning.ApiLogMaxBytes + 5, new FileInfo(Path.Combine(directory, "api.1.log")).Length);
        Assert.Equal(Tuning.ApiLogMaxBytes + 4, new FileInfo(Path.Combine(directory, "api.2.log")).Length);
        Assert.Equal(Tuning.ApiLogMaxBytes + 3, new FileInfo(Path.Combine(directory, "api.3.log")).Length);
    }

    [Fact]
    public void 資料夾不存在時自動建立()
    {
        using var workspace = new TempWorkspace();
        var directory = Path.Combine(workspace.Root, "nested", "logs");
        var log = new ApiLog(directory, new SecretRedactor(), new ManualTimeProvider(Now));

        log.Note("hello");

        Assert.Equal(["# hello"], File.ReadAllLines(Path.Combine(directory, "api.log")));
    }

    [Fact]
    public void 寫入失敗時吞掉例外()
    {
        using var workspace = new TempWorkspace();
        // api.log 被一個同名資料夾佔住，寫入與輪替都會失敗。
        Directory.CreateDirectory(ApiLogPath(workspace));
        var log = CreateLog(workspace);

        var exception = Record.Exception(() =>
        {
            log.Write(new ApiLogEntry("GET", RunsUrl, 200, "core", 1, 2, TimeSpan.Zero, null, null));
            log.Note("x");
        });

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void 沒有資料夾時不寫也不丟例外(string? directory)
    {
        var log = new ApiLog(directory!, new SecretRedactor(), new ManualTimeProvider(Now));

        var exception = Record.Exception(() =>
        {
            log.Write(new ApiLogEntry("GET", RunsUrl, 200, "core", 1, 2, TimeSpan.Zero, null, null));
            log.Note("x");
        });

        Assert.Null(exception);
    }

    [Fact]
    public void 多執行緒同時寫入不遺漏也不交錯()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace);
        const int count = 200;

        Parallel.For(0, count, i =>
            log.Write(new ApiLogEntry("GET", new Uri($"https://api.github.com/repos/octo-org/sample-app/actions/runs/{i}"), 200,
                "core", 1, 2, TimeSpan.Zero, null, null)));

        var lines = ReadLines(workspace);
        Assert.Equal(count, lines.Length);
        Assert.All(lines, line => Assert.Equal(6, line.Split('\t').Length));
    }

    private static ApiLog CreateLog(TempWorkspace workspace) =>
        new(workspace.Paths.LogsDirectory, new SecretRedactor(), new ManualTimeProvider(Now, ManualTimeProvider.Taipei));

    private static string ApiLogPath(TempWorkspace workspace) => Path.Combine(workspace.Paths.LogsDirectory, "api.log");

    /// <summary>以 / 開頭的字串明確當成相對網址（不同平台對 RelativeOrAbsolute 的判斷不一致）。</summary>
    private static Uri ParseUri(string text) =>
        new(text, text.StartsWith('/') ? UriKind.Relative : UriKind.Absolute);

    private static string[] ReadLines(TempWorkspace workspace) => File.ReadAllLines(ApiLogPath(workspace), Encoding.UTF8);

    private static byte[] Filler(long length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)'x');
        return bytes;
    }
}
