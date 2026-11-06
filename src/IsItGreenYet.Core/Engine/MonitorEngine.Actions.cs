using System.Security.Cryptography;
using System.Text;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.GitHub;

namespace IsItGreenYet.Core.Engine;

/// <summary>
/// 寫入權限與每一列的可操作性（第二階段 spec §4、§5.3、§7）：token 的 scope、各 repo 的 push、被拒之後的停用、送出中的 run。
/// 使用者操作（寫入送出前再檢查一次）也會讀這些狀態，所以一律是 volatile 的不可變參考或有鎖保護。
/// </summary>
public sealed partial class MonitorEngine
{
    /// <summary><see cref="TryMarkInFlight"/> 回這個值代表這筆 run 已經有寫入在送出中；寫入的代號從 1 起算。</summary>
    private const long NoWriteCall = 0;

    /// <summary>送出中的 run、寫入代號與被拒的停用共用的鎖：使用者操作會寫、建立快照時會讀（第二階段 spec §5.3）。</summary>
    private readonly Lock _writeGate = new();

    /// <summary>送出中的 run：run id → 這次寫入的代號（<c>_writeGate</c>）。</summary>
    private readonly Dictionary<long, long> _inFlight = [];

    /// <summary>最後發出的寫入代號（<c>_writeGate</c>）。</summary>
    private long _lastWriteCallId;

    /// <summary>
    /// 收到「權限不足」的 403 而停用寫入的 repo：repo id → 送出時那把 token 的識別（<c>_writeGate</c>，第二階段 spec §4.3）。
    /// 只在記憶體；識別不是目前 token 的不算（token 改變就等於清除），按「檢查連線」時清空。
    /// </summary>
    private readonly Dictionary<long, string> _writeDenied = [];

    /// <summary>最近一次 API 回應的 X-OAuth-Scopes 判讀與送出用的 token 的識別（第二階段 spec §4.1）；還沒有任何回應時是 null。</summary>
    private volatile ScopeObservation? _scopes;

    /// <summary>
    /// 記憶體裡的 push 權限（第二階段 spec §4.1），內容和 repo_permissions 表裡這個帳號的資料列相同；整份替換、不改內容。
    /// 不知道帳號，或讀 DB 失敗時是 null（全部不確定）。
    /// </summary>
    private volatile PushPermissions? _pushPermissions;

    /// <summary>
    /// 第二階段 spec §7.2 的共通條件（寫入功能的開關與送出中另外處理），依序：缺少權限、未登入、版本錯誤、離線、限流暫停。
    /// 自動更新的「暫停」不擋（§5.4）。只讀 volatile 或有鎖的狀態，使用者操作也可以呼叫。
    /// </summary>
    private ActionBlock GlobalBlock(long repoId)
    {
        var token = _auth.Token;
        if (MissingPermission(repoId, TokenIdentity.Of(token)))
        {
            return ActionBlock.MissingPermission;
        }

        if (token is null || _auth.Status == AuthStatus.Unauthorized)
        {
            return ActionBlock.NotLoggedIn;
        }

        if (_versionError)
        {
            return ActionBlock.VersionError;
        }

        if (_offline.IsOffline)
        {
            return ActionBlock.Offline;
        }

        return _throttle.IsPaused ? ActionBlock.RateLimitPaused : ActionBlock.None;
    }

    /// <summary>§4 事先停用的三種情況：scope 不夠、這個 repo 沒有寫入權、這個 repo 的寫入已被拒。</summary>
    /// <param name="tokenId">目前 token 的識別；沒有 token 時是 null，scope 與被拒的停用都不算。</param>
    private bool MissingPermission(long repoId, string? tokenId)
    {
        // scope：最近一次回應是用目前的 token 送出的，header 有值而且不含 repo。用別的 token 記下的不算（換 token 就等於清掉）。
        if (tokenId is not null && _scopes is { LacksRepoScope: true } scopes && string.Equals(scopes.TokenId, tokenId, StringComparison.Ordinal))
        {
            return true;
        }

        // push：表是目前帳號的才算；別的帳號讀到的值一律不確定。
        if (_pushPermissions is { } push
            && _account is { } account
            && push.UserId == account.UserId
            && push.CanPush.TryGetValue(repoId, out var canPush)
            && !canPush)
        {
            return true;
        }

        if (tokenId is null)
        {
            return false;
        }

        lock (_writeGate)
        {
            return _writeDenied.TryGetValue(repoId, out var deniedWith) && string.Equals(deniedWith, tokenId, StringComparison.Ordinal);
        }
    }

