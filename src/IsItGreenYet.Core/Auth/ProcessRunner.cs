using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace IsItGreenYet.Core.Auth;

/// <summary>真的啟動外部程式：不開視窗、只讀 stdout；stderr 讀掉丟棄（可能含 gh 的到期警告，不解析也不記錄）。</summary>
/// <remarks>
/// 逾時或取消時結束整個行程樹，並等它真的結束才回傳，不留下孤兒行程。
/// stdout 只放進回傳值；這裡丟出的例外都不帶 stdout（gh 的 stdout 就是 token，spec §13.4）。
/// </remarks>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>沒有退出碼可回報（沒啟動，或逾時被結束）時的 <see cref="ProcessResult.ExitCode"/>。</summary>
    internal const int NoExitCode = -1;

    /// <summary>設計選擇：結束行程樹後最多等這麼久確認它真的結束；TerminateProcess 通常立即生效。</summary>
    private static readonly TimeSpan ExitWaitAfterKill = TimeSpan.FromSeconds(5);

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly ProcessResult NotStarted = new(Started: false, NoExitCode, string.Empty, TimedOut: false);

    /// <summary>測試用：子行程啟動後收到它的 PID，用來確認逾時或取消後行程真的被結束。</summary>
    internal Action<int>? ProcessStarted { get; init; }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<string> removeEnvironmentVariables,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(removeEnvironmentVariables);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "逾時必須大於 0。");
        }

        ct.ThrowIfCancellationRequested();

        var startInfo = CreateStartInfo(fileName, arguments, removeEnvironmentVariables);

        // 期限從啟動前就開始算；超過 CancellationTokenSource 上限的逾時也在啟動前就丟出。
        using var deadline = new CancellationTokenSource(timeout, TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return NotStarted;
            }
        }
        catch (Win32Exception)
        {
            // 找不到執行檔（spec §13.1：找不到 gh →「沒有安裝 gh」），或沒有權限執行。
            return NotStarted;
        }

        Task<string>? output = null;
        Task? discard = null;
        var completed = false;
        try
        {
            ProcessStarted?.Invoke(process.Id);
            CloseInput(process);
            output = process.StandardOutput.ReadToEndAsync(linked.Token);
            discard = process.StandardError.BaseStream.CopyToAsync(Stream.Null, linked.Token);

            // 管線讀取不一定理會取消（同步管線），所以每一段等待都加上同一個期限。
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var standardOutput = await output.WaitAsync(linked.Token).ConfigureAwait(false);
            await discard.WaitAsync(linked.Token).ConfigureAwait(false);

            completed = true;
            return new ProcessResult(Started: true, process.ExitCode, standardOutput, TimedOut: false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new ProcessResult(Started: true, NoExitCode, string.Empty, TimedOut: true);
        }
        finally
        {
            // 逾時、取消或任何例外：結束整個行程樹，不留孤兒行程。
            if (!completed)
            {
                await KillAsync(process).ConfigureAwait(false);
                Observe(output);
                Observe(discard);
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<string> removeEnvironmentVariables)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // stdin 接上後立即關閉：子行程若要讀輸入會直接讀到結尾，不會卡到逾時。
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
        };

        foreach (var argument in arguments)
        {
            if (argument is null)
            {
                throw new ArgumentException("參數不能是 null。", nameof(arguments));
            }

            startInfo.ArgumentList.Add(argument);
        }

        // Windows 上 ProcessStartInfo.Environment 不分大小寫，和系統的環境變數規則一致。
        foreach (var name in removeEnvironmentVariables)
        {
            if (!string.IsNullOrEmpty(name))
            {
                startInfo.Environment.Remove(name);
            }
        }

        return startInfo;
    }

    private static void CloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // 子行程已經結束、管線已斷，不影響結果。
        }
    }

    private static async Task KillAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // 已經結束，或有部分子行程結束不了；下面仍等它一下。
        }

        using var wait = new CancellationTokenSource(ExitWaitAfterKill, TimeProvider.System);
        try
        {
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 等不到就不等了，不讓呼叫端跟著卡住。
        }
    }

    /// <summary>不再等待的讀取工作：之後若失敗，把例外接住，免得變成未觀察的工作例外。</summary>
    private static void Observe(Task? task) =>
        task?.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
