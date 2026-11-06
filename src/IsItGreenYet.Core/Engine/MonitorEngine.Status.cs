using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Filtering;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Status;
using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Core.Engine;

/// <summary>
/// 給命令列讀的狀態表（第二階段 spec §10.2、§10.3）與命令列的刷新（§10.5）：每次發布快照時組 <see cref="StatusPayload"/>，
/// 內容改變才立即寫、每 <see cref="Tuning.StatusHeartbeatInterval"/> 整列重寫一次當成心跳；命令列刷新記 started／done 序號，
/// 刷新的目標都處理完（或被全域狀態擋住、超過 <see cref="Tuning.RefreshCompletionMax"/>）時讓 done 追上 started。
/// </summary>
/// <remarks>
/// 全部步驟專用，只有 <see cref="DisposeAsync"/> 在迴圈與使用者操作都停下之後寫一次 stopped_at。
/// 寫入失敗只寫 error.log、不影響輪詢（§10.2、§14）：不顯示未預期錯誤的提示；DB 損毀照常交給改名重建（<see cref="ReportUnexpected"/>）。
/// 改名重建失敗或重建後仍損毀、等重試的期間（快照沿用上一份內容的那段）不寫；重建後下一次一定整列重寫。
/// </remarks>
public sealed partial class MonitorEngine
{
    /// <summary>寫狀態表失敗時 error.log 的標題。</summary>
    private const string StatusWriteContext = "寫入狀態表";

    /// <summary>初始化時讀不懂狀態表那一列時 error.log 的標題。</summary>
    private const string StatusLoadContext = "讀取狀態表的刷新序號（當成 0，下一次寫入整列覆蓋）";

    /// <summary>主清單「只看需要注意」的篩選（P1 Q13）：狀態表的 runs 和它一樣。</summary>
    private static readonly RunFilterCriteria AttentionOnly = RunFilterCriteria.Empty with { AttentionOnly = true };

    // ── 狀態表（步驟專用）──────────────────────────────────────────────

    /// <summary>
    /// 已經從狀態表讀回刷新的序號（初始化時）。還沒讀回之前不寫：否則會用 0 蓋掉上一次執行的序號，
    /// 正在等的命令列永遠等不到（§10.5）。執行中重建資料庫時不重讀，沿用記憶體裡的值。
    /// </summary>
    private bool _statusLoaded;

    private long _refreshStartedSeq;
    private long _refreshDoneSeq;
    private bool _refreshPartial;

    /// <summary>
    /// 上一次<b>成功</b>寫入的比較值：<see cref="StatusPayload.SnapshotAt"/> 設成 default 之後序列化的字串。
    /// 寫入失敗、重建資料庫之後清成 null，下一次一定算內容改變、整列重寫（§10.2）。
    /// </summary>
    private string? _statusCompare;

    /// <summary>上一次成功寫入的 <see cref="StatusPayload.SnapshotAt"/>：內容沒變時沿用。</summary>
    private DateTimeOffset _statusSnapshotAt;

    /// <summary>下一次心跳（或寫入失敗後的重試）的單調時間戳；還沒寫過、重建資料庫之後是 null（立即寫）。</summary>
    private long? _statusDueAt;

    /// <summary>上一次寫入失敗：到 <see cref="_statusDueAt"/> 之前不重試，內容改變也不寫，不會每一步都重試、都寫 error.log。</summary>
    private bool _statusRetrying;

    /// <summary>刷新的序號改變了、還沒成功寫入：不管內容與心跳都要寫（序號不在比較值裡）。</summary>
    private bool _statusSequenceChanged;

    /// <summary>進行中的命令列刷新；沒有時是 null。</summary>
    private CliRefreshBatch? _cliRefresh;

    /// <summary>刷新進行中又收到訊號：這一次結束後再做一次（最多一次，§10.5 第 6 點）。</summary>
    private bool _cliRefreshAgain;

