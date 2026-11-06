using System.Collections.Frozen;
using System.Globalization;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Polling;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Engine;

/// <summary>
/// 操作後追蹤（第二階段 spec §6、§8.1）：有回應的寫入之後用 E6 確認伺服器的實際狀態。重跑等到新的 attempt
/// （放棄條件、重新啟動後接著追、結束時轉成「完成時通知我」或直接發 N3）；取消等到完成（「取消中」與強制取消的等待）；
/// 被拒與取消的結果不確定只查一次 E6。
/// </summary>
/// <remarks>
/// 三種追蹤分開記（controller 的 ruling）：同一筆 run 可以同時有待確認的重跑與取消中，後來的寫入不會蓋掉另一種的
/// 送出前 attempt、通知旗標與放棄的計數。全部步驟專用：使用者操作只經由 <see cref="OnWriteAnswered"/>（<c>_userEffects</c>）
/// 交進來，唯一的例外是寫入路徑直接寫 DB 的 pending_reruns（§5.3 第 2 點）。
/// </remarks>
public sealed partial class MonitorEngine
{
    /// <summary>啟動時略過 pending_reruns 讀不懂的一列時 error.log 的標題，後面接「（run N）」。</summary>
    private const string PendingRerunSkippedContext = "略過讀不懂的待確認重跑";

    /// <summary>
    /// 步驟專用：待確認的重跑（E11、E12 已受理或結果不確定），含重新啟動時從 pending_reruns 讀回的，鍵是 run id（§6.2）。
    /// 有這一筆就是 <see cref="ActionContext.RerunPending"/>；結束、放棄、E6 回 404、repo 停止監看或被移除時移除並刪資料列。
    /// </summary>
    private readonly Dictionary<long, RerunTracking> _reruns = [];

    /// <summary>步驟專用：取消中（E13、E14 已受理）的 run，鍵是 run id；看到完成才清除，只在記憶體（§6.3）。</summary>
    private readonly Dictionary<long, CancelTracking> _cancels = [];

    /// <summary>
    /// 步驟專用：被拒（任何動作）與取消的結果不確定，只為了把本地狀態對齊伺服器、等第一次 E6 回應的 run：run id → repo id（§6.2 表的第 3 列）。
    /// </summary>
    private readonly Dictionary<long, long> _alignOnce = [];

