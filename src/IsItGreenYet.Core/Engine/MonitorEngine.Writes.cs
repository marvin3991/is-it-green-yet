using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Polling;

namespace IsItGreenYet.Core.Engine;

/// <summary>
/// 寫入專用的送出路徑（第二階段 spec §5.3、§5.4）：送出前再檢查、等使用者操作那一條與節流器（截止時間、寫入間隔）、
/// 只送一次（唯一的例外是 401 換到新 token 重送一次）、結果分類（§3.3、§5.2）、待確認的重跑，以及交給操作後追蹤（§6）。
/// </summary>
/// <remarks>
/// <see cref="SendWriteAsync"/> 在呼叫端的執行緒上跑，和輪詢的步驟同時進行：只讀 volatile 或有鎖的狀態
/// （_settings、_auth、_versionError、_repoLookup、_actionLookup、_runStateLookup、節流器、離線偵測、送出中與被拒的停用），
/// 要改步驟狀態（待確認的操作、排 E6、發布時釋放）一律排進 _userEffects；只有 pending_reruns 直接寫進 DB（§5.3 第 2 點）。
/// </remarks>
public sealed partial class MonitorEngine
{
    /// <summary>§3.3 第 9 列：寫入回 400、410 是「被拒：其他」，但和第 11 列不同，不寫 error.log。</summary>
    private const int BadRequestStatus = 400;
    private const int GoneStatus = 410;

    /// <summary>error.log 裡寫入回應錯誤（§3.3 第 11 列）那一筆的標題。</summary>
    private const string WriteErrorContext = "寫入回應錯誤";

    /// <summary>
    /// 步驟專用：已經套用了結果、等下一次 BuildSnapshot 發布就從送出中移除的寫入，run id → 寫入代號（第二階段 spec §5.3 第 2 點）。
    /// 一筆 run 同時只會有一個送出中的寫入（移除之前不能再送），所以以 run id 為鍵就夠。
    /// </summary>
    private readonly Dictionary<long, long> _releaseOnPublish = [];

    /// <summary>
    /// 送出一個寫入（第二階段 spec §5.1～§5.4）。<paramref name="expectedAttempt"/> 是確認對話框上顯示的那一次嘗試；
    /// repo 與這筆 run 的最新狀態由 Engine 自己查，不信任 App 手上可能已過時的資料。
    /// </summary>
    /// <remarks>
    /// 流程：檢查 → 記下送出中 → 等使用者操作那一條與節流器（最多 <see cref="Tuning.MutativeMaxQueueWait"/>，
    /// 寫入之間至少隔 <see cref="Tuning.MutativeRequestSpacing"/>；放行前在節流器的鎖內再檢查一次）→ 送出一次 → 分類。
    /// 截止時間只管排隊，不傳給送出（逾時交給 GitHubClient 的 HttpTimeout）。不重送；唯一的例外是 401 而且重新取得的 token 不同，
    /// 用新的截止時間重新排隊再送一次（P1 §13.2）。程式結束時被取消、送出前的未預期例外都回「未送出」，不往外丟；
    /// 請求可能已經送出之後才發生的回「結果不確定」。
    /// </remarks>
    /// <exception cref="ObjectDisposedException">引擎已經釋放。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> 不是已定義的動作。</exception>
    public async Task<WriteResult> SendWriteAsync(WriteAction action, long repoId, long runId, int expectedAttempt, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "不認得的寫入動作。");
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var request = new WriteRequest(action, repoId, runId, expectedAttempt);

        // 第 1 步：即時狀態與目前的查表（還沒有自己的送出中）。
        if (CheckWrite(request, NoWriteCall) is { Reason: { } reason } blocked)
        {
            return WithSequence(NotSentResult(action, reason, blocked.Block));
        }

        // 第 2 步：記下送出中；同一筆 run 已經有寫入在送就不送。
        var callId = TryMarkInFlight(runId);
        if (callId == NoWriteCall)
        {
            return WithSequence(NotSentResult(action, NotSentReason.InFlight));
        }

