using System.Reflection;
using IsItGreenYet.Core;

namespace IsItGreenYet.Cli;

/// <summary>App 是否在執行（第二階段 spec §10.5 命令列端第 1 步）。</summary>
internal enum AppProbe
{
    Running,

    /// <summary>Mutex 不存在：App 沒在執行，或在別的登入工作階段（<c>Local\</c> 看不到）。</summary>
    NotRunning,

    /// <summary>Mutex 存在但開不了（例如 App 以系統管理員身分執行）。</summary>
    AccessDenied,
}

/// <summary>開啟 App 的刷新事件的結果（§10.5 命令列端第 2 步）。</summary>
internal enum SignalOpen
{
    Opened,

    /// <summary>事件不存在：App 是第一階段的版本。</summary>
    Missing,

    AccessDenied,
}

/// <summary>開好的刷新事件：<see cref="Set"/> 發出訊號，用完立刻 Dispose（§10.5 命令列端第 4 步）。</summary>
internal interface IRefreshSignal : IDisposable
{
    void Set();
}

/// <summary>
/// 命令列碰到的外部資源，測試時換成假的：時間、cache.db 的位置、自己的版本、App 是否在執行、App 的刷新事件、等待，
/// 以及 <c>notices</c> 用的內嵌全文與原始 stdout。
/// </summary>
/// <param name="Time">牆鐘和狀態表的時間比（心跳、資料過舊）；單調時鐘算 <c>--refresh</c> 的等待上限。</param>
/// <param name="ProbeApp">檢查 App 的單一實例 Mutex；檢查完不能握著任何 handle。</param>
/// <param name="OpenRefreshSignal">開啟 App 建立的刷新事件；只有 <see cref="SignalOpen.Opened"/> 時有 Signal。</param>
/// <param name="ReadNotices">LICENSE 與第三方聲明全文；讀不到時丟例外。</param>
/// <param name="OpenRawStdout">開啟不經過編碼轉換的 stdout，只有 <c>notices</c> 用，用完要關。</param>
internal sealed record CliEnvironment(TimeProvider Time, string DatabasePath, string CliVersion,
    Func<AppProbe> ProbeApp, Func<(SignalOpen Result, IRefreshSignal? Signal)> OpenRefreshSignal,
    Func<TimeSpan, CancellationToken, Task> Delay, Func<string> ReadNotices, Func<Stream> OpenRawStdout)
{
    /// <summary>
    /// 正式執行時的環境：<see cref="AppPaths.Default"/> 的 cache.db、系統時鐘、真的 Mutex 與事件
    /// （<see cref="Tuning.SingleInstanceMutexName"/>、<see cref="Tuning.RefreshSignalName"/>）。
    /// </summary>
    /// <param name="nameSuffix">只給測試用：接在兩個名稱後面，避免碰到使用者正在跑的 App。</param>
    public static CliEnvironment CreateDefault(string nameSuffix = "")
    {
        ArgumentNullException.ThrowIfNull(nameSuffix);

        var mutexName = Tuning.SingleInstanceMutexName + nameSuffix;
        var signalName = Tuning.RefreshSignalName + nameSuffix;
        var time = TimeProvider.System;
        return new CliEnvironment(
            time,
            AppPaths.Default.CacheDbPath,
            InformationalVersion(),
            () => ProbeMutex(mutexName),
            () => OpenEvent(signalName),
            (delay, cancellationToken) => Task.Delay(delay, time, cancellationToken),
            EmbeddedNotices.ReadAll,
            Console.OpenStandardOutput);
    }

    /// <summary>
    /// 只用 TryOpenExisting，開到之後立刻 Dispose，不 WaitOne、也絕不建立（§10.5）：
    /// 命令列握著 Mutex 的期間，重新啟動的 App 會以為自己是第二份而直接結束。
    /// </summary>
    private static AppProbe ProbeMutex(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name, out var mutex))
            {
                return AppProbe.NotRunning;
            }

            mutex.Dispose();
            return AppProbe.Running;
        }
        catch (UnauthorizedAccessException)
        {
            return AppProbe.AccessDenied;
        }
    }

    private static (SignalOpen Result, IRefreshSignal? Signal) OpenEvent(string name)
    {
        // 具名事件只有 Windows 支援；其他平台不會有 App（WPF），當成沒有事件
        if (!OperatingSystem.IsWindows())
        {
            return (SignalOpen.Missing, null);
        }

        try
        {
            return EventWaitHandle.TryOpenExisting(name, out var handle)
                ? (SignalOpen.Opened, new NamedRefreshSignal(handle))
                : (SignalOpen.Missing, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (SignalOpen.AccessDenied, null);
        }
    }

    /// <summary>
    /// 命令列自己的 InformationalVersion（SDK 會在版本號後面接 <c>+&lt;commit&gt;</c>）；
    /// 沒有這個屬性時用組件版本，都沒有時是空字串（同 App 的 AppVersion）。
    /// </summary>
    private static string InformationalVersion()
    {
        var assembly = typeof(CliEnvironment).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString() ?? string.Empty
            : informational;
    }

    private sealed class NamedRefreshSignal(EventWaitHandle handle) : IRefreshSignal
    {
        public void Set() => handle.Set();

        public void Dispose() => handle.Dispose();
    }
}