    /// <summary>
    /// 一份快照轉成狀態表的內容（§10.3 標「App 寫」的欄位）。純轉換：版本錯誤、離線、5xx 從快照讀，其他由呼叫端傳入。
    /// </summary>
    /// <param name="snapshotAtUtc">這份狀態對應的快照時間（比較內容時傳 default）。</param>
    /// <param name="pausedUntil">全域暫停預定恢復的時間（暫停時記下的固定值）；沒有暫停時是 null。</param>
    /// <param name="lastRepoSuccessUtc">
    /// 監看中 repo 最近一次成功回應的時間（各 repo 的 last_success_at 取最大值）。不是 <see cref="MonitorSnapshot.LastUpdatedUtc"/>：
    /// 那個會被 /user、/rate_limit 這些不屬於 repo 的回應更新。
    /// </param>
    internal static StatusPayload BuildStatusPayload(MonitorSnapshot snapshot, string appVersion,
        DateTimeOffset snapshotAtUtc, DateTimeOffset? pausedUntil, DateTimeOffset? lastRepoSuccessUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(appVersion);

        var summary = snapshot.Summary;
        var runs = RunFilter.Apply(snapshot.Rows, AttentionOnly)
            .Select(row => new StatusRun(
                row.Repo.FullName,
                row.WorkflowName,
                row.Run.Id,
                row.Run.RunNumber,
                row.Run.RunAttempt,
                row.Run.DisplayTitle,
                row.Run.HeadBranch,
                row.Run.Event,
                row.Run.Status,
                row.Run.Conclusion,
                CategoryText(row.State.Category),
                snapshot.Cancelling.ContainsKey(row.Run.Id),
                row.Run.RunStartedAt,
                row.Run.HtmlUrl))
            .ToList();

        return new StatusPayload(
            appVersion,
            snapshotAtUtc,
            OverallText(summary.State),
            SignedIn(snapshot.Auth),
            summary.Issues.HasFlag(ConnectionIssue.VersionError),
            RefreshModeText(snapshot.RefreshMode),
            new StatusCounts(summary.FailingCount, summary.PendingCount),
            snapshot.Loading.Total,
            summary.StaleRepoCount,
            new StatusConnection(snapshot.Offline, pausedUntil, summary.Issues.HasFlag(ConnectionIssue.ServerErrors)),
            lastRepoSuccessUtc,
            runs.AsReadOnly());
    }

    /// <summary>系統匣的整體狀態（P1 §7.3）；NoData 與不認得的值都是 no_data。</summary>
    private static string OverallText(OverallState state) => state switch
    {
        OverallState.Failure => "failure",
        OverallState.Pending => "in_progress",
        OverallState.Success => "success",
        _ => "no_data",
    };

    /// <summary>P1 §7.1 的判定類別；灰色（取消、略過、neutral、stale、完成後不認得的結論）與不認得的值都是 neutral。</summary>
    private static string CategoryText(RunCategory category) => category switch
    {
        RunCategory.Pending => "pending",
        RunCategory.Waiting => "waiting",
        RunCategory.Success => "success",
        RunCategory.Failure => "failure",
        _ => "neutral",
    };

    /// <summary>還在確認（剛啟動、離線啟動，E1 還沒驗證完）時是 null（§10.3）。</summary>
    private static bool? SignedIn(AuthStatus auth) => auth switch
    {
        AuthStatus.Ok => true,
        AuthStatus.Unauthorized => false,
        _ => null,
    };

    /// <summary>settings.json 的寫法（同 SettingsJsonConverters.RefreshModeConverter）；不認得的值和那裡一樣寫成預設的 auto。</summary>
    private static string RefreshModeText(RefreshMode mode) => mode switch
    {
        RefreshMode.Seconds30 => "30s",
        RefreshMode.Minute1 => "1m",
        RefreshMode.Minutes5 => "5m",
        RefreshMode.Paused => "paused",
        _ => "auto",
    };

    // ── 讀回與寫入 ────────────────────────────────────────────────────

    /// <summary>
    /// 初始化時讀回上一次執行的刷新序號（沒有資料列就是 0），之後才開始寫狀態表。
    /// 那一列讀不懂（例如時間欄不是這個程式寫的格式）時寫 error.log、序號當成 0，下一次寫入整列覆蓋，不讓初始化一直重試；
    /// SQLite 本身的錯誤（損毀、鎖定等）照常往外丟，交給重建或初始化的退避。
    /// </summary>
    private void LoadStatusSequences()
    {
        StatusSnapshotRow? row;
        try
        {
            row = _cache.GetStatusSnapshot();
        }
        catch (Exception ex) when (ex is not SqliteException && !IsDatabaseCorruption(ex))
        {
            _deps.ErrorLog.Write(StatusLoadContext, ex);
            row = null;
        }

        _refreshStartedSeq = row?.RefreshStartedSeq ?? 0;
        _refreshDoneSeq = row?.RefreshDoneSeq ?? 0;
        _refreshPartial = row?.RefreshPartial ?? false;
        _statusLoaded = true;
    }

    /// <summary>執行中重建資料庫之後：新的資料庫沒有狀態表的資料列，下一次發布快照時立即整列重寫（§10.2），不等心跳或失敗後的重試。</summary>
    private void ForgetStatusWrite()
    {
        _statusCompare = null;
        _statusDueAt = null;
        _statusRetrying = false;
    }