    /// <summary>建立快照時這筆 run 的寫入狀態：送出中、待確認的重跑、取消中與強制取消的等待（第二階段 spec §5.3、§6.2～§6.4）。</summary>
    /// <remarks>
    /// 已經套用了結果、等這份快照發布就移除的送出中（<see cref="_releaseOnPublish"/>）當成不在送出中：
    /// 這份快照發布之後才真的移除（<see cref="PublishSnapshot"/>）。
    /// 待確認的重跑與取消中看操作後追蹤（MonitorEngine.PostAction.cs）：記下它們的 effect 和排進「發布時釋放」是同一個（OnWriteAnswered），
    /// 所以移除送出中的那一份快照一定已經是待確認的重跑或取消中（spec §5.3 第 2 點）。公開的查表與 run 狀態查表都用這一份 context，
    /// 寫入的鎖內再檢查看得到待確認的重跑與已送出取消。
    /// </remarks>
    private ActionContext ActionContextFor(long runId, ActionBlock globalBlock)
    {
        var cancelling = _cancels.GetValueOrDefault(runId);
        return new(globalBlock, IsInFlight(runId, exceptCallId: _releaseOnPublish.GetValueOrDefault(runId, NoWriteCall)),
            RerunPending: HasPendingRerun(runId),
            Cancelling: cancelling?.Info,
            CancelGraceElapsed: cancelling is not null && CancelGraceElapsed(cancelling));
    }

    /// <summary>
    /// 這筆 run 有待確認的重跑（第二階段 spec §6.2）：這次執行已受理或結果不確定的重跑（被拒的不算），
    /// 以及重新啟動時從 pending_reruns 讀回的；看到新的 attempt、放棄、E6 回 404、repo 停止監看或被移除時就不再是。步驟專用。
    /// </summary>
    private bool HasPendingRerun(long runId) => _reruns.ContainsKey(runId);

    // ── scope（第二階段 spec §4.1）────────────────────────────────────

    /// <summary>
    /// 收到 API 回應時記下 X-OAuth-Scopes，連同送出用的 token 的識別；以最近一次的回應為準，不多送請求。
    /// 連線層失敗（沒有回應）不記。輪詢與使用者操作都會呼叫。
    /// </summary>
    private void RecordScopes<T>(ApiResult<T> result, string? token)
    {
        if (result.Outcome == ApiOutcome.NetworkError || result.StatusCode == NoResponse)
        {
            return;
        }

        _scopes = new ScopeObservation(TokenIdentity.Of(token), LacksRepoScope(result.OAuthScopes));
    }

