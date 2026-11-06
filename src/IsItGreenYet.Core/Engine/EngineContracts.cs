using System.Collections.Frozen;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.Filtering;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Logging;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Engine;

/// <summary>App 交給 Engine 的指令；Engine 迴圈每醒來一次會先處理完所有排隊的指令（spec §3.1）。</summary>
public abstract record EngineCommand
{
    public sealed record RefreshAll : EngineCommand;

    public sealed record SetLocked(bool Locked) : EngineCommand;

    public sealed record Suspend : EngineCommand;

    public sealed record Resume : EngineCommand;

    public sealed record NetworkAvailable : EngineCommand;

    public sealed record SetRefreshMode(RefreshMode Mode) : EngineCommand;

    public sealed record SetOverride(long RepoId, RepoOverride Value) : EngineCommand;

    public sealed record SetWatched(long RunId, bool Watched) : EngineCommand;

    public sealed record SettingsChanged(AppSettings Settings) : EngineCommand;

    public sealed record RecheckAuth : EngineCommand;

    public sealed record TimeZoneChanged : EngineCommand;

    public sealed record ToastDismissed : EngineCommand;

    /// <summary>開啟 repo 管理頁：下一輪完整重抓連清單 B 一起抓一次，才能列出所有看得到的 repo（spec §5.1、§11.1）。</summary>
    public sealed record RepoManagerOpened : EngineCommand;

    /// <summary>命令列 <c>--refresh</c> 發出的刷新訊號（第二階段 spec §10.5），由 App 收到具名事件後轉送。</summary>
    public sealed record CliRefresh : EngineCommand;
}

public enum BannerKind
{
    Unauthorized,
    PrimaryLimit,
    SecondaryLimit,
    Offline,
    ServerErrors,
    VersionError,
    Deprecated,
    DatabaseRecovered,
    SettingsRecovered,
    UnexpectedError,
}

/// <param name="Until">預計恢復或預定重試的時間（UTC），沒有則為 null。</param>
public sealed record BannerInfo(BannerKind Kind, string Text, DateTimeOffset? Until)
{
    private static long s_lastNoticeId;

    /// <summary>
    /// 一次性提示（Engine 的 Deprecation、執行中重建資料庫、未預期例外，以及 App 的啟動提示與背景錯誤）是「哪一則」：
    /// 換語言時 Engine 與 App 用 <c>with</c> 換掉 <see cref="Text"/>、保留這個值，App 據此記住使用者關掉的是哪一則。
    /// 0 表示每次快照重新組的橫幅（未登入、限流、離線、5xx、版本錯誤）。用 <see cref="NextNoticeId"/> 取得。
    /// </summary>
    public long NoticeId { get; init; }

    /// <summary>新的一則一次性提示的識別碼（行程內遞增，從 1 開始；可以從任何執行緒呼叫）。</summary>
    public static long NextNoticeId() => Interlocked.Increment(ref s_lastNoticeId);
}

public sealed record RepoStatusRow(
    RepoInfo Repo,
    bool Monitored,
    SkipReason Reason,
    bool CanForceInclude,
    RepoOverride Override,
    string? ProblemNote);

public sealed record LoadingProgress(int Completed, int Total);

/// <summary>Engine 每處理完一批結果發布的不可變快照；App 只讀這個。</summary>
public sealed record MonitorSnapshot(
    IReadOnlyList<RunRow> Rows,
    StatusSummary Summary,
    DateTimeOffset? LastUpdatedUtc,
    RateLimitSnapshot? RateLimit,
    IReadOnlyList<RepoStatusRow> Repos,
    BannerInfo? Banner,
    LoadingProgress Loading,
    AuthStatus Auth,
    TokenSource TokenSource,
    string? TrayIssueLine,
    bool Offline,
    RefreshMode RefreshMode,
    IReadOnlySet<long> WatchedRunIds,
    IReadOnlyDictionary<long, EtaInfo> Eta,
    IReadOnlyDictionary<long, TimeSpan> CompletedDurations,
    IReadOnlyDictionary<long, IReadOnlyList<int>> PullRequestNumbers)
{
    /// <summary>
    /// 這次執行已經抓完至少一輪完整的 repo 清單。看得到 0 個 repo 時，主畫面靠它分辨
    /// 「正在載入」與「目前沒有 workflow 執行紀錄」（spec §10.11）。
    /// 和 <see cref="Repos"/> 是同一刻的值：資料庫重建退避期間沿用上一份快照時一起沿用。
    /// </summary>
    public bool RepoListFetched { get; init; }

    /// <summary>
    /// 每一筆 run 的四個寫入動作能不能用（第二階段 spec §7），鍵是 run id；同一份也是寫入送出前的可操作性查表（第二階段 spec §5.3）。
    /// </summary>
    public IReadOnlyDictionary<long, RunActions> Actions { get; init; } = FrozenDictionary<long, RunActions>.Empty;

    /// <summary>「取消中」的 run（第二階段 spec §6.3），鍵是 run id；只在記憶體，App 重新啟動後就沒有。</summary>
    public IReadOnlyDictionary<long, CancellingInfo> Cancelling { get; init; } = FrozenDictionary<long, CancellingInfo>.Empty;

    /// <summary>寫入功能的開關（設定的 allowWriteActions）；關閉時 run 選單不出現寫入的項目（第二階段 spec §9.1）。</summary>
    public bool WritesEnabled { get; init; }

    /// <summary>
    /// 第幾份發布的快照：Engine 每發布一份加 1，第一份是 1；還沒經過 Engine 發布時是 0。
    /// App 拿它和 <see cref="WriteResult.SnapshotSequence"/> 比較，判斷收到的快照是不是寫入回傳之後的。
    /// </summary>
    public long Sequence { get; init; }
}

public sealed record CopyLogResult(bool Success, string? Text, string Message);

/// <summary>
/// 檢查新版本要寫回 settings.json 的完整狀態。Engine 不寫設定檔，由 App 在 UI 執行緒套用並存檔；
/// 每次都帶兩個欄位的現值，所以後送的一定蓋掉先送的。
/// </summary>
public sealed record UpdateCheckRecord(DateTimeOffset? LastCheckUtc, string? LastNotifiedVersion)
{
    public void ApplyTo(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.LastUpdateCheckUtc = LastCheckUtc;
        settings.LastNotifiedVersion = LastNotifiedVersion;
    }
}

public sealed record ConnectionCheckResult(bool Ok, string? Login, int VisibleRepos, int PrivateRepos, string? ScopesNote, string Message);

/// <param name="AppVersion">
/// App 的完整 InformationalVersion（版本號接 <c>+</c> 與 commit），寫進狀態表給命令列讀（第二階段 spec §2、§10.3）。
/// </param>
public sealed record EngineDependencies(
    TimeProvider Time,
    HttpMessageHandler Handler,
    AppPaths Paths,
    CacheRepository Cache,
    AppSettings Settings,
    ITokenProvider TokenProvider,
    ApiLog ApiLog,
    ErrorLog ErrorLog,
    SecretRedactor Redactor,
    string UserAgent,
    DateTimeOffset StartedAtUtc,
    string AppVersion);
