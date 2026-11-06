using System.ComponentModel;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Logging;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Auth;

/// <summary>
/// gh／PAT 取 token 與 401 流程（spec §13）。全部用假的行程執行器、假的認證管理員，
/// 不會執行真正的 gh，也不會讀這台電腦的 Windows 認證管理員。
/// </summary>
public class AuthTests
{
    /// <summary>換發後的第二把假 token：<see cref="Fixture.FakeToken"/> 後面多一碼。</summary>
    private const string OtherFakeToken = Fixture.FakeToken + "1";

    /// <summary>第三把假 token：<see cref="Fixture.FakeToken"/> 後面多一碼。</summary>
    private const string ThirdFakeToken = Fixture.FakeToken + "2";

    /// <summary>不符合任何 token 樣式的假 token，確認 redactor 是靠精確比對遮掉，不是靠樣式。</summary>
    private const string PlainTokenA = "tok_TESTTOKEN00000000000000000000000000000A";

    private const string PlainTokenB = "tok_TESTTOKEN00000000000000000000000000000B";

    /// <summary>spec §13.1：<c>gh auth token --hostname github.com</c>。</summary>
    private static readonly string[] GhArguments = ["auth", "token", "--hostname", "github.com"];

    private static readonly string[] TokenVariables = ["GH_TOKEN", "GITHUB_TOKEN"];

    // ── GhCliTokenProvider ───────────────────────────────────────────

    [Fact]
    public async Task gh沒安裝()
    {
        var runner = new FakeProcessRunner()
            .Returns(new ProcessResult(Started: false, ExitCode: -1, StandardOutput: "", TimedOut: false));

        var result = await GhProvider(runner).GetTokenAsync(CancellationToken.None);

        Assert.Null(result.Token);
        Assert.Equal(TokenSource.None, result.Source);
        Assert.Equal("沒有安裝 gh", result.Problem);
    }

