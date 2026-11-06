using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using IsItGreenYet.Cli;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Status;
using IsItGreenYet.Core.Tests.TestSupport;
using IsItGreenYet.Core.Tracking;
using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Core.Tests.Cli;

/// <summary>
/// 命令列 IsItGreenYet-cli（第二階段 spec §10）：§10.6 的退出碼順序表、一行純 ASCII 的 JSON、只讀不建檔不改名、
/// <c>--refresh</c> 的訊號與等待。暫存目錄裡建真的 DB；App 是否在執行、刷新事件用假的；時間用手動時鐘，等待立即完成並推進時鐘。
/// </summary>
public class CliTests
{
    /// <summary>InformationalVersion 的格式：版本號接 <c>+</c> 與 40 字元的 commit（假的）。</summary>
    private const string CliVersion = "0.2.0+fedcba9876543210fedcba9876543210fedcba98";

    private const string AppVersion = "0.2.0+0123456789abcdef0123456789abcdef01234567";

    /// <summary>ASCII 的最大字元；stdout 與 stderr 只能有這個範圍的字元（第二階段 spec §10.3、§11.3）。</summary>
    private const char MaxAscii = '\u007F';

    /// <summary>例外訊息含中文：命令列不能把它印出來（stdout、stderr 都不行）。</summary>
    private const string ChineseExceptionMessage = "測試用的例外：這段訊息不能出現在輸出裡";

    private const string NoticesCommand = "notices";

    /// <summary>狀態表裡刷新的序號：App 沒在刷新時 started 等於 done。</summary>
    private const long IdleSeq = 3;

    /// <summary>§10.3 範例的 generatedAt：命令列執行的時間。</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 2, 0, 5, TimeSpan.Zero);

    /// <summary>§10.3 範例的 snapshotAt、heartbeatAt：App 在 5 秒前寫的。</summary>
    private static readonly DateTimeOffset WrittenAt = new(2026, 10, 29, 2, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset LastUpdatedAt = new(2026, 10, 29, 1, 59, 58, TimeSpan.Zero);

    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    /// <summary>上一次執行結束時寫的 stopped_at（也是那一列最後的心跳）。</summary>
    private static readonly DateTimeOffset PreviousStop = WrittenAt - TimeSpan.FromHours(1);

    /// <summary>心跳一直沒前進時，命令列等滿 StatusHeartbeatStale 要讀幾次（每次間隔 CliRefreshPollInterval）。</summary>
    private static readonly int PollsUntilHeartbeatStale = (int)(Tuning.StatusHeartbeatStale / Tuning.CliRefreshPollInterval);

    private static readonly string[] StatusArgs = ["status", "--json"];

    private static readonly string[] RefreshArgs = ["status", "--json", "--refresh"];

    /// <summary>Engine 的整體狀態 → §10.6 第 12～15 列（App 寫的 overall 是什麼由 Engine 決定，命令列只認得這四種）。</summary>
    private static readonly Dictionary<OverallState, (int ExitCode, string State, string? Reason)> EngineOverallContract = new()
    {
        [OverallState.Failure] = (Contract.ExitFailure, Contract.StateFailure, null),
        [OverallState.Pending] = (Contract.ExitInProgress, Contract.StateInProgress, null),
        [OverallState.Success] = (Contract.ExitSuccess, Contract.StateSuccess, null),
        [OverallState.NoData] = (Contract.ExitNoData, Contract.StateNoData, Contract.ReasonNoData),
    };

    /// <summary>§10.3 範例的鍵名與順序。</summary>
    private static readonly string[] OutputKeys =
    [
        "schemaVersion", "state", "exitCode", "reason", "cliVersion", "appVersion", "generatedAt", "snapshotAt",
        "heartbeatAt", "refreshed", "refreshPartial", "overall", "signedIn", "apiVersionError", "refreshMode", "counts",
        "monitoredRepos", "staleRepos", "connection", "lastUpdatedAt", "runs",
    ];

    // ── 退出碼（§10.6）────────────────────────────────────────────────

    [Theory]
    [InlineData("參數錯誤", 1, Contract.ExitError, null, null)]
    [InlineData("未預期的例外", 2, Contract.ExitError, Contract.StateError, Contract.ReasonInternalError)]
    [InlineData("App沒在跑", 3, Contract.ExitError, Contract.StateError, Contract.ReasonAppNotRunning)]
    [InlineData("Mutex存取被拒", 3, Contract.ExitError, Contract.StateError, Contract.ReasonRefreshUnreachable)]
    [InlineData("沒有刷新事件", 3, Contract.ExitError, Contract.StateError, Contract.ReasonRefreshUnsupported)]
    [InlineData("事件存取被拒", 3, Contract.ExitError, Contract.StateError, Contract.ReasonRefreshUnreachable)]
    [InlineData("檔案損毀", 4, Contract.ExitError, Contract.StateError, Contract.ReasonDbError)]
    [InlineData("開不了檔", 4, Contract.ExitError, Contract.StateError, Contract.ReasonDbError)]
    [InlineData("時間欄位不是ISO8601", 4, Contract.ExitError, Contract.StateError, Contract.ReasonDbError)]
    [InlineData("status_json不是JSON", 4, Contract.ExitError, Contract.StateError, Contract.ReasonDbError)]
    [InlineData("status_json少了必要欄位", 4, Contract.ExitError, Contract.StateError, Contract.ReasonDbError)]
    [InlineData("overall不認得", 4, Contract.ExitError, Contract.StateError, Contract.ReasonDbError)]
    [InlineData("schema版本不符", 4, Contract.ExitError, Contract.StateError, Contract.ReasonSchemaMismatch)]
    [InlineData("沒有資料庫", 5, Contract.ExitNoData, Contract.StateNoData, Contract.ReasonNoStatus)]
    [InlineData("第一階段的資料庫", 5, Contract.ExitNoData, Contract.StateNoData, Contract.ReasonNoStatus)]
    [InlineData("狀態表沒有資料", 5, Contract.ExitNoData, Contract.StateNoData, Contract.ReasonNoStatus)]
    [InlineData("刷新逾時", 6, Contract.ExitError, Contract.StateError, Contract.ReasonRefreshTimeout)]
    [InlineData("App已正常結束", 7, Contract.ExitError, Contract.StateError, Contract.ReasonAppNotRunning)]
    [InlineData("心跳過期", 8, Contract.ExitError, Contract.StateError, Contract.ReasonStale)]
    [InlineData("未登入", 9, Contract.ExitError, Contract.StateError, Contract.ReasonSignedOut)]
    [InlineData("API版本錯誤", 10, Contract.ExitError, Contract.StateError, Contract.ReasonVersionError)]
    [InlineData("還沒有成功回應", 11, Contract.ExitError, Contract.StateError, Contract.ReasonDataStale)]
    [InlineData("超過1小時沒有成功回應", 11, Contract.ExitError, Contract.StateError, Contract.ReasonDataStale)]
    [InlineData("失敗", 12, Contract.ExitFailure, Contract.StateFailure, null)]
    [InlineData("進行中", 13, Contract.ExitInProgress, Contract.StateInProgress, null)]
    [InlineData("成功", 14, Contract.ExitSuccess, Contract.StateSuccess, null)]
    [InlineData("沒有資料", 15, Contract.ExitNoData, Contract.StateNoData, Contract.ReasonNoData)]
    public async Task 退出碼照順序表的每一列(string scenario, int tableRow, int exitCode, string? state, string? reason)
    {
        using var harness = new Harness();
        var (args, hold) = Arrange(harness, scenario);

        CliResult result;
        using (hold)
        {
            result = await harness.RunAsync(args);
        }

        Assert.True(exitCode == result.ExitCode, $"第 {tableRow} 列（{scenario}）：退出碼 {result.ExitCode}，stderr：{result.Stderr}");
        if (state is null)
        {
            // 第 1 列：不輸出 JSON
            Assert.Empty(result.Stdout);
            return;
        }

        var json = result.Json();
        Assert.Equal(state, json.GetProperty("state").GetString());
        Assert.Equal(exitCode, json.GetProperty("exitCode").GetInt32());
        Assert.Equal(reason, json.GetProperty("reason").GetString());
    }

    [Fact]
    public void 退出碼state與reason的常數照spec的字面值()
    {
        // 產品常數也是命令列接受的 overall 白名單：改錯值時這裡會失敗，不會只是「產品等於產品自己」
        Assert.Equal(
            [Contract.ExitSuccess, Contract.ExitFailure, Contract.ExitInProgress, Contract.ExitNoData, Contract.ExitError],
            [CliExitCode.Success, CliExitCode.Failure, CliExitCode.InProgress, CliExitCode.NoData, CliExitCode.Error]);
        Assert.Equal(
            [Contract.StateSuccess, Contract.StateFailure, Contract.StateInProgress, Contract.StateNoData, Contract.StateError],
            [CliState.Success, CliState.Failure, CliState.InProgress, CliState.NoData, CliState.Error]);
        Assert.Equal(
            [
                Contract.ReasonInternalError, Contract.ReasonAppNotRunning, Contract.ReasonRefreshUnreachable,
                Contract.ReasonRefreshUnsupported, Contract.ReasonDbError, Contract.ReasonSchemaMismatch, Contract.ReasonNoStatus,
                Contract.ReasonRefreshTimeout, Contract.ReasonStale, Contract.ReasonSignedOut, Contract.ReasonVersionError,
                Contract.ReasonDataStale, Contract.ReasonNoData,
            ],
            [
                CliReason.InternalError, CliReason.AppNotRunning, CliReason.RefreshUnreachable,
                CliReason.RefreshUnsupported, CliReason.DbError, CliReason.SchemaMismatch, CliReason.NoStatus,
                CliReason.RefreshTimeout, CliReason.Stale, CliReason.SignedOut, CliReason.VersionError,
                CliReason.DataStale, CliReason.NoData,
            ]);
        Assert.Equal(Contract.SchemaVersion, CliOutput.CurrentSchemaVersion);
    }

    public static TheoryData<OverallState> EngineOverallStates => new(Enum.GetValues<OverallState>());

    [Theory]
    [MemberData(nameof(EngineOverallStates))]
    public async Task Engine寫的每一種overall命令列都認得(OverallState overall)
    {
        // Engine 新增整體狀態時這裡沒有對應就失敗：命令列的白名單與退出碼要一起補
        Assert.True(EngineOverallContract.TryGetValue(overall, out var expected), $"{overall} 沒有對應的退出碼");
        using var harness = new Harness();
        // 用 App 寫狀態表時同一個轉換組 overall（MonitorEngine.Status.cs），不經過命令列的常數
        var payload = MonitorEngine.BuildStatusPayload(EngineSnapshot(overall), AppVersion, WrittenAt, pausedUntil: null,
            lastRepoSuccessUtc: LastUpdatedAt);
        harness.Save(Row(payload));

        var result = await harness.RunAsync(StatusArgs);

        Assert.Equal(expected.ExitCode, result.ExitCode);
        var json = result.Json();
        Assert.Equal(expected.State, json.GetProperty("state").GetString());
        Assert.Equal(expected.Reason, json.GetProperty("reason").GetString());
        Assert.Equal(payload.Overall, json.GetProperty("overall").GetString());
    }

    [Fact]
    public async Task 失敗與進行中同時成立回1()
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload() with
        {
            Overall = Contract.StateFailure,
            Counts = new StatusCounts(Failing: 1, InProgress: 2),
            Runs = [Run(), Run() with { RunId = 30433643, Status = "in_progress", Conclusion = null, State = "pending" }],
        });

