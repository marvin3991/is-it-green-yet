using System.ComponentModel;
using IsItGreenYet.Core.Localization;

namespace IsItGreenYet.Core.Auth;

/// <summary>
/// 執行 <c>gh auth token --hostname github.com</c>（spec §13.1）。
/// 環境變數 GH_TOKEN／GITHUB_TOKEN 會蓋過 gh 自己存的憑證；<paramref name="ignoreEnvironment"/> 為 true 時從子行程移除。
/// </summary>
/// <remarks>
/// 判斷順序：沒啟動（找不到 gh）→ 逾時 → 退出碼非 0 → stdout 不是可用的 token。
/// stderr 由 <see cref="IProcessRunner"/> 讀掉丟棄，這裡完全看不到（新版 gh 可能在 stderr 印到期警告）。
/// 只用 gh 2.102.0 就有的旗標。
/// 取不到 token 的原因（<see cref="TokenResult.Problem"/>）依 <paramref name="language"/> 的語言；沒給時用共用的目前語言（<see cref="UiText.Current"/>）。
/// spec 沒有給逾時的文字，沿用「gh 沒有登入」會誤導，另外寫明「執行 gh 逾時」。
/// </remarks>
public sealed class GhCliTokenProvider(
    IProcessRunner runner,
    bool ignoreEnvironment,
    Func<string, string?> environment,
    Func<AppLanguage>? language = null) : ITokenProvider
{
    private const string GhFileName = "gh";

    /// <summary>gh 找到 token 時的退出碼；找不到 token 時是 1（spec §13.1）。</summary>
    private const int SuccessExitCode = 0;

    // gh 對 github.com 依序看 GH_TOKEN、GITHUB_TOKEN；有值就優先回傳（spec §13.1、Q32）。
    private const string GhTokenVariable = "GH_TOKEN";
    private const string GitHubTokenVariable = "GITHUB_TOKEN";

    private static readonly string[] Arguments = ["auth", "token", "--hostname", Tuning.WebHost];
    private static readonly string[] TokenVariables = [GhTokenVariable, GitHubTokenVariable];
    private static readonly string[] NoVariables = [];

    private readonly IProcessRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly bool _ignoreEnvironment = ignoreEnvironment;
    private readonly Func<string, string?> _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    private readonly Func<AppLanguage> _language = language ?? (() => UiText.Current);

    public async Task<TokenResult> GetTokenAsync(CancellationToken ct)
    {
        var remove = _ignoreEnvironment ? TokenVariables : NoVariables;
        var result = await _runner.RunAsync(GhFileName, Arguments, remove, Tuning.GhTimeout, ct).ConfigureAwait(false);
        var text = UiText.Of(_language());

        if (!result.Started)
        {
            return Failed(text.AuthGhNotInstalled);
        }

        // 逾時被結束的行程，stdout 就算已經有內容也不完整，不採用。
        if (result.TimedOut)
        {
            return Failed(text.AuthGhTimedOut);
        }

        if (result.ExitCode != SuccessExitCode)
        {
            return Failed(text.AuthGhNotLoggedIn);
        }

        // 退出碼 0 卻沒有可用的 token（空的、多行、夾著空白）：一樣當成沒有登入，不猜哪一行才是 token。
        var token = TokenText.Normalize(result.StandardOutput);
        if (token is null)
        {
            return Failed(text.AuthGhNotLoggedIn);
        }

        return new TokenResult(token, FromEnvironment() ? TokenSource.GhEnvironment : TokenSource.GhStored, Problem: null);
    }

    /// <summary>子行程看得到 GH_TOKEN 或 GITHUB_TOKEN（gh 的規則：空字串不算有值）。忽略環境變數時一律不是。</summary>
    private bool FromEnvironment() =>
        !_ignoreEnvironment && TokenVariables.Any(name => !string.IsNullOrEmpty(_environment(name)));

    private static TokenResult Failed(string problem) => new(Token: null, TokenSource.None, problem);
}

/// <summary>從 Windows 認證管理員讀 PAT（目標名稱 Tuning.CredentialTarget）。</summary>
/// <remarks>
/// <see cref="ICredentialStore.Read"/> 讀不到（ERROR_NOT_FOUND）回 null，視為「尚未設定 PAT」；
/// 其他讀取失敗（例如 ERROR_NO_SUCH_LOGON_SESSION）丟例外，這裡轉成明確的錯誤文字（spec §13.3、§15）。
/// spec §15「Credential Manager 讀寫失敗：顯示錯誤」沒有給文字，用 §13.3 的「認證管理員」稱呼。
/// 原因的語言同 <see cref="GhCliTokenProvider"/>。
/// </remarks>
public sealed class PatTokenProvider(ICredentialStore store, Func<AppLanguage>? language = null) : ITokenProvider
{
    private readonly ICredentialStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly Func<AppLanguage> _language = language ?? (() => UiText.Current);

    public Task<TokenResult> GetTokenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = UiText.Of(_language());

        string? stored;
        try
        {
            stored = _store.Read(Tuning.CredentialTarget);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(new TokenResult(Token: null, TokenSource.None, UiText.Format(text.AuthCredentialReadFailed, Describe(ex, text))));
        }

        var token = TokenText.Normalize(stored);
        return Task.FromResult(token is null
            ? new TokenResult(Token: null, TokenSource.None, text.AuthPatNotConfigured)
            : new TokenResult(token, TokenSource.Pat, Problem: null));
    }

    /// <summary>
    /// 系統錯誤用 Windows 的錯誤說明加錯誤碼；其他例外的訊息內容無法保證（可能夾帶讀到的內容），只寫型別名稱。
    /// </summary>
    private static string Describe(Exception ex, TextTable text) =>
        ex is Win32Exception win32 ? UiText.Format(text.CommonErrorCode, win32.Message, win32.NativeErrorCode) : ex.GetType().Name;
}

/// <summary>把 gh 的輸出或認證管理員讀到的文字，整理成可以放進 Authorization 標頭的 token。</summary>
internal static class TokenText
{
    // RFC 9110 §5.5：標頭值的可見字元 VCHAR（%x21–7E）。token 本身不會有空白、控制字元或非 ASCII 字元。
    private const char FirstVisible = '!';
    private const char LastVisible = '~';

    /// <returns>
    /// 去掉前後空白（含 gh 輸出結尾的換行，spec §13.1）後的 token；
    /// 是空的，或中間夾著空白、換行、控制字元、非 ASCII 字元時回 null。
    /// 這種值放進標頭會讓 HttpClient 丟出可能帶原文的例外，不如當成沒有可用的 token。
    /// </returns>
    public static string? Normalize(string? raw)
    {
        var token = raw?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        foreach (var c in token)
        {
            if (c < FirstVisible || c > LastVisible)
            {
                return null;
            }
        }

        return token;
    }
}