    [Fact]
    public async Task gh退出碼1算沒登入()
    {
        var runner = new FakeProcessRunner()
            .Returns(new ProcessResult(Started: true, ExitCode: 1, StandardOutput: "", TimedOut: false));

        var result = await GhProvider(runner).GetTokenAsync(CancellationToken.None);

        Assert.Null(result.Token);
        Assert.Equal(TokenSource.None, result.Source);
        Assert.Equal("gh 沒有登入", result.Problem);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task gh其他非0退出碼也算沒登入且不採用輸出(int exitCode)
    {
        // 退出碼非 0 時，就算 stdout 剛好有像 token 的文字也不採用。
        var runner = new FakeProcessRunner()
            .Returns(new ProcessResult(Started: true, exitCode, Fixture.FakeToken + "\n", TimedOut: false));

        var result = await GhProvider(runner).GetTokenAsync(CancellationToken.None);

        Assert.Null(result.Token);
        Assert.Equal("gh 沒有登入", result.Problem);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("")]
    public async Task 正常取得並去掉結尾換行(string lineEnding)
    {
        var runner = new FakeProcessRunner().Returns(FakeProcessRunner.Output(Fixture.FakeToken + lineEnding));

        var result = await GhProvider(runner).GetTokenAsync(CancellationToken.None);

        Assert.Equal(Fixture.FakeToken, result.Token);
        Assert.Equal(TokenSource.GhStored, result.Source);
        Assert.Null(result.Problem);
    }

    [Fact]
    public async Task 用固定參數執行gh並套用GhTimeout()
    {
        var runner = new FakeProcessRunner().Returns(FakeProcessRunner.Output(Fixture.FakeToken + "\n"));

        await GhProvider(runner).GetTokenAsync(CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("gh", call.FileName);
        Assert.Equal(GhArguments, call.Arguments);
        // spec 附錄 B：GhTimeout 10 秒。
        Assert.Equal(TimeSpan.FromSeconds(10), call.Timeout);
        Assert.Equal(Tuning.GhTimeout, call.Timeout);
        // 沒有開「忽略環境變數」時照 gh 的規則，不動子行程的環境（Q32）。
        Assert.Empty(call.RemovedVariables);
    }

    [Fact]
    public async Task 逾時算失敗()
    {
        // 逾時被結束的行程，就算 stdout 已經寫出 token、退出碼看起來是 0，也不採用。
        var runner = new FakeProcessRunner()
            .Returns(new ProcessResult(Started: true, ExitCode: 0, Fixture.FakeToken + "\n", TimedOut: true));

        var result = await GhProvider(runner).GetTokenAsync(CancellationToken.None);

        Assert.Null(result.Token);
        Assert.Equal(TokenSource.None, result.Source);
        Assert.Equal("執行 gh 逾時", result.Problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("   \n")]
    public async Task 退出碼0但沒有輸出算沒登入(string output)
    {
        var runner = new FakeProcessRunner().Returns(FakeProcessRunner.Output(output));

        var result = await GhProvider(runner).GetTokenAsync(CancellationToken.None);

        Assert.Null(result.Token);
        Assert.Equal(TokenSource.None, result.Source);
        Assert.Equal("gh 沒有登入", result.Problem);
    }

    [Theory]
    [InlineData(Fixture.FakeToken + "\n" + OtherFakeToken + "\n")]
    [InlineData("warning: token expires soon\n" + Fixture.FakeToken + "\n")]
    [InlineData(Fixture.FakeToken + " " + Fixture.FakeToken + "\n")]
    [InlineData(Fixture.FakeToken + "\t" + Fixture.FakeToken + "\n")]
    [InlineData(Fixture.FakeToken + "測試\n")]
    public async Task 輸出夾著換行空白或非ASCII字元時不採用(string output)
    {
        // 這種值放進 Authorization 標頭會讓 HttpClient 丟出可能帶原文的例外，不如直接當成沒有可用的 token。
        var runner = new FakeProcessRunner().Returns(FakeProcessRunner.Output(output));

        var result = await GhProvider(runner).GetTokenAsync(CancellationToken.None);

        Assert.Null(result.Token);
        Assert.Equal("gh 沒有登入", result.Problem);
        Assert.DoesNotContain(Fixture.FakeToken, result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GH_TOKEN")]
    [InlineData("GITHUB_TOKEN")]
    public async Task 偵測到GH_TOKEN標示來源(string variable)
    {
        var runner = new FakeProcessRunner().Returns(FakeProcessRunner.Output(Fixture.FakeToken + "\n"));
        var provider = new GhCliTokenProvider(runner, ignoreEnvironment: false, EnvironmentWith(variable, Fixture.FakeToken));

        var result = await provider.GetTokenAsync(CancellationToken.None);

        Assert.Equal(Fixture.FakeToken, result.Token);
        Assert.Equal(TokenSource.GhEnvironment, result.Source);
        Assert.Null(result.Problem);
        Assert.Empty(Assert.Single(runner.Calls).RemovedVariables);
    }

    [Fact]
    public async Task 空字串的環境變數不算有值()
    {
        // gh 的規則：變數值是空字串時當成沒有設定，改用 gh 自己存的憑證。
        var runner = new FakeProcessRunner().Returns(FakeProcessRunner.Output(Fixture.FakeToken + "\n"));
        var provider = new GhCliTokenProvider(runner, ignoreEnvironment: false, EnvironmentWith("GH_TOKEN", ""));

        var result = await provider.GetTokenAsync(CancellationToken.None);

        Assert.Equal(TokenSource.GhStored, result.Source);
    }

    [Fact]
    public async Task 忽略環境變數時要求移除兩個變數()
    {
        var runner = new FakeProcessRunner().Returns(FakeProcessRunner.Output(OtherFakeToken + "\n"));
        var environment = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = Fixture.FakeToken,
            ["GITHUB_TOKEN"] = Fixture.FakeToken,
        };
        var provider = new GhCliTokenProvider(runner, ignoreEnvironment: true, name => environment.GetValueOrDefault(name));

        var result = await provider.GetTokenAsync(CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(TokenVariables.Order(StringComparer.Ordinal), call.RemovedVariables.Order(StringComparer.Ordinal));
        // 子行程看不到環境變數，拿到的是 gh 自己存的憑證，來源不能標成環境變數。
        Assert.Equal(OtherFakeToken, result.Token);
        Assert.Equal(TokenSource.GhStored, result.Source);
    }

    [Fact]
    public async Task 取消時往外丟()
    {
        var runner = new FakeProcessRunner().Returns(FakeProcessRunner.Output(Fixture.FakeToken + "\n"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GhProvider(runner).GetTokenAsync(cts.Token));
    }

    // ── PatTokenProvider ─────────────────────────────────────────────

    [Fact]
    public async Task PAT讀不到算尚未設定()
    {
        var store = new FakeCredentialStore();

        var result = await new PatTokenProvider(store).GetTokenAsync(CancellationToken.None);

        Assert.Null(result.Token);
        Assert.Equal(TokenSource.None, result.Source);
        Assert.Equal("尚未設定 PAT", result.Problem);
        // spec §13.3：目標名稱 IsItGreenYet:github.com。
        Assert.Equal(["IsItGreenYet:github.com"], store.ReadTargets);
        Assert.Equal(Tuning.CredentialTarget, Assert.Single(store.ReadTargets));
    }

    [Fact]
    public async Task 讀到PAT標示來源()
    {
        var store = new FakeCredentialStore();
        store.Write(Tuning.CredentialTarget, Fixture.FakeToken);

        var result = await new PatTokenProvider(store).GetTokenAsync(CancellationToken.None);

        Assert.Equal(Fixture.FakeToken, result.Token);
        Assert.Equal(TokenSource.Pat, result.Source);
        Assert.Null(result.Problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public async Task 空白PAT算尚未設定(string stored)
    {
        var store = new FakeCredentialStore();
        store.Write(Tuning.CredentialTarget, stored);

        var result = await new PatTokenProvider(store).GetTokenAsync(CancellationToken.None);

        Assert.Null(result.Token);
        Assert.Equal("尚未設定 PAT", result.Problem);
    }

    [Fact]
    public async Task PAT前後空白會去掉()
    {
        var store = new FakeCredentialStore();
        store.Write(Tuning.CredentialTarget, $"  {Fixture.FakeToken}\r\n");

        var result = await new PatTokenProvider(store).GetTokenAsync(CancellationToken.None);

        Assert.Equal(Fixture.FakeToken, result.Token);
    }

    [Fact]
    public async Task 認證管理員讀取失敗時顯示明確的錯誤()
    {
        // spec §13.3：ERROR_NO_SUCH_LOGON_SESSION（1312）要顯示明確的錯誤，不能當成「尚未設定 PAT」。
        const int errorNoSuchLogonSession = 1312;
        var store = new FakeCredentialStore { ReadFailure = new Win32Exception(errorNoSuchLogonSession) };

        var result = await new PatTokenProvider(store).GetTokenAsync(CancellationToken.None);

        Assert.Null(result.Token);
        Assert.Equal(TokenSource.None, result.Source);
        Assert.NotNull(result.Problem);
        Assert.StartsWith("讀取 Windows 認證管理員失敗：", result.Problem, StringComparison.Ordinal);
        Assert.Contains("1312", result.Problem, StringComparison.Ordinal);
        Assert.NotEqual("尚未設定 PAT", result.Problem);
    }

    [Fact]
    public async Task 認證管理員的非系統例外訊息不帶進Problem()
    {
        // 自訂例外的訊息內容無法保證，只寫例外型別。
        var store = new FakeCredentialStore { ReadFailure = new InvalidOperationException($"壞掉了 {Fixture.FakeToken}") };

        var result = await new PatTokenProvider(store).GetTokenAsync(CancellationToken.None);

        Assert.Equal("讀取 Windows 認證管理員失敗：InvalidOperationException", result.Problem);
    }

    // ── AuthManager ──────────────────────────────────────────────────

    [Fact]
    public async Task 初始化取不到token就是未登入()
    {
        var provider = new FakeTokenProvider().Returns(new TokenResult(null, TokenSource.None, "沒有安裝 gh"));
        var auth = new AuthManager(provider, new SecretRedactor());

        await auth.InitializeAsync(CancellationToken.None);

        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
        Assert.Null(auth.Token);
        Assert.Equal(TokenSource.None, auth.Source);
        Assert.Equal("沒有安裝 gh", auth.Problem);
    }

    [Fact]
    public async Task 初始化取得token後等E1驗證才算Ok()
    {
        var provider = new FakeTokenProvider().Returns(Fixture.FakeToken, TokenSource.GhEnvironment);
        var auth = new AuthManager(provider, new SecretRedactor());

        Assert.Equal(AuthStatus.Unknown, auth.Status);
        await auth.InitializeAsync(CancellationToken.None);

        Assert.Equal(AuthStatus.Unknown, auth.Status);
        Assert.Equal(Fixture.FakeToken, auth.Token);
        Assert.Equal(TokenSource.GhEnvironment, auth.Source);
        Assert.Null(auth.Problem);

        auth.MarkVerified();

        Assert.Equal(AuthStatus.Ok, auth.Status);
    }

    [Fact]
    public async Task 沒有token時MarkVerified不會變成Ok()
    {
        var provider = new FakeTokenProvider().Returns(token: null);
        var auth = new AuthManager(provider, new SecretRedactor());
        await auth.InitializeAsync(CancellationToken.None);

        auth.MarkVerified();

        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
    }

    [Fact]
    public async Task 用已經換掉的token驗證成功時不算驗證了目前的token()
    {
        // E1 還在路上時另一條工作換了 token：那次成功只證明舊 token 有效，新 token 仍要自己用 E1 驗證。
        var provider = new FakeTokenProvider().Returns(Fixture.FakeToken).Returns(OtherFakeToken);
        var auth = new AuthManager(provider, new SecretRedactor());
        await auth.InitializeAsync(CancellationToken.None);
        Assert.True(await auth.RecheckAsync(CancellationToken.None));

        Assert.False(auth.MarkVerified(Fixture.FakeToken));
        Assert.Equal(AuthStatus.Unknown, auth.Status);

        Assert.True(auth.MarkVerified(OtherFakeToken));
        Assert.Equal(AuthStatus.Ok, auth.Status);
    }

    [Fact]
    public async Task 初始化丟例外時當成沒有token()
    {
        var provider = new FakeTokenProvider().Throws(new InvalidOperationException("壞掉了"));
        var auth = new AuthManager(provider, new SecretRedactor());

        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.InitializeAsync(CancellationToken.None));

        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
        Assert.Null(auth.Token);
    }

    [Fact]
    public async Task _401後token不同回true()
    {
        var provider = new FakeTokenProvider().Returns(Fixture.FakeToken).Returns(OtherFakeToken);
        var auth = await VerifiedAuth(provider);

        var resend = await auth.OnUnauthorizedAsync(CancellationToken.None);

        Assert.True(resend);
        Assert.Equal(OtherFakeToken, auth.Token);
        Assert.Equal(AuthStatus.Ok, auth.Status);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task _401後token相同回false且狀態未登入()
    {
        var provider = new FakeTokenProvider()
            .Returns(Fixture.FakeToken, TokenSource.GhEnvironment)
            .Returns(Fixture.FakeToken, TokenSource.GhEnvironment);
        var auth = await VerifiedAuth(provider);

        var resend = await auth.OnUnauthorizedAsync(CancellationToken.None);

        Assert.False(resend);
        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
        Assert.Equal(Fixture.FakeToken, auth.Token);
        // 401 時的橫幅要依來源改文字（spec §10.2），來源要保留。
        Assert.Equal(TokenSource.GhEnvironment, auth.Source);
    }

    [Fact]
    public async Task 重送仍401就進入未登入()
    {
        // spec §13.2：用新值重送一次仍 401，就不再重試。
        var provider = new FakeTokenProvider()
            .Returns(Fixture.FakeToken)
            .Returns(OtherFakeToken)
            .Returns(OtherFakeToken);
        var auth = await VerifiedAuth(provider);

        Assert.True(await auth.OnUnauthorizedAsync(CancellationToken.None));
        Assert.False(await auth.OnUnauthorizedAsync(CancellationToken.None));

        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
    }

    [Fact]
    public async Task 重送仍401時RefuseAsync直接進入未登入不再重取token()
    {
        // spec §13.2 第 2 點：用新值重送仍 401 就進入未登入；就算 gh 這時又能給出第三把，也不能接著送一把還沒驗證過的憑證。
        var provider = new FakeTokenProvider()
            .Returns(Fixture.FakeToken)
            .Returns(OtherFakeToken)
            .Returns(ThirdFakeToken);
        var auth = await VerifiedAuth(provider);
        Assert.True(await auth.OnUnauthorizedAsync(Fixture.FakeToken, CancellationToken.None));

        await auth.RefuseAsync(OtherFakeToken, CancellationToken.None);

        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
        Assert.Equal(OtherFakeToken, auth.Token);
        Assert.Equal(2, provider.Calls);

        // 之後重新檢查換到不同的值，才交給呼叫端用 E1 驗證（第 3 點）。
        Assert.True(await auth.RecheckAsync(CancellationToken.None));
        Assert.Equal(ThirdFakeToken, auth.Token);
    }

    [Fact]
    public async Task RefuseAsync收到已經被換掉的token時不改狀態()
    {
        // 另一條工作已經換上新 token：舊值的重送失敗不能否定還沒試過的新值。
        var provider = new FakeTokenProvider().Returns(Fixture.FakeToken).Returns(OtherFakeToken);
        var auth = await VerifiedAuth(provider);
        Assert.True(await auth.OnUnauthorizedAsync(Fixture.FakeToken, CancellationToken.None));

        await auth.RefuseAsync(Fixture.FakeToken, CancellationToken.None);

        Assert.Equal(AuthStatus.Ok, auth.Status);
        Assert.Equal(OtherFakeToken, auth.Token);
    }

    [Fact]
    public async Task _401後重取拿回更早被拒絕的token不算改變()
    {
        // 第一把被拒、換到第二把又被拒，gh 這時又給回第一把：不能再送一次已知無效的憑證。
        var provider = new FakeTokenProvider()
            .Returns(Fixture.FakeToken)
            .Returns(OtherFakeToken)
            .Returns(Fixture.FakeToken);
        var auth = await VerifiedAuth(provider);

        Assert.True(await auth.OnUnauthorizedAsync(CancellationToken.None));
        Assert.False(await auth.OnUnauthorizedAsync(CancellationToken.None));

        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
    }

    [Fact]
    public async Task _401後取不到token也進入未登入()
    {
        var provider = new FakeTokenProvider().Returns(Fixture.FakeToken).Returns(token: null);
        var auth = await VerifiedAuth(provider);

        var resend = await auth.OnUnauthorizedAsync(CancellationToken.None);

        Assert.False(resend);
        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
        Assert.Null(auth.Token);
        Assert.Equal(TokenSource.None, auth.Source);
        Assert.Equal("gh 沒有登入", auth.Problem);
    }

    [Fact]
    public async Task _401後重取丟例外也進入未登入()
    {
        var provider = new FakeTokenProvider().Returns(Fixture.FakeToken).Throws(new InvalidOperationException("壞掉了"));
        var auth = await VerifiedAuth(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.OnUnauthorizedAsync(CancellationToken.None));

        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
    }

    [Fact]
    public async Task _401後重取被取消也進入未登入()
    {
        var provider = new FakeTokenProvider().Returns(Fixture.FakeToken).Returns(OtherFakeToken);
        var auth = await VerifiedAuth(provider);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.OnUnauthorizedAsync(cts.Token));

        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
    }

    [Fact]
    public async Task 兩條工作同時收到401只重取一次()
    {
        // 輪詢與複製 log 兩條工作（spec §3.1）幾乎同時對舊 token 收到 401：
        // 只執行一次 gh，兩邊都用新值重送一次，不能讓第二個把狀態誤判成未登入。
        var refetch = new TaskCompletionSource<TokenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeTokenProvider().Returns(Fixture.FakeToken).Waits(refetch.Task);
        var auth = await VerifiedAuth(provider);

        var polling = auth.OnUnauthorizedAsync(CancellationToken.None);
        var userAction = auth.OnUnauthorizedAsync(CancellationToken.None);
        refetch.SetResult(new TokenResult(OtherFakeToken, TokenSource.GhStored, null));

        Assert.True(await polling);
        Assert.True(await userAction);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(OtherFakeToken, auth.Token);
        Assert.Equal(AuthStatus.Ok, auth.Status);
    }

    [Fact]
    public async Task 另一條工作換到新token後才到的401直接用新token重送()
    {
        // 兩條工作都用第一把送出；輪詢先收到 401 並換到第二把，使用者操作那條的 401 這時才到。
        // 對那個請求來說新值和送出時不同，要用新值重送一次（spec §13.2 第 1 點），不能把第二把記成被拒。
        var provider = new FakeTokenProvider()
            .Returns(Fixture.FakeToken)
            .Returns(OtherFakeToken)
            .Returns(OtherFakeToken);
        var auth = await VerifiedAuth(provider);
        Assert.True(await auth.OnUnauthorizedAsync(Fixture.FakeToken, CancellationToken.None));

        var resend = await auth.OnUnauthorizedAsync(Fixture.FakeToken, CancellationToken.None);

        Assert.True(resend);
        // 已經有新值，不再執行 gh。
        Assert.Equal(2, provider.Calls);
        Assert.Equal(OtherFakeToken, auth.Token);
        Assert.Equal(AuthStatus.Ok, auth.Status);

        // 之後用第二把重送仍 401，才進入未登入。
        Assert.False(await auth.OnUnauthorizedAsync(OtherFakeToken, CancellationToken.None));
        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
    }

    [Fact]
    public async Task 已經未登入後才到的舊token401不重取也不改狀態()
    {
        // 第一把被拒、換到第二把重送也被拒，已經是未登入；這時另一條工作用第一把送出的 401 才到。
        // 恢復交給 RecheckAsync（spec §13.2 第 3 點）：不再執行 gh，也不能把記下的被拒 token 換回第一把。
        var provider = new FakeTokenProvider()
            .Returns(Fixture.FakeToken)
            .Returns(OtherFakeToken)
            .Returns(OtherFakeToken)
            .Returns(token: null)
            .Returns(OtherFakeToken);
        var auth = await VerifiedAuth(provider);
        Assert.True(await auth.OnUnauthorizedAsync(Fixture.FakeToken, CancellationToken.None));
        Assert.False(await auth.OnUnauthorizedAsync(OtherFakeToken, CancellationToken.None));

        Assert.False(await auth.OnUnauthorizedAsync(Fixture.FakeToken, CancellationToken.None));

        Assert.Equal(3, provider.Calls);
        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
        Assert.Equal(OtherFakeToken, auth.Token);

        // gh 一度取不到、之後又給回第二把：第二把仍記為被拒，不算改變。
        Assert.False(await auth.RecheckAsync(CancellationToken.None));
        Assert.False(await auth.RecheckAsync(CancellationToken.None));
        Assert.Equal(OtherFakeToken, auth.Token);
    }

    [Fact]
    public async Task 已經未登入後才到的同一把token401不重取並交給重新檢查()
    {
        // 兩條工作都用第一把送出；輪詢先收到 401、gh 給回同一把，已經是未登入。使用者操作那條的 401 這時才到，
        // 而 gh 剛好已經刷新成第二把（spec §13.1）。這裡換到第二把就會被這次重送用掉，之後重新檢查看不出改變，
        // 狀態停在未登入。恢復只交給 RecheckAsync＋E1（spec §13.2 第 3 點）：不執行 gh、回 false。
        var provider = new FakeTokenProvider()
            .Returns(Fixture.FakeToken)
            .Returns(Fixture.FakeToken)
            .Returns(OtherFakeToken);
        var auth = await VerifiedAuth(provider);
        Assert.False(await auth.OnUnauthorizedAsync(Fixture.FakeToken, CancellationToken.None));

        Assert.False(await auth.OnUnauthorizedAsync(Fixture.FakeToken, CancellationToken.None));

        Assert.Equal(2, provider.Calls);
        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
        Assert.Equal(Fixture.FakeToken, auth.Token);

        // 重新檢查拿到第二把才算改變；呼叫端用 E1 驗證，成功才恢復。
        Assert.True(await auth.RecheckAsync(CancellationToken.None));
        Assert.Equal(OtherFakeToken, auth.Token);
        Assert.Equal(AuthStatus.Unauthorized, auth.Status);

        auth.MarkVerified();

        Assert.Equal(AuthStatus.Ok, auth.Status);
    }

    [Fact]
    public async Task 還沒取得token就未登入時收到401也不執行gh()
    {
        // 啟動時 gh 沒有登入；未登入期間不論哪一條工作收到 401，都只靠 RecheckAsync 恢復（spec §13.2 第 3 點）。
        var provider = new FakeTokenProvider().Returns(token: null).Returns(Fixture.FakeToken);
        var auth = new AuthManager(provider, new SecretRedactor());
        await auth.InitializeAsync(CancellationToken.None);

        Assert.False(await auth.OnUnauthorizedAsync(CancellationToken.None));

        Assert.Equal(1, provider.Calls);
        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
        Assert.Null(auth.Token);
        Assert.Equal("gh 沒有登入", auth.Problem);

        Assert.True(await auth.RecheckAsync(CancellationToken.None));
        Assert.Equal(Fixture.FakeToken, auth.Token);
    }

    [Fact]
    public async Task 重新檢查換到的token用E1驗證仍401時記為被拒()
    {
        // 未登入期間重新檢查換到第二把，E1 用它驗證仍 401：不執行 gh、不重送（E1 只送一次，spec §13.2 第 3 點），
        // 第二把記為被拒。之後 gh 一度取不到、又給回第二把，也不能再用 E1 送一次已知無效的憑證。
        var provider = new FakeTokenProvider()
            .Returns(Fixture.FakeToken)
            .Returns(Fixture.FakeToken)
            .Returns(OtherFakeToken)
            .Returns(token: null)
            .Returns(OtherFakeToken);
        var auth = await VerifiedAuth(provider);
        Assert.False(await auth.OnUnauthorizedAsync(Fixture.FakeToken, CancellationToken.None));
        Assert.True(await auth.RecheckAsync(CancellationToken.None));

        Assert.False(await auth.OnUnauthorizedAsync(OtherFakeToken, CancellationToken.None));

        Assert.Equal(3, provider.Calls);
        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
        Assert.Equal(OtherFakeToken, auth.Token);

        Assert.False(await auth.RecheckAsync(CancellationToken.None));
        Assert.False(await auth.RecheckAsync(CancellationToken.None));
        Assert.Equal(OtherFakeToken, auth.Token);
        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
    }

    [Fact]
    public async Task 排隊等重取時被取消不影響另一條工作換到的新token()
    {
        // 輪詢正在執行 gh 重取，使用者操作那條排隊時被取消：被取消的呼叫端不會再送出，狀態交給正在重取的那條決定。
        var refetch = new TaskCompletionSource<TokenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeTokenProvider().Returns(Fixture.FakeToken).Waits(refetch.Task);
        var auth = await VerifiedAuth(provider);
        using var cts = new CancellationTokenSource();

        var polling = auth.OnUnauthorizedAsync(CancellationToken.None);
        var userAction = auth.OnUnauthorizedAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => userAction);

        Assert.Equal(AuthStatus.Ok, auth.Status);

        refetch.SetResult(new TokenResult(OtherFakeToken, TokenSource.GhStored, null));

        Assert.True(await polling);
        Assert.Equal(OtherFakeToken, auth.Token);
        Assert.Equal(AuthStatus.Ok, auth.Status);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task 已取消的舊token401不會讓已換上的新token進入未登入()
    {
        // 換到第二把之後，另一條工作用第一把送出的 401 才到，而它已經被取消。
        var provider = new FakeTokenProvider().Returns(Fixture.FakeToken).Returns(OtherFakeToken);
        var auth = await VerifiedAuth(provider);
        Assert.True(await auth.OnUnauthorizedAsync(Fixture.FakeToken, CancellationToken.None));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => auth.OnUnauthorizedAsync(Fixture.FakeToken, cts.Token));

        Assert.Equal(AuthStatus.Ok, auth.Status);
        Assert.Equal(OtherFakeToken, auth.Token);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task 未登入期間token沒變不回true()
    {
        var provider = new FakeTokenProvider()
            .Returns(Fixture.FakeToken)
            .Returns(Fixture.FakeToken)
            .Returns(Fixture.FakeToken)
            .Returns(Fixture.FakeToken);
        var auth = await VerifiedAuth(provider);
        Assert.False(await auth.OnUnauthorizedAsync(CancellationToken.None));

        Assert.False(await auth.RecheckAsync(CancellationToken.None));
        Assert.False(await auth.RecheckAsync(CancellationToken.None));

        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
        Assert.Equal(Fixture.FakeToken, auth.Token);
    }

    [Fact]
    public async Task 未登入期間token改變回true但要等E1驗證才恢復()
    {
        var provider = new FakeTokenProvider()
            .Returns(Fixture.FakeToken)
            .Returns(Fixture.FakeToken)
            .Returns(OtherFakeToken);
        var auth = await VerifiedAuth(provider);
        Assert.False(await auth.OnUnauthorizedAsync(CancellationToken.None));

        Assert.True(await auth.RecheckAsync(CancellationToken.None));

        // spec §13.2：token 改變時才用 E1 驗證一次，成功才恢復輪詢。
        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
        Assert.Equal(OtherFakeToken, auth.Token);

        auth.MarkVerified();

        Assert.Equal(AuthStatus.Ok, auth.Status);
    }

    [Fact]
    public async Task 未登入期間拿回被拒絕的token不算改變()
    {
        // 401 後 gh 一度取不到 token，之後又給回同一把被拒的 token：不用 E1 再送一次無效憑證。
        var provider = new FakeTokenProvider()
            .Returns(Fixture.FakeToken)
            .Returns(token: null)
            .Returns(Fixture.FakeToken)
            .Returns(OtherFakeToken);
        var auth = await VerifiedAuth(provider);
        Assert.False(await auth.OnUnauthorizedAsync(CancellationToken.None));

        Assert.False(await auth.RecheckAsync(CancellationToken.None));
        Assert.True(await auth.RecheckAsync(CancellationToken.None));

        Assert.Equal(OtherFakeToken, auth.Token);
    }

    [Fact]
    public async Task 從沒取得token時重新檢查拿到token回true()
    {
        // 啟動時 gh 還沒登入，之後執行了 gh auth login。
        var provider = new FakeTokenProvider().Returns(token: null).Returns(token: null).Returns(Fixture.FakeToken);
        var auth = new AuthManager(provider, new SecretRedactor());
        await auth.InitializeAsync(CancellationToken.None);

        Assert.False(await auth.RecheckAsync(CancellationToken.None));
        Assert.Equal("gh 沒有登入", auth.Problem);
        Assert.True(await auth.RecheckAsync(CancellationToken.None));

        Assert.Equal(Fixture.FakeToken, auth.Token);
        Assert.Null(auth.Problem);
        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
    }

    [Fact]
    public async Task 重新檢查丟例外時狀態不變()
    {
        var provider = new FakeTokenProvider()
            .Returns(Fixture.FakeToken)
            .Returns(Fixture.FakeToken)
            .Throws(new InvalidOperationException("壞掉了"));
        var auth = await VerifiedAuth(provider);
        Assert.False(await auth.OnUnauthorizedAsync(CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.RecheckAsync(CancellationToken.None));

        Assert.Equal(AuthStatus.Unauthorized, auth.Status);
        Assert.Equal(Fixture.FakeToken, auth.Token);
    }

    [Fact]
    public async Task 新token會交給redactor()
    {
        // 用不符合 token 樣式的值，確認是 SetActiveToken 的精確比對遮掉的。
        const string plainTokenC = "tok_TESTTOKEN00000000000000000000000000000C";
        var redactor = new SecretRedactor();
        var provider = new FakeTokenProvider()
            .Returns(PlainTokenA)
            .Returns(PlainTokenB)
            .Returns(PlainTokenB)
            .Returns(plainTokenC);
        var auth = new AuthManager(provider, redactor);

        await auth.InitializeAsync(CancellationToken.None);
        Assert.Equal("x *** y", redactor.Redact($"x {PlainTokenA} y"));

        Assert.True(await auth.OnUnauthorizedAsync(CancellationToken.None));
        Assert.Equal("x *** y", redactor.Redact($"x {PlainTokenB} y"));

        Assert.False(await auth.OnUnauthorizedAsync(CancellationToken.None));
        Assert.True(await auth.RecheckAsync(CancellationToken.None));
        Assert.Equal("x *** y", redactor.Redact($"x {plainTokenC} y"));
    }

    [Fact]
    public async Task 取不到token時redactor仍遮最後一把token()
    {
        // 被拒的 token 可能還留在進行中的請求或例外訊息裡，不因為取不到新值就停止遮罩。
        var redactor = new SecretRedactor();
        var provider = new FakeTokenProvider().Returns(PlainTokenA).Returns(token: null);
        var auth = new AuthManager(provider, redactor);
        await auth.InitializeAsync(CancellationToken.None);

        Assert.False(await auth.OnUnauthorizedAsync(CancellationToken.None));

        Assert.Equal("x *** y", redactor.Redact($"x {PlainTokenA} y"));
    }

    // ── 文字表示不含 token（spec §13.4）──────────────────────────────

    [Fact]
    public void TokenResult的文字表示不含token()
    {
        var text = new TokenResult(Fixture.FakeToken, TokenSource.GhStored, null).ToString();

        Assert.DoesNotContain(Fixture.FakeToken, text, StringComparison.Ordinal);
        Assert.Equal("TokenResult { Token = ***, Source = GhStored, Problem =  }", text);
        Assert.Equal(
            "TokenResult { Token = , Source = None, Problem = gh 沒有登入 }",
            new TokenResult(null, TokenSource.None, "gh 沒有登入").ToString());
    }

    [Fact]
    public void ProcessResult的文字表示不含輸出()
    {
        var text = FakeProcessRunner.Output(Fixture.FakeToken + "\n").ToString();

        Assert.DoesNotContain(Fixture.FakeToken, text, StringComparison.Ordinal);
        Assert.Equal("ProcessResult { Started = True, ExitCode = 0, StandardOutput = ***, TimedOut = False }", text);
    }

    // ── 輔助 ─────────────────────────────────────────────────────────

    private static Func<string, string?> NoEnvironment => _ => null;

    private static GhCliTokenProvider GhProvider(FakeProcessRunner runner) =>
        new(runner, ignoreEnvironment: false, NoEnvironment);

    private static Func<string, string?> EnvironmentWith(string variable, string value) =>
        name => name == variable ? value : null;

    /// <summary>初始化並用 E1 驗證過（Status = Ok）的 AuthManager；佔用 provider 的第一個結果。</summary>
    private static async Task<AuthManager> VerifiedAuth(FakeTokenProvider provider)
    {
        var auth = new AuthManager(provider, new SecretRedactor());
        await auth.InitializeAsync(CancellationToken.None);
        auth.MarkVerified();
        Assert.Equal(AuthStatus.Ok, auth.Status);
        return auth;
    }
}