    /// <summary>
    /// 步驟裡套用一筆有回應的寫入（<see cref="OnWriteAnswered"/>）：依動作與結果記進對應的追蹤（§6.2 第 1 點的表、§6.3）。
    /// </summary>
    private void RecordPostAction(PendingAction pending)
    {
        switch (pending.Action, pending.Outcome)
        {
            case (WriteAction.RerunFailedJobs or WriteAction.RerunAll, WriteOutcome.Accepted or WriteOutcome.Uncertain)
                when StoppedTrackingSinceSend(pending.RepoId):
                // repo 在送出之後、套用之前被移除（RemoveRepo）或停止監看（OnStopMonitoring）：不追蹤；寫入路徑寫的那一列也刪掉，不留孤兒資料（DeleteRepo）。
                try
                {
                    _cache.DeletePendingRerun(pending.RunId);
                }
                catch (Exception ex)
                {
                    ReportUnexpected("刪除待確認的重跑", ex);
                }

                break;

            case (WriteAction.RerunFailedJobs or WriteAction.RerunAll, WriteOutcome.Accepted or WriteOutcome.Uncertain):
                // 同 pending_reruns 的資料列（RememberPendingRerun）：App 內重跑一律自動「完成時通知我」（§8.1）。
                _reruns[pending.RunId] = new RerunTracking(pending.RunId, pending.RepoId, pending.PreviousAttempt, pending.SentAtUtc,
                    pending.SentTimestamp, Responses: 0, Notify: true);

                // 寫入路徑已經寫過一次，這裡再寫進當下的資料庫（§6.2 第 6 點）：送出之後、套用之前改名重建過時，那一列跟著舊的資料庫移走，
                // 重建時記憶體裡又還沒有這一筆。先記憶體後 DB：這裡又讀到損毀時，下一次重建由 WriteBackPendingReruns 寫回。
                try
                {
                    _cache.UpsertPendingRerun(new PendingRerun(pending.RunId, pending.RepoId, pending.PreviousAttempt, pending.SentAtUtc, Notify: true));
                }
                catch (Exception ex)
                {
                    ReportUnexpected("寫入待確認的重跑", ex);
                }

                break;

            case (WriteAction.Cancel or WriteAction.ForceCancel, WriteOutcome.Accepted)
                when StoppedTrackingSinceSend(pending.RepoId):
                // 同上：之後不再輪詢這個 repo、看不到完成，記成取消中就一直清不掉（§6.3）。取消中只在記憶體，不記就好。
                break;

            case (WriteAction.Cancel, WriteOutcome.Accepted):
                // 已經在取消中就保留原本的受理時間：5 分鐘不重算（§6.4）。
                _cancels.TryAdd(pending.RunId, new CancelTracking(pending.RepoId, pending.SentAtUtc, pending.SentTimestamp, ForceSent: false));
                break;

            case (WriteAction.ForceCancel, WriteOutcome.Accepted):
                // 保留取消（E13）受理的時間，沒有就用這一次的；強制取消不再出現（計畫的差異表第 4 列）。
                _cancels[pending.RunId] = _cancels.TryGetValue(pending.RunId, out var cancelling)
                    ? cancelling with { ForceSent = true }
                    : new CancelTracking(pending.RepoId, pending.SentAtUtc, pending.SentTimestamp, ForceSent: true);
                break;

            default:
                // 被拒、取消的結果不確定：不標成取消中，也不是待確認的重跑（§6.2）。
                _alignOnce[pending.RunId] = pending.RepoId;
                break;
        }
    }

    /// <summary>
    /// 寫入送出之後這個 repo 被移除或停止監看，清除待確認操作的時機（<see cref="ForgetRepoPostActions"/>）已經過了。
    /// 重建資料庫後清單還沒抓回來的不算。
    /// </summary>
    private bool StoppedTrackingSinceSend(long repoId) =>
        _classes.TryGetValue(repoId, out var cls)
            ? EndsPostActions(cls)
            : _repoListFetched && !_repos.ContainsKey(repoId);

    /// <summary>
    /// 這個分類代表 repo 不再監看，待確認的操作不用再追。第 6 條（Stale）不算：重建後本地沒有 run，要等 E4 的首次檢查才知道。
    /// 其他略過原因不是暫時的：第 7、8 條要 E3 真的回來才判定（重建後還沒查過不判定），第 4、5 條是強制納入已遺失。
    /// </summary>
    private static bool EndsPostActions(RepoClassification cls) => cls is { Monitored: false, Reason: not SkipReason.Stale };

    /// <summary>
    /// 讀 DB 之後（<see cref="LoadFromDatabase"/>）：pending_reruns 的每一筆重新追蹤，監看中的 repo 立即排 E6（§6.2 第 6 點）。
    /// 讀回的沒有送出的單調時間戳（不能跨程序比較）、回應次數從 0 起算，放棄只看回應次數。
    /// 執行中重建資料庫時記憶體裡已經有的保留原樣（送出的時間戳與回應次數比較完整）；repo 還沒重新抓回來的，
    /// 等它的 E4 回來由 <see cref="EnsureRunDetails"/> 排 E6。
    /// 讀不懂的資料列略過並寫 error.log（不刪：同一筆 run 再送重跑時整列覆蓋），不讓初始化一直重試，其他列照常追。
    /// </summary>
    private void RestorePendingReruns()
    {
        var rows = _cache.GetPendingReruns((runId, ex) => _deps.ErrorLog.Write(
            string.Create(CultureInfo.InvariantCulture, $"{PendingRerunSkippedContext}（run {runId}）"), ex));
        foreach (var row in rows)
        {
            _reruns.TryAdd(row.RunId, new RerunTracking(row.RunId, row.RepoId, row.PreviousAttempt, row.RequestedAt,
                SentTimestamp: null, Responses: 0, row.Notify));
        }

        foreach (var rerun in _reruns.Values.Where(rerun => IsMonitored(rerun.RepoId)))
        {
            var target = new PollTarget(TargetKind.RunDetail, rerun.RepoId, rerun.RunId);
            _scheduler.ScheduleNow(target, PriorityOf(target));
        }
    }

