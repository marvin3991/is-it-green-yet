using IsItGreenYet.Core.Logging;

namespace IsItGreenYet.Core.Auth;

/// <summary>
/// 目前的 token 與認證狀態（spec §13.2）。收到 401 只重取一次 token、值改變才重送一次，
/// 避免短時間內連續送出無效憑證，讓 GitHub 暫時擋掉這個使用者所有的驗證。
/// </summary>
/// <remarks>
/// 狀態規則：
/// - 沒有 token 一律是 Unauthorized（gh 沒裝、沒登入、尚未設定 PAT，spec §15 第一列）。
/// - 初始化取得 token 後是 Unknown；呼叫端用 E1 驗證成功、呼叫 <see cref="MarkVerified"/> 才是 Ok。
///   收到 401 後換到新 token 時狀態不變，由那一次重送的結果決定。
/// - 收到 401 的 token 記為「被拒絕」；之後重取或重新檢查拿回同一個值都不算改變，不會再用它送一次。
/// - 輪詢迴圈與使用者操作兩條工作都可能收到 401（spec §3.1），同一時間只做一個重取；
///   另一條工作已經換上新 token 之後才到的舊 token 401，直接用新值重送，不再重取。
/// - 已經是 Unauthorized 時收到的 401 不重取、回 false；恢復只走 <see cref="RecheckAsync"/>＋E1（spec §13.2 第 3 點）。
/// token 只放在記憶體；每次取得 token 都交給 <see cref="SecretRedactor"/>（spec §13.4）。
/// </remarks>
public sealed class AuthManager(ITokenProvider provider, SecretRedactor redactor)
{
    private readonly ITokenProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    private readonly SecretRedactor _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));

    // 重取 token（執行 gh 最多 GhTimeout）期間，另一條工作要排隊，不同時重取。
    private readonly SemaphoreSlim _refresh = new(1, 1);

    // 寫入 _state 時用；讀取直接讀 volatile 欄位，一次拿到同一組值。
    private readonly Lock _stateGate = new();

    private volatile State _state = State.Initial;

    public AuthStatus Status => _state.Status;

    public string? Token => _state.Token;

    public TokenSource Source => _state.Source;

    public string? Problem => _state.Problem;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TokenResult result;
            try
            {
                result = await _provider.GetTokenAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // 取 token 本身失敗（含取消）：當成沒有 token，不沿用舊值。
                SetState(State.Initial with { Status = AuthStatus.Unauthorized });
                throw;
            }

            var token = Accept(result);
            SetState(token is null
                ? new State(AuthStatus.Unauthorized, Token: null, result.Source, result.Problem, Rejected: null)
                : new State(AuthStatus.Unknown, token, result.Source, Problem: null, Rejected: null));
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <returns>
    /// 重取到不同的 token 時回 true（呼叫端重送一次）；否則狀態變成 Unauthorized 並回 false。
    /// 已經是 Unauthorized 時不重取，直接回 false。
    /// </returns>
    /// <remarks>
    /// 把呼叫當下的 <see cref="Token"/> 當成收到 401 的 token。另一條工作可能已經先換過 token 時（spec §3.1 的兩條工作），
    /// 改用傳入送出時 token 的 <see cref="OnUnauthorizedAsync(string?, CancellationToken)"/>。
    /// </remarks>
    public Task<bool> OnUnauthorizedAsync(CancellationToken ct) => OnUnauthorizedAsync(_state.Token, ct);

    /// <param name="rejectedToken">送出收到 401 的那個請求時用的 token（送出前讀到的 <see cref="Token"/>）。</param>
    /// <returns>
    /// 要用目前的 <see cref="Token"/> 重送一次時回 true：重取到不同的值，或另一條工作已經換成新值；
    /// 否則狀態是 Unauthorized 並回 false（已經是 Unauthorized 時不重取）。
    /// </returns>
    /// <remarks>
    /// MonitorEngine 的輪詢與使用者操作兩條工作都用這個多載。另一條工作換到新 token 之後才到的 401，
    /// 只有靠送出時的 token 才分得出是舊值被拒（用新值重送，spec §13.2 第 1 點）還是新值重送仍被拒（未登入，第 2 點）。
    /// </remarks>
    internal async Task<bool> OnUnauthorizedAsync(string? rejectedToken, CancellationToken ct)
    {
        try
        {
            await _refresh.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 被取消的呼叫端不會再送出。排隊中代表另一條工作正在重取，狀態由它決定，這裡不改；
            // 沒人在重取時才由這裡進入未登入，不能再用收到 401 的 token 繼續送。
            RefuseIfIdle(rejectedToken);
            throw;
        }

        try
        {
            var waited = _state;
            if (waited.Status == AuthStatus.Unauthorized)
            {
                // 目前的狀態本來就是未登入（可能是排隊時另一條工作剛判定的）：恢復只交給 RecheckAsync＋E1（spec §13.2 第 3 點），
                // 不執行 gh，也不請呼叫端重送。在這裡換到的新值會被這次重送用掉，之後重新檢查看不出改變，狀態就停在未登入。
                // 收到 401 的就是目前的 token 時記為被拒（例如重新檢查換到的 token 用 E1 驗證仍 401）；舊 token 的 401 不改記下的值。
                Refuse(rejectedToken);
                return false;
            }

            if (waited.Token != rejectedToken && waited.Token is not null)
            {
                // 收到 401 的是已經換掉的 token（另一條工作重取過）：目前的 token 沒有被這次 401 否定，
                // 請呼叫端用它重送一次，不再執行 gh。
                return true;
            }

            // 以下是收到 401 的就是目前的 token（或還沒初始化、沒有任何 token）：重取。
            TokenResult result;
            try
            {
                result = await _provider.GetTokenAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // 重取失敗（含取消）也不能再用收到 401 的 token 繼續送（spec §13.2）。
                Refuse(rejectedToken);
                throw;
            }

            var token = Accept(result);
            lock (_stateGate)
            {
                var current = _state;
                var changed = token is not null && token != rejectedToken && token != current.Rejected;
                var refused = rejectedToken ?? current.Rejected;

                // 換到新值：狀態維持原樣，由這一次重送的結果決定；同一個值、被拒過的值或取不到：未登入（spec §13.2 第 2 點）。
                _state = changed
                    ? new State(current.Status, token, result.Source, Problem: null, refused)
                    : new State(AuthStatus.Unauthorized, token, result.Source, result.Problem, refused);
                return changed;
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>
    /// 用重取到的新 token 重送仍收到 401（spec §13.2 第 2 點）：不再重取 token，直接進入未登入，記下被拒的 token。
    /// 之後由 <see cref="RecheckAsync"/> 換到不同的值、再用 E1 驗證一次才恢復（第 3 點）。
    /// </summary>
    /// <param name="rejectedToken">重送時用的 token。</param>
    /// <remarks>
    /// 不能再呼叫 <see cref="OnUnauthorizedAsync(string?, CancellationToken)"/>：它在 gh 又給出不同的值時會回 true、維持原本的狀態，
    /// 呼叫端接著就會用一把還沒驗證過的 token 送出第三個請求。另一條工作已經換上別的 token 時不改狀態（那把還沒被否定）。
    /// </remarks>
    internal async Task RefuseAsync(string? rejectedToken, CancellationToken ct)
    {
        try
        {
            await _refresh.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            RefuseIfIdle(rejectedToken);
            throw;
        }

        try
        {
            Refuse(rejectedToken);
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <returns>未登入期間重取 token，值改變時回 true（呼叫端再用 E1 驗證）。</returns>
    /// <remarks>
    /// 只在本機重取 token，不打 API。值改變也不會直接恢復 Ok，要等 E1 驗證成功（spec §13.2 第 3 點）。
    /// 重取失敗（含取消）時狀態不變。
    /// </remarks>
    public async Task<bool> RecheckAsync(CancellationToken ct)
    {
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _provider.GetTokenAsync(ct).ConfigureAwait(false);
            var token = Accept(result);

            lock (_stateGate)
            {
                var current = _state;
                if (token is null)
                {
                    _state = new State(AuthStatus.Unauthorized, Token: null, result.Source, result.Problem, current.Rejected);
                    return false;
                }

                var changed = token != current.Token && token != current.Rejected;
                _state = current with { Token = token, Source = result.Source, Problem = null };
                return changed;
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>E1 驗證成功後呼叫。</summary>
    /// <remarks>沒有 token 時 E1 不可能成功，這次呼叫不改變狀態。</remarks>
    public void MarkVerified()
    {
        lock (_stateGate)
        {
            var current = _state;
            if (current.Token is null)
            {
                return;
            }

            _state = current with { Status = AuthStatus.Ok, Problem = null, Rejected = null };
        }
    }

    /// <summary>用 <paramref name="verifiedToken"/> 送出的 E1 驗證成功後呼叫：目前的 token 仍是那一把時才算驗證（同 <see cref="MarkVerified()"/>）。</summary>
    /// <returns>目前的 token 就是 <paramref name="verifiedToken"/>、已經標成 Ok 時回 true。</returns>
    /// <remarks>
    /// E1 還在路上時另一條工作可能換了 token（spec §3.1 的兩條工作，例如輪詢的「重新檢查」）：那次成功只證明舊 token 有效，
    /// 新 token 要自己用 E1 驗證（spec §13.2），所以這時不改狀態、回 false。比對與改狀態在同一個鎖裡。
    /// </remarks>
    internal bool MarkVerified(string? verifiedToken)
    {
        lock (_stateGate)
        {
            var current = _state;
            if (verifiedToken is null || current.Token != verifiedToken)
            {
                return false;
            }

            _state = current with { Status = AuthStatus.Ok, Problem = null, Rejected = null };
            return true;
        }
    }

    /// <summary>整理取到的 token，有可用的值就交給 redactor。</summary>
    /// <remarks>
    /// 取不到時不清掉 redactor 目前的 token：被拒的 token 可能還留在進行中的請求或例外訊息裡，要繼續遮。
    /// </remarks>
    private string? Accept(TokenResult result)
    {
        var token = TokenText.Normalize(result.Token);
        if (token is not null)
        {
            _redactor.SetActiveToken(token);
        }

        return token;
    }

    private void SetState(State state)
    {
        lock (_stateGate)
        {
            _state = state;
        }
    }

    /// <summary>沒有別的工作正在重取時，才由被取消的呼叫端進入未登入。</summary>
    private void RefuseIfIdle(string? rejected)
    {
        if (!_refresh.Wait(millisecondsTimeout: 0))
        {
            return;
        }

        try
        {
            Refuse(rejected);
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>收到 401 後沒能換到新 token：進入未登入，記下被拒的 token。</summary>
    /// <remarks>要在 <c>_refresh</c> 裡呼叫。目前的 token 已經不是被拒的那把（另一條工作換過）時不改狀態。</remarks>
    private void Refuse(string? rejected)
    {
        lock (_stateGate)
        {
            if (_state.Token != rejected)
            {
                return;
            }

            _state = _state with { Status = AuthStatus.Unauthorized, Rejected = rejected ?? _state.Rejected };
        }
    }

    /// <param name="Rejected">最近一次收到 401 的 token；重取或重新檢查拿回它時不算改變。</param>
    private sealed record State(AuthStatus Status, string? Token, TokenSource Source, string? Problem, string? Rejected)
    {
        public static readonly State Initial = new(AuthStatus.Unknown, Token: null, TokenSource.None, Problem: null, Rejected: null);

        // record 預設的文字表示會印出 token，改成只印狀態與來源。
        public override string ToString() =>
            $"{nameof(State)} {{ {nameof(Status)} = {Status}, {nameof(Source)} = {Source} }}";
    }
}