    /// <summary>
    /// 發布快照之後（<see cref="PublishSnapshot"/>）：內容和上一次成功寫入的不同、序號改變、或到了心跳的時間，就整列寫入。
    /// 內容沒變時沿用上一次的 snapshotAt；變了才用現在的時間。
    /// </summary>
    private void UpdateStatusTable(MonitorSnapshot snapshot) => WriteStatus(snapshot, stoppedAt: null);

    /// <summary>
    /// 結束時（<see cref="DisposeAsync"/>，迴圈與使用者操作都已經停下）用最後一份快照寫一次 stopped_at：
    /// 不看心跳、內容與失敗後的等待。下次啟動後第一次寫入時清成 null（每一次一般的寫入都是 null）。
    /// </summary>
    private void WriteStoppedStatus()
    {
        if (_lastSnapshot is { } last)
        {
            WriteStatus(last, _time.GetUtcNow());
        }
    }

    /// <param name="stoppedAt">結束時寫入的時間；一般的寫入是 null。</param>
    private void WriteStatus(MonitorSnapshot snapshot, DateTimeOffset? stoppedAt)
    {
        if (!_statusLoaded || Volatile.Read(ref _recoverRetryAt) != NoRecoveryRetry)
        {
            return;
        }

        var stopping = stoppedAt is not null;
        var due = _statusDueAt is not { } dueAt || _time.GetElapsedTime(dueAt) >= TimeSpan.Zero;
        if (_statusRetrying && !due && !stopping)
        {
            return;
        }

        try
        {
            var payload = BuildStatusPayload(snapshot, _deps.AppVersion, default, PausedUntil(), LastMonitoredSuccessUtc());
            var compare = StatusJson.Serialize(payload);
            var changed = !string.Equals(compare, _statusCompare, StringComparison.Ordinal);
            if (!changed && !due && !_statusSequenceChanged && !stopping)
            {
                return;
            }

            // 每次都整列寫（§10.2）：寫入時間與心跳都是這一次。
            var now = _time.GetUtcNow();
            var snapshotAt = changed ? now : _statusSnapshotAt;
            _cache.SaveStatusSnapshot(new StatusSnapshotRow(now, now, stoppedAt, _deps.AppVersion,
                StatusJson.Serialize(payload with { SnapshotAt = snapshotAt }), _refreshStartedSeq, _refreshDoneSeq, _refreshPartial));

            _statusCompare = compare;
            _statusSnapshotAt = snapshotAt;
            _statusSequenceChanged = false;
            _statusRetrying = false;
            _statusDueAt = FutureTimestamp(Tuning.StatusHeartbeatInterval);
        }
        catch (Exception ex)
        {
            // 下一次一定整列重寫，但要等一個心跳間隔才重試（§10.2）。
            _statusCompare = null;
            _statusRetrying = true;
            _statusDueAt = FutureTimestamp(Tuning.StatusHeartbeatInterval);
            if (IsDatabaseCorruption(ex))
            {
                ReportUnexpected(StatusWriteContext, ex);
            }
            else
            {
                _deps.ErrorLog.Write(StatusWriteContext, ex);
            }
        }
    }

    /// <summary>
    /// 全域暫停預定恢復的時間：暫停橫幅記下的固定值（<see cref="PauseAll"/>），不是「現在 + 剩餘時間」，
    /// 否則每一步的內容都不同、每一步都重寫。
    /// </summary>
    private DateTimeOffset? PausedUntil()
    {
        lock (_bannerGate)
        {
            return _throttle.IsPaused ? _pauseBanner?.Until : null;
        }
    }

    /// <summary>監看中 repo 最近一次成功回應的時間（repos.last_success_at 的最大值，跨重新啟動保留）；還沒有時是 null。</summary>
    private DateTimeOffset? LastMonitoredSuccessUtc() =>
        _repos.Keys.Where(IsMonitored).Select(repoId => StateOf(repoId).LastSuccessAt).Max();

    /// <summary>下一次心跳（或寫入失敗後的重試）還要多久；不寫狀態表的期間（還沒讀回序號、等重建重試）回 null。</summary>
    /// <remarks>已經過了（這一步該寫卻沒寫，例如建立快照失敗）時等 EngineTick，迴圈不會空轉。</remarks>
    private TimeSpan? StatusWakeDelay()
    {
        if (!_statusLoaded || Volatile.Read(ref _recoverRetryAt) != NoRecoveryRetry)
        {
            return null;
        }

        return AtLeastTick(_statusDueAt is { } due ? Remaining(due, TimeSpan.Zero) : TimeSpan.Zero);
    }

    // ── 命令列刷新（§10.5）────────────────────────────────────────────

