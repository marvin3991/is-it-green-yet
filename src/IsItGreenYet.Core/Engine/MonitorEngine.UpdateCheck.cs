using System.Runtime.CompilerServices;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Updates;

namespace IsItGreenYet.Core.Engine;

/// <summary>
/// 檢查新版本（E15）。到期只看 UTC 牆上時間（<see cref="UpdateCheckRules"/>），單調時鐘只決定什麼時候在本機判斷一次，判斷本身不連網。
/// 請求走輪詢那條線（<see cref="SendNextAsync"/>：睡眠、驗證中、未登入、離線都擋在前面，也經過節流），但不經過 <see cref="CallAsync{T}"/> 與 <see cref="Observe{T}"/>：
/// 結果不影響「!」角標、離線、限流、未登入、5xx、版本錯誤、額度與最後更新時間。
/// 這裡的欄位都是步驟專用。Engine 不寫設定檔：上次檢查時間與已通知版本由 <see cref="UpdateCheckRecorded"/> 交給 App 存。
/// </summary>
public sealed partial class MonitorEngine
{
    private const string UpdateCheckContext = "檢查新版本";

    /// <summary>目前版本；解析不了時是 null，這次執行不檢查，送了也比不出結果。</summary>
    private readonly SemVersion? _currentVersion;

    /// <summary>上次完成（200、304、404）的檢查時間。</summary>
    private DateTimeOffset? _updateLastCheckUtc;

    /// <summary>已經通知過的版本（<see cref="SemVersion.ToString"/> 的結果）。</summary>
    private string? _updateLastNotified;

    /// <summary>上次失敗的時間，<see cref="Tuning.UpdateCheckRetryFloor"/> 內不再送。只放在記憶體，不存進設定檔：重開 App 可能提早重試。</summary>
    private DateTimeOffset? _updateLastFailedUtc;

    /// <summary>上一次每小時判斷的單調時間戳；只決定什麼時候判斷，不決定是否到期。</summary>
    private long? _updateLastEvalAt;

    /// <summary>喚醒、解鎖、驗證成功之後要判斷一次（不等每小時的那一次）。</summary>
    private bool _updateEvalRequested;

    /// <summary>判斷的結果是到期，等節流器放行就送。</summary>
    private bool _updateCheckPending;

    private bool _updateBadCurrentLogged;

    /// <summary>上一次寫進 error.log 的失敗種類，同一種連續失敗只寫一次；完成時清除。</summary>
    private ApiOutcome? _updateLastLoggedOutcome;

    /// <summary>
    /// 讀設定物件本身，不讀 <see cref="_settings"/> 副本：設定頁先改物件才送 <see cref="EngineCommand.SettingsChanged"/>，
    /// 按下儲存之後就不會再有新請求；請求進行中才關閉時，處理結果也看得到。
    /// </summary>
    private bool UpdateCheckEnabled => _deps.Settings.CheckForUpdates;

    private bool UpdateCheckReady => _updateCheckPending && UpdateCheckEnabled;

    /// <summary>
    /// 在本機判斷一次要不要送：每 <see cref="Tuning.UpdateCheckLocalInterval"/> 一次，喚醒、解鎖、驗證成功之後也各一次。
    /// 這裡的例外只寫 log，不能影響這一步接下來的輪詢。
    /// </summary>
    /// <remarks>這個檔案標 NoInlining 的方法，是測試用呼叫堆疊找注入點（動態 PGO 內嵌之後堆疊裡就沒有它）；代價只是多一次方法呼叫。</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EvaluateUpdateCheck()
    {
        try
        {
            var hourly = _updateLastEvalAt is not { } last || _time.GetElapsedTime(last) >= Tuning.UpdateCheckLocalInterval;
            if (!hourly && !_updateEvalRequested)
            {
                return;
            }

            _updateEvalRequested = false;
            if (hourly)
            {
                // 解鎖、喚醒觸發的判斷不推遲每小時的節奏。
                _updateLastEvalAt = _time.GetTimestamp();
            }

            _updateCheckPending = false;
            if (!UpdateCheckEnabled)
            {
                return;
            }

            if (_currentVersion is null)
            {
                LogBadCurrentOnce();
                return;
            }

            // 跳過，不算嘗試；喚醒與驗證成功時都會再判斷一次。
            if (_suspended || _needsVerify || _auth.Status != AuthStatus.Ok)
            {
                return;
            }

            var due = UpdateCheckRules.Evaluate(_time.GetUtcNow(), _updateLastCheckUtc, _updateLastFailedUtc);
            _updateLastFailedUtc = due.LastFailedUtc;
            if (!due.Due)
            {
                return;
            }

            // 引擎知道送了也不會成功：不送，但算一次嘗試，最快 1 小時後才再送。
            if (_offline.IsOffline || _throttle.IsPaused || _versionError)
            {
                NoteUpdateAttemptFailed();
                return;
            }

            _updateCheckPending = true;
        }
        catch (Exception ex)
        {
            LogUpdateCheckException(ex);
        }
    }