    /// <summary>
    /// 執行中重建資料庫（<see cref="ResetAfterDatabaseRecovery"/>）：記憶體裡待確認的重跑寫回新的資料庫，重新啟動後才接得上。
    /// 寫回失敗只回報：記憶體裡的追蹤照常保留，重建後面的重讀與重新排程照常做，監看不會因此停下。
    /// </summary>
    private void WriteBackPendingReruns()
    {
        try
        {
            foreach (var rerun in _reruns.Values)
            {
                _cache.UpsertPendingRerun(new PendingRerun(rerun.RunId, rerun.RepoId, rerun.PreviousAttempt, rerun.RequestedAtUtc, rerun.Notify));
            }
        }
        catch (Exception ex)
        {
            ReportUnexpected("寫回待確認的重跑", ex);
        }
    }

    /// <summary>
    /// 看到 run 的最新狀態（<see cref="ApplyRuns"/>，E4、E5、E6 哪一個先看到都算；E6 回 304 時看本地的資料）：判斷結束條件（§6.2）。
    /// 取消看到已完成就不再是取消中；重跑看到新的 attempt，或 run 不再是已完成，就結束並刪資料列（§8.1：新的 attempt 未完成時
    /// 轉成「完成時通知我」）。
    /// </summary>
    /// <returns>重跑在這一次就看到新的 attempt 已經完成、要在這次完成觀察直接走 N3 的 run（§8.1 第 3 步）。</returns>
    private HashSet<long> ObservePostActions(IEnumerable<RunInfo> runs)
    {
        var notifyOnCompletion = new HashSet<long>();
        foreach (var run in runs)
        {
            // status 是 completed，或 status 本身是 startup_failure（同 §7.1 的「已完成」）。
            if (_cancels.ContainsKey(run.Id) && ActionRules.IsFinished(run))
            {
                _cancels.Remove(run.Id);
            }

            if (!_reruns.TryGetValue(run.Id, out var rerun))
            {
                continue;
            }

            // 用判定類別、不用 status 字串：status 本身是 startup_failure 的 run 算已完成（§7.1），不能當成重跑已經開始。
            var completed = RunStateMapper.Map(run).IsCompleted;
            if (run.RunAttempt <= rerun.PreviousAttempt && completed)
            {
                continue;
            }

            EndRerun(run.Id);
            if (!rerun.Notify)
            {
                continue;
            }

            if (completed)
            {
                notifyOnCompletion.Add(run.Id);
            }
            else
            {
                SetWatched(run.Id, watched: true);
            }
        }

        return notifyOnCompletion;
    }

    /// <summary>
    /// E6 有回應（200 或 304），結束條件已經判斷過之後：只查一次的結束；待確認的重跑累計回應次數，達到放棄條件就放棄並刪資料列，
    /// 不通知（§6.2 的放棄條件、§8.1 第 4 步）。回應次數只算 E6（同一筆 run），離線、限流等送不出去的期間不累計。
    /// </summary>
    private void OnRunDetailAnswered(long runId)
    {
        _alignOnce.Remove(runId);
        if (!_reruns.TryGetValue(runId, out var rerun))
        {
            return;
        }

        var responses = rerun.Responses + 1;
        var timedOut = rerun.SentTimestamp is not { } sent || _time.GetElapsedTime(sent) >= Tuning.RerunConfirmTimeout;
        if (responses >= Tuning.RerunConfirmMinResponses && timedOut)
        {
            EndRerun(runId);
            return;
        }

        _reruns[runId] = rerun with { Responses = responses };
    }

