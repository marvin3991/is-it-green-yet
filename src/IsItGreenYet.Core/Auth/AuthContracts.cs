namespace IsItGreenYet.Core.Auth;

public sealed record ProcessResult(bool Started, int ExitCode, string StandardOutput, bool TimedOut)
{
    // gh 的 stdout 就是 token；record 預設的文字表示會整段印出（例如被寫進 log 或例外訊息），改成只標示有沒有內容（spec §13.4）。
    public override string ToString() =>
        $"{nameof(ProcessResult)} {{ {nameof(Started)} = {Started}, {nameof(ExitCode)} = {ExitCode}, " +
        $"{nameof(StandardOutput)} = {SecretText.Mask(StandardOutput)}, {nameof(TimedOut)} = {TimedOut} }}";
}

/// <summary>執行外部程式（gh）。測試用假的實作取代。</summary>
public interface IProcessRunner
{
    /// <param name="removeEnvironmentVariables">子行程環境中要移除的變數（Q32 忽略 GH_TOKEN／GITHUB_TOKEN）。</param>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<string> removeEnvironmentVariables,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>Windows 認證管理員；App 端提供 P/Invoke 實作。</summary>
public interface ICredentialStore
{
    string? Read(string target);

    void Write(string target, string secret);

    void Delete(string target);
}

public enum TokenSource
{
    None,
    GhStored,
    GhEnvironment,
    Pat,
}

/// <param name="Problem">
/// 取不到 token 時給使用者看的原因：「沒有安裝 gh」「gh 沒有登入」「尚未設定 PAT」，
/// 另有「執行 gh 逾時」與「讀取 Windows 認證管理員失敗：…」。
/// </param>
public sealed record TokenResult(string? Token, TokenSource Source, string? Problem)
{
    // record 預設的文字表示會印出 token 原文，改成遮罩（spec §13.4）。
    public override string ToString() =>
        $"{nameof(TokenResult)} {{ {nameof(Token)} = {SecretText.Mask(Token)}, {nameof(Source)} = {Source}, {nameof(Problem)} = {Problem} }}";
}

public interface ITokenProvider
{
    Task<TokenResult> GetTokenAsync(CancellationToken ct);
}

public enum AuthStatus
{
    Unknown,
    Ok,
    Unauthorized,
}

/// <summary>可能含 token 的值，在文字表示裡的寫法。</summary>
internal static class SecretText
{
    /// <summary>和 SecretRedactor 相同的遮罩字串。</summary>
    private const string Masked = "***";

    /// <summary>有內容就寫成 ***；null 或空字串照 record 預設寫成空白，看得出「沒有值」。</summary>
    public static string Mask(string? value) => string.IsNullOrEmpty(value) ? string.Empty : Masked;
}