    /// <summary>
    /// <see cref="EngineCommand.CliRefresh"/>：沒有刷新在進行時開始一次並立即寫入；進行中又收到時只記下「之後再做一次」，
    /// 不合併進這一次，也不動 started。
    /// </summary>
    private void OnCliRefresh()
    {
        if (_cliRefresh is not null)
        {
            _cliRefreshAgain = true;
            return;
        }

        try
        {
            StartCliRefresh();
        }
        finally
        {
            // 開始失敗、這一次已經收尾（StartCliRefresh）時也立即寫；WriteStatus 自己攔下寫入的例外，不會蓋掉原本的例外。
            if (_lastSnapshot is { } last)
            {
                UpdateStatusTable(last);
            }
        }
    }

    /// <summary>
    /// started 加 1。當下就被全域狀態擋住時立即完成並標 partial（例如離線時全量刷新只排一次離線探測，
    /// 探測的結果不能當成刷新完成）；否則做一次全量刷新（同 F5，自動更新暫停時照樣做），排入的目標就是這一批。
    /// 寫入由呼叫端決定：收到指令時立即寫，結束後接著開始的那一次和同一步的快照一起寫。
    /// 全量刷新丟例外時這一次立即結束並標 partial（started 已經加 1，不收尾的話 done 永遠追不上，等待中的命令列要等到逾時）；
    /// 例外照常往外丟，由呼叫端的規則回報。
    /// </summary>
    private void StartCliRefresh()
    {
        _refreshStartedSeq++;
        _statusSequenceChanged = true;
        var seq = _refreshStartedSeq;
        if (CliRefreshBlocked())
        {
            FinishCliRefresh(seq, partial: true);
            return;
        }

        try
        {
            _cliRefresh = new CliRefreshBatch(seq, _time.GetTimestamp(), FullRefresh());
        }
        catch (Exception)
        {
            FinishCliRefresh(seq, partial: true);
            throw;
        }
    }

    /// <summary>
    /// 每一步結束前（發布快照之前）檢查進行中的刷新：已經不在排程、也不是正在處理的目標算處理完；
    /// 途中被全域狀態擋住就結束並標 partial；這一批空了、或從開始經過 RefreshCompletionMax（超時也標 partial）就結束。
    /// 結束時如果之後還要再做一次，接著開始下一次；這一步的快照連同新的序號一起整列寫入。
    /// </summary>
    private void CheckCliRefresh()
    {
        if (_cliRefresh is not { } refresh)
        {
            return;
        }

        refresh.Remaining.RemoveWhere(target => target != _current && !_scheduler.Contains(target));
        bool partial;
        if (CliRefreshBlocked())
        {
            partial = true;
        }
        else if (refresh.Remaining.Count == 0)
        {
            partial = false;
        }
        else if (_time.GetElapsedTime(refresh.StartedAt) >= Tuning.RefreshCompletionMax)
        {
            partial = true;
        }
        else
        {
            return;
        }

        _cliRefresh = null;
        FinishCliRefresh(refresh.Seq, partial);
        if (_cliRefreshAgain)
        {
            _cliRefreshAgain = false;
            StartCliRefresh();
        }
    }

    private void FinishCliRefresh(long seq, bool partial)
    {
        _refreshDoneSeq = seq;
        _refreshPartial = partial;
        _statusSequenceChanged = true;
    }

    /// <summary>
    /// 會讓刷新拿不到新資料的全域狀態：未登入（同 <see cref="UserStopReason"/>）、API 版本錯誤、全域暫停、離線、睡眠。
    /// 認證還在確認（<see cref="AuthStatus.Unknown"/>，剛啟動、E1 還沒驗證完）不算，照常刷新。
    /// </summary>
    private bool CliRefreshBlocked() =>
        _auth.Token is null
        || _auth.Status == AuthStatus.Unauthorized
        || _versionError
        || _throttle.IsPaused
        || _offline.IsOffline
        || _suspended;

    /// <summary>進行中的刷新還要多久到 RefreshCompletionMax；沒有刷新時回 null。</summary>
    private TimeSpan? CliRefreshWakeDelay() =>
        _cliRefresh is { } refresh ? AtLeastTick(Remaining(refresh.StartedAt, Tuning.RefreshCompletionMax)) : null;

    /// <summary>已經到了的時間點（這一步該處理卻還沒處理完）至少等 EngineTick 再醒來，迴圈不會空轉。</summary>
    private static TimeSpan AtLeastTick(TimeSpan remaining) => remaining > TimeSpan.Zero ? remaining : Tuning.EngineTick;

    /// <summary>一次命令列刷新：序號、開始的單調時間戳，與全量刷新排入、還沒處理完的目標。</summary>
    private sealed class CliRefreshBatch(long seq, long startedAt, IEnumerable<PollTarget> targets)
    {
        public long Seq { get; } = seq;

        public long StartedAt { get; } = startedAt;

        public HashSet<PollTarget> Remaining { get; } = [.. targets];
    }
}