    /// <summary>E6 回 404：這筆 run 的所有待確認操作一起清除，pending_reruns 的資料列也刪掉（§6.2 第 5 點）。</summary>
    private void ForgetPostActions(long runId)
    {
        _alignOnce.Remove(runId);
        _cancels.Remove(runId);
        if (_reruns.ContainsKey(runId))
        {
            EndRerun(runId);
        }
    }

    /// <summary>repo 停止監看或被移除：它底下所有 run 的待確認操作一起清除（§6.2）。</summary>
    private void ForgetRepoPostActions(long repoId)
    {
        foreach (var runId in PostActionRunIds(repoId).ToList())
        {
            ForgetPostActions(runId);
        }
    }

    /// <summary>
    /// 抓完一輪完整清單之後：repo 連續 <see cref="Tuning.RemovalConfirmRounds"/> 輪不在 <see cref="_repos"/> 的待確認重跑一起清除
    /// （第二階段 spec §6.2，輪數同 spec §5.4）。重建資料庫時 _repos 清空，這一輪沒列出的 repo 不會走 RemoveRepo；重新啟動後讀回的孤兒列也一樣。
    /// 只漏列一輪就清會掉「完成時通知我」的提示。輪數只記在記憶體，重新啟動後從 0 算。
    /// </summary>
    private void ForgetRerunsOfUnknownRepos()
    {
        foreach (var rerun in _reruns.Values.ToList())
        {
            var missing = _repos.ContainsKey(rerun.RepoId) ? 0 : rerun.MissingRounds + 1;
            if (missing >= Tuning.RemovalConfirmRounds)
            {
                EndRerun(rerun.RunId);
            }
            else if (missing != rerun.MissingRounds)
            {
                _reruns[rerun.RunId] = rerun with { MissingRounds = missing };
            }
        }
    }

    /// <summary>待確認的重跑結束、放棄或清除：移出記憶體並刪 pending_reruns 的資料列。刪除失敗只寫 error.log（下一次啟動讀回後會再判斷一次）。</summary>
    private void EndRerun(long runId)
    {
        _reruns.Remove(runId);
        try
        {
            _cache.DeletePendingRerun(runId);
        }
        catch (Exception ex)
        {
            ReportUnexpected("刪除待確認的重跑", ex);
        }
    }

    /// <summary>這個 repo 有待確認操作的 run（都是 E6 的對象，<see cref="EnsureRunDetails"/> 和 P1 的對象取聯集）。</summary>
    private HashSet<long> PostActionRunIds(long repoId)
    {
        var ids = _reruns.Values.Where(rerun => rerun.RepoId == repoId).Select(rerun => rerun.RunId).ToHashSet();
        ids.UnionWith(_cancels.Where(pair => pair.Value.RepoId == repoId).Select(pair => pair.Key));
        ids.UnionWith(_alignOnce.Where(pair => pair.Value == repoId).Select(pair => pair.Key));
        return ids;
    }

    private bool HasPostAction(long runId) => _reruns.ContainsKey(runId) || _cancels.ContainsKey(runId) || _alignOnce.ContainsKey(runId);

    /// <summary>操作後追蹤的 E6：切到自動更新的「暫停」時不移出排程（§6.2）。</summary>
    private bool IsPostActionTarget(PollTarget target) =>
        target.Kind == TargetKind.RunDetail && target.ItemId is { } runId && HasPostAction(runId);

    /// <summary>重跑確認的 E6：不套用鎖定下限（§6.2）。</summary>
    private bool IsRerunConfirmation(PollTarget target) =>
        target.Kind == TargetKind.RunDetail && target.ItemId is { } runId && _reruns.ContainsKey(runId);