        var result = await harness.RunAsync(StatusArgs);

        // 1 > 2 > 0 和系統匣一致（P1 §7.3）；命令列不自己從 counts 推，照 App 算好的 overall
        Assert.Equal(Contract.ExitFailure, result.ExitCode);
        Assert.Equal(Contract.StateFailure, result.Json().GetProperty("state").GetString());
    }

    [Fact]
    public async Task 等待核准算進行中回2()
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload() with
        {
            Overall = Contract.StateInProgress,
            Counts = new StatusCounts(Failing: 0, InProgress: 1),
            Runs = [Run() with { Status = "waiting", Conclusion = null, State = "waiting" }],
        });

        var result = await harness.RunAsync(StatusArgs);

        Assert.Equal(Contract.ExitInProgress, result.ExitCode);
        var json = result.Json();
        Assert.Equal(Contract.StateInProgress, json.GetProperty("state").GetString());
        Assert.Equal("waiting", json.GetProperty("runs")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task signedIn是null不算未登入()
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload() with { SignedIn = null });

        var result = await harness.RunAsync(StatusArgs);

        // 還在確認（App 剛啟動、E1 還沒驗證完）不算未登入（§10.6）
        Assert.Equal(Contract.ExitSuccess, result.ExitCode);
        var json = result.Json();
        Assert.Equal(Contract.StateSuccess, json.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("signedIn").ValueKind);
    }

    [Fact]
    public async Task 監看0個repo而且沒有lastUpdatedAt不算資料過舊()
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload() with { MonitoredRepos = 0, LastUpdatedAt = null, Overall = Contract.StateNoData });

        var result = await harness.RunAsync(StatusArgs);

        // 第 11 列只在 monitoredRepos > 0 時成立；這裡落到第 15 列
        Assert.Equal(Contract.ExitNoData, result.ExitCode);
        var json = result.Json();
        Assert.Equal(Contract.StateNoData, json.GetProperty("state").GetString());
        Assert.Equal(Contract.ReasonNoData, json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task 自動更新暫停超過1小時回data_stale()
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload() with
        {
            RefreshMode = "paused",
            LastUpdatedAt = Now - Tuning.StaleDataThreshold - OneSecond,
        });

        var result = await harness.RunAsync(StatusArgs);

        // 心跳照寫（Engine 還在跑），但資料超過 1 小時沒更新（§10.6 第 11 列的說明）
        Assert.Equal(Contract.ExitError, result.ExitCode);
        var json = result.Json();
        Assert.Equal(Contract.ReasonDataStale, json.GetProperty("reason").GetString());
        Assert.Equal("paused", json.GetProperty("refreshMode").GetString());
    }

    [Fact]
    public async Task 剛好在門檻上不算過期()
    {
        using var harness = new Harness();
        // 「超過」才算：心跳剛好 180 秒、最近一次成功回應剛好 1 小時都還不算
        harness.SaveStatus(
            Payload() with { LastUpdatedAt = Now - Tuning.StaleDataThreshold },
            row => row with { HeartbeatAt = Now - Tuning.StatusHeartbeatStale });

        var result = await harness.RunAsync(StatusArgs);

        Assert.Equal(Contract.ExitSuccess, result.ExitCode);
    }

    [Fact]
    public async Task 條件同時成立時取順序在前的列()
    {
        using var harness = new Harness();
        var payload = Payload() with
        {
            Overall = Contract.StateFailure,
            SignedIn = false,
            ApiVersionError = true,
            LastUpdatedAt = null,
        };
        var row = Row(payload) with { StoppedAt = WrittenAt, HeartbeatAt = Now - Tuning.StatusHeartbeatStale - OneSecond };

        // 每次拿掉最前面那一列的條件，結果就換成下一列（§10.6：由上往下判斷，第一個成立的就是結果）
        var steps = new (string? Reason, Func<StatusSnapshotRow, StatusSnapshotRow> Next)[]
        {
            (Contract.ReasonAppNotRunning, r => r with { StoppedAt = null }),
            (Contract.ReasonStale, r => r with { HeartbeatAt = WrittenAt }),
            (Contract.ReasonSignedOut, r => Edit(r, p => p with { SignedIn = true })),
            (Contract.ReasonVersionError, r => Edit(r, p => p with { ApiVersionError = false })),
            (Contract.ReasonDataStale, r => Edit(r, p => p with { LastUpdatedAt = LastUpdatedAt })),
            (null, r => r),
        };

        foreach (var (reason, next) in steps)
        {
            harness.Save(row);
            var result = await harness.RunAsync(StatusArgs);
            Assert.Equal(reason, result.Json().GetProperty("reason").GetString());
            row = next(row);
        }

        Assert.Equal(Contract.ExitFailure, (await harness.RunAsync(StatusArgs)).ExitCode);
    }

    // ── status_json 不能用（§10.6 第 4 列）───────────────────────────────

    [Theory]
    [InlineData("status_json不是JSON")]
    [InlineData("status_json少了必要欄位")]
    [InlineData("overall不認得")]
    [InlineData("status_json含單獨的代理字元")]
    public async Task status_json不能用時回db_error並附上資料列的欄位(string scenario)
    {
        using var harness = new Harness();
        var (args, _) = Arrange(harness, scenario);

        var result = await harness.RunAsync(args);

        Assert.Equal(Contract.ExitError, result.ExitCode);
        var json = result.Json();
        Assert.Equal(Contract.ReasonDbError, json.GetProperty("reason").GetString());
        // 資料列層級的欄位照附；status_json 裡的欄位都是 null、runs 是空陣列
        Assert.Equal(AppVersion, json.GetProperty("appVersion").GetString());
        Assert.Equal("2026-10-29T02:00:00Z", json.GetProperty("heartbeatAt").GetString());
        foreach (var key in new[] { "snapshotAt", "overall", "signedIn", "apiVersionError", "refreshMode", "counts",
                     "monitoredRepos", "staleRepos", "connection", "lastUpdatedAt" })
        {
            Assert.True(json.GetProperty(key).ValueKind == JsonValueKind.Null, $"{key} 不是 null");
        }

        Assert.Equal(0, json.GetProperty("runs").GetArrayLength());
    }

    [Theory]
    [InlineData("monitoredRepos")]
    [InlineData("apiVersionError")]
    [InlineData("snapshotAt")]
    public async Task status_json缺少數值欄位時回db_error(string key)
    {
        using var harness = new Harness();
        // 不能補成 0、false、最小日期：monitoredRepos 是 0 會讓第 11 列不成立，apiVersionError 是 false 會讓第 10 列不成立
        harness.SaveStatus(Payload(), row => row with { StatusJson = WithoutProperty(row.StatusJson, key) });

        var result = await harness.RunAsync(StatusArgs);

        Assert.Equal(Contract.ExitError, result.ExitCode);
        Assert.Equal(Contract.ReasonDbError, result.Json().GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("appVersion")]
    [InlineData("refreshMode")]
    [InlineData("runs/0/title")]
    public async Task status_json必要字串是null時回db_error(string path)
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload(), row => row with { StatusJson = WithNull(row.StatusJson, path) });

        var result = await harness.RunAsync(StatusArgs);

        Assert.Equal(Contract.ExitError, result.ExitCode);
        Assert.Equal(Contract.ReasonDbError, result.Json().GetProperty("reason").GetString());
    }

    // ── 輸出（§10.3）──────────────────────────────────────────────────

    [Fact]
    public async Task 輸出是一行純ASCII()
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload());

        var result = await harness.RunAsync(StatusArgs);

        // stdout 只有一個 JSON 物件加換行；版本字串的 + 會被跳脫，解析後的值不變
        Assert.EndsWith(Environment.NewLine, result.Stdout, StringComparison.Ordinal);
        var line = result.Stdout[..^Environment.NewLine.Length];
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        AssertAscii(result.Stdout);
        Assert.Empty(result.Stderr);

        var json = result.Json();
        Assert.Equal(OutputKeys, json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Contract.SchemaVersion, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Contract.StateSuccess, json.GetProperty("state").GetString());
        Assert.Equal(Contract.ExitSuccess, json.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("reason").ValueKind);
        Assert.Equal(CliVersion, json.GetProperty("cliVersion").GetString());
        Assert.Equal(AppVersion, json.GetProperty("appVersion").GetString());
        Assert.Equal("2026-10-29T02:00:05Z", json.GetProperty("generatedAt").GetString());
        Assert.Equal("2026-10-29T02:00:00Z", json.GetProperty("snapshotAt").GetString());
        Assert.Equal("2026-10-29T02:00:00Z", json.GetProperty("heartbeatAt").GetString());
        Assert.False(json.GetProperty("refreshed").GetBoolean());
        Assert.False(json.GetProperty("refreshPartial").GetBoolean());
        Assert.Equal(Contract.StateSuccess, json.GetProperty("overall").GetString());
        Assert.True(json.GetProperty("signedIn").GetBoolean());
        Assert.False(json.GetProperty("apiVersionError").GetBoolean());
        Assert.Equal("auto", json.GetProperty("refreshMode").GetString());
        Assert.Equal(0, json.GetProperty("counts").GetProperty("failing").GetInt32());
        Assert.Equal(11, json.GetProperty("monitoredRepos").GetInt32());
        Assert.Equal(0, json.GetProperty("staleRepos").GetInt32());
        Assert.False(json.GetProperty("connection").GetProperty("offline").GetBoolean());
        Assert.Equal("2026-10-29T01:59:58Z", json.GetProperty("lastUpdatedAt").GetString());
        Assert.Equal("octo-org/sample-app", json.GetProperty("runs")[0].GetProperty("repo").GetString());
    }

    [Fact]
    public async Task 中文標題輸出成跳脫字串且解析後相同()
    {
        using var harness = new Harness();
        var run = Run() with { Repo = "octo-org/範例", Workflow = "持續整合", Title = "修正登入", Branch = "功能/登入" };
        harness.SaveStatus(Payload() with { Overall = Contract.StateFailure, Runs = [run] });

        var result = await harness.RunAsync(StatusArgs);

        // 只有純 ASCII 在每一種 shell 的接法下都讀得回正確的中文（§11.3）
        AssertAscii(result.Stdout);
        Assert.Contains("\\u", result.Stdout, StringComparison.Ordinal);
        var parsed = result.Json().GetProperty("runs")[0];
        Assert.Equal(run.Repo, parsed.GetProperty("repo").GetString());
        Assert.Equal(run.Workflow, parsed.GetProperty("workflow").GetString());
        Assert.Equal(run.Title, parsed.GetProperty("title").GetString());
        Assert.Equal(run.Branch, parsed.GetProperty("branch").GetString());
    }

    [Fact]
    public async Task 錯誤時讀得到狀態表仍附上資料()
    {
        using var harness = new Harness();
        harness.SaveStatus(
            Payload() with { Overall = Contract.StateFailure, Counts = new StatusCounts(1, 0) },
            row => row with { HeartbeatAt = Now - Tuning.StatusHeartbeatStale - OneSecond });

        var result = await harness.RunAsync(StatusArgs);

        Assert.Equal(Contract.ExitError, result.ExitCode);
        var json = result.Json();
        Assert.Equal(Contract.ReasonStale, json.GetProperty("reason").GetString());
        // 方便判斷原因：App 寫的欄位照附（§10.6）
        Assert.Equal(AppVersion, json.GetProperty("appVersion").GetString());
        Assert.Equal(Contract.StateFailure, json.GetProperty("overall").GetString());
        Assert.Equal(1, json.GetProperty("counts").GetProperty("failing").GetInt32());
        Assert.Equal(1, json.GetProperty("runs").GetArrayLength());
        // 錯誤說明在 stderr，英文、純 ASCII
        Assert.NotEmpty(result.Stderr);
        AssertAscii(result.Stderr);
    }

    [Theory]
    [InlineData("")]
    [InlineData("status")]
    [InlineData("--json")]
    [InlineData("status --refresh")]
    [InlineData("status --json --bogus")]
    [InlineData("status --json --json")]
    [InlineData("status --json --refresh --refresh")]
    [InlineData("--json status")]
    [InlineData("STATUS --json")]
    [InlineData("--help status")]
    [InlineData("-h")]
    [InlineData("notices --json")]
    [InlineData("notices notices")]
    [InlineData("NOTICES")]
    [InlineData("--help notices")]
    [InlineData("status --json notices")]
    public async Task 參數錯誤不輸出JSON而且stderr是英文(string commandLine)
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload());

        var result = await harness.RunAsync(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(Contract.ExitError, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("Usage:", result.Stderr, StringComparison.Ordinal);
        AssertAscii(result.Stderr);
        Assert.Empty(harness.Events);
    }

    [Fact]
    public async Task help回0()
    {
        using var harness = new Harness();

        var result = await harness.RunAsync("--help");

        Assert.Equal(Contract.ExitSuccess, result.ExitCode);
        Assert.Contains("Usage:", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("status --json [--refresh]", result.Stdout, StringComparison.Ordinal);
        // 含行尾：--help 的導向提示也有 IsItGreenYet-cli notices，只比對子字串時刪掉用法行還是會通過
        Assert.Contains("  IsItGreenYet-cli notices" + Environment.NewLine, result.Stdout, StringComparison.Ordinal);
        AssertAscii(result.Stdout);
        Assert.Empty(result.Stderr);
        Assert.False(File.Exists(harness.DatabasePath));
    }

    [Fact]
    public async Task 未預期的例外不印出例外訊息()
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload());
        harness.AfterDelay = _ => throw new InvalidOperationException(ChineseExceptionMessage);

        var result = await harness.RunAsync(RefreshArgs);

        Assert.Equal(Contract.ExitError, result.ExitCode);
        var json = result.Json();
        Assert.Equal(Contract.ReasonInternalError, json.GetProperty("reason").GetString());
        // 例外之前讀到的狀態表照附
        Assert.Equal(Contract.StateSuccess, json.GetProperty("overall").GetString());
        // 例外的 Message 可能有中文，stdout、stderr 都只用固定的英文說明
        Assert.DoesNotContain(ChineseExceptionMessage, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(ChineseExceptionMessage, result.Stderr, StringComparison.Ordinal);
        Assert.NotEmpty(result.Stderr);
        AssertAscii(result.Stdout);
        AssertAscii(result.Stderr);
    }

    // ── 檔案（§10.1、§10.4）─────────────────────────────────────────────

    [Fact]
    public async Task 沒有資料庫時不建檔()
    {
        using var harness = new Harness();
        var before = Files(harness.Workspace.Root);

        var result = await harness.RunAsync(StatusArgs);

        Assert.Equal(Contract.ExitNoData, result.ExitCode);
        Assert.Equal(before, Files(harness.Workspace.Root));
    }

    [Fact]
    public async Task 有資料庫時只可能多出wal與shm()
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload());
        SqliteConnection.ClearAllPools();
        var before = Files(harness.Workspace.Root);
        var content = File.ReadAllBytes(harness.DatabasePath);

        var result = await harness.RunAsync(StatusArgs);

        // WAL 模式的唯讀連線可能在旁邊建立附屬檔（§11.2）；cache.db 本身的內容不變
        Assert.Equal(Contract.ExitSuccess, result.ExitCode);
        var after = Files(harness.Workspace.Root);
        Assert.Empty(before.Except(after));
        Assert.Empty(after.Except(before).Except(["cache.db-wal", "cache.db-shm"]));
        Assert.Equal(content, File.ReadAllBytes(harness.DatabasePath));
    }

    [Fact]
    public async Task 第一階段的資料庫回no_status()
    {
        using var harness = new Harness();
        PhaseOneDatabase.Create(harness.DatabasePath);

        var result = await harness.RunAsync(StatusArgs);

        Assert.Equal(Contract.ExitNoData, result.ExitCode);
        var json = result.Json();
        Assert.Equal(Contract.ReasonNoStatus, json.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("appVersion").ValueKind);
        Assert.Equal(0, json.GetProperty("runs").GetArrayLength());
        // 只讀：不會順手補建表
        Assert.Null(Scalar(harness.DatabasePath, "SELECT name FROM sqlite_master WHERE name = 'status_snapshot';"));
    }

    [Fact]
    public async Task 不改名損毀的資料庫()
    {
        using var harness = new Harness();
        var content = Encoding.UTF8.GetBytes("這不是 SQLite 檔，是使用者誤蓋上去的垃圾內容");
        File.WriteAllBytes(harness.DatabasePath, content);

        var result = await harness.RunAsync(StatusArgs);

        // 絕不呼叫 Ensure／Recover：不改名、不重建（§10.4）
        Assert.Equal(Contract.ExitError, result.ExitCode);
        Assert.Equal(Contract.ReasonDbError, result.Json().GetProperty("reason").GetString());
        Assert.Equal(content, File.ReadAllBytes(harness.DatabasePath));
        Assert.Empty(Directory.GetFiles(harness.Workspace.Root, "cache.db.corrupt-*"));
    }

    // ── --refresh（§10.5）─────────────────────────────────────────────

    [Fact]
    public async Task App沒在跑回app_not_running()
    {
        using var harness = new Harness { Probe = () => AppProbe.NotRunning };
        harness.SaveStatus(Payload());

        var result = await harness.RunAsync(RefreshArgs);

        Assert.Equal(Contract.ExitError, result.ExitCode);
        var json = result.Json();
        Assert.Equal(Contract.ReasonAppNotRunning, json.GetProperty("reason").GetString());
        Assert.False(json.GetProperty("refreshed").GetBoolean());
        // 讀得到狀態表就附上資料；沒有去開事件，也沒有等
        Assert.Equal(Contract.StateSuccess, json.GetProperty("overall").GetString());
        Assert.Equal(["probe"], harness.Events);
    }

    [Fact]
    public async Task 開不到事件回refresh_unsupported()
    {
        using var harness = new Harness { SignalResult = SignalOpen.Missing };
        harness.SaveStatus(Payload());

        var result = await harness.RunAsync(RefreshArgs);

        // 第一階段的 App 沒有刷新事件（§14）
        Assert.Equal(Contract.ExitError, result.ExitCode);
        Assert.Equal(Contract.ReasonRefreshUnsupported, result.Json().GetProperty("reason").GetString());
        Assert.Equal(["probe", "open"], harness.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 存取被拒回refresh_unreachable(bool mutexDenied)
    {
        using var harness = new Harness();
        if (mutexDenied)
        {
            harness.Probe = () => AppProbe.AccessDenied;
        }
        else
        {
            harness.SignalResult = SignalOpen.AccessDenied;
        }

        harness.SaveStatus(Payload());

        var result = await harness.RunAsync(RefreshArgs);

        Assert.Equal(Contract.ExitError, result.ExitCode);
        Assert.Equal(Contract.ReasonRefreshUnreachable, result.Json().GetProperty("reason").GetString());
        Assert.DoesNotContain("set", harness.Events);
        Assert.DoesNotContain("delay", harness.Events);
    }

    [Fact]
    public async Task 讀不到狀態表時不發訊號()
    {
        using var harness = new Harness();
        DatabaseInitializer.Ensure(harness.DatabasePath);

        var result = await harness.RunAsync(RefreshArgs);

        // §10.5 命令列端第 3 步：讀不到 started／done 就照 §10.6 回 no_status，事件照樣關閉
        Assert.Equal(Contract.ExitNoData, result.ExitCode);
        Assert.Equal(Contract.ReasonNoStatus, result.Json().GetProperty("reason").GetString());
        Assert.Equal(["probe", "open", "dispose"], harness.Events);
    }

    [Fact]
    public async Task done追上後回refreshed()
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload());
        harness.AfterDelay = poll =>
        {
            if (poll == 2)
            {
                // App 收到訊號、刷新完成：started 與 done 都前進 1，內容也更新了
                harness.SaveStatus(Payload() with { Overall = Contract.StateFailure },
                    row => row with { RefreshStartedSeq = IdleSeq + 1, RefreshDoneSeq = IdleSeq + 1 });
            }
        };

        var result = await harness.RunAsync(RefreshArgs);

        // 先發訊號、關掉事件，才開始每 500 毫秒讀一次；輸出用刷新後的資料
        Assert.Equal(["probe", "open", "set", "dispose", "delay", "delay"], harness.Events);
        Assert.Equal(Contract.ExitFailure, result.ExitCode);
        var json = result.Json();
        Assert.True(json.GetProperty("refreshed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("reason").ValueKind);
        Assert.Equal(Contract.StateFailure, json.GetProperty("overall").GetString());
    }

    [Fact]
    public async Task App正在刷新時要多等一次()
    {
        using var harness = new Harness();
        const long started = IdleSeq + 1;
        harness.SaveStatus(Payload() with { Overall = Contract.StateInProgress },
            row => row with { RefreshStartedSeq = started, RefreshDoneSeq = IdleSeq });
        harness.AfterDelay = poll =>
        {
            switch (poll)
            {
                case 1:
                    // 進行中的那一次完成，接著做訊號排的下一次：done 追上 s0 還不夠
                    harness.SaveStatus(Payload() with { Overall = Contract.StateInProgress },
                        row => row with { RefreshStartedSeq = started + 1, RefreshDoneSeq = started });
                    break;
                case 3:
                    harness.SaveStatus(Payload() with { Overall = Contract.StateSuccess },
                        row => row with { RefreshStartedSeq = started + 1, RefreshDoneSeq = started + 1 });
                    break;
            }
        };

        var result = await harness.RunAsync(RefreshArgs);

        Assert.Equal(3, harness.Delays);
        Assert.Equal(Contract.ExitSuccess, result.ExitCode);
        Assert.True(result.Json().GetProperty("refreshed").GetBoolean());
    }

    [Fact]
    public async Task 超過210秒回refresh_timeout並照樣輸出資料()
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload());
        harness.KeepBeatingWithoutFinishing(Payload());

        var result = await harness.RunAsync(RefreshArgs);

        // done 一直沒前進：等滿 CliRefreshTimeout 就停，不多等一輪
        var waited = Tuning.CliRefreshPollInterval * harness.Delays;
        Assert.InRange(waited, Tuning.CliRefreshTimeout, Tuning.CliRefreshTimeout + Tuning.CliRefreshPollInterval - TimeSpan.FromTicks(1));
        Assert.Equal(Contract.ExitError, result.ExitCode);
        var json = result.Json();
        Assert.Equal(Contract.ReasonRefreshTimeout, json.GetProperty("reason").GetString());
        Assert.False(json.GetProperty("refreshed").GetBoolean());
        Assert.False(json.GetProperty("refreshPartial").GetBoolean());
        Assert.Equal(Contract.StateSuccess, json.GetProperty("overall").GetString());
        Assert.Equal(AppVersion, json.GetProperty("appVersion").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task partial照表裡的值(bool partial)
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload());
        harness.AfterDelay = poll =>
        {
            if (poll == 1)
            {
                harness.SaveStatus(Payload(), row => row with
                {
                    RefreshStartedSeq = IdleSeq + 1,
                    RefreshDoneSeq = IdleSeq + 1,
                    RefreshPartial = partial,
                });
            }
        };

        var result = await harness.RunAsync(RefreshArgs);

        var json = result.Json();
        Assert.True(json.GetProperty("refreshed").GetBoolean());
        Assert.Equal(partial, json.GetProperty("refreshPartial").GetBoolean());
    }

    [Fact]
    public async Task 不帶refresh時refreshPartial是false()
    {
        using var harness = new Harness();
        // 表裡的 partial 是之前某一次刷新的結果，和這次輸出無關
        harness.SaveStatus(Payload(), row => row with { RefreshPartial = true });

        var result = await harness.RunAsync(StatusArgs);

        var json = result.Json();
        Assert.False(json.GetProperty("refreshed").GetBoolean());
        Assert.False(json.GetProperty("refreshPartial").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 等待刷新時App結束立即回app_not_running(bool previousStopInBaseline)
    {
        using var harness = new Harness();
        // 送出訊號前讀到的 stopped_at 可能是上一次執行留下的；App 寫了不一樣的新值才算這次結束了
        harness.SaveStatus(Payload(), row => row with { StoppedAt = previousStopInBaseline ? PreviousStop : null });
        harness.AfterDelay = poll =>
        {
            if (poll == 2)
            {
                // App 在刷新途中正常結束：只寫 stopped_at，done 不前進
                harness.SaveStatus(Payload(), row => row with { StoppedAt = Now });
            }
        };

        var result = await harness.RunAsync(RefreshArgs);

        Assert.Equal(2, harness.Delays);
        Assert.Equal(Contract.ExitError, result.ExitCode);
        var json = result.Json();
        Assert.Equal(Contract.ReasonAppNotRunning, json.GetProperty("reason").GetString());
        Assert.False(json.GetProperty("refreshed").GetBoolean());
    }

    [Fact]
    public async Task App剛啟動時沿用上次的stopped_at不會提早結束等待()
    {
        using var harness = new Harness();
        // App 剛建立事件、還沒寫第一次：那一列是上一次結束時寫的，stopped_at 有值、心跳也早就過期
        harness.SaveStatus(Payload(), row => row with { WrittenAt = PreviousStop, HeartbeatAt = PreviousStop, StoppedAt = PreviousStop });
        harness.AfterDelay = poll =>
        {
            var now = harness.Time.GetUtcNow();
            switch (poll)
            {
                case 2:
                    // 啟動後第一次寫入：清掉 stopped_at，收到的訊號開始刷新
                    harness.SaveStatus(Payload(), row => row with
                    {
                        WrittenAt = now, HeartbeatAt = now, StoppedAt = null, RefreshStartedSeq = IdleSeq + 1,
                    });
                    break;
                case 4:
                    harness.SaveStatus(Payload(), row => row with
                    {
                        WrittenAt = now, HeartbeatAt = now, RefreshStartedSeq = IdleSeq + 1, RefreshDoneSeq = IdleSeq + 1,
                    });
                    break;
            }
        };

        var result = await harness.RunAsync(RefreshArgs);

        Assert.Equal(4, harness.Delays);
        Assert.Equal(Contract.ExitSuccess, result.ExitCode);
        Assert.True(result.Json().GetProperty("refreshed").GetBoolean());
    }

    [Fact]
    public async Task 等待刷新時心跳一直沒前進180秒後回stale()
    {
        using var harness = new Harness();
        // App 停住了：送出訊號之後再也沒寫過狀態表
        harness.SaveStatus(Payload());

        var result = await harness.RunAsync(RefreshArgs);

        // 等滿 StatusHeartbeatStale 就停，不必等到 CliRefreshTimeout
        Assert.Equal(PollsUntilHeartbeatStale, harness.Delays);
        Assert.Equal(Contract.ExitError, result.ExitCode);
        var json = result.Json();
        Assert.Equal(Contract.ReasonStale, json.GetProperty("reason").GetString());
        Assert.False(json.GetProperty("refreshed").GetBoolean());
    }

    [Fact]
    public async Task 等待刷新時牆鐘跳過心跳門檻仍等滿180秒才回stale()
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload());
        harness.AfterDelay = poll =>
        {
            if (poll == 2)
            {
                // 只動牆鐘：狀態表的心跳看起來已經過期，但命令列才等了 1 秒，不算 App 停住
                harness.Time.SetUtcNow(WrittenAt + Tuning.StatusHeartbeatStale + OneSecond);
            }
        };

        var result = await harness.RunAsync(RefreshArgs);

        // 等了多久看命令列自己的單調時鐘
        Assert.Equal(PollsUntilHeartbeatStale, harness.Delays);
        Assert.Equal(Contract.ExitError, result.ExitCode);
        var json = result.Json();
        Assert.Equal(Contract.ReasonStale, json.GetProperty("reason").GetString());
        Assert.False(json.GetProperty("refreshed").GetBoolean());
    }

    [Fact]
    public async Task 等待刷新時牆鐘往回調而App照常寫心跳時180秒不提早結束()
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload());

        // 牆鐘往回調的幅度：之後 App 寫的心跳都比送出訊號前讀到的舊，但每次都和上一次不同
        var stepBack = TimeSpan.FromHours(1);

        // 等滿 StatusHeartbeatStale 之後的下一次讀取，App 才完成這次刷新
        var finishedAt = PollsUntilHeartbeatStale + 1;
        DateTimeOffset? lastBeat = null;
        harness.AfterDelay = poll =>
        {
            if (poll == 1)
            {
                harness.Time.SetUtcNow(harness.Time.GetUtcNow() - stepBack);
            }

            var now = harness.Time.GetUtcNow();
            if (poll == finishedAt)
            {
                harness.SaveStatus(Payload(), row => row with
                {
                    WrittenAt = now, HeartbeatAt = now, RefreshStartedSeq = IdleSeq + 1, RefreshDoneSeq = IdleSeq + 1,
                });
            }
            else if (lastBeat is not { } last || now - last >= Tuning.StatusHeartbeatInterval)
            {
                lastBeat = now;
                harness.SaveStatus(Payload(), row => row with { WrittenAt = now, HeartbeatAt = now, RefreshStartedSeq = IdleSeq + 1 });
            }
        };

        var result = await harness.RunAsync(RefreshArgs);

        // 心跳和送出訊號前不同就代表 App 有寫，不看大小：牆鐘往回調時不誤判成停住（stale）
        Assert.Equal(finishedAt, harness.Delays);
        Assert.Equal(Contract.ExitSuccess, result.ExitCode);
        Assert.True(result.Json().GetProperty("refreshed").GetBoolean());
    }

    [Fact]
    public async Task 等待中讀到鎖定時繼續等()
    {
        using var harness = new Harness();
        harness.SaveStatus(Payload());
        ExclusiveLock? locker = null;
        harness.AfterDelay = poll =>
        {
            switch (poll)
            {
                case 1:
                    locker = ExclusiveLock.Take(harness.DatabasePath);
                    break;
                case 2:
                    locker!.Dispose();
                    harness.SaveStatus(Payload(), row => row with { RefreshStartedSeq = IdleSeq + 1, RefreshDoneSeq = IdleSeq + 1 });
                    break;
            }
        };

        var watch = Stopwatch.StartNew();
        var result = await harness.RunAsync(RefreshArgs);
        watch.Stop();

        // 第一次讀取等鎖等到 CliDbBusyTimeout 才放棄（證明真的被鎖住），但只是暫時讀不到，繼續等到 done 追上
        Assert.True(watch.Elapsed >= Tuning.CliDbBusyTimeout - OneSecond, $"耗時 {watch.Elapsed}");
        Assert.Equal(2, harness.Delays);
        Assert.Equal(Contract.ExitSuccess, result.ExitCode);
        Assert.True(result.Json().GetProperty("refreshed").GetBoolean());
    }

    // ── notices：LICENSE 與第三方聲明全文 ─────────────────────────────

    [Fact]
    public async Task notices輸出內嵌的全文_UTF8沒有BOM_退出碼0()
    {
        using var harness = new Harness();

        var result = await harness.RunAsync(NoticesCommand);

        Assert.Equal(Contract.ExitSuccess, result.ExitCode);
        var bytes = harness.RawStdout.ToArray();
        Assert.Equal(EmbeddedNotices.ReadAll(), new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes));
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "輸出不能有 BOM");

        // 聲明有非 ASCII，所以不走 stdout 的 TextWriter（編碼跟著主控台字碼頁）
        Assert.Contains(bytes, value => value > MaxAscii);
        Assert.Empty(result.Stdout);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task notices不碰App也不建檔()
    {
        using var harness = new Harness();
        var before = Files(harness.Workspace.Root);

        await harness.RunAsync(NoticesCommand);

        Assert.Empty(harness.Events);
        Assert.Equal(before, Files(harness.Workspace.Root));
    }

    [Fact]
    public async Task notices讀不到內嵌資源時stderr是英文_退出碼4_stdout沒有內容()
    {
        using var harness = new Harness { ReadNotices = () => throw new IOException(ChineseExceptionMessage) };

        var result = await harness.RunAsync(NoticesCommand);

        Assert.Equal(Contract.ExitError, result.ExitCode);
        Assert.Empty(harness.RawStdout.ToArray());
        Assert.Empty(result.Stdout);
        Assert.StartsWith("Error:", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(ChineseExceptionMessage, result.Stderr, StringComparison.Ordinal);
        AssertAscii(result.Stderr);
    }

    [Fact]
    public async Task notices寫不出去時stderr是英文_退出碼4_不輸出JSON()
    {
        using var harness = new Harness { OpenRawStdout = () => throw new IOException(ChineseExceptionMessage) };

        var result = await harness.RunAsync(NoticesCommand);

        Assert.Equal(Contract.ExitError, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.StartsWith("Error:", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(ChineseExceptionMessage, result.Stderr, StringComparison.Ordinal);
        AssertAscii(result.Stderr);
    }

    [Fact]
    public async Task status不碰原始的stdout()
    {
        using var harness = new Harness { OpenRawStdout = () => throw new InvalidOperationException(ChineseExceptionMessage) };
        harness.SaveStatus(Payload());

        var result = await harness.RunAsync(StatusArgs);

        Assert.Equal(Contract.ExitSuccess, result.ExitCode);
        AssertAscii(result.Stdout);
        Assert.Equal(Contract.StateSuccess, result.Json().GetProperty("state").GetString());
    }

    // ── 預設環境：真的 Mutex 與事件（§10.5 命令列端第 1、2 步）──────────

    [Fact]
    public void 預設環境檢查完不持有Mutex()
    {
        // 名稱加上這個測試專用的後綴，不碰使用者正在跑的 App
        var suffix = UniqueSuffix();
        var environment = CliEnvironment.CreateDefault(suffix);
        var name = Tuning.SingleInstanceMutexName + suffix;

        using (new Mutex(initiallyOwned: false, name))
        {
            Assert.Equal(AppProbe.Running, environment.ProbeApp());
        }

        // 模擬 App 的 handle 關掉之後，命令列也沒有握著：重新啟動的 App 會拿到 createdNew
        using var restarted = new Mutex(initiallyOwned: true, name, out var createdNew);
        Assert.True(createdNew);
        restarted.ReleaseMutex();
    }

    [Fact]
    public void 沒有App時回NotRunning()
    {
        var environment = CliEnvironment.CreateDefault(UniqueSuffix());

        Assert.Equal(AppProbe.NotRunning, environment.ProbeApp());
    }

    [Fact]
    public void 預設環境開得到App建立的刷新事件()
    {
        var suffix = UniqueSuffix();
        var environment = CliEnvironment.CreateDefault(suffix);

        var missing = environment.OpenRefreshSignal();
        Assert.Equal(SignalOpen.Missing, missing.Result);
        Assert.Null(missing.Signal);

        using var appEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Tuning.RefreshSignalName + suffix);
        var (result, signal) = environment.OpenRefreshSignal();
        Assert.Equal(SignalOpen.Opened, result);
        Assert.NotNull(signal);
        using (signal)
        {
            signal.Set();
        }

        Assert.True(appEvent.WaitOne(TimeSpan.Zero));
    }

    [Fact]
    public void 預設環境讀AppPaths的cache_db()
    {
        var environment = CliEnvironment.CreateDefault(UniqueSuffix());

        Assert.Equal(AppPaths.Default.CacheDbPath, environment.DatabasePath);
        Assert.Same(TimeProvider.System, environment.Time);
        // 命令列自己的 InformationalVersion（csproj 的 Version，SDK 可能接上 + 與 commit）
        Assert.StartsWith(CliProjectVersion(), environment.CliVersion, StringComparison.Ordinal);
        Assert.Equal(EmbeddedNotices.ReadAll(), environment.ReadNotices());
        Assert.Equal(nameof(Console.OpenStandardOutput), environment.OpenRawStdout.Method.Name);
    }

    // ── 輔助 ───────────────────────────────────────────────────────────

    /// <summary>命令列 csproj 的 Version。從專案檔讀，不寫死：發版流程第一步就是改版本，寫死的話這裡會擋住那個 PR。</summary>
    private static string CliProjectVersion()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var project = Path.Combine(directory.FullName, "src", "IsItGreenYet.Cli", "IsItGreenYet.Cli.csproj");
            if (File.Exists(project))
            {
                return XDocument.Load(project).Descendants("Version").Single().Value;
            }
        }

        throw new InvalidOperationException($"從 {AppContext.BaseDirectory} 往上找不到命令列的專案檔。");
    }

    /// <summary>依情境準備狀態表與假的環境，回傳參數；<c>Hold</c> 是執行期間要一直握著的資源（例如鎖住的檔案）。</summary>
    private static (string[] Args, IDisposable? Hold) Arrange(Harness harness, string scenario)
    {
        switch (scenario)
        {
            case "參數錯誤":
                return (["status"], null);
            case "未預期的例外":
                harness.SaveStatus(Payload());
                harness.Probe = () => throw new InvalidOperationException(ChineseExceptionMessage);
                return (RefreshArgs, null);
            case "App沒在跑":
                harness.SaveStatus(Payload());
                harness.Probe = () => AppProbe.NotRunning;
                return (RefreshArgs, null);
            case "Mutex存取被拒":
                harness.SaveStatus(Payload());
                harness.Probe = () => AppProbe.AccessDenied;
                return (RefreshArgs, null);
            case "沒有刷新事件":
                harness.SaveStatus(Payload());
                harness.SignalResult = SignalOpen.Missing;
                return (RefreshArgs, null);
            case "事件存取被拒":
                harness.SaveStatus(Payload());
                harness.SignalResult = SignalOpen.AccessDenied;
                return (RefreshArgs, null);
            case "檔案損毀":
                File.WriteAllBytes(harness.DatabasePath, Encoding.UTF8.GetBytes("not a SQLite database"));
                return (StatusArgs, null);
            case "開不了檔":
                // 鎖定、損毀以外的 SQLite 錯誤（這裡是別的行程獨占檔案、開不了檔）也是讀 DB 失敗
                harness.SaveStatus(Payload());
                SqliteConnection.ClearAllPools();
                return (StatusArgs, new FileStream(harness.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            case "時間欄位不是ISO8601":
                harness.SaveStatus(Payload());
                Execute(harness.DatabasePath, "UPDATE status_snapshot SET heartbeat_at = 'yesterday';");
                return (StatusArgs, null);
            case "status_json不是JSON":
                harness.SaveStatus(Payload(), row => row with { StatusJson = "{" });
                return (StatusArgs, null);
            case "status_json少了必要欄位":
                harness.SaveStatus(Payload(), row => row with
                {
                    StatusJson = Without(row.StatusJson, "\"overall\":\"success\","),
                });
                return (StatusArgs, null);
            case "overall不認得":
                harness.SaveStatus(Payload() with { Overall = "unknown" });
                return (StatusArgs, null);
            case "status_json含單獨的代理字元":
                harness.SaveStatus(Payload(), row => row with
                {
                    StatusJson = Replace(row.StatusJson, "\"title\":\"Fix tests\"", "\"title\":\"\\uD800\""),
                });
                return (StatusArgs, null);
            case "schema版本不符":
                harness.SaveStatus(Payload());
                Execute(harness.DatabasePath, $"UPDATE schema_version SET version = {DatabaseInitializer.CurrentSchemaVersion + 1};");
                return (StatusArgs, null);
            case "沒有資料庫":
                return (StatusArgs, null);
            case "第一階段的資料庫":
                PhaseOneDatabase.Create(harness.DatabasePath);
                return (StatusArgs, null);
            case "狀態表沒有資料":
                DatabaseInitializer.Ensure(harness.DatabasePath);
                return (StatusArgs, null);
            case "刷新逾時":
                harness.SaveStatus(Payload());
                harness.KeepBeatingWithoutFinishing(Payload());
                return (RefreshArgs, null);
            case "App已正常結束":
                harness.SaveStatus(Payload(), row => row with { StoppedAt = WrittenAt });
                return (StatusArgs, null);
            case "心跳過期":
                harness.SaveStatus(Payload(), row => row with { HeartbeatAt = Now - Tuning.StatusHeartbeatStale - OneSecond });
                return (StatusArgs, null);
            case "未登入":
                harness.SaveStatus(Payload() with { SignedIn = false });
                return (StatusArgs, null);
            case "API版本錯誤":
                harness.SaveStatus(Payload() with { ApiVersionError = true });
                return (StatusArgs, null);
            case "還沒有成功回應":
                harness.SaveStatus(Payload() with { LastUpdatedAt = null });
                return (StatusArgs, null);
            case "超過1小時沒有成功回應":
                harness.SaveStatus(Payload() with { LastUpdatedAt = Now - Tuning.StaleDataThreshold - OneSecond });
                return (StatusArgs, null);
            case "失敗":
                harness.SaveStatus(Payload() with { Overall = Contract.StateFailure, Counts = new StatusCounts(1, 0) });
                return (StatusArgs, null);
            case "進行中":
                harness.SaveStatus(Payload() with { Overall = Contract.StateInProgress, Counts = new StatusCounts(0, 1) });
                return (StatusArgs, null);
            case "成功":
                harness.SaveStatus(Payload());
                return (StatusArgs, null);
            case "沒有資料":
                harness.SaveStatus(Payload() with { Overall = Contract.StateNoData });
                return (StatusArgs, null);
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "沒有這個情境");
        }
    }

    /// <summary>§10.3 範例的 App 寫的部分，整體狀態是成功、資料是新的。</summary>
    private static StatusPayload Payload() => new(
        AppVersion: AppVersion,
        SnapshotAt: WrittenAt,
        Overall: Contract.StateSuccess,
        SignedIn: true,
        ApiVersionError: false,
        RefreshMode: "auto",
        Counts: new StatusCounts(Failing: 0, InProgress: 0),
        MonitoredRepos: 11,
        StaleRepos: 0,
        Connection: new StatusConnection(Offline: false, PausedUntil: null, ServerErrors: false),
        LastUpdatedAt: LastUpdatedAt,
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

    /// <summary>App 5 秒前寫的一列：沒有在刷新、沒有結束。</summary>
    private static StatusSnapshotRow Row(StatusPayload payload) => new(
        WrittenAt: WrittenAt,
        HeartbeatAt: WrittenAt,
        StoppedAt: null,
        AppVersion: AppVersion,
        StatusJson: StatusJson.Serialize(payload),
        RefreshStartedSeq: IdleSeq,
        RefreshDoneSeq: IdleSeq,
        RefreshPartial: false);

    /// <summary>改資料列裡的 status_json。</summary>
    private static StatusSnapshotRow Edit(StatusSnapshotRow row, Func<StatusPayload, StatusPayload> edit)
    {
        var payload = StatusJson.Deserialize<StatusPayload>(row.StatusJson);
        Assert.NotNull(payload);
        return row with { StatusJson = StatusJson.Serialize(edit(payload)) };
    }

    private static string Without(string json, string part) => Replace(json, part, string.Empty);

    /// <summary>拿掉最上層的一個鍵（原本要有）。</summary>
    private static string WithoutProperty(string json, string key)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.True(root.Remove(key), $"沒有 {key}");
        return root.ToJsonString();
    }

    /// <summary>把 <paramref name="path"/>（用 / 分隔，數字是陣列索引）指到的值換成 null（原本要有值）。</summary>
    private static string WithNull(string json, string path)
    {
        var root = JsonNode.Parse(json)!;
        var parts = path.Split('/');
        var parent = root;
        foreach (var part in parts[..^1])
        {
            parent = (int.TryParse(part, out var index) ? parent[index] : parent[part])!;
        }

        Assert.NotNull(parent[parts[^1]]);
        parent[parts[^1]] = null;
        return root.ToJsonString();
    }

    /// <summary>把 <paramref name="original"/> 換掉（原文要找得到）。</summary>
    private static string Replace(string json, string original, string replacement)
    {
        Assert.Contains(original, json, StringComparison.Ordinal);
        return json.Replace(original, replacement, StringComparison.Ordinal);
    }

    private static void AssertAscii(string text) =>
        Assert.All(text, c => Assert.True(c <= MaxAscii, $"非 ASCII 字元 U+{(int)c:X4}"));

    private static string UniqueSuffix() => "." + Guid.NewGuid().ToString("N");

    /// <summary>資料夾裡所有檔案（含子資料夾）的相對路徑，排好序。</summary>
    private static List<string> Files(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>
    /// App 一切正常（已登入、沒有版本錯誤、資料是新的）時，整體狀態是 <paramref name="overall"/> 的一份快照，
    /// 交給 <see cref="MonitorEngine.BuildStatusPayload"/>；結果只由 §10.6 第 12～15 列決定。
    /// </summary>
    private static MonitorSnapshot EngineSnapshot(OverallState overall) => new(
        Rows: [],
        new StatusSummary(overall, PendingCount: 0, FailingCount: 0, StaleRepoCount: 0, ConnectionIssue.None),
        LastUpdatedUtc: WrittenAt,
        RateLimit: null,
        Repos: [],
        Banner: null,
        new LoadingProgress(Completed: 1, Total: 1),
        AuthStatus.Ok,
        TokenSource.GhStored,
        TrayIssueLine: null,
        Offline: false,
        RefreshMode.Auto,
        WatchedRunIds: FrozenSet<long>.Empty,
        Eta: FrozenDictionary<long, EtaInfo>.Empty,
        CompletedDurations: FrozenDictionary<long, TimeSpan>.Empty,
        PullRequestNumbers: FrozenDictionary<long, IReadOnlyList<int>>.Empty);

    /// <summary>
    /// §10.6 的退出碼、state、reason 與 §10.3 的 schemaVersion：腳本依賴的公開契約。
    /// 用字面值釘住，不引用產品的 CliExitCode／CliState／CliReason，產品改錯值時測試才會失敗。
    /// </summary>
    private static class Contract
    {
        /// <summary>§10.3：JSON 結構的版本從 1 開始。</summary>
        public const int SchemaVersion = 1;

        /// <summary>§10.6 第 14 列（§10.1 的 --help 也是 0）。</summary>
        public const int ExitSuccess = 0;

        /// <summary>§10.6 第 12 列。</summary>
        public const int ExitFailure = 1;

        /// <summary>§10.6 第 13 列。</summary>
        public const int ExitInProgress = 2;

        /// <summary>§10.6 第 5、15 列。</summary>
        public const int ExitNoData = 3;

        /// <summary>§10.6 第 1～4、6～11 列。</summary>
        public const int ExitError = 4;

        // §10.6 的 state 欄；前四個也是 App 寫的 overall（§10.3）
        public const string StateSuccess = "success";
        public const string StateFailure = "failure";
        public const string StateInProgress = "in_progress";
        public const string StateNoData = "no_data";
        public const string StateError = "error";

        // §10.6 的 reason 欄，依列的順序
        public const string ReasonInternalError = "internal_error";
        public const string ReasonAppNotRunning = "app_not_running";
        public const string ReasonRefreshUnreachable = "refresh_unreachable";
        public const string ReasonRefreshUnsupported = "refresh_unsupported";
        public const string ReasonDbError = "db_error";
        public const string ReasonSchemaMismatch = "schema_mismatch";
        public const string ReasonNoStatus = "no_status";
        public const string ReasonRefreshTimeout = "refresh_timeout";
        public const string ReasonStale = "stale";
        public const string ReasonSignedOut = "signed_out";
        public const string ReasonVersionError = "version_error";
        public const string ReasonDataStale = "data_stale";
        public const string ReasonNoData = "no_data";
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr)
    {
        public JsonElement Json()
        {
            using var document = JsonDocument.Parse(Stdout);
            return document.RootElement.Clone();
        }
    }

    /// <summary>
    /// 一次命令列執行的環境：暫存目錄的 cache.db、手動時鐘、假的 App 探測與刷新事件。
    /// 等待立即完成並推進時鐘（牆鐘與單調時鐘一起），之後呼叫 <see cref="AfterDelay"/> 模擬 App 在這段時間寫狀態表。
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public TempWorkspace Workspace { get; } = new();

        public ManualTimeProvider Time { get; } = new(Now);

        public string DatabasePath => Workspace.Paths.CacheDbPath;

        public Func<AppProbe> Probe { get; set; } = () => AppProbe.Running;

        public SignalOpen SignalResult { get; set; } = SignalOpen.Opened;

        /// <summary>參數是第幾次等待（從 1 開始）。</summary>
        public Action<int>? AfterDelay { get; set; }

        public int Delays { get; private set; }

        /// <summary>依序記下 probe、open、set、dispose、delay。</summary>
        public List<string> Events { get; } = [];

        /// <summary><c>notices</c> 寫進原始 stdout 的位元組（命令列用完會關掉，ToArray 照樣讀得到）。</summary>
        public MemoryStream RawStdout { get; } = new();

        public Func<Stream>? OpenRawStdout { get; init; }

        public Func<string> ReadNotices { get; init; } = EmbeddedNotices.ReadAll;

        public async Task<CliResult> RunAsync(params string[] args)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var environment = new CliEnvironment(Time, DatabasePath, CliVersion, ProbeApp, OpenSignal, DelayAsync,
                ReadNotices, OpenRawStdout ?? (() => RawStdout));
            var exitCode = await CliApp.RunAsync(args, stdout, stderr, environment, CancellationToken.None);
            return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
        }

        /// <summary>寫一列狀態表（沒有 DB 時先建）。</summary>
        public void SaveStatus(StatusPayload payload, Func<StatusSnapshotRow, StatusSnapshotRow>? editRow = null)
        {
            var row = Row(payload);
            Save(editRow is null ? row : editRow(row));
        }

        /// <summary>
        /// 模擬 App 照常執行、但這次刷新一直沒完成：started 前進了，done 不動，
        /// 每 <see cref="Tuning.StatusHeartbeatInterval"/> 整列重寫一次（心跳），所以不會因為心跳太舊而提早結束。
        /// </summary>
        public void KeepBeatingWithoutFinishing(StatusPayload payload)
        {
            var lastBeat = WrittenAt;
            AfterDelay = _ =>
            {
                var now = Time.GetUtcNow();
                if (now - lastBeat >= Tuning.StatusHeartbeatInterval)
                {
                    lastBeat = now;
                    SaveStatus(payload, row => row with { WrittenAt = now, HeartbeatAt = now, RefreshStartedSeq = IdleSeq + 1 });
                }
            };
        }

        public void Save(StatusSnapshotRow row)
        {
            if (!File.Exists(DatabasePath))
            {
                DatabaseInitializer.Ensure(DatabasePath);
            }

            new CacheRepository(DatabasePath).SaveStatusSnapshot(row);
        }

        public void Dispose()
        {
            RawStdout.Dispose();
            Workspace.Dispose();
        }

        private AppProbe ProbeApp()
        {
            Events.Add("probe");
            return Probe();
        }

        private (SignalOpen Result, IRefreshSignal? Signal) OpenSignal()
        {
            Events.Add("open");
            return SignalResult == SignalOpen.Opened ? (SignalResult, new FakeSignal(Events)) : (SignalResult, null);
        }

        private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Events.Add("delay");
            Delays++;
            Time.Advance(delay);
            AfterDelay?.Invoke(Delays);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSignal(List<string> events) : IRefreshSignal
    {
        public void Set() => events.Add("set");

        public void Dispose() => events.Add("dispose");
    }

    /// <summary>
    /// 一直佔著 cache.db 的寫入鎖：WAL 模式下 locking_mode=EXCLUSIVE 的連線一旦寫入就獨占整個檔案，讀取會一直拿到 BUSY
    /// （同 StatusSnapshotReaderTests）。
    /// </summary>
    private sealed class ExclusiveLock : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;

        private ExclusiveLock(SqliteConnection connection, SqliteTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public static ExclusiveLock Take(string databasePath)
        {
            SqliteConnection.ClearAllPools();
            var connection = Open(databasePath);
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA locking_mode = EXCLUSIVE;";
                pragma.ExecuteNonQuery();
            }

            var transaction = connection.BeginTransaction();
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE status_snapshot SET written_at = written_at;";
                update.ExecuteNonQuery();
            }

            return new ExclusiveLock(connection, transaction);
        }

        public void Dispose()
        {
            _transaction.Dispose();
            _connection.Dispose();
        }
    }
}
