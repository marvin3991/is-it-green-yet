using System.Text.Json;
using System.Text.Json.Nodes;
using IsItGreenYet.Core.Status;

namespace IsItGreenYet.Core.Tests.Status;

public class StatusPayloadTests
{
    /// <summary>InformationalVersion 的格式：版本號接 <c>+</c> 與 40 字元的 commit（假的）。</summary>
    private const string AppVersion = "0.2.0+0123456789abcdef0123456789abcdef01234567";

    /// <summary>ASCII 的最大字元；命令列的 stdout 只能有這個範圍的字元（第二階段 spec §10.3、§11.3）。</summary>
    private const char MaxAscii = '\u007F';

    private static readonly DateTimeOffset SnapshotAt = new(2026, 10, 29, 2, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<StatusRun> NoRuns = [];

    [Fact]
    public void 序列化用camelCase而且不縮排()
    {
        var json = StatusJson.Serialize(Sample());

        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\r', json);
        Assert.Contains("\"monitoredRepos\":", json);

        // 鍵名與順序照第二階段 spec §10.3 標「App 寫」的欄位；值是 null 的欄位照樣寫出來（pausedUntil）。
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(
            [
                "appVersion", "snapshotAt", "overall", "signedIn", "apiVersionError", "refreshMode", "counts",
                "monitoredRepos", "staleRepos", "connection", "lastUpdatedAt", "runs",
            ],
            PropertyNames(root));
        Assert.Equal(["failing", "inProgress"], PropertyNames(root.GetProperty("counts")));
        Assert.Equal(["offline", "pausedUntil", "serverErrors"], PropertyNames(root.GetProperty("connection")));
        Assert.Equal(
            [
                "repo", "workflow", "runId", "runNumber", "attempt", "title", "branch", "event", "status", "conclusion",
                "state", "cancelling", "startedAt", "url",
            ],
            PropertyNames(root.GetProperty("runs")[0]));
    }

    [Fact]
    public void 中文字串跳脫成ASCII()
    {
        var payload = Sample() with { Runs = [Run() with { Title = "修正登入", Branch = "功能/登入" }] };

        var json = StatusJson.Serialize(payload);

        Assert.All(json, c => Assert.True(c <= MaxAscii, $"非 ASCII 字元 U+{(int)c:X4}"));
        var back = StatusJson.Deserialize<StatusPayload>(json);
        Assert.NotNull(back);
        var run = Assert.Single(back.Runs);
        Assert.Equal("修正登入", run.Title);
        Assert.Equal("功能/登入", run.Branch);
        // 版本字串的 + 也會被跳脫，解析後的值不變（第二階段 spec §10.3）。
        Assert.Equal(AppVersion, back.AppVersion);
    }

    [Fact]
    public void 往返後欄位相同()
    {
        var payload = Sample();

        var back = StatusJson.Deserialize<StatusPayload>(StatusJson.Serialize(payload));

        Assert.NotNull(back);
        // record 比較 IReadOnlyList 時比的是參考，清單另外逐筆比。
        Assert.Equal(payload.Runs, back.Runs);
        Assert.Equal(payload with { Runs = NoRuns }, back with { Runs = NoRuns });
    }

    [Fact]
    public void null欄位寫成null而且往返後相同()
    {
        var run = Run() with { Branch = null, Status = null, Conclusion = null, StartedAt = null };
        var payload = Sample() with
        {
            SignedIn = null,
            LastUpdatedAt = null,
            Connection = new StatusConnection(Offline: true, PausedUntil: null, ServerErrors: true),
            Runs = [run],
        };

        var json = StatusJson.Serialize(payload);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("signedIn").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastUpdatedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("connection").GetProperty("pausedUntil").ValueKind);
        var runElement = root.GetProperty("runs")[0];
        Assert.Equal(JsonValueKind.Null, runElement.GetProperty("branch").ValueKind);
        Assert.Equal(JsonValueKind.Null, runElement.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, runElement.GetProperty("conclusion").ValueKind);
        Assert.Equal(JsonValueKind.Null, runElement.GetProperty("startedAt").ValueKind);

        var back = StatusJson.Deserialize<StatusPayload>(json);
        Assert.NotNull(back);
        Assert.Equal(run, Assert.Single(back.Runs));
        Assert.Equal(payload with { Runs = NoRuns }, back with { Runs = NoRuns });
    }

    [Fact]
    public void 沒有run時寫成空陣列()
    {
        var json = StatusJson.Serialize(Sample() with { Runs = NoRuns });

        using var document = JsonDocument.Parse(json);
        var runs = document.RootElement.GetProperty("runs");
        Assert.Equal(JsonValueKind.Array, runs.ValueKind);
        Assert.Equal(0, runs.GetArrayLength());
        var back = StatusJson.Deserialize<StatusPayload>(json);
        Assert.NotNull(back);
        Assert.NotNull(back.Runs);
        Assert.Empty(back.Runs);
    }