    /// <summary>
    /// 操作後追蹤的 E6 間隔（§6.2 的表），都不看自動更新模式；有多種時取最短的，沒有回 null。
    /// 重跑：ActiveRepoInterval × k，不套鎖定下限。取消：受理後 CancelGraceBeforeForce 內 ActiveRepoInterval × k，之後 QuietMax × k，
    /// 套鎖定下限。只查一次的：同一次性目標的重試（QuietMax × k，套鎖定下限），只在 E6 失敗、要退避重送時用得到。
    /// </summary>
    private TimeSpan? PostActionInterval(long runId)
    {
        if (!HasPostAction(runId))
        {
            return null;
        }

        var k = Stretch();
        TimeSpan? interval = null;
        if (_reruns.ContainsKey(runId))
        {
            interval = Shorter(interval, IntervalPolicy.Periodic(Tuning.ActiveRepoInterval, RefreshMode.Auto, k, locked: false, followsRefreshMode: false));
        }

        if (_cancels.TryGetValue(runId, out var cancelling))
        {
            var baseInterval = CancelGraceElapsed(cancelling) ? Tuning.QuietMax : Tuning.ActiveRepoInterval;
            interval = Shorter(interval, IntervalPolicy.Periodic(baseInterval, RefreshMode.Auto, k, _locked, followsRefreshMode: false));
        }

        if (_alignOnce.ContainsKey(runId))
        {
            interval = Shorter(interval, IntervalPolicy.Periodic(Tuning.QuietMax, RefreshMode.Auto, k, _locked, followsRefreshMode: false));
        }

        return interval;
    }

    /// <summary>取消（E13）受理已滿 <see cref="Tuning.CancelGraceBeforeForce"/>：用受理時的單調時間戳算（§6.4）。</summary>
    private bool CancelGraceElapsed(CancelTracking cancelling) =>
        _time.GetElapsedTime(cancelling.AcceptedTimestamp) >= Tuning.CancelGraceBeforeForce;

    /// <summary>快照的 <see cref="MonitorSnapshot.Cancelling"/>：取消中的 run 與受理時間（§6.3）。</summary>
    private FrozenDictionary<long, CancellingInfo> CancellingRuns() =>
        _cancels.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.Info);

    /// <summary>兩個間隔取較短的；null 是沒有這一種。</summary>
    private static TimeSpan? Shorter(TimeSpan? first, TimeSpan? second) =>
        first is { } a && second is { } b ? (a < b ? a : b) : first ?? second;

    /// <summary>待確認的重跑（§6.2 第 1 點），步驟專用。</summary>
    /// <param name="PreviousAttempt">送出前（確認對話框上）的 run_attempt；看到更大的值才算重跑開始。</param>
    /// <param name="RequestedAtUtc">送出的 UTC 時間（pending_reruns 的 requested_at），重建資料庫時寫回。</param>
    /// <param name="SentTimestamp">送出時的單調時間戳；重新啟動後讀回的是 null（不能跨程序比較，視為已超過 RerunConfirmTimeout）。</param>
    /// <param name="Responses">送出之後收到幾次 E6 回應（200 或 304）。</param>
    /// <param name="Notify">結束時自動「完成時通知我」（§8.1）。</param>
    /// <param name="MissingRounds">連續幾輪完整清單之後 repo 都不在 _repos。</param>
    private sealed record RerunTracking(long RunId, long RepoId, int PreviousAttempt, DateTimeOffset RequestedAtUtc, long? SentTimestamp,
        int Responses, bool Notify, int MissingRounds = 0);

    /// <summary>取消中的 run（§6.3），步驟專用。</summary>
    /// <param name="AcceptedAtUtc">取消受理的 UTC 時間，給 App 顯示用。</param>
    /// <param name="AcceptedTimestamp">取消受理時的單調時間戳：強制取消的出現時機與追蹤間隔都用它算（§6.4）。</param>
    /// <param name="ForceSent">已經送出強制取消。</param>
    private sealed record CancelTracking(long RepoId, DateTimeOffset AcceptedAtUtc, long AcceptedTimestamp, bool ForceSent)
    {
        /// <summary>給 App 的取消中資訊（快照與每一列的可操作性）。</summary>
        public CancellingInfo Info => new(AcceptedAtUtc, ForceSent);
    }
}
