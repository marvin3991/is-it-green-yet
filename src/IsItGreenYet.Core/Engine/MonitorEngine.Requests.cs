using System.Globalization;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Polling;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Engine;

/// <summary>送出請求、401 流程、全域的回應處理（額度、限流、離線、5xx、版本），以及排程的間隔與優先序。</summary>
public sealed partial class MonitorEngine
{
    /// <summary>連線層錯誤沒有狀態碼，GitHubClient 記成 0（不合法的網址也是 0，代表請求根本沒有送出）。</summary>
    private const int NoResponse = 0;

    /// <summary>x-ratelimit-resource 的主額度（spec §6.4 只看 core）。</summary>
    private const string CoreResource = "core";

    /// <summary>api.log 暫停註記裡的時間格式，同 api.log 每行開頭（spec §14.4）。</summary>
    private const string PauseNoteTimeFormat = "yyyy-MM-dd'T'HH:mm:sszzz";

    /// <summary>一次 API 呼叫的結果中，Engine 判斷時需要的部分（不含內容）。</summary>
    private sealed record Outcome(
        ApiOutcome Kind,
        int StatusCode,
        bool InsufficientPermission,
        string? AcceptedPermissions,
        string? AcceptedOAuthScopes,
        string? OAuthScopes,
        string? Message);

    private static Outcome OutcomeOf<T>(ApiResult<T> result) =>
        new(result.Outcome, result.StatusCode, result.InsufficientPermission, result.AcceptedPermissions,
            result.AcceptedOAuthScopes, result.OAuthScopes, result.Message);

    /// <summary><see cref="NoteIfUnauthorized"/> 與 <see cref="TryMarkVerified"/> 的鎖：兩條工作都會用。</summary>
    private readonly Lock _verifyGate = new();

    /// <summary>這次執行收到過幾次 401（<c>_verifyGate</c>），也是每一次 401 的序號。</summary>
    private long _unauthorizedSeen;

    /// <summary>
    /// 每把 token 最近一次收到 401 時的序號（<c>_verifyGate</c>）：E1 回 200 時只看送出用的那一把，
    /// 別的 token（例如已經換掉的舊 token）遲到的 401 不影響這次驗證。只放在記憶體、不印出（spec §13.4）；
    /// 筆數是這次執行收到過 401 的不同 token 數，每一把都來自你的設定或 gh 登入，數量很少。
    /// </summary>
    private readonly Dictionary<string, long> _unauthorizedAt = new(StringComparer.Ordinal);

    /// <summary><see cref="CallAsync{T}"/> 最後一次送出用的 token 與當時的 401 次數；E1 回 200 時用（步驟專用）。</summary>
    private SentWith _lastCallSent = new(token: null, unauthorizedSeen: 0);

    /// <summary>一個請求送出時用的 token，與送出之前已經收到過幾次 401（送出時的 401 序號）。</summary>
    /// <remarks>一般類別（不是 record）：預設的文字表示不會印出 token（spec §13.4）。</remarks>
    private sealed class SentWith(string? token, long unauthorizedSeen)
    {
        public string? Token { get; } = token;

        public long UnauthorizedSeen { get; } = unauthorizedSeen;
    }

    /// <summary>
    /// 依序：版本錯誤或睡眠中不送 → 離線時只送 E10 探測 → 需要驗證就送 E1 → 未登入不送 → 啟動時的 E10 →
    /// 已登入時「重新檢查」的 E1 → 到期的檢查新版本 → 一個到期的輪詢目標。
    /// 送出前在同一個鎖裡問節流器、記下送出（spec §6.3）；有使用者操作在等節流器時讓它先送。
    /// </summary>
    private async Task<int> SendNextAsync(CancellationToken ct)
    {
        if (_versionError || _suspended)
        {
            return 0;
        }

        if (_offline.IsOffline)
        {
            if (!TryReserve(() => _scheduler.TryTakeIfDue(OfflineProbeTarget)))
            {
                return 0;
            }

            return await RunTargetAsync(OfflineProbeTarget, ct).ConfigureAwait(false);
        }

        if (_needsVerify)
        {
            // 等驗證期間 token 不見了（重新檢查或使用者操作收到 401 時重新取得 token 卻拿不到）：不送不帶憑證的 E1。
            if (_auth.Token is null)
            {
                AbandonVerify();
                return 0;
            }

            if (_verifyAt is { } at && _time.GetElapsedTime(at) < TimeSpan.Zero)
            {
                return 0;
            }

            if (!TryReserve(() => true))
            {
                return 0;
            }

            try
            {
                return await VerifyAccountAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 處理 E1 時丟出例外（例如送出前讀 http_cache 失敗）：和 E1 的連線失敗一樣退避後再送（spec §15 每一輪各自 try/catch、§6.4），
                // 不會因為還在等驗證就不等待、每一步都重試並寫一筆 error.log。
                ReportUnexpected("驗證帳號", ex);
                if (_needsVerify)
                {
                    OnVerifyFailed();
                }

                return 1;
            }
        }

        if (_auth.Status != AuthStatus.Ok)
        {
            return 0;
        }

        if (_startupRateLimitPending)
        {
            if (!TryReserve(() => true))
            {
                return 0;
            }

            _startupRateLimitPending = false;
            return await StartupRateLimitAsync(ct).ConfigureAwait(false);
        }

        if (_recheckPending)
        {
            if (!TryReserve(() => true))
            {
                return 0;
            }

            _recheckPending = false;
            return await RecheckAccountAsync(ct).ConfigureAwait(false);
        }

        if (UpdateCheckReady)
        {
            if (!TryReserve(() => true))
            {
                return 0;
            }

            _updateCheckPending = false;
            return await SendUpdateCheckAsync(ct).ConfigureAwait(false);
        }

        PollTarget? target = null;
        if (!TryReserve(() => (target = _scheduler.TakeNextDue()) is not null))
        {
            return 0;
        }

        return await RunTargetAsync(target!, ct).ConfigureAwait(false);
    }

