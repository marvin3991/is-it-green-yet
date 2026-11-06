using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Engine;

/// <summary>建立快照時，一筆 run 在 run 狀態以外、會影響寫入動作的狀態（第二階段 spec §7）。</summary>
/// <param name="GlobalBlock">§7.2 的共通條件（缺少權限、未登入、版本錯誤、離線、限流暫停）；都不成立是 <see cref="ActionBlock.None"/>。</param>
/// <param name="InFlight">這筆 run 已有寫入在送出中（§5.3）。</param>
/// <param name="RerunPending">已有待確認的重跑，還沒看到新的 attempt（§6.2）。</param>
/// <param name="Cancelling">取消中（§6.3）；不在取消中是 null。</param>
/// <param name="CancelGraceElapsed">取消受理已超過 <see cref="Tuning.CancelGraceBeforeForce"/>（§6.4）。</param>
internal sealed record ActionContext(
    ActionBlock GlobalBlock,
    bool InFlight,
    bool RerunPending,
    CancellingInfo? Cancelling,
    bool CancelGraceElapsed);

/// <summary>
/// 每一列的四個寫入動作能不能用（第二階段 spec §7）。純函式：依序檢查，第一個成立的條件就是結果。
/// status／conclusion 的字串比對同 <see cref="Tracking.RunStateMapper"/>（區分大小寫）。
/// </summary>
internal static class ActionRules
{
    private const string Completed = "completed";
    private const string StartupFailure = "startup_failure";
    private const string Failure = "failure";
    private const string TimedOut = "timed_out";
    private const string Cancelled = "cancelled";
    private const string ActionRequired = "action_required";

    /// <summary>第 1 次執行；重跑上限是它加上 <see cref="Tuning.MaxReruns"/> 次重跑（§7.3）。</summary>
    private const int FirstAttempt = 1;

    private static readonly TimeSpan RerunWindow = TimeSpan.FromDays(Tuning.RerunWindowDays);

    public static RunActions Evaluate(RunInfo run, WorkflowInfo? workflow, DateTimeOffset nowUtc, ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(context);

        var finished = IsFinished(run);

        // 強制取消要不要出現另外算（§6.4）：共通條件成立時照樣出現、只是停用，例如取消超過 5 分鐘時離線。
        var showForceCancel = context.Cancelling is { ForceSent: false } && context.CancelGraceElapsed && !finished;

        // 共通條件（§7.2）：送出中優先，其次是其他共通條件；任一成立，四個動作都不能用。
        var common = context.InFlight ? ActionBlock.InFlight : context.GlobalBlock;
        if (common != ActionBlock.None)
        {
            return new RunActions(run.RepoId, run.RunAttempt, common, common, common, showForceCancel, common);
        }

        ActionBlock forceCancel;
        if (showForceCancel)
        {
            forceCancel = ActionBlock.None;
        }
        else
        {
            // 強制取消送出後不再出現，原因是「已送出取消」（第二階段實作計畫「和 spec 的差異與補充」第 4 列）。
            forceCancel = context.Cancelling?.ForceSent == true ? ActionBlock.CancelPending : ActionBlock.ForceNotYet;
        }

        return new RunActions(
            run.RepoId,
            run.RunAttempt,
            Rerun(run, workflow, nowUtc, context, failedJobsOnly: true),
            Rerun(run, workflow, nowUtc, context, failedJobsOnly: false),
            Cancel(context, finished),
            showForceCancel,
            forceCancel);
    }

    /// <summary>重跑失敗的 job（E11）或整個 run 重跑（E12）：§7.1 與 §7.3。</summary>
    private static ActionBlock Rerun(RunInfo run, WorkflowInfo? workflow, DateTimeOffset nowUtc, ActionContext context, bool failedJobsOnly)
    {
        if (!IsFinished(run))
        {
            return ActionBlock.NotCompleted;
        }

        if (Is(run.Conclusion, ActionRequired))
        {
            return ActionBlock.AwaitingApproval;
        }

        if (context.RerunPending)
        {
            return ActionBlock.RerunPending;
        }

        if (failedJobsOnly)
        {
            // 啟動失敗時可能根本沒有 job（P1 §10.9）。
            if (Is(run.Status, StartupFailure) || Is(run.Conclusion, StartupFailure))
            {
                return ActionBlock.NoRerunnableJobs;
            }

            // cancelled 能不能重跑失敗的 job 官方頁沒寫，先開放（§7.1，實測 R7）。
            if (!Is(run.Conclusion, Failure) && !Is(run.Conclusion, TimedOut) && !Is(run.Conclusion, Cancelled))
            {
                return ActionBlock.NoFailedJobs;
            }
        }

        // 本地沒有這個 workflow 時不擋，由伺服器決定。
        if (workflow is { IsActive: false })
        {
            return ActionBlock.WorkflowDisabled;
        }

        if (nowUtc - run.CreatedAt > RerunWindow)
        {
            return ActionBlock.TooOld;
        }

        return run.RunAttempt >= Tuning.MaxReruns + FirstAttempt ? ActionBlock.RerunLimit : ActionBlock.None;
    }

    /// <summary>取消（E13）：§7.1 與 §7.4。</summary>
    private static ActionBlock Cancel(ActionContext context, bool finished)
    {
        if (context.Cancelling is not null)
        {
            return ActionBlock.CancelPending;
        }

        return finished ? ActionBlock.Completed : ActionBlock.None;
    }

    /// <summary>
    /// 已完成：status 是 completed，或 status 本身是 startup_failure（P1 判成失敗，§7.1 不歸到「未完成」）。
    /// 取消中的結束條件也用它（MonitorEngine 的操作後追蹤，§6.3）。
    /// </summary>
    internal static bool IsFinished(RunInfo run) => Is(run.Status, Completed) || Is(run.Status, StartupFailure);

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.Ordinal);
}