    /// <summary>一定要帶 token 送（<see cref="SendWithTokenAsync{TResult}"/>）：沒有 token 時私有 repo 回 404，而 404 算完成，錯誤會被靜默吞掉。</summary>
    /// <returns>送出的請求數，一定是 1。</returns>
    private async Task<int> SendUpdateCheckAsync(CancellationToken ct)
    {
        try
        {
            var conditional = UpdateCheckRules.ShouldSendEtag(_updateLastCheckUtc);
            var result = await SendWithTokenAsync(_auth.Token, c => _api.GetLatestReleaseAsync(conditional, c), ct).ConfigureAwait(false);
            RecordFollowedRedirect(result);

            // 請求進行中才關閉選項：已送出的收不回來，結果整個丟棄。
            if (UpdateCheckEnabled)
            {
                ApplyUpdateCheckResult(result);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogUpdateCheckException(ex);
            if (UpdateCheckEnabled)
            {
                NoteUpdateAttemptFailed();
            }
        }

        return 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ApplyUpdateCheckResult(ApiResult<ReleaseInfo> result)
    {
        if (!UpdateCheckRules.IsCompleted(result.Outcome))
        {
            LogUpdateCheckFailure(result);
            NoteUpdateAttemptFailed();
            return;
        }

        // 收到回應當下就記，不等通知送出：通知前程式結束時，最多 3 天後回 200 再通知，不會遺失。
        _updateLastCheckUtc = _time.GetUtcNow();
        _updateLastFailedUtc = null;
        _updateLastLoggedOutcome = null;
        RaiseUpdateRecord();

        if (result.Outcome == ApiOutcome.Ok && result.Value is { } release && _currentVersion is { } current)
        {
            ApplyRelease(current, release, result.CacheEntry);
        }
    }

    private void ApplyRelease(SemVersion current, ReleaseInfo release, HttpCacheEntry? entry)
    {
        var decision = UpdateCheckRules.Decide(current, release, _updateLastNotified);
        switch (decision.Kind)
        {
            case UpdateDecisionKind.Notify:
                // 這個回應的 ETag 不存，下一次回 200 時已經是「已通知過」才存：已通知版本由 App 非同步存檔，
                // 沒存到時（例如程式正在結束）要是回 304，就沒有內容可以再通知，這個版本會遺失。
                _toasts.AddNotice(decision.Notice!);
                _toastPendingSince ??= _time.GetTimestamp();
                return;

            case UpdateDecisionKind.BadTag:
                _deps.ErrorLog.WriteMessage(UpdateCheckContext, EngineText.UpdateCheckBadTagLog(release.TagName));
                break;

            case UpdateDecisionKind.BadUrl:
                _deps.ErrorLog.WriteMessage(UpdateCheckContext, EngineText.UpdateCheckBadUrlLog(release.TagName, release.HtmlUrl));
                break;
        }

        // 內容沒變就回 304，不會重複判斷，也不會重複寫 log。
        SaveUpdateEtag(entry);
    }

    /// <summary>失敗或跳過但算嘗試：下一次判斷剛好在下限滿的時候。先讀牆鐘再讀單調時鐘，單調時鐘滿 1 小時時牆鐘也滿了。</summary>
    private void NoteUpdateAttemptFailed()
    {
        _updateLastFailedUtc = _time.GetUtcNow();
        _updateLastEvalAt = _time.GetTimestamp();
        _updateCheckPending = false;
    }

    /// <summary>
    /// 選項關閉：排隊中的請求與通知都不做。下一次要等重新開啟之後的每小時判斷。
    /// 由 <see cref="FlushToasts"/> 在每一步結尾呼叫，不放在處理 <see cref="EngineCommand.SettingsChanged"/> 的地方：
    /// 步驟已經過了指令處理才關閉時，指令要到下一步才處理，排隊中的提示會先送出。
    /// </summary>
    private void CancelUpdateCheck()
    {
        _updateCheckPending = false;
        _toasts.DropNotice();
    }

    /// <summary>
    /// 提示交給 App 之後（<see cref="ToastReady"/>）才算通知過，才記已通知版本；鎖定中程式結束時，暫存的通知會消失。
    /// <see cref="FlushToasts"/> 在步驟的 try 之外，所以這裡不能讓例外逸出：事件由 <see cref="Raise{T}"/> 接住。
    /// </summary>
    private void OnUpdateNoticeFlushed()
    {
        if (_toasts.TakeFlushedNotice() is not { } notice)
        {
            return;
        }

        _updateLastNotified = notice.Version;
        RaiseUpdateRecord();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SaveUpdateEtag(HttpCacheEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        try
        {
            _cache.PutCache(entry);
        }
        catch (Exception ex)
        {
            LogUpdateCheckException(ex);
        }
    }

    private void RaiseUpdateRecord() =>
        Raise(UpdateCheckRecorded, new UpdateCheckRecord(_updateLastCheckUtc, _updateLastNotified), "UpdateCheckRecorded 事件處理");

    /// <summary>限流與網路錯誤靜默，只有 api.log；其他失敗是未預期的，寫 error.log，同一種連續失敗只寫一次。</summary>
    private void LogUpdateCheckFailure(ApiResult<ReleaseInfo> result)
    {
        if (result.Outcome is ApiOutcome.RateLimited or ApiOutcome.NetworkError || result.Outcome == _updateLastLoggedOutcome)
        {
            return;
        }

        _updateLastLoggedOutcome = result.Outcome;
        _deps.ErrorLog.WriteMessage(UpdateCheckContext, EngineText.UpdateCheckFailedLog(result.Outcome.ToString(), result.StatusCode));
    }

    private void LogBadCurrentOnce()
    {
        if (_updateBadCurrentLogged)
        {
            return;
        }

        _updateBadCurrentLogged = true;
        _deps.ErrorLog.WriteMessage(UpdateCheckContext, EngineText.UpdateCheckBadCurrentLog(_deps.AppVersion));
    }

    /// <summary>cache.db 損毀要走重建（<see cref="ReportUnexpected"/>）；其他只寫 error.log，不跳橫幅，也不佔其他路徑跳橫幅的去重名額。</summary>
    private void LogUpdateCheckException(Exception exception)
    {
        if (IsDatabaseCorruption(exception))
        {
            ReportUnexpected(UpdateCheckContext, exception);
        }
        else
        {
            _deps.ErrorLog.WriteWithoutBanner(UpdateCheckContext, exception);
        }
    }
}