    [Fact]
    public void 格式錯誤的JSON丟出JsonException()
    {
        Assert.ThrowsAny<JsonException>(() => StatusJson.Deserialize<StatusPayload>("{"));
    }

    [Theory]
    [InlineData("monitoredRepos")]
    [InlineData("apiVersionError")]
    [InlineData("snapshotAt")]
    [InlineData("overall")]
    [InlineData("runs")]
    public void 缺少欄位丟出JsonException(string key)
    {
        // 嚴格解析：缺少的欄位不補成 0、false、最小日期或 null（命令列靠這些欄位判斷退出碼）
        var root = JsonNode.Parse(StatusJson.Serialize(Sample()))!.AsObject();
        Assert.True(root.Remove(key));

        Assert.ThrowsAny<JsonException>(() => StatusJson.Deserialize<StatusPayload>(root.ToJsonString()));
    }

    [Theory]
    [InlineData("appVersion")]
    [InlineData("overall")]
    [InlineData("refreshMode")]
    [InlineData("counts")]
    [InlineData("connection")]
    [InlineData("runs")]
    public void 不可為null的欄位是null丟出JsonException(string key)
    {
        var root = JsonNode.Parse(StatusJson.Serialize(Sample()))!.AsObject();
        root[key] = null;

        Assert.ThrowsAny<JsonException>(() => StatusJson.Deserialize<StatusPayload>(root.ToJsonString()));
    }

    [Theory]
    [InlineData("repo")]
    [InlineData("workflow")]
    [InlineData("title")]
    [InlineData("event")]
    [InlineData("state")]
    [InlineData("url")]
    public void run裡不可為null的字串是null丟出JsonException(string key)
    {
        var root = JsonNode.Parse(StatusJson.Serialize(Sample()))!;
        root["runs"]![0]![key] = null;

        Assert.ThrowsAny<JsonException>(() => StatusJson.Deserialize<StatusPayload>(root.ToJsonString()));
    }

    [Fact]
    public void 不可為null的欄位是null時序列化也丟出JsonException()
    {
        // App 寫狀態表時就擋下來，不會寫出命令列讀不了的內容
        Assert.ThrowsAny<JsonException>(() => StatusJson.Serialize(Sample() with { RefreshMode = null! }));
    }

    [Fact]
    public void JSON是null時回傳null()
    {
        Assert.Null(StatusJson.Deserialize<StatusPayload>("null"));
    }

    [Fact]
    public void 序列化選項是唯讀的()
    {
        // App 與命令列共用同一份選項，不能被任何一方改掉。
        Assert.True(StatusJson.Options.IsReadOnly);
    }

    // ── 時間格式：一律 UTC、結尾 Z（第二階段 spec §10.3 的範例）──────────

    [Fact]
    public void 時間輸出成UTC的Z格式()
    {
        // 台北時間 10:00 = UTC 02:00；可為 null 的欄位有值時（lastUpdatedAt、startedAt）也一樣。
        var taipei = TimeSpan.FromHours(8);
        var payload = Sample() with
        {
            SnapshotAt = new DateTimeOffset(2026, 10, 29, 10, 0, 0, taipei),
            LastUpdatedAt = new DateTimeOffset(2026, 10, 29, 9, 59, 58, taipei),
            Runs = [Run() with { StartedAt = new DateTimeOffset(2026, 10, 29, 9, 50, 0, taipei) }],
        };

        using var document = JsonDocument.Parse(StatusJson.Serialize(payload));
        var root = document.RootElement;

        Assert.Equal("2026-10-29T02:00:00Z", root.GetProperty("snapshotAt").GetString());
        Assert.Equal("2026-10-29T01:59:58Z", root.GetProperty("lastUpdatedAt").GetString());
        Assert.Equal("2026-10-29T01:50:00Z", root.GetProperty("runs")[0].GetProperty("startedAt").GetString());
    }

    [Fact]
    public void 有小數秒時保留()
    {
        // 1,234,500 ticks = 0.12345 秒；結尾的 0 不寫。
        var at = SnapshotAt.AddTicks(1_234_500);
        var payload = Sample() with { SnapshotAt = at, Connection = new StatusConnection(false, at, false) };

        var json = StatusJson.Serialize(payload);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("2026-10-29T02:00:00.12345Z", root.GetProperty("snapshotAt").GetString());
        Assert.Equal("2026-10-29T02:00:00.12345Z", root.GetProperty("connection").GetProperty("pausedUntil").GetString());
        var back = StatusJson.Deserialize<StatusPayload>(json);
        Assert.NotNull(back);
        Assert.Equal(at, back.SnapshotAt);
        Assert.Equal(at, back.Connection.PausedUntil);
    }

