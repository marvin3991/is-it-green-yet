using System.Net.NetworkInformation;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Logging;
using Microsoft.Win32;

namespace IsItGreenYet.App.Services;

/// <summary>
/// 把 Windows 的鎖定、解鎖、睡眠、喚醒、網路恢復、時間或時區變更轉成 Engine 指令（spec §6.5、§6.4）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Start"/> 要在 UI 執行緒、主視窗 SourceInitialized 之後呼叫：依 dotnet/runtime 原始碼推論，
/// 這樣 SystemEvents 的處理常式會被送回 UI 執行緒執行；WPF 與 WinForms 併用時是否如此沒有查證，
/// 所以第一次收到 SystemEvents 時比對一次執行緒，不同就寫進 error.log（spec §6.5）。
/// </para>
/// <para>
/// 處理常式只做快速、執行緒安全的事：把指令交給 Engine 的指令佇列，不碰 UI。
/// 收到 Suspend 後系統只給約 2 秒，這裡不做任何耗時的工作。
/// SystemEvents 是靜態事件，結束前一定要 <see cref="Dispose"/>，否則會洩漏。
/// </para>
/// </remarks>
internal sealed class SessionMonitor : IDisposable
{
    /// <summary>旗標欄位的「已發生」值；預設的 0 代表還沒。</summary>
    private const int Done = 1;

    private readonly Action<EngineCommand> _post;
    private readonly Action<string, Exception> _reportError;
    private readonly ErrorLog _errorLog;

    private int _started;
    private int _disposed;
    private int _threadChecked;
    private int _uiThreadId;

    /// <param name="post">把指令交給 Engine（<see cref="MonitorEngine.Post"/>）；必須是執行緒安全的。</param>
    /// <param name="reportError">處理常式失敗時呼叫：寫 error.log，必要時顯示橫幅。</param>
    public SessionMonitor(Action<EngineCommand> post, Action<string, Exception> reportError, ErrorLog errorLog)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
        _errorLog = errorLog ?? throw new ArgumentNullException(nameof(errorLog));
    }

    /// <summary>訂閱系統事件。只有第一次呼叫有作用。</summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) == Done || Interlocked.Exchange(ref _started, Done) == Done)
        {
            return;
        }

        Volatile.Write(ref _uiThreadId, Environment.CurrentManagedThreadId);

        // 鎖定偵測只用來省額度，不當作正確性依據：訂閱失敗時照常輪詢、照常通知，不擋啟動（spec §6.5）。
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.TimeChanged += OnTimeChanged;
        }
        catch (Exception ex)
        {
            _reportError("訂閱系統事件失敗", ex);
        }

        try
        {
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }
        catch (Exception ex)
        {
            _reportError("訂閱網路狀態變化失敗", ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, Done) == Done)
        {
            return;
        }

        // 沒訂閱過的處理常式取消訂閱也不會出錯，不必區分訂閱到哪一步。
        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.TimeChanged -= OnTimeChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        }
        catch (Exception ex)
        {
            // 程式正在結束，記下來就好，不能讓收尾中斷。
            _errorLog.Write("取消訂閱系統事件失敗", ex);
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        CheckThreadOnce(nameof(SystemEvents.SessionSwitch));

        // 只處理鎖定與解鎖（spec §6.5）；登入、登出、遠端連線等其他原因不改變輪詢行為。
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                Post(new EngineCommand.SetLocked(true));
                break;
            case SessionSwitchReason.SessionUnlock:
                Post(new EngineCommand.SetLocked(false));
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        CheckThreadOnce(nameof(SystemEvents.PowerModeChanged));

        // 無人值守喚醒（PBT_APMRESUMEAUTOMATIC）不會有 Resume，由 Engine 比對 tick 間隔補上（spec §6.5）。
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Post(new EngineCommand.Suspend());
                break;
            case PowerModes.Resume:
                Post(new EngineCommand.Resume());
                break;
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        CheckThreadOnce(nameof(SystemEvents.TimeChanged));

        // 改時區時 TimeZoneInfo.Local 不會自己更新，先清快取再讓 Engine 用新時區重算顯示（spec §6.5）。
        // 改時區會不會觸發這個事件沒有查證（手動測試 M11）。
        try
        {
            TimeZoneInfo.ClearCachedData();
        }
        catch (Exception ex)
        {
            _reportError("更新時區資訊失敗", ex);
        }

        Post(new EngineCommand.TimeZoneChanged());
    }

    /// <summary>在執行緒集區上觸發（.NET 文件），不做執行緒比對。</summary>
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        // 只在 Windows 回報網路可用時立即探測一次；斷線由 Engine 依連線錯誤自己判定（spec §6.4）。
        if (e.IsAvailable)
        {
            Post(new EngineCommand.NetworkAvailable());
        }
    }

    private void Post(EngineCommand command)
    {
        if (Volatile.Read(ref _disposed) == Done)
        {
            return;
        }

        try
        {
            _post(command);
        }
        catch (Exception ex)
        {
            // 例外若從 SystemEvents 的處理常式丟出去，會變成 UI 執行緒的未處理例外並跳出對話框。
            _reportError($"轉送系統事件失敗（{command.GetType().Name}）", ex);
        }
    }

    /// <summary>spec §6.5：記錄處理常式的 ManagedThreadId，和 UI 執行緒比對一次；不同才寫 error.log。</summary>
    private void CheckThreadOnce(string eventName)
    {
        if (Interlocked.Exchange(ref _threadChecked, Done) == Done)
        {
            return;
        }

        var uiThreadId = Volatile.Read(ref _uiThreadId);
        var handlerThreadId = Environment.CurrentManagedThreadId;
        if (handlerThreadId != uiThreadId)
        {
            _errorLog.WriteMessage(
                "SystemEvents 的處理常式不在 UI 執行緒上執行",
                $"事件 {eventName}：UI 執行緒 {uiThreadId}，處理常式執行緒 {handlerThreadId}。");
        }
    }
}
