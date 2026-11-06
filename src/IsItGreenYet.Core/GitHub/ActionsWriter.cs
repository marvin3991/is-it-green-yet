using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.GitHub;

/// <summary>四個寫入動作，各對應一個端點（第二階段 spec §3.1）。</summary>
public enum WriteAction
{
    /// <summary>E11：重跑失敗的 job 與其下游 job。</summary>
    RerunFailedJobs,

    /// <summary>E12：整個 run 重跑。</summary>
    RerunAll,

    /// <summary>E13：取消。</summary>
    Cancel,

    /// <summary>E14：強制取消；只在一般取消沒反應時才用（第二階段 spec §6.4）。</summary>
    ForceCancel,
}

/// <summary>伺服器有回應、但沒有執行的原因（第二階段 spec §3.3 的分類，依表的順序判斷）。</summary>
public enum WriteRejectReason
{
    /// <summary>第 2 列：限流（同 spec §6.4 第 1、2 列），全域暫停。</summary>
    RateLimited,

    /// <summary>第 3 列：401，重取 token 重送一次後仍是 401。</summary>
    Unauthorized,

    /// <summary>第 4 列：403，而且 message 以 <c>Resource not accessible by</c> 開頭；這個 repo 的寫入改成停用。</summary>
    InsufficientPermission,

    /// <summary>第 5 列：其他 403。</summary>
    Forbidden,

    /// <summary>第 6 列：404；分不出是 run 不存在還是缺少寫入權限。</summary>
    NotFound,

    /// <summary>第 7 列：409。</summary>
    Conflict,

    /// <summary>第 8 列：422。</summary>
    Unprocessable,

    /// <summary>第 10 列：3xx；寫入一律不跟隨轉址。</summary>
    Moved,

    /// <summary>第 9、11 列：400、410、5xx 與其他狀態碼。400／410 不當成 API 版本錯誤。</summary>
    Other,
}

/// <summary>
/// 四個寫入端點（第二階段 spec §3.1、§3.2）：POST 不送 body、不帶 If-None-Match、不讀寫 http_cache、不跟隨轉址，這一層也不重送。
/// </summary>
/// <remarks>
/// 回傳 <see cref="ApiResult{T}"/> 而不是第二階段 spec §5.1 寫的 WriteResult：Engine 要拿到額度 header、401 與限流資訊，
/// 才能套用 spec §13.2 與 §6.4（401 換到新 token 後重送一次由 Engine 做）；給 App 的 WriteResult 由 Engine 組。
/// App 不直接呼叫這裡，一律經過 Engine。2xx 一律算受理，不解析 body，所以 <see cref="ApiResult{T}.Value"/> 沒有內容。
/// </remarks>
public interface IActionsWriter
{
    Task<ApiResult<object>> RerunFailedJobsAsync(RepoInfo repo, long runId, CancellationToken ct);

    Task<ApiResult<object>> RerunAllAsync(RepoInfo repo, long runId, CancellationToken ct);

    Task<ApiResult<object>> CancelRunAsync(RepoInfo repo, long runId, CancellationToken ct);

    Task<ApiResult<object>> ForceCancelRunAsync(RepoInfo repo, long runId, CancellationToken ct);
}
