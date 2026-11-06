using IsItGreenYet.Core.GitHub;

namespace IsItGreenYet.Core.Engine;

/// <summary>一次寫入的結果分類（第二階段 spec §5.2）。</summary>
public enum WriteOutcome
{
    /// <summary>伺服器回 2xx；進入操作後追蹤。</summary>
    Accepted,

    /// <summary>伺服器有回應但沒有執行（5xx 除外），原因在 <see cref="WriteResult.Rejected"/>；進入操作後追蹤。</summary>
    Rejected,

    /// <summary>送出前就停下，請求沒有離開本機，原因在 <see cref="WriteResult.NotSent"/>；不進入操作後追蹤。</summary>
    NotSent,

    /// <summary>
    /// 請求可能已經生效：沒拿到回應（逾時、連線中斷、程式結束時被取消），或伺服器回 5xx（帶 <see cref="WriteResult.StatusCode"/>）；
    /// 進入操作後追蹤。
    /// </summary>
    Uncertain,
}

/// <summary>「未送出」的原因（第二階段 spec §5.2 的表，順序同表），最後加上送出前的未預期例外。</summary>
public enum NotSentReason
{
    /// <summary>寫入功能的開關是關的。</summary>
    WritesDisabled,

    NotLoggedIn,

    VersionError,

    /// <summary>限流的全域暫停中（不是自動更新的「暫停」）。</summary>
    RateLimitPaused,

    Offline,

    /// <summary>第二階段 spec §4 事先停用的情況。</summary>
    MissingPermission,

    /// <summary>依第二階段 spec §7 這筆 run 現在不能做這個動作，原因代碼在 <see cref="WriteResult.Block"/>。</summary>
    Unavailable,

    /// <summary>這筆 run 最新的 run_attempt 和確認對話框上的那一次不同。</summary>
    AttemptChanged,

    /// <summary>同一筆 run 已經有一個寫入在送。</summary>
    InFlight,

    /// <summary>找不到 repo 或 run：對話框開著時 repo 被靜音、略過、移出，或 run 已不存在。</summary>
    NotFound,

    /// <summary>等待超過 <see cref="Tuning.MutativeMaxQueueWait"/>（第二階段 spec §5.3）。</summary>
    QueueTimeout,

    /// <summary>App 結束時被取消；App 不顯示（同複製 log）。</summary>
    ShuttingDown,

    /// <summary>送出前發生未預期的例外（已寫 error.log）。</summary>
    Unexpected,
}

/// <summary>
/// 一列的某個動作能不能用（第二階段 spec §7）；<see cref="None"/> 是可以，其他是不能用的原因代碼。
/// 原因不放文字，由 App 轉成目前語言的文字，換介面語言時才能立即更新。
/// </summary>
public enum ActionBlock
{
    None,

    // ── 共通條件（§7.2）：任一成立，四個動作都不能用 ──

    /// <summary>這筆 run 已有操作在送出中。</summary>
    InFlight,

    /// <summary>帳號的 scope 不夠、repo 沒有寫入權，或寫入已被停用（第二階段 spec §4）。</summary>
    MissingPermission,

    NotLoggedIn,

    VersionError,

    Offline,

    /// <summary>限流的全域暫停中；自動更新的「暫停」不擋。</summary>
    RateLimitPaused,

    // ── 依 run 狀態（§7.1）──

    /// <summary>還沒完成（未完成或等待），不能重跑。</summary>
    NotCompleted,

    /// <summary>已完成，不能取消。</summary>
    Completed,

    /// <summary>啟動失敗，可能根本沒有 job，不能重跑失敗的 job。</summary>
    NoRerunnableJobs,

    /// <summary>結論是 success、skipped、neutral、stale 或不認得的值，沒有失敗的 job。</summary>
    NoFailedJobs,

    /// <summary><c>action_required</c>：等待核准，只能到網頁上處理。</summary>
    AwaitingApproval,

    // ── 只限重跑（§7.3）──

    /// <summary>已送出重跑，等待 GitHub 更新（還沒看到新的 attempt）。</summary>
    RerunPending,

    /// <summary>workflow 不是 active。</summary>
    WorkflowDisabled,

    /// <summary>created_at 超過 <see cref="Tuning.RerunWindowDays"/> 天。</summary>
    TooOld,

    /// <summary>run_attempt ≥ <see cref="Tuning.MaxReruns"/> + 1：可能已達重跑上限。</summary>
    RerunLimit,

    // ── 只限取消（§7.4、§6.4）──

    /// <summary>已送出取消：一般取消在取消中時是這個；強制取消送出後，強制取消也是這個。</summary>
    CancelPending,

    /// <summary>
    /// 強制取消的出現條件（第二階段 spec §6.4）不成立：不在取消中、取消受理還沒超過 <see cref="Tuning.CancelGraceBeforeForce"/>，
    /// 或 run 已經完成。
    /// </summary>
    ForceNotYet,
}

/// <summary>
/// 一筆 run 的四個動作能不能用（第二階段 spec §7）。快照的 <see cref="MonitorSnapshot.Actions"/> 以 run id 為鍵，
/// 同一份也是寫入送出前再檢查一次的可操作性查表（第二階段 spec §5.3）。
/// </summary>
/// <param name="RunAttempt">建立快照時這筆 run 最新的 run_attempt；送出前和確認對話框上的那一次比對。</param>
/// <param name="ShowForceCancel">選單要不要出現「強制取消」（第二階段 spec §6.4）。</param>
public sealed record RunActions(
    long RepoId,
    int RunAttempt,
    ActionBlock RerunFailedJobs,
    ActionBlock RerunAll,
    ActionBlock Cancel,
    bool ShowForceCancel,
    ActionBlock ForceCancel);

/// <summary>「取消中」的 run（第二階段 spec §6.3）：只在記憶體，看到 status == completed 才清除。</summary>
/// <param name="AcceptedAtUtc">取消受理的時間（UTC），給 App 顯示用。</param>
/// <param name="ForceSent">已經送出強制取消。</param>
public sealed record CancellingInfo(DateTimeOffset AcceptedAtUtc, bool ForceSent);

/// <summary>Engine 回給 App 的寫入結果（第二階段 spec §5.2、§9.4）。</summary>
/// <param name="StatusCode">伺服器回應的狀態碼；沒有回應（未送出、沒拿到回應的結果不確定）時是 0，5xx 的結果不確定帶狀態碼。</param>
/// <param name="Rejected"><see cref="WriteOutcome.Rejected"/> 時的原因（第二階段 spec §3.3）。</param>
/// <param name="NotSent"><see cref="WriteOutcome.NotSent"/> 時的原因。</param>
/// <param name="Block"><see cref="NotSentReason.Unavailable"/> 時第二階段 spec §7 的原因代碼。</param>
/// <param name="ServerMessage">伺服器回應的 message，原樣顯示。</param>
/// <param name="AcceptedPermissions">權限不足時的 X-Accepted-GitHub-Permissions，組出缺少哪個權限的說明。</param>
/// <param name="SnapshotSequence">
/// 回傳當下最後一份已發布快照的 <see cref="MonitorSnapshot.Sequence"/>；App 收到比它新的快照之前，這筆 run 的寫入項目維持停用。
/// </param>
public sealed record WriteResult(
    WriteAction Action,
    WriteOutcome Outcome,
    int StatusCode = 0,
    WriteRejectReason? Rejected = null,
    NotSentReason? NotSent = null,
    ActionBlock? Block = null,
    string? ServerMessage = null,
    string? AcceptedPermissions = null,
    long SnapshotSequence = 0);
