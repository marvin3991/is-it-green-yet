using System.Globalization;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.GitHub.Dto;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Engine;

/// <summary>
/// 使用者操作（spec §3.1）：走另一條工作，一次只做一個（MaxConcurrentUser），和輪詢共用同一個節流器與額度狀態。
/// 錯誤只組成訊息回給 App，不進全域流程（不停輪詢、不改 repo 狀態）；只有限流照 §6.4 全域暫停，401 照 §13.2。
/// 釋放引擎時進行中的操作以 OperationCanceledException 結束（不寫 error.log）。
/// </summary>
public sealed partial class MonitorEngine
{
    /// <summary>複製失敗 log 只取這兩種結論的 job（spec §10.9 第 1 點）。</summary>
    private const string FailureConclusion = "failure";
    private const string TimedOutConclusion = "timed_out";

    /// <summary>job 的結論轉成 §7.1 的顯示文字時，status 一律當成 completed。</summary>
    private const string CompletedStatus = "completed";

    /// <summary>X-OAuth-Scopes 裡代表可讀私有 repo 的 scope（spec §4.4）。</summary>
    private const string RepoScope = "repo";

    /// <summary>
    /// 複製失敗 log（spec §10.9）：E8 取 filter=latest 的 job，只留 failure 與 timed_out，
    /// 每個用 E9 串流下載、只留最後 AppSettings.LogTailLines 行。log 只回給呼叫端，不寫入任何檔案。
    /// </summary>
    public async Task<CopyLogResult> CopyFailedLogAsync(long repoId, long runId, CancellationToken ct)
    {
        ThrowIfDisposed();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = operation.Token;
        await _userGate.WaitAsync(token).ConfigureAwait(false);

        // 這次操作回給你的文字一律用開始時的語言。
        var language = Language;
        try
        {
            if (UserBlockedReason(language) is { } blocked)
            {
                return new CopyLogResult(false, null, blocked);
            }

            if (!_repoLookup.TryGetValue(repoId, out var repo))
            {
                return new CopyLogResult(false, null, EngineText.RepoUnknown(language));
            }

            var jobs = await GetJobsAsync(repo, runId, token).ConfigureAwait(false);
            if (jobs.Outcome != ApiOutcome.Ok)
            {
                return new CopyLogResult(false, null, EngineText.JobsUnavailable(jobs.StatusCode, language));
            }

            var failed = jobs.Jobs.Where(job => job.Conclusion is FailureConclusion or TimedOutConclusion).ToList();
            if (failed.Count == 0)
            {
                // 例如 startup_failure 可能根本沒有 job（spec §10.9 第 4 點）。
                return new CopyLogResult(false, null, EngineText.NoFailedJobs(language));
            }

            var tailLines = _settings.LogTailLines;
            var sections = new List<string>(failed.Count);
            var problems = new List<string>();
            foreach (var job in failed)
            {
                var header = EngineText.JobHeader(job.Name, RunStateMapper.Map(CompletedStatus, job.Conclusion, language).Label, language);
                (ApiResult<object> Result, IReadOnlyList<string> Lines) download;
                try
                {
                    download = await DownloadLogAsync(repo, job.Id, tailLines, token).ConfigureAwait(false);
                }
                catch (UserOperationStoppedException stopped)
                {
                    // 操作進行到一半時輪詢進入未登入或版本錯誤：不再送其他 job 的請求，已經拿到的 log 照常回給你。
                    sections.Add(header + "\n" + stopped.Message);
                    problems.Add(stopped.Message);
                    break;
                }

                var (result, lines) = download;
                if (result.Outcome == ApiOutcome.Ok)
                {
                    sections.Add(header + "\n" + string.Join("\n", lines));
                    continue;
                }

                var problem = EngineText.LogUnavailable(result.StatusCode, language);
                sections.Add(header + "\n" + problem);
                problems.Add(problem);
                if (result.Outcome is ApiOutcome.RateLimited or ApiOutcome.Unauthorized)
                {
                    // 全域暫停或未登入之後不再送其他 job 的請求。
                    break;
                }
            }

            var copied = sections.Count - problems.Count;
            return copied == 0
                ? new CopyLogResult(false, null, problems[0])
                : new CopyLogResult(true, string.Join("\n\n", sections), EngineText.Copied(copied, failed.Count - copied, language));
        }
        catch (UserOperationStoppedException stopped)
        {
            return new CopyLogResult(false, null, stopped.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (Volatile.Read(ref _disposed) != 0)
        {
            throw Stopped(ex, token);
        }
        catch (Exception ex)
        {
            ReportUnexpected("複製失敗 log", ex);
            Wake();
            return new CopyLogResult(false, null, EngineText.UnexpectedError(language));
        }
        finally
        {
            _userGate.Release();
        }
    }

    /// <summary>
    /// 設定頁的「檢查連線」（spec §11.2）：E1 驗證 token，再數清單 A 與 B 看得到幾個 repo、其中私有幾個，
    /// 並判讀 X-OAuth-Scopes。只讀不寫：清單回 200 的內容不存進 DB（ETag 也不更新），回 304 時用本地資料判斷是否私有。
    /// 一開始先清除被拒之後的寫入停用（第二階段 spec §4.3），之後的寫入重新由伺服器判斷。
    /// </summary>
    public async Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = operation.Token;
        await _userGate.WaitAsync(token).ConfigureAwait(false);

        // 這次操作回給你的文字一律用開始時的語言。
        var language = Language;
        try
        {
            ClearWriteDenied();
            if (UserBlockedReason(language) is { } blocked)
            {
                return new ConnectionCheckResult(false, null, 0, 0, null, blocked);
            }

            var (user, sent) = await UserCallWithTokenAsync(_api.GetUserAsync, result => result, token).ConfigureAwait(false);
            if (user.Outcome != ApiOutcome.Ok)
            {
                return new ConnectionCheckResult(false, null, 0, 0, null, EngineText.ConnectionFailed(user.StatusCode, language));
            }

            var account = user.Value!;
            var scopesNote = ScopesNote(user.OAuthScopes, language);

            // 這次 E1 驗證的是送出時那一把 token。還在路上時 token 換了（重新檢查、改認證方式），或輪詢先收到 401
            // 而進入未登入：結果只回給你，不套用到輪詢；新 token 要自己用 E1 驗證（spec §13.2、handoff Task 10），帳號也以輪詢驗證的為準。
            if (sent.Token is { } verifiedToken && TryMarkVerified(sent))
            {
                _userEffects.Enqueue(() => OnUserVerified(account, verifiedToken));
                Wake();
            }

            var seen = new HashSet<long>();
            var privateRepos = 0;
            foreach (var collaborator in new[] { false, true })
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                for (var url = GitHubApi.RepoListUrl(collaborator, probe: false); url is not null && visited.Add(url);)
                {
                    var current = url;
                    var page = await UserCallAsync(c => _api.GetRepoPageAsync(current, c), result => result, token).ConfigureAwait(false);
                    IEnumerable<(long Id, bool IsPrivate)> items;
                    if (page.Outcome == ApiOutcome.Ok)
                    {
                        items = page.Value!.Repos.Select(repo => (repo.Id, repo.IsPrivate));
                    }
                    else if (page.Outcome == ApiOutcome.NotModified && page.CacheEntry?.ItemIds is { } ids)
                    {
                        var lookup = _repoLookup;
                        items = ids.Select(id => (id, lookup.TryGetValue(id, out var known) && known.IsPrivate));
                    }
                    else
                    {
                        return new ConnectionCheckResult(false, account.Login, seen.Count, privateRepos, scopesNote,
                            EngineText.ReposUnavailable(page.StatusCode, language));
                    }

                    foreach (var (id, isPrivate) in items)
                    {
                        if (seen.Add(id) && isPrivate)
                        {
                            privateRepos++;
                        }
                    }

                    url = page.CacheEntry?.NextUrl;
                }
            }

            return new ConnectionCheckResult(true, account.Login, seen.Count, privateRepos, scopesNote,
                EngineText.VisibleRepos(seen.Count, privateRepos, language));
        }
        catch (UserOperationStoppedException stopped)
        {
            return new ConnectionCheckResult(false, null, 0, 0, null, stopped.Message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (Volatile.Read(ref _disposed) != 0)
        {
            throw Stopped(ex, token);
        }
        catch (Exception ex)
        {
            ReportUnexpected("檢查連線", ex);
            Wake();
            return new ConnectionCheckResult(false, null, 0, 0, null, EngineText.UnexpectedError(language));
        }
        finally
        {
            _userGate.Release();
        }
    }

    /// <summary>釋放引擎期間才發生的例外：當成取消，不寫 error.log、不提示。</summary>
    private static OperationCanceledException Stopped(Exception exception, CancellationToken token) =>
        new("監看引擎已經停止。", exception, token);

    /// <summary>未登入不送任何 API（spec §13.2）；版本錯誤與全域暫停期間也不送（spec §6.4）。</summary>
    private string? UserBlockedReason(AppLanguage language) =>
        UserStopReason(language)
        ?? (_throttle.PauseRemaining is { } pause ? EngineText.Paused(LocalTime(_time.GetUtcNow() + pause), language) : null);

    /// <summary>
    /// 操作進行中也要停下的原因：未登入（spec §13.2，不能用被拒的 token 繼續送）與版本錯誤（spec §4.1）。
    /// 全域暫停不在這裡：進行中的操作等暫停結束再送（<see cref="WaitForUserSlotAsync"/>）。
    /// </summary>
    private string? UserStopReason(AppLanguage language)
    {
        if (_auth.Token is null || _auth.Status == AuthStatus.Unauthorized)
        {
            return EngineText.NotLoggedIn(language);
        }

        return _versionError ? EngineText.VersionError(language) : null;
    }

    /// <summary>
    /// E8（filter=latest、per_page=ListPageSize，不帶 If-None-Match）在這裡逐頁送：每一頁都經過節流器（spec §6.3 每分鐘上限含每一個請求），
    /// 不用 GitHubApi.GetJobsAsync 在一次呼叫裡跟完所有分頁。分頁規則同 GetJobsAsync：任何一頁失敗就回那一頁的錯誤、
    /// Link 指回已經抓過的頁就停、兩頁之間 job 清單變動造成的重複項以 id 去重。
    /// </summary>
    private async Task<(ApiOutcome Outcome, int StatusCode, IReadOnlyList<JobInfo> Jobs)> GetJobsAsync(
        RepoInfo repo, long runId, CancellationToken ct)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{GitHubClient.RepoPath(repo.FullName)}/actions/runs/{runId}/jobs?filter=latest&per_page={Tuning.ListPageSize}");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var jobs = new List<JobInfo>();
        var seenIds = new HashSet<long>();

        while (true)
        {
            var current = url;
            var page = await UserCallAsync(c => _client.GetAsync<JobsPageDto>(current, conditional: false, ct: c), result => result, ct)
                .ConfigureAwait(false);
            if (page.Outcome != ApiOutcome.Ok)
            {
                return (page.Outcome, page.StatusCode, []);
            }

            visited.Add(page.CacheEntry?.Url ?? current);
            foreach (var job in (page.Value!.Jobs ?? []).Select(DtoMapper.ToJob).OfType<JobInfo>())
            {
                if (seenIds.Add(job.Id))
                {
                    jobs.Add(job);
                }
            }

            var next = page.CacheEntry?.NextUrl;
            if (next is null || !visited.Add(next))
            {
                return (ApiOutcome.Ok, page.StatusCode, jobs);
            }

            url = next;
        }
    }

    /// <summary>E9；下載主機回非 2xx 時重新呼叫 E9 一次（spec §4.2 E9）。</summary>
    private async Task<(ApiResult<object> Result, IReadOnlyList<string> Lines)> DownloadLogAsync(
        RepoInfo repo, long jobId, int tailLines, CancellationToken ct)
    {
        var first = await UserCallAsync(c => _api.GetJobLogTailAsync(repo, jobId, tailLines, c), pair => pair.Result, ct).ConfigureAwait(false);
        if (first.Result.Outcome != ApiOutcome.OtherError || first.Result.StatusCode == NoResponse)
        {
            return first;
        }

        return await UserCallAsync(c => _api.GetJobLogTailAsync(repo, jobId, tailLines, c), pair => pair.Result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 使用者操作送出一個請求：等節流器（比輪詢優先）、401 照 §13.2 最多重送一次（重送仍 401 就進入未登入）、限流照 §6.4 全域暫停。
    /// </summary>
    private async Task<TResponse> UserCallAsync<TResponse, T>(
        Func<CancellationToken, Task<TResponse>> call, Func<TResponse, ApiResult<T>> resultOf, CancellationToken ct) =>
        (await UserCallWithTokenAsync(call, resultOf, ct).ConfigureAwait(false)).Response;

    /// <summary>
    /// 同 <see cref="UserCallAsync{TResponse, T}"/>，另外回傳這個回應是用哪一把 token 送出的（401 換 token 重送時是重送用的那一把），
    /// 與送出之前已經收到過幾次 401（<see cref="TryMarkVerified"/>）。
    /// </summary>
    private async Task<(TResponse Response, SentWith Sent)> UserCallWithTokenAsync<TResponse, T>(
        Func<CancellationToken, Task<TResponse>> call, Func<TResponse, ApiResult<T>> resultOf, CancellationToken ct)
    {
        await WaitForUserSlotAsync(ct).ConfigureAwait(false);
        var token = _auth.Token;
        var sent = new SentWith(token, UnauthorizedSeen());
        var response = await SendWithTokenAsync(token, call, ct).ConfigureAwait(false);
        var result = resultOf(response);
        RecordFollowedRedirect(result);
        ObserveUserResult(result, token);
        NoteIfUnauthorized(result, token);
        if (result.Outcome != ApiOutcome.Unauthorized || !await OnUnauthorizedAsync(token, ct).ConfigureAwait(false))
        {
            return (response, sent);
        }

        await WaitForUserSlotAsync(ct).ConfigureAwait(false);
        var retryToken = _auth.Token;
        var retrySent = new SentWith(retryToken, UnauthorizedSeen());
        var retry = await SendWithTokenAsync(retryToken, call, ct).ConfigureAwait(false);
        var retryResult = resultOf(retry);
        RecordFollowedRedirect(retryResult);
        ObserveUserResult(retryResult, retryToken);
        NoteIfUnauthorized(retryResult, retryToken);
        if (retryResult.Outcome == ApiOutcome.Unauthorized)
        {
            await RefuseTokenAsync(retryToken, ct).ConfigureAwait(false);
        }

        return (retry, retrySent);
    }

    /// <summary>
    /// 等到節流器放行並記下送出（同一個鎖裡）。等待期間輪詢讓路，所以使用者操作排在所有輪詢之前（spec §6.3 第 1 級）。
    /// </summary>
    /// <remarks>
    /// 每次放行前都再看一次 <see cref="UserStopReason(AppLanguage)"/>（文字用目前設定的語言）：操作進行到一半時輪詢可能已經進入未登入，
    /// 這時不再送出（spec §13.2 避免連續送出無效憑證），以 <see cref="UserOperationStoppedException"/> 結束。
    /// </remarks>
    private async Task WaitForUserSlotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _userWaiting);
        try
        {
            while (true)
            {
                TimeSpan? wait;
                lock (_throttleGate)
                {
                    if (UserStopReason(Language) is { } stop)
                    {
                        throw new UserOperationStoppedException(stop);
                    }

                    wait = _throttle.TimeUntilAllowed();
                    if (wait is null)
                    {
                        _throttle.RecordSent();
                        return;
                    }
                }

                if (wait <= TimeSpan.Zero)
                {
                    await Task.Yield();
                    continue;
                }

                await Task.Delay(wait.Value, _time, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _userWaiting);
            Wake();
        }
    }

    /// <summary>
    /// 和輪詢共用的額度狀態：限流全域暫停；額度樣本與 Deprecation 交給下一步記錄。token 的 scope 直接記下（第二階段 spec §4.1，volatile）。
    /// </summary>
    /// <param name="token">這個請求送出時用的 token。</param>
    private void ObserveUserResult<T>(ApiResult<T> result, string? token)
    {
        RecordScopes(result, token);
        if (result.Outcome == ApiOutcome.RateLimited)
        {
            ApplyRateLimit(result.LimitKind, result.RetryWait, result.HasRetryAfter);
        }

        if (result.RateLimit is { } rateLimit)
        {
            _userEffects.Enqueue(() => RecordRateLimit(rateLimit));
        }

        if (result.Deprecated)
        {
            _userEffects.Enqueue(NoticeDeprecated);
        }

        Wake();
    }

    /// <summary>檢查連線的 E1 成功：記下帳號；輪詢還在等驗證時直接用這次的結果。</summary>
    /// <param name="verifiedToken">這次 E1 用的 token；排隊等這一步的期間 token 又換了，就丟掉這次結果（理由同 <see cref="CheckConnectionAsync"/>）。</param>
    private void OnUserVerified(AccountInfo account, string verifiedToken)
    {
        if (!string.Equals(_auth.Token, verifiedToken, StringComparison.Ordinal))
        {
            return;
        }

        if (_needsVerify && _auth.Status == AuthStatus.Ok)
        {
            OnAccountVerified(account);
            return;
        }

        _account = account;
        _cache.SaveAccount(account);
    }

    /// <summary>使用者操作進行中遇到要停下的原因（未登入、版本錯誤）；訊息就是回給 App 的文字。</summary>
    private sealed class UserOperationStoppedException(string message) : Exception(message);

    /// <summary>X-OAuth-Scopes 有值但不含 repo 時提示（spec §4.4）；header 不存在或值為空時不判斷。</summary>
    private static string? ScopesNote(string? scopes, AppLanguage language)
    {
        if (string.IsNullOrWhiteSpace(scopes))
        {
            return null;
        }

        var hasRepo = scopes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(RepoScope, StringComparer.Ordinal);
        return hasRepo ? null : EngineText.ScopesWithoutRepo(language);
    }
}
