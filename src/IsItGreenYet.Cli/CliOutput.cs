using IsItGreenYet.Core.Status;

namespace IsItGreenYet.Cli;

/// <summary>
/// 命令列印到 stdout 的 JSON（第二階段 spec §10.3）：欄位順序照 spec 的範例，用 <see cref="StatusJson.Options"/> 序列化成一行純 ASCII。
/// 讀不到狀態表時 App 寫的欄位是 null、<see cref="Runs"/> 是空陣列；錯誤時讀得到狀態表照樣附上（§10.6）。
/// </summary>
/// <param name="State"><see cref="CliState"/> 的其中一個；沒有錯誤時等於 <see cref="Overall"/>。</param>
/// <param name="ExitCode">和程式的退出碼相同。</param>
/// <param name="Reason"><see cref="State"/> 是 error 或 no_data 時的原因代碼（<see cref="CliReason"/>），其他時候是 null。</param>
/// <param name="CliVersion">命令列自己的 InformationalVersion。</param>
/// <param name="AppVersion">狀態表 app_version 欄（App 的 InformationalVersion）。</param>
/// <param name="GeneratedAt">命令列產生這份輸出的時間。</param>
/// <param name="HeartbeatAt">狀態表 heartbeat_at 欄。</param>
/// <param name="Refreshed"><c>--refresh</c> 等到這次刷新完成（§10.5）。</param>
/// <param name="RefreshPartial">只有 <see cref="Refreshed"/> 時才是狀態表 refresh_partial 的值，其他時候是 false。</param>
internal sealed record CliOutput(int SchemaVersion, string State, int ExitCode, string? Reason, string CliVersion,
    string? AppVersion, DateTimeOffset GeneratedAt, DateTimeOffset? SnapshotAt, DateTimeOffset? HeartbeatAt,
    bool Refreshed, bool RefreshPartial, string? Overall, bool? SignedIn, bool? ApiVersionError, string? RefreshMode,
    StatusCounts? Counts, int? MonitoredRepos, int? StaleRepos, StatusConnection? Connection,
    DateTimeOffset? LastUpdatedAt, IReadOnlyList<StatusRun> Runs)
{
    /// <summary>這個 JSON 結構的版本：從 1 開始，欄位只增不改，改變意義時加 1（§10.3）。</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>§10.6 的退出碼。1 &gt; 2 &gt; 0 的順序和系統匣一致（P1 §7.3）。</summary>
internal static class CliExitCode
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int InProgress = 2;
    public const int NoData = 3;
    public const int Error = 4;
}

/// <summary>輸出的 <c>state</c>（§10.3）。前四個也是狀態表 <c>overall</c> 的四種值（App 寫，§10.3）。</summary>
internal static class CliState
{
    public const string Failure = "failure";
    public const string InProgress = "in_progress";
    public const string Success = "success";
    public const string NoData = "no_data";
    public const string Error = "error";
}

/// <summary>輸出的 <c>reason</c>（§10.6）。</summary>
internal static class CliReason
{
    public const string InternalError = "internal_error";
    public const string AppNotRunning = "app_not_running";
    public const string RefreshUnreachable = "refresh_unreachable";
    public const string RefreshUnsupported = "refresh_unsupported";
    public const string DbError = "db_error";
    public const string SchemaMismatch = "schema_mismatch";
    public const string NoStatus = "no_status";
    public const string RefreshTimeout = "refresh_timeout";
    public const string Stale = "stale";
    public const string SignedOut = "signed_out";
    public const string VersionError = "version_error";
    public const string DataStale = "data_stale";
    public const string NoData = "no_data";
}

/// <summary>§10.6 判斷的結果。</summary>
/// <param name="State">null：不輸出 JSON（第 1 列，參數錯誤）。</param>
internal sealed record CliDecision(int ExitCode, string? State, string? Reason);