    /// <summary>
    /// X-OAuth-Scopes 有值但不含 repo（逗號分隔、去掉空白、不分大小寫）。header 不存在或值是空的（fine-grained token）是不確定，回 false。
    /// 只有 public_repo 也算不夠（第二階段 spec §4.1 的推測）。
    /// </summary>
    private static bool LacksRepoScope(string? scopes)
    {
        var entries = (scopes ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return entries.Length > 0 && !entries.Contains(RepoScope, StringComparer.OrdinalIgnoreCase);
    }

    // ── push（第二階段 spec §4.1）─────────────────────────────────────

    /// <summary>讀這個帳號存過的 push（換到另一個帳號時）。讀不到時全部當成不確定，下一次處理 repo 清單時再讀。</summary>
    private void ReloadPushPermissions(long userId)
    {
        try
        {
            _pushPermissions = new PushPermissions(userId, _cache.GetRepoPermissions(userId));
        }
        catch (Exception ex)
        {
            _pushPermissions = null;
            ReportUnexpected("讀取寫入權限", ex);
        }
    }

    /// <summary>
    /// repo 清單的權限已經和清單同一個交易寫進 DB（<see cref="CacheRepository.UpsertRepos"/>）之後，同樣的值記進記憶體：
    /// 不確定（null）的不寫、不蓋掉之前的值，同 DB 的規則。表不是這個帳號的（或還沒讀到）就整份從 DB 重讀，和 DB 保持一致。
    /// </summary>
    private void RememberPushPermissions(RepoPermissionsUpdate? update)
    {
        if (update is null)
        {
            return;
        }

        var current = _pushPermissions;
        if (current is null || current.UserId != update.UserId)
        {
            ReloadPushPermissions(update.UserId);
            return;
        }

        Dictionary<long, bool>? merged = null;
        foreach (var (repoId, canPush) in update.CanPush)
        {
            if (canPush is not { } value || (current.CanPush.TryGetValue(repoId, out var known) && known == value))
            {
                continue;
            }

            merged ??= new Dictionary<long, bool>(current.CanPush);
            merged[repoId] = value;
        }

        if (merged is not null)
        {
            _pushPermissions = current with { CanPush = merged };
        }
    }

    /// <summary>repo 被移除時 DB 的資料列一起刪掉（<see cref="CacheRepository.DeleteRepo"/>），記憶體也拿掉。</summary>
    private void ForgetPushPermission(long repoId)
    {
        if (_pushPermissions is not { } current || !current.CanPush.ContainsKey(repoId))
        {
            return;
        }

        var remaining = new Dictionary<long, bool>(current.CanPush);
        remaining.Remove(repoId);
        _pushPermissions = current with { CanPush = remaining };
    }

    // ── 被拒之後的停用（第二階段 spec §4.3）────────────────────────────

    /// <summary>
    /// 寫入收到「權限不足」的 403：停用這個 repo 的寫入，記下送出時那把 token 的識別。送出期間換了 token 時，
    /// 識別和目前的 token 不同，讀的時候就不算（§4.3 第 2 點）。寫入的送出路徑呼叫；測試也直接呼叫。
    /// </summary>
    internal void MarkWriteDenied(long repoId, string? sentToken)
    {
        if (TokenIdentity.Of(sentToken) is not { } identity)
        {
            return;
        }

        lock (_writeGate)
        {
            _writeDenied[repoId] = identity;
        }
    }

    /// <summary>設定頁按「檢查連線」：清除所有被拒的停用（fine-grained PAT 的權限可能在網頁上改過，token 字串不變）。</summary>
    private void ClearWriteDenied()
    {
        lock (_writeGate)
        {
            _writeDenied.Clear();
        }
    }

    // ── 送出中（第二階段 spec §5.3）───────────────────────────────────

    /// <returns>這筆 run 已經有寫入在送出中時回 <see cref="NoWriteCall"/>；否則記下並回這次寫入的新代號。</returns>
    private long TryMarkInFlight(long runId)
    {
        lock (_writeGate)
        {
            if (_inFlight.ContainsKey(runId))
            {
                return NoWriteCall;
            }

            var callId = ++_lastWriteCallId;
            _inFlight[runId] = callId;
            return callId;
        }
    }

    /// <summary>只移除這次寫入自己記下的那一筆（代號相同）。</summary>
    private void ReleaseInFlight(long runId, long callId)
    {
        lock (_writeGate)
        {
            if (_inFlight.TryGetValue(runId, out var current) && current == callId)
            {
                _inFlight.Remove(runId);
            }
        }
    }

    /// <param name="exceptCallId">在鎖內重新檢查時排除自己這一筆；<see cref="NoWriteCall"/> 是不排除。</param>
    private bool IsInFlight(long runId, long exceptCallId = NoWriteCall)
    {
        lock (_writeGate)
        {
            return _inFlight.TryGetValue(runId, out var callId) && callId != exceptCallId;
        }
    }

    /// <param name="TokenId">送出這個請求的 token 的識別（<see cref="TokenIdentity"/>）；沒有 token 時是 null。</param>
    /// <param name="LacksRepoScope">X-OAuth-Scopes 有值但不含 repo；header 不存在或值是空的（不確定）是 false。</param>
    private sealed record ScopeObservation(string? TokenId, bool LacksRepoScope);

    /// <param name="UserId">讀到這些值的帳號；不是目前的帳號時整份當成不確定。</param>
    /// <param name="CanPush">讀到值的 repo；不在表裡的是不確定。</param>
    private sealed record PushPermissions(long UserId, IReadOnlyDictionary<long, bool> CanPush);
}

/// <summary>
/// 分辨「是不是同一把 token」用的識別（第二階段 spec §4.1）：token 的 SHA-256 十六進位。
/// 只拿來比較，不保存 token 原值，也不會從識別還原出 token。
/// </summary>
internal static class TokenIdentity
{
    /// <returns>沒有 token（null 或空字串）時回 null。</returns>
    public static string? Of(string? token) =>
        string.IsNullOrEmpty(token) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
