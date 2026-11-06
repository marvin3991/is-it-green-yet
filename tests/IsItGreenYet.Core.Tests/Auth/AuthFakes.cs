using IsItGreenYet.Core.Auth;

namespace IsItGreenYet.Core.Tests.Auth;

/// <summary>一次 <see cref="IProcessRunner.RunAsync"/> 呼叫的參數（集合已複製，之後不受呼叫端影響）。</summary>
internal sealed record ProcessCall(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> RemovedVariables,
    TimeSpan Timeout);

/// <summary>假的 <see cref="IProcessRunner"/>：記錄每次呼叫並依序回傳排好的結果，不會啟動任何行程。</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<Func<ProcessResult>> _next = new();

    public List<ProcessCall> Calls { get; } = [];

    /// <summary>gh 正常結束、stdout 是 <paramref name="standardOutput"/>。</summary>
    public static ProcessResult Output(string standardOutput) =>
        new(Started: true, ExitCode: 0, standardOutput, TimedOut: false);

    public FakeProcessRunner Returns(ProcessResult result)
    {
        _next.Enqueue(() => result);
        return this;
    }

    public FakeProcessRunner Throws(Exception exception)
    {
        _next.Enqueue(() => throw exception);
        return this;
    }

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<string> removeEnvironmentVariables,
        TimeSpan timeout,
        CancellationToken ct)
    {
        Calls.Add(new ProcessCall(fileName, [.. arguments], [.. removeEnvironmentVariables], timeout));
        ct.ThrowIfCancellationRequested();

        if (!_next.TryDequeue(out var next))
        {
            throw new InvalidOperationException("測試沒有排好下一個行程結果。");
        }

        return Task.FromResult(next());
    }
}

/// <summary>假的 <see cref="ITokenProvider"/>：依序回傳排好的結果，可以讓某一次呼叫停住等測試放行。</summary>
internal sealed class FakeTokenProvider : ITokenProvider
{
    private readonly Queue<Func<Task<TokenResult>>> _next = new();
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>取得 token；<paramref name="token"/> 為 null 時當成「gh 沒有登入」。</summary>
    public FakeTokenProvider Returns(string? token, TokenSource source = TokenSource.GhStored) =>
        Returns(token is null
            ? new TokenResult(null, TokenSource.None, "gh 沒有登入")
            : new TokenResult(token, source, null));

    public FakeTokenProvider Returns(TokenResult result)
    {
        _next.Enqueue(() => Task.FromResult(result));
        return this;
    }

    /// <summary>這一次呼叫等 <paramref name="pending"/> 完成才回傳。</summary>
    public FakeTokenProvider Waits(Task<TokenResult> pending)
    {
        _next.Enqueue(() => pending);
        return this;
    }

    public FakeTokenProvider Throws(Exception exception)
    {
        _next.Enqueue(() => Task.FromException<TokenResult>(exception));
        return this;
    }

    public Task<TokenResult> GetTokenAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        ct.ThrowIfCancellationRequested();

        if (!_next.TryDequeue(out var next))
        {
            throw new InvalidOperationException("測試沒有排好下一個 token 結果。");
        }

        return next();
    }
}

/// <summary>假的認證管理員：只存在記憶體，不會讀寫這台電腦的 Windows 認證管理員。</summary>
internal sealed class FakeCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public List<string> ReadTargets { get; } = [];

    /// <summary>設定後每次 Read 都丟出這個例外。</summary>
    public Exception? ReadFailure { get; set; }

    public string? Read(string target)
    {
        ReadTargets.Add(target);
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        return _secrets.GetValueOrDefault(target);
    }

    public void Write(string target, string secret) => _secrets[target] = secret;

    public void Delete(string target) => _secrets.Remove(target);
}

/// <summary>只在 Windows 上執行的測試（要啟動 cmd.exe、ping.exe）。</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "只在 Windows 上執行";
        }
    }
}
