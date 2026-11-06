using System.Diagnostics;
using System.Text;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Auth;

/// <summary>
/// 真正啟動行程的 <see cref="ProcessRunner"/>。只跑無害的系統指令（cmd.exe 的 echo／exit、ping 本機），
/// 絕對不執行 gh。
/// </summary>
public class ProcessRunnerTests
{
    /// <summary>一般指令的逾時：遠大於 cmd.exe 實際需要的時間，只防止測試卡住。</summary>
    private static readonly TimeSpan GenerousTimeout = TimeSpan.FromSeconds(30);

    /// <summary>逾時測試用的短逾時。</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>取消測試：行程啟動後過這麼久才取消。</summary>
    private static readonly TimeSpan CancelDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>逾時或取消後最晚要在這之內回傳；<see cref="LongPing"/> 本身要跑約 29 秒。</summary>
    private static readonly TimeSpan PromptReturn = TimeSpan.FromSeconds(10);

    /// <summary>ping 本機 30 次，每次間隔約 1 秒，遠超過短逾時。</summary>
    private static readonly string[] LongPing = ["-n", "30", "127.0.0.1"];

    private const string PingProcessName = "PING";

    private static readonly string[] NoVariables = [];

    [WindowsFact]
    public async Task 用UTF8解讀stdout()
    {
        // type 對沒有 BOM 的檔案是原樣複製位元組，不受主控台字碼頁影響（cmd 的 echo 會用啟動時的字碼頁）。
        using var workspace = new TempWorkspace();
        var file = workspace.PathTo("utf8.txt");
        File.WriteAllText(file, "測試\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var result = await new ProcessRunner().RunAsync(
            "cmd.exe", ["/c", "type", file], NoVariables, GenerousTimeout, CancellationToken.None);

        Assert.True(result.Started);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("測試\r\n", result.StandardOutput);
    }

    [WindowsFact]
    public async Task 回傳退出碼()
    {
        var result = await new ProcessRunner().RunAsync(
            "cmd.exe", ["/c", "exit", "3"], NoVariables, GenerousTimeout, CancellationToken.None);

        Assert.True(result.Started);
        Assert.False(result.TimedOut);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
    }

    [WindowsFact]
    public async Task 找不到執行檔算沒啟動()
    {
        int? pid = null;
        var runner = new ProcessRunner { ProcessStarted = id => pid = id };

        var result = await runner.RunAsync(
            "isitgreenyet-no-such-program-7f3a", [], NoVariables, GenerousTimeout, CancellationToken.None);

        Assert.False(result.Started);
        Assert.False(result.TimedOut);
        Assert.Equal(ProcessRunner.NoExitCode, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.Null(pid);
    }

    [WindowsFact]
    public async Task 只回傳stdout不含stderr()
    {
        var result = await new ProcessRunner().RunAsync(
            "cmd.exe", ["/c", "echo", "err", "1>&2", "&", "echo", "out"], NoVariables, GenerousTimeout, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out\r\n", result.StandardOutput);
    }

    [WindowsFact]
    public async Task stderr大量輸出也不會卡住()
    {
        // 約 130 KB 的 stderr，遠超過管線緩衝區；沒有一邊讀掉的話子行程會卡住到逾時。
        string[] arguments =
        [
            "/c", "(for", "/l", "%i", "in", "(1,1,3000)", "do", "@echo",
            "0123456789012345678901234567890123456789", "1>&2)", "&", "echo", "done",
        ];

        var result = await new ProcessRunner().RunAsync("cmd.exe", arguments, NoVariables, GenerousTimeout, CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("done\r\n", result.StandardOutput);
    }

    [WindowsFact]
    public async Task 逾時會結束子行程()
    {
        int? pid = null;
        var runner = new ProcessRunner { ProcessStarted = id => pid = id };
        var start = TimeProvider.System.GetTimestamp();

        var result = await runner.RunAsync("ping", LongPing, NoVariables, ShortTimeout, CancellationToken.None);

        Assert.True(TimeProvider.System.GetElapsedTime(start) < PromptReturn);
        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.Equal(ProcessRunner.NoExitCode, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.NotNull(pid);
        Assert.True(HasExited(pid.Value));
    }

    [WindowsFact]
    public async Task 取消會結束子行程並往外丟()
    {
        int? pid = null;
        using var cts = new CancellationTokenSource();
        var runner = new ProcessRunner
        {
            ProcessStarted = id =>
            {
                pid = id;
                cts.CancelAfter(CancelDelay);
            },
        };
        var start = TimeProvider.System.GetTimestamp();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync("ping", LongPing, NoVariables, GenerousTimeout, cts.Token));

        Assert.True(TimeProvider.System.GetElapsedTime(start) < PromptReturn);
        Assert.NotNull(pid);
        Assert.True(HasExited(pid.Value));
    }

    [WindowsFact]
    public async Task 已取消就不啟動()
    {
        int? pid = null;
        var runner = new ProcessRunner { ProcessStarted = id => pid = id };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync("cmd.exe", ["/c", "exit", "0"], NoVariables, GenerousTimeout, cts.Token));

        Assert.Null(pid);
    }

    [WindowsFact]
    public async Task 移除指定的環境變數且不分大小寫()
    {
        // 用測試專屬的變數名稱驗證 Q32 的移除機制，不在測試行程裡設定 GH_TOKEN。
        const string name = "ISITGREENYET_TEST_REMOVED_VARIABLE";
        string[] echo = ["/c", "echo", $"%{name}%"];
        System.Environment.SetEnvironmentVariable(name, "present");
        try
        {
            var runner = new ProcessRunner();

            var kept = await runner.RunAsync("cmd.exe", echo, NoVariables, GenerousTimeout, CancellationToken.None);
            var removed = await runner.RunAsync(
                "cmd.exe", echo, [name.ToLowerInvariant()], GenerousTimeout, CancellationToken.None);

            Assert.Equal("present\r\n", kept.StandardOutput);
            // cmd.exe 遇到沒有定義的變數會原樣印出 %名稱%。
            Assert.Equal($"%{name}%\r\n", removed.StandardOutput);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task 逾時不合理時不啟動行程(int milliseconds)
    {
        int? pid = null;
        var runner = new ProcessRunner { ProcessStarted = id => pid = id };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync(
            "cmd.exe", ["/c", "exit", "0"], NoVariables, TimeSpan.FromMilliseconds(milliseconds), CancellationToken.None));

        Assert.Null(pid);
    }

    /// <summary>這個 PID 的行程已經不在了（或 PID 已被別的程式重用）。</summary>
    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited
                || !string.Equals(process.ProcessName, PingProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // 找不到這個 PID：行程已結束。
            return true;
        }
    }
}
