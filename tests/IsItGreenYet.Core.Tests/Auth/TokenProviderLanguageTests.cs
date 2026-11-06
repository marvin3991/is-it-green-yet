using System.ComponentModel;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Localization;

namespace IsItGreenYet.Core.Tests.Auth;

/// <summary>取不到 token 的原因（spec §13.1、§13.3）依傳入的語言；沒傳時用共用的目前語言（測試組件是繁體中文）。</summary>
public class TokenProviderLanguageTests
{
    private static readonly Func<AppLanguage> English = () => AppLanguage.English;

    [Fact]
    public async Task gh的原因是英文()
    {
        var notInstalled = new FakeProcessRunner().Returns(new ProcessResult(Started: false, ExitCode: -1, StandardOutput: "", TimedOut: false));
        var notLoggedIn = new FakeProcessRunner().Returns(new ProcessResult(Started: true, ExitCode: 1, StandardOutput: "", TimedOut: false));
        var timedOut = new FakeProcessRunner().Returns(new ProcessResult(Started: true, ExitCode: -1, StandardOutput: "", TimedOut: true));

        Assert.Equal("gh isn't installed", (await Gh(notInstalled, English).GetTokenAsync(CancellationToken.None)).Problem);
        Assert.Equal("gh isn't signed in", (await Gh(notLoggedIn, English).GetTokenAsync(CancellationToken.None)).Problem);
        Assert.Equal("gh timed out", (await Gh(timedOut, English).GetTokenAsync(CancellationToken.None)).Problem);
    }

    [Fact]
    public async Task 沒傳語言時用共用的目前語言()
    {
        var runner = new FakeProcessRunner().Returns(new ProcessResult(Started: false, ExitCode: -1, StandardOutput: "", TimedOut: false));

        Assert.Equal("沒有安裝 gh", (await Gh(runner, language: null).GetTokenAsync(CancellationToken.None)).Problem);
    }

    [Fact]
    public async Task 每次取token時才讀語言()
    {
        // App 的設定頁換語言後，下一次取 token 就用新的語言（不必重建 provider）。
        var language = AppLanguage.TraditionalChinese;
        var store = new FakeCredentialStore();
        var provider = new PatTokenProvider(store, () => language);

        Assert.Equal("尚未設定 PAT", (await provider.GetTokenAsync(CancellationToken.None)).Problem);
        language = AppLanguage.English;
        Assert.Equal("No PAT saved", (await provider.GetTokenAsync(CancellationToken.None)).Problem);
    }

    [Fact]
    public async Task 認證管理員讀取失敗的英文說明帶錯誤碼()
    {
        const int errorNoSuchLogonSession = 1312;
        var store = new FakeCredentialStore { ReadFailure = new Win32Exception(errorNoSuchLogonSession) };

        var result = await new PatTokenProvider(store, English).GetTokenAsync(CancellationToken.None);

        Assert.NotNull(result.Problem);
        Assert.StartsWith("Couldn't read Windows Credential Manager: ", result.Problem, StringComparison.Ordinal);
        Assert.EndsWith("(error code 1312)", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 非系統例外只寫型別名稱()
    {
        var store = new FakeCredentialStore { ReadFailure = new InvalidOperationException("not shown") };

        var result = await new PatTokenProvider(store, English).GetTokenAsync(CancellationToken.None);

        Assert.Equal("Couldn't read Windows Credential Manager: InvalidOperationException", result.Problem);
    }

    private static GhCliTokenProvider Gh(FakeProcessRunner runner, Func<AppLanguage>? language) =>
        new(runner, ignoreEnvironment: false, _ => null, language);
}
