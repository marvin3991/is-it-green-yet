using System.Text;
using IsItGreenYet.Core.Logging;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Logging;

public class ErrorLogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 2, 7, 33, 123, TimeSpan.Zero);

    [Fact]
    public void 每筆紀錄含本地時間情境與完整例外()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace, new ManualTimeProvider(Now, ManualTimeProvider.Taipei));
        var exception = Thrown(new InvalidOperationException("壞掉了"));

        log.Write("背景輪詢", exception);

        var nl = Environment.NewLine;
        Assert.Equal($"[2026-10-24 10:07:33] 背景輪詢{nl}{exception}{nl}{nl}", ReadLog(workspace));
    }

    [Fact]
    public void WriteMessage寫入訊息本文()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace, new ManualTimeProvider(Now));

        log.WriteMessage("Deprecation", "回應帶 Deprecation header");
        log.WriteMessage("空訊息", null!);

        var nl = Environment.NewLine;
        Assert.Equal(
            $"[2026-10-24 02:07:33] Deprecation{nl}回應帶 Deprecation header{nl}{nl}[2026-10-24 02:07:33] 空訊息{nl}{nl}{nl}",
            ReadLog(workspace));
    }

    [Fact]
    public void WriteWithoutBanner寫完整例外_不佔橫幅的去重名額()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace, new ManualTimeProvider(Now));
        var exception = Thrown(new InvalidOperationException("壞掉了"));

        log.WriteWithoutBanner("檢查新版本", exception);

        Assert.Contains(exception.ToString(), ReadLog(workspace), StringComparison.Ordinal);
        Assert.True(log.Write("背景輪詢", exception));
    }

    [Fact]
    public void WriteWithoutBanner例外為null時不丟例外()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace, new ManualTimeProvider(Now));

        Assert.Null(Record.Exception(() => log.WriteWithoutBanner("背景", null!)));
    }

    [Fact]
    public void error_log超過512KiB改名為old()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.Paths.ErrorLogPath;
        File.WriteAllBytes(path, Filler(Tuning.ErrorLogMaxBytes + 1));
        File.WriteAllText(path + ".old", "更舊的內容", Encoding.UTF8);
        var log = CreateLog(workspace, new ManualTimeProvider(Now));

        log.WriteMessage("輪替後", "第一筆");

        Assert.Equal(Tuning.ErrorLogMaxBytes + 1, new FileInfo(path + ".old").Length);
        Assert.StartsWith("[2026-10-24 02:07:33] 輪替後", ReadLog(workspace), StringComparison.Ordinal);
        Assert.Equal(
            ["error.log", "error.log.old"],
            Directory.GetFiles(workspace.Root).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void 剛好512KiB不輪替()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.Paths.ErrorLogPath;
        File.WriteAllBytes(path, Filler(Tuning.ErrorLogMaxBytes));
        var log = CreateLog(workspace, new ManualTimeProvider(Now));

        log.WriteMessage("x", "y");

        Assert.False(File.Exists(path + ".old"));
        Assert.True(new FileInfo(path).Length > Tuning.ErrorLogMaxBytes);
    }

    [Fact]
    public void 同一例外十分鐘內只回報一次()
    {
        using var workspace = new TempWorkspace();
        var time = new ManualTimeProvider(Now);
        var log = CreateLog(workspace, time);

        Assert.True(log.Write("背景", new InvalidOperationException("壞了")));
        time.Advance(TimeSpan.FromSeconds(599));
        Assert.False(log.Write("背景", new InvalidOperationException("壞了")));
        time.Advance(TimeSpan.FromSeconds(1));
        // 滿 ErrorBannerDedupWindow 後再出現，重新算第一次。
        Assert.True(log.Write("背景", new InvalidOperationException("壞了")));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(log.Write("背景", new InvalidOperationException("壞了")));
    }

    [Fact]
    public void 去重視窗是第一次回報後固定十分鐘不因重複出現而延長()
    {
        using var workspace = new TempWorkspace();
        var time = new ManualTimeProvider(Now);
        var log = CreateLog(workspace, time);

        Assert.True(log.Write("背景", new InvalidOperationException("壞了")));
        for (var minute = 1; minute < 10; minute++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            Assert.False(log.Write("背景", new InvalidOperationException("壞了")));
        }

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(log.Write("背景", new InvalidOperationException("壞了")));
    }

    [Fact]
    public void 型別或訊息不同算不同例外()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace, new ManualTimeProvider(Now));

        Assert.True(log.Write("背景", new InvalidOperationException("壞了")));
        Assert.True(log.Write("背景", new InvalidOperationException("另一個錯")));
        Assert.True(log.Write("背景", new IOException("壞了")));
        // 情境不同但型別與訊息相同，仍算同一種例外。
        Assert.False(log.Write("UI 執行緒", new InvalidOperationException("壞了")));
    }

    [Fact]
    public void 重複的例外仍然寫進log()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace, new ManualTimeProvider(Now));

        log.Write("背景", new InvalidOperationException("壞了"));
        log.Write("背景", new InvalidOperationException("壞了"));

        var content = ReadLog(workspace);
        Assert.Equal(2, CountOccurrences(content, "] 背景"));
    }

    [Fact]
    public void 去重用單調時鐘不受牆鐘調整影響()
    {
        using var workspace = new TempWorkspace();
        var time = new ManualTimeProvider(Now);
        var log = CreateLog(workspace, time);

        Assert.True(log.Write("背景", new InvalidOperationException("壞了")));
        // 牆鐘往前跳一天，單調時鐘沒走：仍在視窗內。
        time.SetUtcNow(Now.AddDays(1));
        Assert.False(log.Write("背景", new InvalidOperationException("壞了")));
        // 牆鐘往回調一小時，單調時鐘走了十分鐘：視窗已過。
        time.SetUtcNow(Now.AddHours(-1));
        time.AdvanceMonotonic(TimeSpan.FromMinutes(10));
        Assert.True(log.Write("背景", new InvalidOperationException("壞了")));
    }

    [Fact]
    public void 例外與情境都經過遮罩()
    {
        using var workspace = new TempWorkspace();
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(Fixture.FakeToken);
        var log = new ErrorLog(workspace.Paths.RootDirectory, redactor, new ManualTimeProvider(Now));

        log.Write($"情境 {Fixture.FakeToken}", Thrown(new InvalidOperationException($"Authorization: Bearer {Fixture.FakeToken}")));
        log.WriteMessage("訊息", $"token={Fixture.FakeToken}");

        var content = ReadLog(workspace);
        Assert.DoesNotContain(Fixture.FakeToken, content, StringComparison.Ordinal);
        Assert.Contains("] 情境 ***", content, StringComparison.Ordinal);
        Assert.Contains("Authorization: ***", content, StringComparison.Ordinal);
        Assert.Contains("token=***", content, StringComparison.Ordinal);
    }

    [Fact]
    public void 寫入失敗時吞掉例外且照常回報去重結果()
    {
        using var workspace = new TempWorkspace();
        // error.log 被一個同名資料夾佔住，寫入與輪替都會失敗。
        Directory.CreateDirectory(workspace.Paths.ErrorLogPath);
        var log = CreateLog(workspace, new ManualTimeProvider(Now));

        var first = false;
        var second = true;
        var exception = Record.Exception(() =>
        {
            first = log.Write("背景", new InvalidOperationException("壞了"));
            second = log.Write("背景", new InvalidOperationException("壞了"));
            log.WriteMessage("x", "y");
        });

        Assert.Null(exception);
        Assert.True(first);
        Assert.False(second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void 沒有資料夾時不寫也不丟例外(string? directory)
    {
        var log = new ErrorLog(directory!, new SecretRedactor(), new ManualTimeProvider(Now));

        var exception = Record.Exception(() =>
        {
            log.Write("背景", new InvalidOperationException("壞了"));
            log.WriteMessage("x", "y");
        });

        Assert.Null(exception);
    }

    [Fact]
    public void 例外為null時不丟例外也不回報()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace, new ManualTimeProvider(Now));

        var reported = true;
        var exception = Record.Exception(() => reported = log.Write("背景", null!));

        Assert.Null(exception);
        Assert.False(reported);
    }

    [Fact]
    public void 資料夾不存在時自動建立()
    {
        using var workspace = new TempWorkspace();
        var directory = Path.Combine(workspace.Root, "nested");
        var log = new ErrorLog(directory, new SecretRedactor(), new ManualTimeProvider(Now));

        log.WriteMessage("x", "y");

        Assert.True(File.Exists(Path.Combine(directory, "error.log")));
    }

    [Fact]
    public void 多執行緒同時寫入不遺漏()
    {
        using var workspace = new TempWorkspace();
        var log = CreateLog(workspace, new ManualTimeProvider(Now));
        const int count = 100;

        var reported = new bool[count];
        Parallel.For(0, count, i => reported[i] = log.Write("背景", new InvalidOperationException("壞了")));

        Assert.Equal(count, CountOccurrences(ReadLog(workspace), "] 背景"));
        Assert.Equal(1, reported.Count(r => r));
    }

    private static ErrorLog CreateLog(TempWorkspace workspace, TimeProvider time) =>
        new(workspace.Paths.RootDirectory, new SecretRedactor(), time);

    private static string ReadLog(TempWorkspace workspace) => File.ReadAllText(workspace.Paths.ErrorLogPath, Encoding.UTF8);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>真的丟出再接住，讓例外帶有堆疊。</summary>
    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    private static byte[] Filler(long length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)'x');
        return bytes;
    }
}
