using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.GitHub;

/// <summary><see cref="IActionsWriter"/> 的輔助方法。</summary>
public static class ActionsWriterExtensions
{
    /// <summary>依 <paramref name="action"/> 呼叫對應的寫入端點（第二階段 spec §3.1），讓 Engine 用一個入口處理四個動作。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> 不是已定義的動作；不送出請求。</exception>
    public static Task<ApiResult<object>> WriteAsync(this IActionsWriter writer, WriteAction action, RepoInfo repo, long runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);

        return action switch
        {
            WriteAction.RerunFailedJobs => writer.RerunFailedJobsAsync(repo, runId, ct),
            WriteAction.RerunAll => writer.RerunAllAsync(repo, runId, ct),
            WriteAction.Cancel => writer.CancelRunAsync(repo, runId, ct),
            WriteAction.ForceCancel => writer.ForceCancelRunAsync(repo, runId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "不認得的寫入動作。"),
        };
    }
}