    /// <summary>節流器放行、沒有使用者操作在等、<paramref name="take"/> 也拿到東西時，記下送出並回 true。</summary>
    private bool TryReserve(Func<bool> take)
    {
        lock (_throttleGate)
        {
            if (Volatile.Read(ref _userWaiting) > 0 || _throttle.TimeUntilAllowed() is not null || !take())
            {
                return false;
            }

            _throttle.RecordSent();
            return true;
        }
    }

    /// <summary>401 換到新 token 後的重送：節流器放行時記下送出並回 true（同一個鎖裡，spec §6.3）。</summary>
    /// <remarks>請求已經送到一半，不讓給等待中的使用者操作；兩者照節流器的視窗各自放行。</remarks>
    private bool TryReserveRetry()
    {
        lock (_throttleGate)
        {
            if (_throttle.TimeUntilAllowed() is not null)
            {
                return false;
            }

            _throttle.RecordSent();
            return true;
        }
    }

    /// <summary>處理一個目標；任何例外都寫 error.log，該目標依錯誤退避後重排，迴圈繼續（spec §15）。</summary>
    /// <remarks>
    /// 處理完之後補做這個 repo 上一次沒做完的完成觀察（見 <see cref="PlanPendingCompletions"/>）。
    /// 不論結果（成功、304 或錯誤），都從進行中的命令列刷新的這一批移除（第二階段 spec §10.5 第 4 點）；
    /// 完整重抓例外：同一個目標接著排下一頁，這一輪結束才算處理完。
    /// </remarks>
    private async Task<int> RunTargetAsync(PollTarget target, CancellationToken ct)
    {
        _current = target;
        _lastSent[target] = _time.GetTimestamp();
        _unanswered.Remove(target);
        try
        {
            var sent = await ProcessTargetAsync(target, ct).ConfigureAwait(false);
            if (target.RepoId is { } repoId)
            {
                PlanPendingCompletions(repoId);
            }

            return sent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportUnexpected("處理輪詢目標 " + target.Kind, ex);
            RescheduleAfterUnexpected(target);
            return 1;
        }
        finally
        {
            _current = null;
            if (target != RepoListTarget || _round is null)
            {
                _cliRefresh?.Remaining.Remove(target);
            }
        }
    }

    /// <summary>
    /// 送出一次請求；收到 401 時依 spec §13.2 重新取得 token，值改變才用新值重送一次，重送仍 401 就進入未登入。
    /// </summary>
    /// <remarks>
    /// 重送也要經過節流器（spec §6.3 任何 60 秒視窗內的每個請求都算、§6.4 全域暫停期間所有請求都不送）。
    /// 不放行時不在步驟裡等（指令、快照不能卡住）：這次只回 401，呼叫端把目標排成現在到期，之後放行時才用新 token 送出；
    /// 那一次就是「重送」，仍 401 時直接進入未登入，不再重取（<c>_deferredRetryToken</c>）。
    /// </remarks>
    /// <returns>結果與實際送出的請求數（1 或 2）。</returns>
    private async Task<(ApiResult<T> Result, int Requests)> CallAsync<T>(
        Func<CancellationToken, Task<ApiResult<T>>> call, long? repoId, CancellationToken ct)
    {
        var token = _auth.Token;
        _lastCallSent = new SentWith(token, UnauthorizedSeen());
        var result = await SendWithTokenAsync(token, call, ct).ConfigureAwait(false);
        RecordFollowedRedirect(result);
        Observe(result, repoId, token);
        NoteIfUnauthorized(result, token);

        // 延後的重送有了 HTTP 回應（連線失敗不算，下一次送出仍是那一次重送）。
        var deferredRetry = token is not null && token == _deferredRetryToken;
        if (deferredRetry && result.StatusCode != NoResponse)
        {
            _deferredRetryToken = null;
        }

        if (result.Outcome != ApiOutcome.Unauthorized)
        {
            return (result, 1);
        }

        if (deferredRetry)
        {
            await RefuseTokenAsync(token, ct).ConfigureAwait(false);
            return (result, 1);
        }

        if (!await OnUnauthorizedAsync(token, ct).ConfigureAwait(false))
        {
            return (result, 1);
        }

        var retryToken = _auth.Token;
        if (!TryReserveRetry())
        {
            _deferredRetryToken = retryToken;
            return (result, 1);
        }

        _lastCallSent = new SentWith(retryToken, UnauthorizedSeen());
        var retry = await SendWithTokenAsync(retryToken, call, ct).ConfigureAwait(false);
        RecordFollowedRedirect(retry);
        Observe(retry, repoId, retryToken);
        NoteIfUnauthorized(retry, retryToken);
        if (retry.Outcome == ApiOutcome.Unauthorized)
        {
            await RefuseTokenAsync(retryToken, ct).ConfigureAwait(false);
        }

        return (retry, 2);
    }

