using IsItGreenYet.Core;

namespace IsItGreenYet.App.Services;

/// <summary>
/// 確保同一個登入工作階段只跑一份（同 SAM）。重複啟動時，
/// 新行程會通知既有行程把視窗叫出來，然後自己結束。
/// 第一份另外建立命令列 <c>--refresh</c> 用的刷新事件（第二階段 spec §10.5），和喚回事件由同一條監聽執行緒等待。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    // spec §3.3 指定的名稱；Local\ 只在目前的登入工作階段內有效。Mutex 與刷新事件的名稱命令列也要用，放在 Core 的 Tuning。
    private const string MutexName = Tuning.SingleInstanceMutexName;
    private const string SignalName = @"Local\IsItGreenYet.Activate";
    private const string RefreshSignalName = Tuning.RefreshSignalName;

    /// <summary>同 SAM：關閉時最多等監聽執行緒這麼久。</summary>
    private static readonly TimeSpan ListenerJoinTimeout = TimeSpan.FromSeconds(1);

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _signal;
    private readonly EventWaitHandle? _refreshSignal;
    private readonly CancellationTokenSource _cancellation = new();
    private Thread? _listener;

    public SingleInstanceGuard()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            IsFirstInstance = createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            // 同名 Mutex 已經存在、但安全性不允許開啟（例如另一份以系統管理員身分執行）：
            // 代表已經有一份在跑，當成第二份處理，不要讓啟動流程丟出例外。
            IsFirstInstance = false;
        }

        try
        {
            _signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        }
        catch (UnauthorizedAccessException)
        {
            // 同上，打不開就沒辦法喚回既有視窗；第二份照樣直接結束。
            _signal = null;
        }

        // 刷新事件只由第一份建立（spec §10.5「App 取得單一實例之後」）：第二份馬上就要結束，
        // 由它建立的話，命令列會在它結束前開到一個沒有人等待的事件，等到逾時才知道。
        // 在這裡（還沒開始監聽）就建立：啟動途中命令列送來的訊號會留在 AutoReset 事件上，開始監聽後照樣收到。
        if (IsFirstInstance)
        {
            _refreshSignal = TryCreateRefreshSignal(out var refreshError);
            RefreshSignalError = refreshError;
        }
    }

    public bool IsFirstInstance { get; }

    /// <summary>
    /// 第一份建立刷新事件失敗的原因；成功、或不是第一份時是 null。這時 error.log 還沒建立，由 App 建立之後寫一筆。
    /// </summary>
    public Exception? RefreshSignalError { get; }

    /// <summary>另一個行程嘗試啟動時觸發（在監聽執行緒上），用來把主視窗叫回前景。</summary>
    public event Action? ActivationRequested;

    /// <summary>命令列 <c>--refresh</c> 發出刷新訊號時觸發（在監聽執行緒上，第二階段 spec §10.5）。</summary>
    public event Action? RefreshRequested;

    /// <summary>通知既有行程顯示視窗。只有第二份行程會呼叫。</summary>
    public void SignalExistingInstance() => _signal?.Set();

    /// <summary>開始監聽其他行程的啟動通知與命令列的刷新訊號。只有第一份行程需要呼叫。</summary>
    public void StartListening()
    {
        if (!IsFirstInstance || (_signal is null && _refreshSignal is null) || _listener is not null)
        {
            return;
        }

        _listener = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "SingleInstanceListener",
        };
        _listener.Start();
    }

    /// <summary>
    /// 建立刷新事件。打不開（同名物件的安全性不允許，或同名的是別種核心物件）時不支援 <c>--refresh</c>，
    /// 不讓啟動流程丟出例外：命令列會回 refresh_unreachable 或 refresh_unsupported（spec §10.5）。
    /// </summary>
    /// <param name="error">失敗的原因（給 <see cref="RefreshSignalError"/>）；成功時是 null。</param>
    private static EventWaitHandle? TryCreateRefreshSignal(out Exception? error)
    {
        error = null;
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, RefreshSignalName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            error = ex;
            return null;
        }
    }

    private void ListenLoop()
    {
        // 停止訊號放第一個：WaitAny 同時有多個訊號時回最小的索引，關閉時不再處理其他訊號。
        // 每次只取走一個 AutoReset 事件，另一個同時發出的訊號留到下一圈。
        var stop = _cancellation.Token.WaitHandle;
        WaitHandle[] handles = [stop, .. new[] { _signal, _refreshSignal }.OfType<WaitHandle>()];
        while (!_cancellation.IsCancellationRequested)
        {
            var signaled = handles[WaitHandle.WaitAny(handles)];
            if (ReferenceEquals(signaled, stop))
            {
                return;
            }

            if (ReferenceEquals(signaled, _signal))
            {
                ActivationRequested?.Invoke();
            }
            else
            {
                // 同名事件可能被別的行程先建成 ManualReset（建立時沿用既有物件的模式）：WaitAny 不會清掉它的訊號，
                // 不清的話每一圈都成立、一直全量刷新。AutoReset 的訊號已經被 WaitAny 取走，Reset 沒有副作用；
                // 這中間命令列又送來的訊號被清掉也不會漏：接著觸發的這一次刷新在它讀完序號之後才開始，追得上它要等的序號。
                _refreshSignal?.Reset();
                RefreshRequested?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        // 先停監聽執行緒，再釋放它等待的控制代碼。
        _cancellation.Cancel();
        var listenerStopped = _listener?.Join(ListenerJoinTimeout) ?? true;

        if (IsFirstInstance && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 已經被釋放過，忽略。
            }
        }

        _mutex?.Dispose();

        // 監聽執行緒沒在時限內結束（事件處理卡住）時，不釋放它還在用的控制代碼，免得它丟 ObjectDisposedException；
        // 它是背景執行緒，不會讓行程繼續留著，程式結束時由系統收回控制代碼。
        if (listenerStopped)
        {
            _signal?.Dispose();
            _refreshSignal?.Dispose();
            _cancellation.Dispose();
        }
    }
}
