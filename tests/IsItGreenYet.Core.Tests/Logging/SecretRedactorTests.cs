using System.Net;
using IsItGreenYet.Core.Logging;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Logging;

public class SecretRedactorTests
{
    /// <summary>不符合任何 token 樣式的假 token，用來確認精確比對本身就會遮掉。</summary>
    private const string NonPatternToken = "tok_TESTTOKEN000000000000000000000000000000";

    private const string SignedUrl =
        "https://pipelines.actions.githubusercontent.com/x/y?sig=FAKESIGNATURE&se=2026-10-24T02%3A08%3A33Z";

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 2, 7, 33, 123, TimeSpan.Zero);

    [Fact]
    public void 精確遮掉目前token()
    {
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(NonPatternToken);

        var result = redactor.Redact($"token={NonPatternToken}，再一次 {NonPatternToken}");

        Assert.Equal("token=***，再一次 ***", result);
    }

    [Fact]
    public void 精確遮掉Fixture的假token()
    {
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(Fixture.FakeToken);

        var result = redactor.Redact($"使用 {Fixture.FakeToken} 失敗");

        Assert.Equal("使用 *** 失敗", result);
    }

    [Fact]
    public void 樣式遮掉gho與github_pat()
    {
        // 沒有設定目前的 token，只靠樣式比對。
        var redactor = new SecretRedactor();
        const string oauth = "gho_TESTTOKEN000000000000000000000000000000";
        const string fineGrained = "github_pat_11TESTTOKEN0000_00000000000000000000000000";

        var result = redactor.Redact($"a {oauth} b {fineGrained} c");

        Assert.Equal("a *** b *** c", result);
    }

    [Theory]
    [InlineData("ghp_")]
    [InlineData("gho_")]
    [InlineData("ghu_")]
    [InlineData("ghs_")]
    [InlineData("ghr_")]
    public void 樣式涵蓋所有gh前綴(string prefix)
    {
        var redactor = new SecretRedactor();

        var result = redactor.Redact($"x {prefix}TESTTOKEN00000000000000000000 y");

        Assert.Equal("x *** y", result);
    }

    [Fact]
    public void 樣式少於20碼不視為token()
    {
        var redactor = new SecretRedactor();
        // 前綴後只有 19 碼，不符合 spec 的 {20,}。
        const string text = "ghp_TESTTOKEN0000000000";

        Assert.Equal(text, redactor.Redact(text));
    }

    [Fact]
    public void Authorization後面全遮()
    {
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(Fixture.FakeToken);

        Assert.Equal("Authorization: ***", redactor.Redact($"Authorization: Bearer {Fixture.FakeToken}"));
        // 不是 token 樣式、也不是目前 token 的內容，一樣全遮。
        Assert.Equal("Authorization: ***", redactor.Redact("Authorization: Basic dXNlcjpwYXNz"));
    }

    [Fact]
    public void Authorization只遮到行尾且不分大小寫()
    {
        var redactor = new SecretRedactor();
        var text = "GET /user\r\nauthorization:Bearer abc def\r\nAccept: application/vnd.github+json\nProxy-Authorization: Basic xyz";

        var result = redactor.Redact(text);

        Assert.Equal(
            "GET /user\r\nauthorization: ***\r\nAccept: application/vnd.github+json\nProxy-Authorization: ***",
            result);
    }

    [Fact]
    public void HttpRequestMessage的文字表示也遮掉Authorization()
    {
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(NonPatternToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {NonPatternToken}");

        var result = redactor.Redact(request.ToString());

        Assert.DoesNotContain(NonPatternToken, result, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", result, StringComparison.Ordinal);
        Assert.Contains("Authorization: ***", result, StringComparison.Ordinal);
    }

    [Fact]
    public void 簽名網址只留主機路徑()
    {
        var result = SecretRedactor.DescribeUrl(new Uri("https://pipelines.actions.githubusercontent.com/x/y?sig=abc"));

        Assert.Equal("pipelines.actions.githubusercontent.com/x/y?***", result);
    }

    [Fact]
    public void 非api網址沒有查詢字串時不加遮罩記號()
    {
        Assert.Equal(
            "pipelines.actions.githubusercontent.com/x/y",
            SecretRedactor.DescribeUrl(new Uri("https://pipelines.actions.githubusercontent.com/x/y")));
        Assert.Equal(
            "github.com/octo-org/sample-app",
            SecretRedactor.DescribeUrl(new Uri("https://github.com/octo-org/sample-app#readme")));
    }

    [Fact]
    public void 非api網址不帶帳密與連接埠()
    {
        var result = SecretRedactor.DescribeUrl(new Uri("https://user:secret@logs.example.test:8443/a/b?sig=abc"));

        Assert.Equal("logs.example.test/a/b?***", result);
    }

    [Fact]
    public void api網址保留路徑與查詢字串()
    {
        var result = SecretRedactor.DescribeUrl(
            new Uri("https://api.github.com/repos/octo-org/sample-app/actions/runs?branch=feature%2Fa%231&per_page=30"));

        Assert.Equal("/repos/octo-org/sample-app/actions/runs?branch=feature%2Fa%231&per_page=30", result);
    }

    [Fact]
    public void 主機名只是開頭像api不算api網址()
    {
        var result = SecretRedactor.DescribeUrl(new Uri("https://api.github.com.evil.example/x?sig=abc"));

        Assert.Equal("api.github.com.evil.example/x?***", result);
    }

    [Fact]
    public void api主機名大小寫不同仍算api網址()
    {
        var result = SecretRedactor.DescribeUrl(new Uri("https://API.GitHub.com/user?per_page=1"));

        Assert.Equal("/user?per_page=1", result);
    }

    [Fact]
    public void 相對網址原樣回傳()
    {
        var result = SecretRedactor.DescribeUrl(new Uri("/repos/octo-org/sample-app?x=1", UriKind.Relative));

        Assert.Equal("/repos/octo-org/sample-app?x=1", result);
    }

    [Fact]
    public void 省略scheme的相對網址指向其他主機時只留主機路徑()
    {
        // 轉址的 Location 若是 //host/path 這種寫法，Uri 會當成相對網址。
        var result = SecretRedactor.DescribeUrl(
            new Uri("//pipelines.actions.githubusercontent.com/x/y?sig=abc", UriKind.Relative));

        Assert.Equal("pipelines.actions.githubusercontent.com/x/y?***", result);
    }

    [Fact]
    public void 省略scheme的相對網址指向api時保留路徑與查詢字串()
    {
        var result = SecretRedactor.DescribeUrl(new Uri("//api.github.com/user?per_page=1", UriKind.Relative));

        Assert.Equal("/user?per_page=1", result);
    }

    [Fact]
    public void 不以斜線開頭的相對路徑視為api路徑()
    {
        var result = SecretRedactor.DescribeUrl(new Uri("repos/octo-org/sample-app?x=1", UriKind.Relative));

        Assert.Equal("/repos/octo-org/sample-app?x=1", result);
    }

    [Theory]
    [InlineData("//")]
    [InlineData("///x?sig=abc")]
    public void 解析不出主機的相對網址整個遮掉(string relative)
    {
        Assert.Equal("***", SecretRedactor.DescribeUrl(new Uri(relative, UriKind.Relative)));
    }

    [Theory]
    // Windows 上 \\host\x?sig=abc 會被解析成這個 file: 網址，問號變成路徑的一部分。
    [InlineData("file://evil.example/x%3Fsig=abc")]
    [InlineData("ftp://evil.example/x?sig=abc")]
    public void http以外的網址整個遮掉(string url)
    {
        Assert.Equal("***", SecretRedactor.DescribeUrl(new Uri(url)));
    }

    [Fact]
    public void 文字中的簽名網址遮掉查詢字串但api網址保留()
    {
        var redactor = new SecretRedactor();
        var text = $"原請求 https://api.github.com/repos/octo-org/sample-app/actions/jobs/1/logs?x=1；下載失敗 {SignedUrl}";

        var result = redactor.Redact(text);

        Assert.Equal(
            "原請求 https://api.github.com/repos/octo-org/sample-app/actions/jobs/1/logs?x=1；下載失敗 pipelines.actions.githubusercontent.com/x/y?***",
            result);
    }

    [Fact]
    public void 簽名網址之後同一行的文字一併遮掉但下一欄與下一行保留()
    {
        var redactor = new SecretRedactor();
        var text = $"轉到 {SignedUrl} 之後的說明\t200\r\n下一行 https://api.github.com/user?per_page=1";

        var result = redactor.Redact(text);

        Assert.Equal(
            "轉到 pipelines.actions.githubusercontent.com/x/y?***\t200\r\n下一行 https://api.github.com/user?per_page=1",
            result);
    }

    [Fact]
    public void 文字中的簽名網址查詢字串含空白時仍整段遮掉()
    {
        var redactor = new SecretRedactor();
        // 字串內插與 HttpRequestMessage.ToString() 都用 Uri.ToString()，它會把 %20 還原成空白。
        var uri = new Uri("https://results.example.test/a/b?rscd=attachment%3B%20filename%3Dx&sig=FAKESIGNATURE");

        var result = redactor.Redact($"RequestUri: '{uri}'");

        Assert.Equal("RequestUri: 'results.example.test/a/b?***", result);
    }

    [Fact]
    public void HttpRequestMessage的文字表示也遮掉簽名網址()
    {
        var redactor = new SecretRedactor();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "https://results.example.test/a/b?rscd=attachment%3B%20filename%3Dx&sig=FAKESIGNATURE");

        var result = redactor.Redact(request.ToString());

        Assert.DoesNotContain("FAKESIGNATURE", result, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", result, StringComparison.Ordinal);
        Assert.StartsWith("Method: GET, RequestUri: 'results.example.test/a/b?***", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("%22")]
    [InlineData("%3C")]
    [InlineData("%E4%B8%AD")]
    [InlineData("%E3%80%80")]
    [InlineData("%C2%A0")]
    public void 文字中的簽名網址查詢字串含被還原的字元時仍整段遮掉(string escaped)
    {
        var redactor = new SecretRedactor();
        // 依序是雙引號、小於、中文、全形空白、不換行空白，Uri.ToString() 都會還原成原字元。
        var uri = new Uri($"https://results.example.test/a/b?x=1{escaped}2&sig=FAKESIGNATURE");

        var result = redactor.Redact($"下載失敗 {uri}");

        Assert.Equal("下載失敗 results.example.test/a/b?***", result);
    }

    [Fact]
    public void 文字中的網址路徑含空白時簽名仍遮掉()
    {
        var redactor = new SecretRedactor();
        var uri = new Uri("https://results.example.test/job%20logs/1.txt?sig=FAKESIGNATURE");

        var result = redactor.Redact($"下載失敗 {uri}");

        // 看不出空白後面還是不是網址，從網址斷開的地方起整段遮掉。
        Assert.Equal("下載失敗 results.example.test/job?***", result);
    }

    [Fact]
    public void 文字中的網址不留帳密與連接埠()
    {
        var redactor = new SecretRedactor();

        Assert.Equal(
            "失敗 logs.example.test/a/b?***",
            redactor.Redact("失敗 https://user:secretpw@logs.example.test:8443/a/b?sig=FAKESIGNATURE"));
        // 沒有查詢字串時只拿掉帳密與連接埠，後面的文字保留。
        Assert.Equal(
            "代理 proxy.example.test/ 連線失敗",
            redactor.Redact("代理 https://user:secretpw@proxy.example.test:8080/ 連線失敗"));
    }

    [Fact]
    public void api主機帶帳密時當成外部網址遮掉()
    {
        var redactor = new SecretRedactor();

        Assert.Equal(
            "請求 api.github.com/user?***",
            redactor.Redact("請求 https://user:secretpw@api.github.com/user?per_page=1"));
    }

    [Fact]
    public void api網址查詢字串裡夾帶的外部網址仍遮掉()
    {
        var redactor = new SecretRedactor();

        Assert.Equal(
            "https://api.github.com/x?u=evil.example/y?***",
            redactor.Redact("https://api.github.com/x?u=https://evil.example/y?sig=FAKESIGNATURE"));
    }

    [Fact]
    public void api網址的查詢字串含空白也保留()
    {
        var redactor = new SecretRedactor();
        var uri = new Uri("https://api.github.com/search/issues?q=repo%3Aocto-org%2Fsample-app%20is%3Aopen&per_page=1");
        var text = $"原請求 {uri}";

        Assert.Equal(text, redactor.Redact(text));
    }

    [Fact]
    public void 沒有查詢字串也沒有帳密的外部網址保留原樣()
    {
        var redactor = new SecretRedactor();
        const string text = "開啟 https://github.com/octo-org/sample-app#readme 失敗；http://localhost:8080/a 不是 https";

        Assert.Equal(text, redactor.Redact(text));
    }

    [Fact]
    public void 無法解析的網址後面有問號時整段遮掉()
    {
        var redactor = new SecretRedactor();

        Assert.Equal("網址 ***", redactor.Redact("網址 https://?sig=FAKESIGNATURE 結尾"));
    }

    [Fact]
    public void 一行很多網址也能在合理時間內遮完()
    {
        var redactor = new SecretRedactor();
        var urls = string.Concat(Enumerable.Repeat("https://user:secretpw@logs.example.test/a ", 20_000));

        var withoutQuery = redactor.Redact(urls);
        var withQuery = redactor.Redact(urls + SignedUrl);

        Assert.DoesNotContain("secretpw", withoutQuery, StringComparison.Ordinal);
        Assert.StartsWith("logs.example.test/a logs.example.test/a ", withoutQuery, StringComparison.Ordinal);
        Assert.Equal("logs.example.test/a?***", withQuery);
    }

    [Fact]
    public void 遮罩重複套用結果不變()
    {
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(Fixture.FakeToken);
        var once = redactor.Redact($"Authorization: Bearer {Fixture.FakeToken}\n{SignedUrl} {Fixture.FakeToken}");

        Assert.Equal(once, redactor.Redact(once));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 沒有token時只用樣式比對且不丟例外(string? token)
    {
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(token);

        Assert.Equal("一般文字 *** 結尾", redactor.Redact($"一般文字 {Fixture.FakeToken} 結尾"));
        Assert.Equal("一般文字", redactor.Redact("一般文字"));
    }

    [Fact]
    public void token前後的空白不影響精確比對()
    {
        var redactor = new SecretRedactor();
        redactor.SetActiveToken($"  {NonPatternToken}\n");

        Assert.Equal("x *** y", redactor.Redact($"x {NonPatternToken} y"));
    }

    [Fact]
    public void 換token後遮新的token()
    {
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(Fixture.FakeToken);
        redactor.SetActiveToken(NonPatternToken);

        Assert.Equal("x ***", redactor.Redact($"x {NonPatternToken}"));
    }

    [Fact]
    public void 空字串回傳空字串()
    {
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(Fixture.FakeToken);

        Assert.Equal(string.Empty, redactor.Redact(string.Empty));
    }

    [Fact]
    public void 很長的文字也能在合理時間內遮完()
    {
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(Fixture.FakeToken);
        // 沒有空白、沒有查詢字串的超長網址與超長英數字串，避免樣式比對退化成平方時間。
        var text = "https://" + new string('a', 200_000) + " " + new string('b', 200_000) + " " + Fixture.FakeToken;

        var result = redactor.Redact(text);

        Assert.EndsWith(" ***", result, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.FakeToken, result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Fixture.FakeToken)]
    [InlineData(NonPatternToken)]
    public void 走過所有路徑後兩個log都找不到token(string token)
    {
        using var workspace = new TempWorkspace();
        var time = new ManualTimeProvider(Now, ManualTimeProvider.Taipei);
        var redactor = new SecretRedactor();
        redactor.SetActiveToken(token);
        var apiLog = new ApiLog(workspace.Paths.LogsDirectory, redactor, time);
        var errorLog = new ErrorLog(workspace.Paths.RootDirectory, redactor, time);
        var runs = new Uri("https://api.github.com/repos/octo-org/sample-app/actions/runs?per_page=30");
        var jobLog = new Uri("https://api.github.com/repos/octo-org/sample-app/actions/jobs/7/logs");
        var signed = new Uri(SignedUrl);
        var elapsed = TimeSpan.FromMilliseconds(150);

        // 成功與 304
        apiLog.Write(new ApiLogEntry("GET", runs, 200, "core", 4999, 5000, elapsed, null, null));
        apiLog.Write(new ApiLogEntry("GET", runs, 304, "core", 4999, 5000, elapsed, null, null));
        // 401：呼叫端可能把送出的 header 寫進訊息
        apiLog.Write(new ApiLogEntry("GET", new Uri("https://api.github.com/user"), 401, null, null, null, elapsed, null, null));
        errorLog.WriteMessage("認證失敗", $"401 Unauthorized，Authorization: Bearer {token}");
        // 403（次級限流，帶 retry-after）
        apiLog.Write(new ApiLogEntry("GET", runs, 403, "core", 4000, 5000, elapsed, null, 60));
        errorLog.WriteMessage("權限不足", $"403 Forbidden；X-Accepted-GitHub-Permissions: actions=read；目前 token {token}");
        // 轉址到簽名網址，再下載（成功與過期兩種）
        apiLog.Write(new ApiLogEntry("GET", jobLog, (int)HttpStatusCode.Found, "core", 3999, 5000, elapsed, signed, null));
        apiLog.Write(new ApiLogEntry("GET", signed, 200, null, null, null, elapsed, null, null));
        apiLog.Write(new ApiLogEntry("GET", signed, 403, null, null, null, elapsed, null, null));
        // 轉址目標是省略 scheme 的相對網址
        apiLog.Write(new ApiLogEntry("GET", jobLog, (int)HttpStatusCode.Found, "core", 3998, 5000, elapsed,
            new Uri("//pipelines.actions.githubusercontent.com/x/y?sig=FAKESIGNATURE", UriKind.Relative), null));
        // 簽名下載主機自己再回相對轉址
        apiLog.Write(new ApiLogEntry("GET", signed, (int)HttpStatusCode.Found, null, null, null, elapsed,
            new Uri("/z/w?sig=FAKESIGNATURE", UriKind.Relative), null));
        // 全域暫停的註記
        apiLog.Note($"paused until 2026-10-24T10:08:33+08:00（{token}）");
        // 例外：訊息、內部例外與堆疊都可能帶 token 或簽名網址
        errorLog.Write("下載 job log", CaptureThrown(token));
        errorLog.WriteMessage("轉址", $"轉到 {SignedUrl}，token={token}");
        // 呼叫端直接內插 Uri：%20 會被還原成空白，還帶著帳密
        errorLog.WriteMessage("轉址", $"HTTPS 轉 HTTP，不跟隨：{new Uri("https://user:secretpw@results.example.test:8443/a/b?rscd=attachment%3B%20filename%3Dx&sig=FAKESIGNATURE")}");

        var files = Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories);
        var apiLogPath = Path.Combine(workspace.Paths.LogsDirectory, "api.log");
        Assert.Contains(apiLogPath, files);
        Assert.Contains(workspace.Paths.ErrorLogPath, files);
        Assert.Equal(10, File.ReadAllLines(apiLogPath).Length);
        Assert.Contains("pipelines.actions.githubusercontent.com/x/y?***", File.ReadAllText(apiLogPath), StringComparison.Ordinal);
        Assert.Contains("下載 job log", File.ReadAllText(workspace.Paths.ErrorLogPath), StringComparison.Ordinal);
        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain(token, content, StringComparison.Ordinal);
            Assert.DoesNotContain("FAKESIGNATURE", content, StringComparison.Ordinal);
            Assert.DoesNotContain("sig=", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", content, StringComparison.Ordinal);
            Assert.DoesNotContain("secretpw", content, StringComparison.Ordinal);
        }
    }

    private static Exception CaptureThrown(string token)
    {
        try
        {
            try
            {
                throw new InvalidOperationException($"內部錯誤，token {token}");
            }
            catch (InvalidOperationException inner)
            {
                throw new HttpRequestException($"Response status code does not indicate success: 403 ({SignedUrl})", inner);
            }
        }
        catch (HttpRequestException ex)
        {
            return ex;
        }
    }
}