    [Fact]
    public void 可為null的時間輸出null()
    {
        var payload = Sample() with
        {
            LastUpdatedAt = null,
            Connection = new StatusConnection(Offline: false, PausedUntil: null, ServerErrors: false),
            Runs = [Run() with { StartedAt = null }],
        };

        var json = StatusJson.Serialize(payload);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastUpdatedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("connection").GetProperty("pausedUntil").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("runs")[0].GetProperty("startedAt").ValueKind);
        var back = StatusJson.Deserialize<StatusPayload>(json);
        Assert.NotNull(back);
        Assert.Null(back.LastUpdatedAt);
        Assert.Null(back.Connection.PausedUntil);
        Assert.Null(Assert.Single(back.Runs).StartedAt);
    }

    [Fact]
    public void 讀得回加號時區格式()
    {
        // 其他程式寫的、或 Z 格式之前寫進狀態表的時間：任何 ISO 8601 時區寫法都讀成同一個時間點。
        var json = StatusJson.Serialize(Sample());
        json = ReplaceValue(json, "snapshotAt", "\"2026-10-29T02:00:00Z\"", "\"2026-10-29T10:00:00+08:00\"");
        json = ReplaceValue(json, "lastUpdatedAt", "\"2026-10-29T01:59:58Z\"", "\"2026-10-29T01:59:58+00:00\"");
        json = ReplaceValue(json, "startedAt", "\"2026-10-29T01:50:00Z\"", "\"2026-10-29T01:50:00.000Z\"");

        var back = StatusJson.Deserialize<StatusPayload>(json);

        Assert.NotNull(back);
        Assert.Equal(SnapshotAt, back.SnapshotAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 1, 59, 58, TimeSpan.Zero), back.LastUpdatedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 1, 50, 0, TimeSpan.Zero), Assert.Single(back.Runs).StartedAt);
    }

    [Theory]
    [InlineData("\"yesterday\"")]
    [InlineData("\"2026-13-40T00:00:00Z\"")]
    [InlineData("\"\"")]
    [InlineData("1798761600")]
    [InlineData("true")]
    [InlineData("\"2026-10-29T02:00:00\"")]
    [InlineData("\"2026-10-29\"")]
    public void 時間格式不對丟出JsonException(string value)
    {
        // 沒有 Z 或 ±hh:mm 的時間不知道是哪個時區（以前會被當成本機時間），一律拒收。
        var json = ReplaceValue(StatusJson.Serialize(Sample()), "snapshotAt", "\"2026-10-29T02:00:00Z\"", value);

        var ex = Assert.ThrowsAny<JsonException>(() => StatusJson.Deserialize<StatusPayload>(json));

        // 訊息是英文、純 ASCII：命令列的 stderr 只能有英文（第二階段 spec §10.3）。
        Assert.All(ex.Message, c => Assert.True(c <= MaxAscii, $"非 ASCII 字元 U+{(int)c:X4}"));
    }

    // ── 輔助 ───────────────────────────────────────────────────────────

    /// <summary>第二階段 spec §10.3 範例的 App 寫的部分。</summary>
    private static StatusPayload Sample() => new(
        AppVersion: AppVersion,
        SnapshotAt: SnapshotAt,
        Overall: "failure",
        SignedIn: true,
        ApiVersionError: false,
        RefreshMode: "auto",
        Counts: new StatusCounts(Failing: 1, InProgress: 2),
        MonitoredRepos: 11,
        StaleRepos: 0,
        Connection: new StatusConnection(Offline: false, PausedUntil: null, ServerErrors: false),
        LastUpdatedAt: new DateTimeOffset(2026, 10, 29, 1, 59, 58, TimeSpan.Zero),
        Runs: [Run()]);

    private static StatusRun Run() => new(
        Repo: "octo-org/sample-app",
        Workflow: "CI",
        RunId: 30433642,
        RunNumber: 123,
        Attempt: 1,
        Title: "Fix tests",
        Branch: "main",
        Event: "push",
        Status: "completed",
        Conclusion: "failure",
        State: "failure",
        Cancelling: false,
        StartedAt: new DateTimeOffset(2026, 10, 29, 1, 50, 0, TimeSpan.Zero),
        Url: "https://github.com/octo-org/sample-app/actions/runs/30433642");

    private static List<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToList();

    /// <summary>把 <paramref name="key"/> 的值從 <paramref name="original"/> 換成 <paramref name="replacement"/>（原文要找得到）。</summary>
    private static string ReplaceValue(string json, string key, string original, string replacement)
    {
        var from = $"\"{key}\":{original}";
        Assert.Contains(from, json);
        return json.Replace(from, $"\"{key}\":{replacement}", StringComparison.Ordinal);
    }
}