        var progress = new WriteProgress();
        var handedOff = false;
        try
        {
            var result = await SendAndClassifyAsync(request, callId, progress, operation.Token).ConfigureAwait(false);
            if (result.Outcome != WriteOutcome.NotSent)
            {
                // 第 7 步：還佔著使用者操作那一條時寫，程式結束時 DisposeAsync 會等到寫完，結束前一刻送出的重跑也不漏記。
                if (IsRerun(action) && result.Outcome is WriteOutcome.Accepted or WriteOutcome.Uncertain)
                {
                    RememberPendingRerun(request, progress.SentAtUtc);
                }

                HandOff(request, callId, result, progress);
                handedOff = true;
            }

            return WithSequence(result);
        }
        finally
        {
            if (progress.GateHeld)
            {
                _userGate.Release();
            }

            // 交給下一步的等那一步發布快照之後才移除（ReleasePublishedWrites）；其他路徑在這裡移除。
            if (!handedOff)
            {
                ReleaseInFlight(runId, callId);
            }
        }
    }

    /// <summary>第 3～6 步：排隊、送出（必要時 401 重送一次）、分類。任何例外都轉成結果，不往外丟。</summary>
    private async Task<WriteResult> SendAndClassifyAsync(WriteRequest request, long callId, WriteProgress progress, CancellationToken token)
    {
        var action = request.Action;
        try
        {
            var slot = await WaitForWriteSlotAsync(request, callId, progress, acquireGate: true, token).ConfigureAwait(false);
            if (slot.Reason is { } reason)
            {
                return NotSentResult(action, reason, slot.Block);
            }

            var sentToken = _auth.Token;
            var response = await SendOnceAsync(request, progress, sentToken, token).ConfigureAwait(false);
            if (response is { Outcome: ApiOutcome.Unauthorized } && await OnUnauthorizedAsync(sentToken, token).ConfigureAwait(false))
            {
                // 401 代表伺服器沒有執行；換到新 token：用新的截止時間重新排隊，再送一次（P1 §13.2 第 1 點）。
                // 這一次沒送出時，兩個請求都沒有被執行，結果就是這一次「未送出」的原因。
                var retrySlot = await WaitForWriteSlotAsync(request, callId, progress, acquireGate: false, token).ConfigureAwait(false);
                if (retrySlot.Reason is { } retryReason)
                {
                    return NotSentResult(action, retryReason, retrySlot.Block);
                }

                sentToken = _auth.Token;
                response = await SendOnceAsync(request, progress, sentToken, token).ConfigureAwait(false);
                if (response is { Outcome: ApiOutcome.Unauthorized })
                {
                    // 重送仍 401：直接進入未登入，不再重取 token（P1 §13.2 第 2 點）。
                    await RefuseTokenAsync(sentToken, token).ConfigureAwait(false);
                }
            }

            return response is null ? UncertainResult(action) : Classify(request, response, sentToken, progress.Repo!);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return progress.MayHaveExecuted ? UncertainResult(action) : NotSentResult(action, NotSentReason.ShuttingDown);
        }
        catch (Exception) when (Volatile.Read(ref _disposed) != 0)
        {
            // 釋放引擎期間才發生的例外：當成取消，不寫 error.log、不提示（同複製 log）。
            return progress.MayHaveExecuted ? UncertainResult(action) : NotSentResult(action, NotSentReason.ShuttingDown);
        }
        catch (Exception ex)
        {
            ReportUnexpected("送出寫入", ex);
            Wake();
            return progress.MayHaveExecuted ? UncertainResult(action) : NotSentResult(action, NotSentReason.Unexpected);
        }
    }

    /// <summary>
    /// 第 3 步：等使用者操作那一條（<paramref name="acquireGate"/>）與節流器，兩者加起來最多 <see cref="Tuning.MutativeMaxQueueWait"/>。
    /// </summary>
    /// <remarks>
    /// 截止時間用 TimeProvider 的計時器，只連結到這裡的等待：放行時就釋放，不會取消已經送出的請求。
    /// 截止時回「排隊太久」；程式結束或呼叫端取消回「程式正在結束」。
    /// </remarks>
    private async Task<WriteCheck> WaitForWriteSlotAsync(
        WriteRequest request, long callId, WriteProgress progress, bool acquireGate, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(Tuning.MutativeMaxQueueWait, _time);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            if (acquireGate)
            {
                await _userGate.WaitAsync(waiting.Token).ConfigureAwait(false);
                progress.GateHeld = true;
            }

            return await WaitForThrottleAsync(request, callId, progress, waiting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return WriteCheck.Stop(NotSentReason.ShuttingDown);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return WriteCheck.Stop(NotSentReason.QueueTimeout);
        }
    }

    /// <summary>
    /// 等節流器放行：每一次都先在節流器的鎖內再檢查一次（§5.3 第 3 點），每分鐘上限與寫入間隔都放行時才記下送出
    /// （RecordSent 與 RecordMutativeSent 在同一個鎖裡，spec §6.3）。全域暫停不等，直接回「限流暫停中」。
    /// </summary>
    /// <remarks>等待期間照 <see cref="WaitForUserSlotAsync"/> 遞增 _userWaiting：輪詢讓寫入先送（spec §6.3 第 1 級），也不空轉。</remarks>
    private async Task<WriteCheck> WaitForThrottleAsync(WriteRequest request, long callId, WriteProgress progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _userWaiting);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                TimeSpan wait;
                lock (_throttleGate)
                {
                    var check = CheckWrite(request, callId);
                    if (check.Reason is not null)
                    {
                        return check;
                    }

                    var window = _throttle.TimeUntilAllowed();
                    var spacing = _throttle.TimeUntilMutativeAllowed();
                    if (window is null && spacing is null)
                    {
                        _throttle.RecordSent();
                        _throttle.RecordMutativeSent();
                        progress.Released(check.Repo!, _time.GetUtcNow(), _time.GetTimestamp());
                        return check;
                    }

                    wait = window ?? TimeSpan.Zero;
                    if (spacing is { } mutative && mutative > wait)
                    {
                        wait = mutative;
                    }
                }

                if (wait <= TimeSpan.Zero)
                {
                    await Task.Yield();
                    continue;
                }

                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _userWaiting);
            Wake();
        }
    }

    /// <summary>
    /// 送出前的檢查（第 1 步與每一次放行前在節流器的鎖內），第一個成立的就是結果（§5.2「未送出」的原因、§5.4）。
    /// 全域狀態與送出中一律用即時值。run 狀態類的原因讀 <see cref="_runStateLookup"/>：公開查表（Actions）在送出中
    /// （包含自己）或全域狀態期間組出時，四個動作都被共通代碼蓋掉，看不到已完成、待確認的重跑這類原因（§5.3 第 3.1 點、第 4 點）。
    /// 兩份查表都要有這筆 run、屬於同一個 repo、嘗試編號相同才算數：先讀 <see cref="_actionLookup"/> 再讀 run 狀態查表
    /// （發布時的順序相反），讀到不同次發布的兩份時，以較新的那份為準。
    /// </summary>
    /// <param name="callId">自己這次寫入的代號（第 1 步還沒有，是 <see cref="NoWriteCall"/>）：送出中排除自己這一筆。</param>
    private WriteCheck CheckWrite(WriteRequest request, long callId)
    {
        if (!_settings.AllowWriteActions)
        {
            return WriteCheck.Stop(NotSentReason.WritesDisabled);
        }

        // 同 UserStopReason 的兩種情況：未登入不送任何 API（P1 §13.2）、版本錯誤（P1 §4.1）。
        if (_auth.Token is null || _auth.Status == AuthStatus.Unauthorized)
        {
            return WriteCheck.Stop(NotSentReason.NotLoggedIn);
        }

        if (_versionError)
        {
            return WriteCheck.Stop(NotSentReason.VersionError);
        }

        if (_throttle.PauseRemaining is not null)
        {
            return WriteCheck.Stop(NotSentReason.RateLimitPaused);
        }

        if (_offline.IsOffline)
        {
            return WriteCheck.Stop(NotSentReason.Offline);
        }

        if (GlobalBlock(request.RepoId) == ActionBlock.MissingPermission)
        {
            return WriteCheck.Stop(NotSentReason.MissingPermission);
        }

        var actions = _actionLookup;
        var runStates = _runStateLookup;
        if (!_repoLookup.TryGetValue(request.RepoId, out var repo)
            || !actions.TryGetValue(request.RunId, out var published)
            || !runStates.TryGetValue(request.RunId, out var runState)
            || published.RepoId != request.RepoId
            || runState.RepoId != request.RepoId)
        {
            return WriteCheck.Stop(NotSentReason.NotFound);
        }

        if (published.RunAttempt != request.ExpectedAttempt || runState.RunAttempt != request.ExpectedAttempt)
        {
            return WriteCheck.Stop(NotSentReason.AttemptChanged);
        }

        if (IsInFlight(request.RunId, exceptCallId: callId))
        {
            return WriteCheck.Stop(NotSentReason.InFlight);
        }

        // run 狀態查表是用不算送出中、沒有共通條件的 context 組出的：不是 None 的就是 run 狀態類的原因。
        var block = BlockOf(runState, request.Action);
        return block != ActionBlock.None ? WriteCheck.Stop(NotSentReason.Unavailable, block) : new WriteCheck(Reason: null, Repo: repo);
    }

    /// <summary>
    /// 第 4、5 步：用 <paramref name="sentToken"/> 送出一次，只傳 _lifetime 與呼叫端的 token（逾時交給 GitHubClient 的 HttpTimeout）。
    /// 有回應時照使用者操作的規則處理額度狀態（限流全域暫停、額度樣本、Deprecation、scope）並記下 401。
    /// </summary>
    /// <returns>
    /// 回應；寫入那一層丟出取消以外的例外時回 null（已寫 error.log）。例外本身分不出是不是交給 handler 之後才丟出，
    /// 保守當成請求可能已經送出：結果不確定，由操作後追蹤確認。
    /// </returns>
    /// <exception cref="OperationCanceledException">送出前被取消：GitHubClient 只在交給 handler 之前丟出，之後的取消回 NetworkError。</exception>
    private async Task<ApiResult<object>?> SendOnceAsync(WriteRequest request, WriteProgress progress, string? sentToken, CancellationToken token)
    {
        ApiResult<object> response;
        try
        {
            response = await SendWithTokenAsync(sentToken, c => _writer.WriteAsync(request.Action, progress.Repo!, request.RunId, c), token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress.MayHaveExecuted = true;
            if (Volatile.Read(ref _disposed) == 0)
            {
                ReportUnexpected("送出寫入", ex);
                Wake();
            }

            return null;
        }

        // 401 代表伺服器沒有執行（P1 §13.2）；其他回應（含交給 handler 之後的逾時與連線中斷）都可能已經生效。
        if (response.Outcome != ApiOutcome.Unauthorized)
        {
            progress.MayHaveExecuted = true;
        }

        ObserveUserResult(response, sentToken);
        NoteIfUnauthorized(response, sentToken);
        return response;
    }

    /// <summary>
    /// 第 6 步：§3.3 的分類。權限不足的 403 停用這個 repo 的寫入（§4.3）；5xx 與表上沒列的狀態碼（第 11 列）寫 error.log，
    /// message 在 ErrorLog 落地前整筆經過 SecretRedactor。5xx 可能已經生效（§3.2），結果不確定，不是被拒。
    /// </summary>
    private WriteResult Classify(WriteRequest request, ApiResult<object> response, string? sentToken, RepoInfo repo)
    {
        var action = request.Action;
        switch (response.Outcome)
        {
            case ApiOutcome.Ok:
                return new WriteResult(action, WriteOutcome.Accepted, response.StatusCode);

            case ApiOutcome.NetworkError:
                // GitHubClient 的寫入 NetworkError 一定已經交給 handler（逾時、連線中斷、送出後被取消）：可能已經生效（§5.2）。
                return UncertainResult(action);

            case ApiOutcome.ServerError:
                // GitHub 處理太久會自己回 5xx，後端可能已經執行（§3.2）：和 MayHaveExecuted 一致，回結果不確定，
                // 重跑因此記成待確認（§6.2）。帶狀態碼與 message，App 才不會說成「沒有收到回應」。
                LogWriteError(action, repo, response);
                return new WriteResult(action, WriteOutcome.Uncertain, response.StatusCode, ServerMessage: response.Message);
        }

        var reason = response.WriteRejected ?? WriteRejectReason.Other;

        // 送出期間換了 token 就不停用（§4.3 第 2 點）。
        if (reason == WriteRejectReason.InsufficientPermission && string.Equals(_auth.Token, sentToken, StringComparison.Ordinal))
        {
            MarkWriteDenied(request.RepoId, sentToken);
        }

        if (reason == WriteRejectReason.Other && response.StatusCode is not (BadRequestStatus or GoneStatus))
        {
            LogWriteError(action, repo, response);
        }

        return new WriteResult(action, WriteOutcome.Rejected, response.StatusCode, reason,
            ServerMessage: response.Message, AcceptedPermissions: response.AcceptedPermissions);
    }

    /// <summary>§3.3 第 11 列的 error.log：動作、repo 全名、狀態碼、message。</summary>
    private void LogWriteError(WriteAction action, RepoInfo repo, ApiResult<object> response) =>
        _deps.ErrorLog.WriteMessage(WriteErrorContext, EngineText.WriteErrorLog(action, repo.FullName, response.StatusCode, response.Message));

    /// <summary>第 7 步：重跑的已受理與結果不確定直接寫進 pending_reruns（§5.3 第 2 點，不經 _userEffects）。寫入失敗只寫 error.log。</summary>
    private void RememberPendingRerun(WriteRequest request, DateTimeOffset sentAtUtc)
    {
        try
        {
            _cache.UpsertPendingRerun(new PendingRerun(request.RunId, request.RepoId, request.ExpectedAttempt, sentAtUtc, Notify: true));
        }
        catch (Exception ex)
        {
            ReportUnexpected("寫入待確認的重跑", ex);
        }
    }

    /// <summary>第 8 步：有回應的寫入交給下一步（§6.2 第 1、2 點）。排入之後送出中由那一步發布快照時移除。</summary>
    private void HandOff(WriteRequest request, long callId, WriteResult result, WriteProgress progress)
    {
        var pending = new PendingAction(request.RunId, request.RepoId, request.Action, result.Outcome, request.ExpectedAttempt,
            progress.SentAtUtc, progress.SentTimestamp);
        var moved = result.Rejected == WriteRejectReason.Moved;
        _userEffects.Enqueue(() => OnWriteAnswered(pending, callId, moved));
        Wake();
    }

    /// <summary>步驟裡套用寫入的結果：排進「發布時釋放」、記下待確認的操作（操作後追蹤）、用使用者操作的優先序排一次 E6。</summary>
    private void OnWriteAnswered(PendingAction pending, long callId, bool moved)
    {
        // 最先排進「發布時釋放」：後面任何一步失敗，送出中也會在下一次 BuildSnapshot 發布時移除，不會一直留著。
        _releaseOnPublish[pending.RunId] = callId;
        RecordPostAction(pending);
        _scheduler.ScheduleNow(new PollTarget(TargetKind.RunDetail, pending.RepoId, pending.RunId), RequestPriority.User);
        if (moved)
        {
            // 寫入不跟隨轉址（§3.2）：排一次 repo 完整重抓，清單更新成新名稱後你再按一次就會送到正確的位置。
            _scheduler.ScheduleNow(RepoListTarget, RequestPriority.User);
        }
    }

    /// <summary>
    /// 第 9 步：BuildSnapshot 組出的快照已經換上查表，「發布時釋放」的寫入從送出中移除（只移除代號相同的那一筆）。
    /// 由 <see cref="PublishSnapshot"/> 呼叫；沿用上一份內容的快照不呼叫。
    /// </summary>
    private void ReleasePublishedWrites()
    {
        foreach (var (runId, callId) in _releaseOnPublish)
        {
            ReleaseInFlight(runId, callId);
        }

        _releaseOnPublish.Clear();
    }

    /// <summary>第 10 步：帶上回傳當下最後一份已發布快照的序號。</summary>
    private WriteResult WithSequence(WriteResult result) => result with { SnapshotSequence = Volatile.Read(ref _snapshotSequence) };

    private static WriteResult NotSentResult(WriteAction action, NotSentReason reason, ActionBlock? block = null) =>
        new(action, WriteOutcome.NotSent, NotSent: reason, Block: block);

    private static WriteResult UncertainResult(WriteAction action) => new(action, WriteOutcome.Uncertain);

    private static bool IsRerun(WriteAction action) => action is WriteAction.RerunFailedJobs or WriteAction.RerunAll;

    private static ActionBlock BlockOf(RunActions actions, WriteAction action) => action switch
    {
        WriteAction.RerunFailedJobs => actions.RerunFailedJobs,
        WriteAction.RerunAll => actions.RerunAll,
        WriteAction.Cancel => actions.Cancel,
        WriteAction.ForceCancel => actions.ForceCancel,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "不認得的寫入動作。"),
    };

    /// <summary>
    /// 一筆有回應的寫入交給下一步的內容（第二階段 spec §6.2 第 1 點）；步驟依動作與結果記進操作後追蹤（<see cref="RecordPostAction"/>）。
    /// </summary>
    /// <param name="PreviousAttempt">送出前（確認對話框上）的 run_attempt。</param>
    /// <param name="SentTimestamp">送出時的單調時間戳。</param>
    private sealed record PendingAction(long RunId, long RepoId, WriteAction Action, WriteOutcome Outcome,
        int PreviousAttempt, DateTimeOffset SentAtUtc, long SentTimestamp);

    /// <param name="ExpectedAttempt">確認對話框上顯示的那一次嘗試。</param>
    private sealed record WriteRequest(WriteAction Action, long RepoId, long RunId, int ExpectedAttempt);

    /// <summary>送出前檢查的結果：<see cref="Reason"/> 是 null 代表可以送，<see cref="Repo"/> 是這時查到的 repo。</summary>
    private readonly record struct WriteCheck(NotSentReason? Reason, ActionBlock? Block = null, RepoInfo? Repo = null)
    {
        public static WriteCheck Stop(NotSentReason reason, ActionBlock? block = null) => new(reason, block);
    }

    /// <summary>一次寫入的進度：只在這次 <see cref="SendWriteAsync"/> 的 async 流程裡依序讀寫，不和其他工作共用。</summary>
    private sealed class WriteProgress
    {
        /// <summary>已經拿到使用者操作那一條，結束時要歸還。</summary>
        public bool GateHeld { get; set; }

        /// <summary>
        /// 請求可能已經被伺服器執行：收到 401 以外的回應，或寫入那一層丟出取消以外的例外（分不出是不是交給 handler 之後才丟出，
        /// 保守當成可能已經送出）。
        /// </summary>
        public bool MayHaveExecuted { get; set; }

        /// <summary>最後一次放行時查到的 repo：送出用的是當時最新的名稱。</summary>
        public RepoInfo? Repo { get; private set; }

        /// <summary>最後一次放行（送出）的 UTC 時間。</summary>
        public DateTimeOffset SentAtUtc { get; private set; }

        /// <summary>最後一次放行（送出）的單調時間戳。</summary>
        public long SentTimestamp { get; private set; }

        public void Released(RepoInfo repo, DateTimeOffset sentAtUtc, long sentTimestamp)
        {
            Repo = repo;
            SentAtUtc = sentAtUtc;
            SentTimestamp = sentTimestamp;
        }
    }
}