    /// <summary>收到 401 就記一筆（兩條工作都會呼叫，spec §3.1）：之前送出的 E1 回來時用它判斷那次成功還算不算數。</summary>
    /// <param name="token">收到 401 的那個請求送出時用的 token。</param>
    private void NoteIfUnauthorized<T>(ApiResult<T> result, string? token)
    {
        if (result.Outcome != ApiOutcome.Unauthorized)
        {
            return;
        }

        lock (_verifyGate)
        {
            _unauthorizedSeen++;
            if (token is not null)
            {
                _unauthorizedAt[token] = _unauthorizedSeen;
            }
        }
    }

    /// <summary>到目前為止收到過幾次 401；送出 E1 之前記下。</summary>
    private long UnauthorizedSeen()
    {
        lock (_verifyGate)
        {
            return _unauthorizedSeen;
        }
    }

    /// <summary>
    /// 用 <paramref name="sent"/> 的 token 送出的 E1 回 200：送出之後兩條工作都沒有再用這一把 token 收到 401、目前的 token 也仍是那一把，才標成已驗證。
    /// E1 還在路上時 token 被撤銷，另一條工作先收到 401、重取又拿回同一把而進入未登入時，這次成功不能把狀態改回 Ok，
    /// 否則會接著用被拒的 token 送出（spec §13.2）。比對與標記在同一個鎖裡，和記下 401 不會交錯。
    /// 送出之後才回來的其他 token 的 401（例如換 token 之前送出的使用者操作）不否定這一把：從未登入換到新 token 時，
    /// 那種 401 不能讓這次成功不算數，否則拿著有效的 token 卻停在未登入（spec §13.2 第 3 點「成功就恢復輪詢」）。
    /// </summary>
    private bool TryMarkVerified(SentWith sent)
    {
        lock (_verifyGate)
        {
            var rejectedSinceSent = sent.Token is { } token
                && _unauthorizedAt.TryGetValue(token, out var at)
                && at > sent.UnauthorizedSeen;
            return !rejectedSinceSent && _auth.MarkVerified(sent.Token);
        }
    }

    /// <summary>
    /// GitHubClient 照 301 的 Location 重送時，一次呼叫實際送出兩個 api.github.com 請求：補記一筆進每分鐘的視窗（spec §6.3 每個請求都算）。
    /// </summary>
    /// <remarks>
    /// 轉址是在 GitHubClient 裡跟隨的，送出前沒辦法先問節流器，所以視窗剛好滿的時候可能多出這一個；之後的請求照補記後的數量等待。
    /// 連續多次轉址（最多 MaxRedirects）也只補記一筆：ApiResult 只帶最後的位置。
    /// </remarks>
    private void RecordFollowedRedirect<T>(ApiResult<T> result)
    {
        if (result.MovedTo is { } movedTo && GitHubClient.IsApiHost(movedTo))
        {
            lock (_throttleGate)
            {
                _throttle.RecordSent();
            }
        }
    }

    /// <summary>這一次請求用 <paramref name="token"/> 送出（GitHubClient 的 tokenAccessor 讀 AsyncLocal）。</summary>
    private async Task<TResult> SendWithTokenAsync<TResult>(string? token, Func<CancellationToken, Task<TResult>> call, CancellationToken ct)
    {
        _requestToken.Value = token;
        return await call(ct).ConfigureAwait(false);
    }

