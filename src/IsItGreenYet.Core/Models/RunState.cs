namespace IsItGreenYet.Core.Models;

/// <summary>判定類別（spec §7.1）：決定算不算失敗、要不要加速輪詢、系統匣顏色。</summary>
public enum RunCategory
{
    /// <summary>未完成：queued、in_progress、requested、pending，以及不認得的非 completed 狀態。</summary>
    Pending,

    /// <summary>等待核准：waiting，或 completed＋action_required。</summary>
    Waiting,

    Success,

    /// <summary>failure、timed_out、startup_failure。</summary>
    Failure,

    /// <summary>cancelled、skipped、neutral、stale，以及完成後不認得的結論。</summary>
    Gray,
}

/// <summary>清單上的顯示狀態，每一種有自己的圖示形狀（spec §7.1）。</summary>
public enum DisplayState
{
    Queued,
    InProgress,
    Waiting,
    Success,
    Failure,
    Cancelled,
    Skipped,
    Unknown,
}

/// <summary>一筆 run 判讀後的狀態。<see cref="Label"/> 是畫面上的文字（依語言，見 RunStateMapper），例如「失敗（逾時）」。</summary>
public sealed record RunState(DisplayState Display, RunCategory Category, string Label, string? RawValue)
{
    /// <summary>已經有最終結果（成功、失敗或灰色）。等待核准不算完成。</summary>
    public bool IsCompleted => Category is RunCategory.Success or RunCategory.Failure or RunCategory.Gray;
}

/// <summary>系統匣與摘要列的整體狀態，依優先序由上到下（spec §7.3）。</summary>
public enum OverallState
{
    Failure,
    Pending,
    Success,
    NoData,
}

/// <summary>全域性的連線問題，用疊加角標表示，不取代狀態色（spec §7.3）。</summary>
[Flags]
public enum ConnectionIssue
{
    None = 0,
    Unauthorized = 1,
    RateLimited = 2,
    Offline = 4,
    ServerErrors = 8,
    VersionError = 16,
}