    /// <returns>要用目前的 token 重送一次時回 true。</returns>
    private async Task<bool> OnUnauthorizedAsync(string? rejectedToken, CancellationToken ct)
    {
        try
        {
            return await _auth.OnUnauthorizedAsync(rejectedToken, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // AuthManager 已經進入未登入；只記下例外。
            ReportUnexpected("重新取得 token", ex);
            return false;
        }
    }

    /// <summary>
    /// 用新 token 重送仍收到 401（spec §13.2 第 2 點）：不再重取 token，直接進入未登入；
    /// 恢復交給定期重新檢查（值改變才用 E1 驗證一次）與「重新檢查」按鈕。
    /// </summary>
    private async Task RefuseTokenAsync(string? rejectedToken, CancellationToken ct)
    {
        try
        {
            await _auth.RefuseAsync(rejectedToken, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportUnexpected("進入未登入", ex);
        }
    }

    /// <summary>
    /// 每個輪詢回應都要做的全域處理：離線判定、5xx 角標、額度、最後更新時間、Deprecation、SSO、限流、版本錯誤、
    /// §6.4 第 5 列（60 秒內 3 個 repo 無法分類的 403），以及 token 的 scope（第二階段 spec §4.1）。
    /// </summary>
    /// <param name="token">這個請求送出時用的 token。</param>
    private void Observe<T>(ApiResult<T> result, long? repoId, string? token)
    {
        RecordScopes(result, token);
        if (result.Outcome == ApiOutcome.NetworkError)
        {
            if (_offline.OnNetworkError())
            {
                EnterOffline();
            }

            return;
        }

        if (result.Outcome == ApiOutcome.OtherError && result.StatusCode == NoResponse)
        {
            // 網址不合法，請求根本沒有送出。
            return;
        }

        var wasOffline = _offline.IsOffline;
        _offline.OnSuccess();
        if (wasOffline)
        {
            OnBackOnline();
        }

        // 只有 2xx 或 304 打斷「連續 5xx」（spec §6.4）；4xx、限流、版本錯誤既不累加也不歸零。
        if (result.Outcome == ApiOutcome.ServerError)
        {
            _serverErrors.OnServerError();
        }
        else if (result.IsSuccess)
        {
            _serverErrors.OnOtherResponse();
        }

        RecordRateLimit(result.RateLimit);
        if (result.IsSuccess)
        {
            _lastSuccessUtc = _time.GetUtcNow();
        }

        if (result.Deprecated)
        {
            NoticeDeprecated();
        }

        if (!string.IsNullOrWhiteSpace(result.Sso))
        {
            NoticeSso(result.Sso);
        }

        switch (result.Outcome)
        {
            case ApiOutcome.RateLimited:
                ApplyRateLimit(result.LimitKind, result.RetryWait, result.HasRetryAfter);
                break;

            case ApiOutcome.VersionError:
                EnterVersionError(result.StatusCode);
                break;

            case ApiOutcome.Forbidden when !result.InsufficientPermission && repoId is { } id && _forbiddenBurst.Record(id):
                ApplyForbiddenBurst();
                break;
        }
    }

    /// <summary>
    /// 全域暫停（spec §6.4 第 1、2 列）：主額度用 reset − Date；有 retry-after 依它；都沒有時依次級限流的退避等級。
    /// 輪詢與使用者操作都可能呼叫。
    /// </summary>
    private void ApplyRateLimit(RateLimitKind? kind, TimeSpan? retryWait, bool hasRetryAfter)
    {
        TimeSpan wait;
        BannerKind bannerKind;
        Func<AppLanguage, DateTimeOffset, string> text;
        if (kind == RateLimitKind.Primary)
        {
            wait = retryWait ?? Tuning.MinRateLimitWait;
            bannerKind = BannerKind.PrimaryLimit;
            text = (language, until) => EngineText.PrimaryLimit(LocalTime(until), language);
        }
        else if (hasRetryAfter && retryWait is { } given)
        {
            wait = given;
            bannerKind = BannerKind.SecondaryLimit;
            text = (language, until) => EngineText.SecondaryLimitWithRetryAfter(LocalTime(until), language);
        }
        else
        {
            wait = _secondary.OnLimited();
            var level = _secondary.Level;
            var persistent = _secondary.Persistent;
            bannerKind = BannerKind.SecondaryLimit;
            text = (language, until) => EngineText.SecondaryLimitWithoutRetryAfter(level, persistent, LocalTime(until), language);
        }

        PauseAll(wait, kind == RateLimitKind.Primary ? "primary rate limit" : "secondary rate limit", bannerKind, text);
    }

    /// <summary>§6.4 第 5 列：可能是沒寫明的次級限流，全域暫停 MinRateLimitWait 一次（不倍增），寫 error.log（Q23）。</summary>
    private void ApplyForbiddenBurst()
    {
        _deps.ErrorLog.WriteMessage("全域暫停", EngineText.ForbiddenBurstLog);
        PauseAll(Tuning.MinRateLimitWait, "forbidden burst", BannerKind.SecondaryLimit,
            (language, until) => EngineText.ForbiddenBurst(LocalTime(until), language));
    }

    /// <remarks>
    /// 暫停、橫幅與 api.log 的註記都在 <c>_bannerGate</c> 裡：使用者操作可能在另一條工作上同時呼叫，
    /// 不能和 <see cref="NotePauseEnded"/> 交錯成「仍在暫停中卻寫了 resumed、橫幅也被清掉」。
    /// 橫幅的文字用目前設定的語言；<paramref name="text"/> 留著，換語言時重建（<see cref="ApplyLanguage"/>）。
    /// </remarks>
    private void PauseAll(TimeSpan wait, string reason, BannerKind bannerKind, Func<AppLanguage, DateTimeOffset, string> text)
    {
        // 防禦：reset 或 retry-after 異常大時，暫停可達數十年，App 內又解除不了，只能重新啟動。全部的暫停都經過這裡。
        if (wait > Tuning.MaxRateLimitWait)
        {
            _deps.ErrorLog.WriteMessage("全域暫停", EngineText.RateLimitWaitCappedLog(wait, reason));
            wait = Tuning.MaxRateLimitWait;
        }

        lock (_bannerGate)
        {
            _throttle.PauseFor(wait, reason);

            // 已經有更長的暫停時保留原本的（RequestThrottle.PauseFor 的規則），橫幅也跟著保留。
            var remaining = _throttle.PauseRemaining ?? wait;
            var until = _time.GetUtcNow() + remaining;
            if (_pauseBanner is null || remaining <= wait || _pauseBanner.Until < until)
            {
                _pauseBanner = new BannerInfo(bannerKind, text(Language, until), until);
                _pauseText = text;
            }

            _deps.ApiLog.Note("paused until " + TimeZoneInfo.ConvertTime(until, _time.LocalTimeZone)
                .ToString(PauseNoteTimeFormat, CultureInfo.InvariantCulture) + " (" + reason + ")");
            _pauseNoted = true;
        }
    }

    /// <summary>全域暫停結束時在 api.log 記一行（spec §14.4）。</summary>
    /// <remarks>在鎖裡確認暫停真的結束了才清掉橫幅、寫 resumed（見 <see cref="PauseAll"/>）。</remarks>
    private void NotePauseEnded()
    {
        lock (_bannerGate)
        {
            if (!_pauseNoted || _throttle.IsPaused)
            {
                return;
            }

            _pauseNoted = false;
            _pauseBanner = null;
            _pauseText = null;
            _deps.ApiLog.Note("resumed");
        }
    }

    private void EnterVersionError(int statusCode)
    {
        if (_versionError)
        {
            return;
        }

        // 400／410：停止所有輪詢，等你更新程式（spec §4.1）。
        _versionError = true;
        _deps.ErrorLog.WriteMessage("API 版本錯誤", string.Create(CultureInfo.InvariantCulture,
            $"GitHub 回應 {statusCode}，停止所有輪詢（X-GitHub-Api-Version: {Tuning.ApiVersion}）"));
    }

    private void NoticeDeprecated()
    {
        if (_deprecatedNoticed)
        {
            return;
        }

        _deprecatedNoticed = true;
        _deps.ErrorLog.WriteMessage("API 版本即將停止支援", "回應帶有 Deprecation 或 Sunset header（X-GitHub-Api-Version: " + Tuning.ApiVersion + "）");
        SetNotice(ref _deprecatedNotice, BannerKind.Deprecated, EngineText.Deprecated);
    }

    /// <summary>X-GitHub-SSO：spec §4.4 要一次性提示，但沒有對應的橫幅種類，只寫 error.log 一次。</summary>
    private void NoticeSso(string value)
    {
        if (_ssoNoticed)
        {
            return;
        }

        _ssoNoticed = true;
        _deps.ErrorLog.WriteMessage("SAML SSO", "回應帶有 X-GitHub-SSO：" + value);
    }

    private void EnterOffline()
    {
        // 離線：停止所有輪詢，改用 E10 探測，從 OfflineProbeInitial 起倍增到 OfflineProbeMax（spec §6.4）。
        _offlineProbeDelay = Tuning.OfflineProbeInitial;
        _scheduler.Schedule(OfflineProbeTarget, _offlineProbeDelay, RequestPriority.Probe);
    }

    private void OnBackOnline()
    {
        _scheduler.Remove(OfflineProbeTarget);
        _offlineProbeDelay = Tuning.OfflineProbeInitial;
        _verifyAt = null;
        FullRefresh();
    }

    /// <summary>core 額度：最近一次的快照給摘要列，QuotaSampleWindow 內的樣本給低額度倍率 k。</summary>
    private void RecordRateLimit(RateLimitSnapshot? snapshot)
    {
        if (snapshot is null || !string.Equals(snapshot.Resource, CoreResource, StringComparison.Ordinal))
        {
            return;
        }

        _rateLimit = snapshot;
        if (snapshot is { Remaining: { } remaining, Limit: { } limit } && limit > 0)
        {
            _quotaSamples.Add(new QuotaSample(_time.GetTimestamp(), remaining, limit, snapshot.ResetEpoch));
        }
    }

    /// <summary>低額度拉長倍率 k（spec §6.4）：QuotaSampleWindow 內、和最新樣本同一個 reset 視窗的最小 remaining ÷ limit。</summary>
    private double Stretch()
    {
        _quotaSamples.RemoveAll(sample => _time.GetElapsedTime(sample.At) > Tuning.QuotaSampleWindow);
        if (_quotaSamples.Count == 0)
        {
            return IntervalPolicy.StretchFactor(null);
        }

        var latest = _quotaSamples[^1];
        var sameWindow = _quotaSamples.Where(sample => sample.Reset == latest.Reset).ToList();
        var minimum = sameWindow.MinBy(sample => sample.Remaining)!;
        return IntervalPolicy.StretchFactor((double)minimum.Remaining / minimum.Limit);
    }

    // ── 排程 ──────────────────────────────────────────────────────────

    /// <summary>排到 <paramref name="delay"/> 之後，記下這次的間隔（錯誤退避的「上次間隔」）；不是退避排的就不再算在錯誤退避中。</summary>
    private void Schedule(PollTarget target, TimeSpan delay)
    {
        _lastDelay[target] = delay;
        _errorBackoff.Remove(target);
        _scheduler.Schedule(target, delay, PriorityOf(target));
    }

    /// <summary>
    /// 正常間隔改變（切換固定刷新秒數、標記完成時通知我）時，重排正在錯誤退避中的目標（spec §6.4）：
    /// 從上一次送出起算 max(新的正常間隔, 退避後的間隔)，不改「上次間隔」，退避等級不因此重設；成功一次才恢復正常間隔。
    /// </summary>
    /// <returns>這個目標正在錯誤退避、已經照上面的規則重排時回 true。</returns>
    private bool RescheduleBackoff(PollTarget target, TimeSpan normal)
    {
        if (!_errorBackoff.Contains(target) || !_lastDelay.TryGetValue(target, out var backoff) || !_lastSent.TryGetValue(target, out var sent))
        {
            return false;
        }

        var interval = backoff > normal ? backoff : normal;
        _scheduler.Schedule(target, Clamp(interval - _time.GetElapsedTime(sent), interval), PriorityOf(target));
        return true;
    }

    /// <summary>
    /// 被移出排程（暫停、靜音、重新分類）的目標排回去時，正在 5xx 等錯誤退避中的照退避排（同 <see cref="RescheduleBackoff"/>）：
    /// 不立即重送，退避等級也不重設（spec §6.4）。暫停模式下沒有正常間隔的目標只照退避後的間隔。
    /// </summary>
    /// <returns>這個目標正在錯誤退避、已經重排時回 true；否則由呼叫端照原本的規則排。</returns>
    private bool RestoreBackoff(PollTarget target) =>
        _errorBackoff.Contains(target) && RescheduleBackoff(target, NormalInterval(target) ?? TimeSpan.Zero);

    /// <summary>週期性重排；暫停模式（間隔為 null）時不排。</summary>
    private void Reschedule(PollTarget target, TimeSpan? interval)
    {
        if (interval is { } delay)
        {
            Schedule(target, delay);
        }
        else
        {
            _scheduler.Remove(target);
        }
    }

    /// <summary>spec §6.3 的基本優先序。</summary>
    private RequestPriority PriorityOf(PollTarget target) => target.Kind switch
    {
        TargetKind.Probe or TargetKind.OfflineProbe => RequestPriority.Probe,
        TargetKind.Runs => target.RepoId is { } id && _repos.TryGetValue(id, out var repo) ? RunsPriority(repo) : RequestPriority.RunsPoll,
        TargetKind.RunDetail => RequestPriority.RunsPoll,
        TargetKind.PullRequests or TargetKind.PullRequestSeed => RequestPriority.PullRequests,
        _ => RequestPriority.Maintenance,
    };

    private RequestPriority RunsPriority(RepoInfo repo) => RepoRunning(repo) ? RequestPriority.RunningRepo : RequestPriority.RunsPoll;

    /// <summary>這個目標正常（沒有錯誤時）的間隔；暫停模式下的週期性目標回 null。</summary>
    private TimeSpan? NormalInterval(PollTarget target)
    {
        var k = Stretch();
        var mode = _refreshMode;
        RepoInfo? repo = target.RepoId is { } id ? _repos.GetValueOrDefault(id) : null;
        return target.Kind switch
        {
            TargetKind.Probe => IntervalPolicy.Periodic(Tuning.ProbeInterval, mode, k, _locked, followsRefreshMode: true),
            // 還沒抓完過的完整清單同下面還沒查過的 workflow 清單：失敗後的重試用安靜間隔的上限，第一次載入不必為一次 5xx 等一小時。
            TargetKind.RepoList when !_repoListFetched =>
                IntervalPolicy.Periodic(Tuning.QuietMax, mode, k, _locked, followsRefreshMode: false),
            TargetKind.RepoList => IntervalPolicy.Periodic(Tuning.RepoListRefresh, mode, k, _locked, followsRefreshMode: false),
            // 還沒查過的 workflow 清單是排 E4 之前的一次性檢查：失敗後的重試和其他一次性目標一樣用安靜間隔的上限。
            TargetKind.Workflows when repo is not null && StateOf(repo.Id).WorkflowsCheckedAt is null =>
                IntervalPolicy.Periodic(Tuning.QuietMax, mode, k, _locked, followsRefreshMode: false),
            TargetKind.Workflows => IntervalPolicy.Periodic(Tuning.WorkflowsRefresh, mode, k, _locked, followsRefreshMode: false),
            TargetKind.Runs when repo is not null => RunsInterval(repo, pollInterval: null),
            TargetKind.RunDetail when repo is not null => RunDetailInterval(repo, target.ItemId ?? 0),
            TargetKind.PullRequests when repo is not null => IntervalPolicy.Periodic(PullListTtl(repo), mode, k, _locked, followsRefreshMode: false),
            TargetKind.DefaultBranchSeed => IntervalPolicy.Periodic(Tuning.SeedRecheck, mode, k, _locked, followsRefreshMode: false),
            // 一次性的目標失敗後的重試：沿用安靜間隔的上限。
            _ => IntervalPolicy.Periodic(Tuning.QuietMax, mode, k, _locked, followsRefreshMode: false),
        };
    }

    /// <summary>spec §6.2 的間隔公式；回應帶 x-poll-interval 時取兩者較大（spec §4.1）。</summary>
    private TimeSpan? RunsInterval(RepoInfo repo, TimeSpan? pollInterval)
    {
        var data = Data(repo.Id);
        var interval = IntervalPolicy.RepoRunsInterval(
            RepoRunning(repo),
            IntervalPolicy.LastActivity(data.Runs, repo.PushedAt),
            _time.GetUtcNow(),
            _refreshMode,
            Stretch(),
            _locked);
        return WithPollInterval(interval, pollInterval);
    }

    /// <summary>回應帶 x-poll-interval 時，該目標下次間隔取 max(自己的間隔, header 值)（spec §4.1）；暫停模式（null）維持不排。</summary>
    private static TimeSpan? WithPollInterval(TimeSpan? interval, TimeSpan? pollInterval) =>
        interval is { } value && pollInterval is { } header && header > value ? header : interval;

    /// <summary>
    /// repoRunning（spec §6.2）：最近一次 E4 第 1 頁裡的 run，加上追蹤中（各鍵的 latest）或被標記的 run，
    /// 有任何一筆是「未完成」類別。等待核准不算。
    /// </summary>
    private bool RepoRunning(RepoInfo repo)
    {
        var data = Data(repo.Id);
        var page = _latestPage.GetValueOrDefault(repo.Id) ?? [];
        var keyLatest = EvaluateKeys(repo).Select(key => key.Latest.Id).ToHashSet();
        return data.Runs.Any(run =>
            (page.Contains(run.Id) || keyLatest.Contains(run.Id) || _watched.Contains(run.Id))
            && RunStateMapper.Map(run).Category == RunCategory.Pending);
    }

    /// <summary>
    /// E6：追蹤中或被標記的用所屬 repo 的間隔，其他用 QuietMax（spec §6.1）。有操作後追蹤的 run 照第二階段 spec §6.2 的表
    /// （<see cref="PostActionInterval"/>，不看自動更新模式）；它同時也是 P1 的對象（本地未完成、不在第 1 頁）時取兩者較短的。
    /// 不是 P1 的對象、只因為算成失敗而重查的（<see cref="FailingRecheckCandidates"/>）用 FailingRunRecheck，暫停自動更新時不排；
    /// 同時有操作後追蹤時取兩者較短的。
    /// </summary>
    private TimeSpan? RunDetailInterval(RepoInfo repo, long runId)
    {
        var postAction = PostActionInterval(runId);
        var page = _latestPage.GetValueOrDefault(repo.Id) ?? [];
        if (!RunDetailCandidates(Data(repo.Id), page).Contains(runId))
        {
            if (FailingRecheckCandidates(repo, page).Contains(runId))
            {
                var recheck = IntervalPolicy.Periodic(Tuning.FailingRunRecheck, _refreshMode, Stretch(), _locked, followsRefreshMode: false);
                return Shorter(postAction, recheck);
            }

            if (postAction is not null)
            {
                return postAction;
            }
        }

        var tracked = _watched.Contains(runId)
            || EvaluateKeys(repo).Any(key => key.Latest.Id == runId || key.LastDecisiveGroupRunIds.Contains(runId));
        var normal = tracked
            ? RunsInterval(repo, pollInterval: null)
            : IntervalPolicy.Periodic(Tuning.QuietMax, _refreshMode, Stretch(), _locked, followsRefreshMode: false);
        return Shorter(postAction, normal);
    }

    /// <summary>有 PR 鍵處於 failing 或 keyPending 時 PullListTtlActive，否則 PullListTtl（Q25）。</summary>
    private TimeSpan PullListTtl(RepoInfo repo) =>
        EvaluateKeys(repo).Any(key => key.Kind == KeyKind.PullRequestBranch && (key.Failing || key.Pending))
            ? Tuning.PullListTtlActive
            : Tuning.PullListTtl;

    // ── 錯誤 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 單一目標失敗後的重排（spec §6.4）：限流與 401 等全域狀態恢復後再送；權限錯誤每 PermissionRetryInterval；
    /// 5xx、422、JSON 錯誤等只讓這個目標退避（不短於正常間隔，上限 RepoErrorBackoffMax × k），並寫 error.log。
    /// </summary>
    private void HandleFailure(PollTarget target, Outcome outcome)
    {
        if (outcome.Kind is not (ApiOutcome.Forbidden or ApiOutcome.NotFound))
        {
            _unanswered.Add(target);
        }

        var normal = NormalInterval(target);
        switch (outcome.Kind)
        {
            case ApiOutcome.RateLimited:
            case ApiOutcome.Unauthorized:
                // 暫停結束或重新登入後馬上重送。
                _scheduler.ScheduleNow(target, PriorityOf(target));
                return;

            case ApiOutcome.NetworkError:
                // 用戶端逾時只算在離線判定，不算單一目標錯誤（spec §6.4）。
                Reschedule(target, normal);
                return;

            case ApiOutcome.VersionError:
                return;

            case ApiOutcome.Forbidden:
                if (target.RepoId is { } repoId)
                {
                    var (insufficient, permissions, scopes, message) =
                        (outcome.InsufficientPermission, outcome.AcceptedPermissions, outcome.AcceptedOAuthScopes, outcome.Message);
                    SetProblem(repoId, target.Kind,
                        language => EngineText.Forbidden(insufficient, permissions, scopes, message, language),
                        Tuning.PermissionRetryInterval);
                }
                else if (target.Kind is TargetKind.Probe or TargetKind.RepoList)
                {
                    // 探測與清單也是錯誤狀態的目標：被暫停移出排程、切換刷新模式、全量刷新時照重試間隔，不提前重送（spec §6.1）。
                    _targetRetries[target] = Tuning.PermissionRetryInterval;
                }

                Reschedule(target, IntervalPolicy.Periodic(Tuning.PermissionRetryInterval, _refreshMode, Stretch(), _locked, followsRefreshMode: false));
                return;

            case ApiOutcome.NotFound:
                // 會到這裡的 repo 目標只有 E5 與 PR 分支補抓（E4／E3 走 §5.4、E6 標成已不存在、E7 停 PR 追蹤）：
                // 記成錯誤狀態的目標（不顯示提示），被暫停、靜音移出排程再排回去時照 NotFoundRetryInterval，不提前重送（spec §6.1）。成功時清掉。
                if (target.RepoId is { } notFoundRepoId)
                {
                    SetProblem(notFoundRepoId, target.Kind, note: null, Tuning.NotFoundRetryInterval);
                }
                else if (target.Kind is TargetKind.Probe or TargetKind.RepoList)
                {
                    // 探測與清單同上面的 403。
                    _targetRetries[target] = Tuning.NotFoundRetryInterval;
                }

                Reschedule(target, IntervalPolicy.Periodic(Tuning.NotFoundRetryInterval, _refreshMode, Stretch(), _locked, followsRefreshMode: false));
                return;

            default:
                if (outcome.Kind is ApiOutcome.Unprocessable or ApiOutcome.ParseError or ApiOutcome.RedirectError or ApiOutcome.ServerError
                    or ApiOutcome.OtherError)
                {
                    _deps.ErrorLog.WriteMessage("輪詢目標回應錯誤", EngineText.TargetErrorLog(target, outcome.Kind.ToString(), outcome.StatusCode, outcome.Message));
                }

                Backoff(target, normal);
                return;
        }
    }

    private void Backoff(PollTarget target, TimeSpan? normal)
    {
        if (normal is not { } interval)
        {
            _scheduler.Remove(target);
            return;
        }

        var previous = _lastDelay.GetValueOrDefault(target);
        Schedule(target, IntervalPolicy.ErrorBackoff(interval, previous, Stretch()));
        _errorBackoff.Add(target);
    }

    /// <summary>處理過程丟出例外：當成單一目標的錯誤退避（spec §15「Engine 每一輪各自 try/catch」）。</summary>
    private void RescheduleAfterUnexpected(PollTarget target)
    {
        try
        {
            if (target.Kind == TargetKind.OfflineProbe)
            {
                _scheduler.Schedule(OfflineProbeTarget, _offlineProbeDelay, RequestPriority.Probe);
                return;
            }

            if (target.Kind == TargetKind.RepoList)
            {
                _round = null;
            }

            _unanswered.Add(target);
            Backoff(target, NormalInterval(target));
        }
        catch (Exception ex)
        {
            ReportUnexpected("重排輪詢目標 " + target.Kind, ex);
        }
    }

    private string LocalTime(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, _time.LocalTimeZone).ToString(EngineText.ShortTimeFormat, CultureInfo.InvariantCulture);
}
