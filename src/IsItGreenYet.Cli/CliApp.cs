using System.Text;
using System.Text.Json;
using IsItGreenYet.Core;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Status;
using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Cli;

/// <summary>
/// IsItGreenYet-cli（第二階段 spec §10）：讀 App 寫的狀態表，依 §10.6 的順序表決定退出碼，stdout 印一行純 ASCII 的 JSON。
/// 不打 GitHub API（D11）；<c>--refresh</c> 只透過具名事件請正在執行的 App 刷新，等它寫完再讀（§10.5）。
/// 另有 <c>notices</c>：輸出內嵌的 LICENSE 與第三方聲明全文，規格之外的指令，不讀狀態表（見 docs/spec-deviations.md）。
/// </summary>
/// <remarks>
/// 只讀資料：<see cref="StatusSnapshotReader"/>（唯讀、不共用連線、不建檔、不改名），不讀 settings.json。
/// stdout 只有一個 JSON 物件加換行；stderr 只寫固定的英文說明，絕不寫例外的 Message（可能有中文）。
/// </remarks>
internal static class CliApp
{
    private const string StatusCommand = "status";
    private const string NoticesCommand = "notices";
    private const string JsonOption = "--json";
    private const string RefreshOption = "--refresh";
    private const string HelpOption = "--help";

    /// <summary><c>notices</c> 失敗時 stderr 的一行英文說明（固定的文字，不含例外訊息）。</summary>
    private const string NoticesErrorLine = "Error: Cannot print the embedded license and third-party notices.";

    /// <summary>用法（英文、純 ASCII）：<c>--help</c> 印到 stdout，參數錯誤印到 stderr（§10.1）。</summary>
    private static readonly string[] UsageLines =
    [
        "Usage:",
        "  IsItGreenYet-cli status --json [--refresh]",
        "  IsItGreenYet-cli notices",
        "  IsItGreenYet-cli --help",
        "",
        "Reads the status written by the running IsItGreenYet app and prints one JSON object on one line.",
        "  --refresh  Ask the running app to refresh first and wait until it finishes.",
        "",
        "notices prints the license and third-party notices as UTF-8 (they are not pure ASCII).",
        "If the console shows garbled text, redirect to a file: IsItGreenYet-cli notices > notices.txt",
        "(in Windows PowerShell 5.1, through cmd: cmd /c \"IsItGreenYet-cli notices > notices.txt\").",
        FormattableString.Invariant($"It exits with {CliExitCode.Success}, or {CliExitCode.Error} if a read or write reports an error. The exit codes below are for status."),
        "",
        FormattableString.Invariant(
            $"Exit codes: {CliExitCode.Success} success, {CliExitCode.Failure} failure, {CliExitCode.InProgress} in progress,"),
        FormattableString.Invariant($"            {CliExitCode.NoData} no data, {CliExitCode.Error} error (see \"reason\")."),
    ];

    /// <summary>狀態表 overall 的四種值（§10.3）；其他值當成 status_json 不能用。</summary>
    private static readonly string[] KnownOverall = [CliState.Failure, CliState.InProgress, CliState.Success, CliState.NoData];

    /// <summary><c>notices</c> 的輸出編碼：UTF-8，沒有 BOM（接給別的程式或存檔時不多一個字元）。</summary>
    private static readonly UTF8Encoding NoticesEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private enum Command { Invalid, Help, Status, StatusRefresh, Notices }

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CliEnvironment env, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(env);

        var session = new Session(env);
        try
        {
            return await RunCoreAsync(args, stdout, stderr, session, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return WriteUnexpected(stdout, stderr, session);
        }
    }

    /// <summary>
    /// §10.6 的順序表：由上往下判斷，第一個成立的就是結果，一列對一個分支。
    /// </summary>
    internal static CliDecision Decide(CliFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // 1. 參數錯誤：不輸出 JSON
        if (facts.InvalidArguments)
        {
            return new CliDecision(CliExitCode.Error, State: null, Reason: null);
        }

        // 2. 未預期的例外
        if (facts.Unexpected)
        {
            return Error(CliReason.InternalError);
        }

        // 3. --refresh 的步驟 1、2 失敗
        if (facts.RefreshFailure is { } refreshFailure)
        {
            return Error(refreshFailure);
        }

        var read = facts.Read ?? throw new ArgumentException("A read result is required after row 3.", nameof(facts));

        // 4. 讀 DB 失敗：鎖定逾時、檔案損毀、其他 SQLite 錯誤、資料列內容不能用；schema 版本不符
        if (read.Problem == StatusReadProblem.DbError)
        {
            return Error(CliReason.DbError);
        }

        if (read.Problem == StatusReadProblem.SchemaMismatch)
        {
            return Error(CliReason.SchemaMismatch);
        }

        // 5. 沒有 cache.db、沒有 status_snapshot 表、或表裡沒有資料
        if (read.Problem == StatusReadProblem.NoStatus)
        {
            return new CliDecision(CliExitCode.NoData, CliState.NoData, CliReason.NoStatus);
        }

        // 6. --refresh 逾時
        if (facts.RefreshTimedOut)
        {
            return Error(CliReason.RefreshTimeout);
        }

        if (read is not { Problem: StatusReadProblem.None, Row: { } row, Payload: { } payload })
        {
            throw new ArgumentException("A usable read must carry the row and its payload.", nameof(facts));
        }

        // 7. App 已正常結束
        if (row.StoppedAt is not null)
        {
            return Error(CliReason.AppNotRunning);
        }

        // 8. 心跳太舊：App 沒有在執行，或停住了
        if (IsHeartbeatStale(row.HeartbeatAt, facts.Now))
        {
            return Error(CliReason.Stale);
        }

        // 9. 未登入；null 是還在確認，不算
        if (payload.SignedIn is false)
        {
            return Error(CliReason.SignedOut);
        }

        // 10. API 版本錯誤
        if (payload.ApiVersionError)
        {
            return Error(CliReason.VersionError);
        }

        // 11. 資料過舊
        if (IsDataStale(payload, facts.Now))
        {
            return Error(CliReason.DataStale);
        }

        return payload.Overall switch
        {
            // 12～14
            CliState.Failure => new CliDecision(CliExitCode.Failure, CliState.Failure, Reason: null),
            CliState.InProgress => new CliDecision(CliExitCode.InProgress, CliState.InProgress, Reason: null),
            CliState.Success => new CliDecision(CliExitCode.Success, CliState.Success, Reason: null),

            // 15. 以上都不成立：overall 是 no_data（不認得的值在讀取時就算 db_error）
            _ => new CliDecision(CliExitCode.NoData, CliState.NoData, CliReason.NoData),
        };
    }

    /// <summary>
    /// 讀一次狀態表，依 §10.6 歸類。鎖定、損毀以外的 SQLite 錯誤（開不了檔、磁碟錯誤）與時間欄位不是 ISO 8601，
    /// 都算讀 DB 失敗（第 4 列），不是未預期的例外。
    /// </summary>
    private static StatusRead ReadStatus(string databasePath)
    {
        StatusReadResult result;
        try
        {
            result = StatusSnapshotReader.Read(databasePath, Tuning.CliDbBusyTimeout);
        }
        catch (Exception ex) when (ex is SqliteException or FormatException)
        {
            return new StatusRead(StatusReadProblem.DbError, Row: null, Payload: null);
        }

        return result.Kind switch
        {
            StatusReadKind.Ok when result.Row is { } row => FromRow(row),
            StatusReadKind.NoDatabase or StatusReadKind.NoTable or StatusReadKind.NoRow =>
                new StatusRead(StatusReadProblem.NoStatus, Row: null, Payload: null),
            StatusReadKind.Locked or StatusReadKind.Corrupt => new StatusRead(StatusReadProblem.DbError, Row: null, Payload: null),
            StatusReadKind.SchemaMismatch => new StatusRead(StatusReadProblem.SchemaMismatch, Row: null, Payload: null),
            _ => throw new InvalidOperationException($"Unexpected read result {result.Kind}."),
        };
    }

    private static async Task<int> RunCoreAsync(string[] args, TextWriter stdout, TextWriter stderr, Session session, CancellationToken ct)
    {
        switch (Parse(args))
        {
            case Command.Help:
                WriteUsage(stdout);
                // §10.1：--help 退出碼 0（不在 §10.6 的表裡）
                return CliExitCode.Success;

            case Command.Notices:
                return WriteNotices(stderr, session.Env);

            case Command.Status:
                return Finish(stdout, stderr, session, new Outcome(session.Read()));

            case Command.StatusRefresh:
                return Finish(stdout, stderr, session, await RefreshAsync(session, ct).ConfigureAwait(false));

            default:
                WriteUsage(stderr);
                return Decide(new CliFacts(InvalidArguments: true, Unexpected: false, RefreshFailure: null, Read: null,
                    RefreshTimedOut: false, Now: default)).ExitCode;
        }
    }

    /// <summary>
    /// <c>--help</c>、<c>notices</c> 要單獨出現；<c>status</c> 要在第一個，後面接 <c>--json</c> 與選用的 <c>--refresh</c>，
    /// 這兩個順序不限、各一次。大小寫要完全相同。其他一律是參數錯誤（§10.1）。
    /// </summary>
    private static Command Parse(string[] args)
    {
        if (args is [HelpOption])
        {
            return Command.Help;
        }

        if (args is [NoticesCommand])
        {
            return Command.Notices;
        }

        if (args is not [StatusCommand, .. var options])
        {
            return Command.Invalid;
        }

        var json = false;
        var refresh = false;
        foreach (var option in options)
        {
            switch (option)
            {
                case JsonOption when !json:
                    json = true;
                    break;
                case RefreshOption when !refresh:
                    refresh = true;
                    break;
                default:
                    return Command.Invalid;
            }
        }

        return !json ? Command.Invalid : refresh ? Command.StatusRefresh : Command.Status;
    }

    /// <summary>
    /// 輸出內嵌的 LICENSE 與第三方聲明全文。內容有非 ASCII，做不到 JSON 那種純 ASCII（§10.3），每一種編碼都有接法會亂碼（§11.3 的實測），
    /// 所以固定寫 UTF-8 的位元組，由文件說明亂碼時怎麼處理（導向檔案）。不經過 <c>Console.Out</c>：它用主控台字碼頁，cp950 沒有的字會變成問號；
    /// 也不設 <c>Console.OutputEncoding</c>：改到的字碼頁在程式結束後還留在使用者的主控台。
    /// 讀不到或寫入時回報錯誤就在 stderr 寫固定的英文說明，退出碼 4；這個指令不輸出 JSON。管線另一端先關閉、stdout 不能寫入時 .NET 不回報，退出碼仍是 0（docs/cli）。
    /// </summary>
    private static int WriteNotices(TextWriter stderr, CliEnvironment env)
    {
        try
        {
            var bytes = NoticesEncoding.GetBytes(env.ReadNotices());
            using var output = env.OpenRawStdout();
            output.Write(bytes);
            output.Flush();
            return CliExitCode.Success;
        }
        catch (Exception)
        {
            stderr.WriteLine(NoticesErrorLine);
            return CliExitCode.Error;
        }
    }

    /// <summary>
    /// §10.5 命令列端的 1～6 步。App 在刷新途中結束或停住時 done 不會前進，所以等待中出現下列情況之一就不再等，
    /// 照那一次的讀取判斷（§10.6，通常是第 7、8 列），<c>refreshed</c> 是 false：
    /// <list type="bullet">
    /// <item>讀到的 stopped_at 有值，而且和送出訊號前讀到的不同：App 在這之後寫了新的結束時間。
    /// 送出訊號前就有的值可能是上一次執行留下的（App 剛啟動、還沒寫第一次），不算。</item>
    /// <item>已經等了 <see cref="Tuning.StatusHeartbeatStale"/>（命令列的單調時鐘），而且沒有任何一次讀到和送出訊號前不同的心跳：
    /// 這段期間 App 什麼都沒寫。</item>
    /// </list>
    /// 這兩個判斷只看讀得到資料列的那幾次；暫時讀不到（例如鎖定）不中止，繼續等到逾時。
    /// </summary>
    private static async Task<Outcome> RefreshAsync(Session session, CancellationToken ct)
    {
        var env = session.Env;

        // 1. 只檢查 App 的 Mutex 是否存在，不握著
        var probeFailure = env.ProbeApp() switch
        {
            AppProbe.Running => null,
            AppProbe.NotRunning => CliReason.AppNotRunning,
            AppProbe.AccessDenied => CliReason.RefreshUnreachable,
            var other => throw new InvalidOperationException($"Unexpected app probe result {other}."),
        };
        if (probeFailure is not null)
        {
            return Failed(session, probeFailure);
        }

        // 2～4. 開事件、讀 s0 與 d0、發訊號；開始等之前就關掉事件
        long target;
        StatusSnapshotRow baseline;
        var (open, signal) = env.OpenRefreshSignal();
        using (signal)
        {
            switch (open)
            {
                case SignalOpen.Opened:
                    break;
                case SignalOpen.Missing:
                    return Failed(session, CliReason.RefreshUnsupported);
                case SignalOpen.AccessDenied:
                    return Failed(session, CliReason.RefreshUnreachable);
                default:
                    throw new InvalidOperationException($"Unexpected signal open result {open}.");
            }

            if (signal is null)
            {
                throw new InvalidOperationException("The refresh signal was opened but not returned.");
            }

            var first = session.Read();
            if (first.Row is not { } row)
            {
                // 3. 讀不到 started／done：不發訊號，照 §10.6 判斷這次的讀取（第 4、5 列）
                return new Outcome(first);
            }

            // s0 == d0：等這次訊號觸發的刷新；s0 > d0（App 正在刷新）：訊號排在進行中的那一次之後，要多等一次
            baseline = row;
            target = Math.Max(row.RefreshStartedSeq, row.RefreshDoneSeq) + 1;
            signal.Set();
        }

        // 5、6. 每 CliRefreshPollInterval 讀一次，直到 done 追上或等滿 CliRefreshTimeout（單調時鐘）
        var startedAt = env.Time.GetTimestamp();
        var heartbeatAdvanced = false;
        while (true)
        {
            await env.Delay(Tuning.CliRefreshPollInterval, ct).ConfigureAwait(false);
            var poll = session.Read();
            if (poll.Row is { } current)
            {
                if (current.RefreshDoneSeq >= target)
                {
                    return new Outcome(poll, Refreshed: true);
                }

                // 比「不同」不比大小：App 寫心跳用牆鐘，牆鐘往回調時新的心跳可能比送出訊號前的舊
                heartbeatAdvanced |= current.HeartbeatAt != baseline.HeartbeatAt;
                var stoppedSinceSignal = current.StoppedAt is { } stoppedAt && stoppedAt != baseline.StoppedAt;
                var silentSinceSignal = !heartbeatAdvanced && env.Time.GetElapsedTime(startedAt) >= Tuning.StatusHeartbeatStale;
                if (stoppedSinceSignal || silentSinceSignal)
                {
                    return new Outcome(poll);
                }
            }

            if (env.Time.GetElapsedTime(startedAt) >= Tuning.CliRefreshTimeout)
            {
                // 照樣輸出目前的資料：逾時之後重新讀一次
                return new Outcome(session.Read(), TimedOut: true);
            }
        }
    }

    /// <summary>§10.6 第 3 列；讀得到狀態表就附上資料。</summary>
    private static Outcome Failed(Session session, string reason) => new(session.Read(), RefreshFailure: reason);

    private static int Finish(TextWriter stdout, TextWriter stderr, Session session, Outcome outcome)
    {
        var now = session.Env.Time.GetUtcNow();
        var decision = Decide(new CliFacts(InvalidArguments: false, Unexpected: false, outcome.RefreshFailure, outcome.Read,
            outcome.TimedOut, now));
        Emit(stdout, stderr, session, decision, BuildOutput(decision, outcome.Read, outcome.Refreshed, now, session.Env.CliVersion));
        return decision.ExitCode;
    }

    /// <summary>§10.6 第 2 列。附上例外之前最後一次讀到的資料；連這份輸出都寫不出來（例如 stdout 已關閉）時只剩退出碼。</summary>
    private static int WriteUnexpected(TextWriter stdout, TextWriter stderr, Session session)
    {
        var decision = Decide(new CliFacts(InvalidArguments: false, Unexpected: true, RefreshFailure: null, session.LastRead,
            RefreshTimedOut: false, Now: default));
        try
        {
            if (session.StdoutUsed)
            {
                // stdout 已經開始寫了：不再寫第二個 JSON
                stderr.WriteLine(ErrorLine(decision.Reason));
            }
            else
            {
                var now = session.Env.Time.GetUtcNow();
                Emit(stdout, stderr, session, decision,
                    BuildOutput(decision, session.LastRead, refreshed: false, now, session.Env.CliVersion));
            }
        }
        catch (Exception)
        {
            // 沒有別的地方可以回報了
        }

        return decision.ExitCode;
    }

    /// <summary>錯誤時先寫 stderr 的一行說明，最後才寫 stdout，整份 JSON 一次寫出。</summary>
    private static void Emit(TextWriter stdout, TextWriter stderr, Session session, CliDecision decision, CliOutput output)
    {
        var line = StatusJson.Serialize(output);
        if (decision.State == CliState.Error)
        {
            stderr.WriteLine(ErrorLine(decision.Reason));
        }

        session.StdoutUsed = true;
        stdout.WriteLine(line);
        stdout.Flush();
    }

    /// <summary>讀不到狀態表時 App 寫的欄位是 null、runs 是空陣列；refreshPartial 只在這次真的刷新完成時才照表裡的值。</summary>
    private static CliOutput BuildOutput(CliDecision decision, StatusRead? read, bool refreshed, DateTimeOffset now, string cliVersion)
    {
        var state = decision.State ?? throw new ArgumentException("Row 1 has no JSON output.", nameof(decision));
        var row = read?.Row;
        var payload = read?.Payload;
        return new CliOutput(
            CliOutput.CurrentSchemaVersion,
            state,
            decision.ExitCode,
            decision.Reason,
            cliVersion,
            AppVersion: row?.AppVersion,
            GeneratedAt: now,
            SnapshotAt: payload?.SnapshotAt,
            HeartbeatAt: row?.HeartbeatAt,
            Refreshed: refreshed,
            RefreshPartial: refreshed && row is { RefreshPartial: true },
            Overall: payload?.Overall,
            SignedIn: payload?.SignedIn,
            ApiVersionError: payload?.ApiVersionError,
            RefreshMode: payload?.RefreshMode,
            Counts: payload?.Counts,
            MonitoredRepos: payload?.MonitoredRepos,
            StaleRepos: payload?.StaleRepos,
            Connection: payload?.Connection,
            LastUpdatedAt: payload?.LastUpdatedAt,
            Runs: payload?.Runs ?? []);
    }

    /// <summary>
    /// status_json 不能用（不是 JSON、缺少欄位、不可為 null 的欄位是 null、overall 不認得）也算讀 DB 失敗（§10.6 第 4 列），
    /// 但資料列層級的欄位照附。
    /// </summary>
    private static StatusRead FromRow(StatusSnapshotRow row)
    {
        StatusPayload? payload;
        try
        {
            payload = StatusJson.Deserialize<StatusPayload>(row.StatusJson);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // ArgumentException：字串裡有單獨的代理字元（例如 \uD800）時 System.Text.Json 丟的
            payload = null;
        }

        return payload is not null && IsUsable(payload)
            ? new StatusRead(StatusReadProblem.None, row, payload)
            : new StatusRead(StatusReadProblem.DbError, row, Payload: null);
    }

    /// <summary>
    /// <see cref="StatusJson.Options"/> 已經擋下缺少的欄位與不可為 null 的欄位是 null；
    /// 這裡只補它不檢查的：overall 不是四種值之一，以及 runs 裡有 null 元素（清單元素不在它的檢查範圍）。
    /// </summary>
    private static bool IsUsable(StatusPayload payload) =>
        KnownOverall.Contains(payload.Overall, StringComparer.Ordinal)
        && !payload.Runs.Any(run => run is null);

    /// <summary>§10.6 第 8 列：超過 <see cref="Tuning.StatusHeartbeatStale"/> 才算（剛好等於不算）。</summary>
    private static bool IsHeartbeatStale(DateTimeOffset heartbeatAt, DateTimeOffset now) =>
        now - heartbeatAt > Tuning.StatusHeartbeatStale;

    /// <summary>§10.6 第 11 列：有監看中的 repo，而且還沒有成功回應、或早於現在超過 <see cref="Tuning.StaleDataThreshold"/>。</summary>
    private static bool IsDataStale(StatusPayload payload, DateTimeOffset now) =>
        payload.MonitoredRepos > 0
        && (payload.LastUpdatedAt is not { } lastUpdatedAt || now - lastUpdatedAt > Tuning.StaleDataThreshold);

    private static CliDecision Error(string reason) => new(CliExitCode.Error, CliState.Error, reason);

    private static void WriteUsage(TextWriter writer)
    {
        foreach (var line in UsageLines)
        {
            writer.WriteLine(line);
        }
    }

    /// <summary>stderr 的一行英文說明（§10.3、§14）：固定的文字，不含任何例外訊息。</summary>
    private static string ErrorLine(string? reason)
    {
        var message = reason switch
        {
            CliReason.InternalError => "Unexpected internal error.",
            CliReason.AppNotRunning => "The IsItGreenYet app is not running in this session.",
            CliReason.RefreshUnreachable => "Cannot signal the running IsItGreenYet app (access denied).",
            CliReason.RefreshUnsupported => "The running IsItGreenYet app does not support --refresh; update the app.",
            CliReason.DbError => "Cannot read cache.db (locked, damaged or unreadable).",
            CliReason.SchemaMismatch => "cache.db has a schema version this CLI does not recognize.",
            CliReason.RefreshTimeout => "The app did not finish refreshing in time; printing the current status.",
            CliReason.Stale => "The app has not updated its status recently; it may have stopped or hung.",
            CliReason.SignedOut => "The app is signed out of GitHub.",
            CliReason.VersionError => "The app stopped polling because of a GitHub API version error.",
            CliReason.DataStale => "The status has not been updated from GitHub for too long.",
            _ => "Error.",
        };
        return $"Error: {message} (reason: {reason})";
    }

    /// <param name="RefreshFailure">§10.6 第 3 列的原因代碼。</param>
    /// <param name="Refreshed">done 追上了這次訊號的序號。</param>
    /// <param name="TimedOut">§10.6 第 6 列。</param>
    private sealed record Outcome(StatusRead Read, string? RefreshFailure = null, bool Refreshed = false, bool TimedOut = false);

    /// <summary>一次執行的狀態：最近一次讀取的結果與 stdout 是否已經寫過，給未預期的例外用。</summary>
    private sealed class Session(CliEnvironment env)
    {
        public CliEnvironment Env { get; } = env;

        /// <summary>未預期的例外時照樣附上（§10.6：錯誤時讀得到狀態表就附上資料）。</summary>
        public StatusRead? LastRead { get; private set; }

        /// <summary>已經開始寫 stdout：stdout 只能有一個 JSON 物件。</summary>
        public bool StdoutUsed { get; set; }

        public StatusRead Read() => LastRead = ReadStatus(Env.DatabasePath);
    }
}

/// <summary>讀狀態表的結果，依 §10.6 的分法歸類。</summary>
internal enum StatusReadProblem
{
    /// <summary>讀到資料列，status_json 也能用。</summary>
    None,

    /// <summary>第 4 列 db_error：鎖定逾時、檔案損毀、其他 SQLite 錯誤、資料列內容不能用。</summary>
    DbError,

    /// <summary>第 4 列 schema_mismatch。</summary>
    SchemaMismatch,

    /// <summary>第 5 列 no_status：沒有 cache.db、沒有 status_snapshot 表、或表裡沒有資料。</summary>
    NoStatus,
}

/// <param name="Row">讀到資料列時有值；status_json 不能用（<see cref="StatusReadProblem.DbError"/>）時也有，用來附上資料列層級的欄位。</param>
/// <param name="Payload">只有 <see cref="StatusReadProblem.None"/> 時有值。</param>
internal sealed record StatusRead(StatusReadProblem Problem, StatusSnapshotRow? Row, StatusPayload? Payload);

/// <summary>§10.6 各列的條件。</summary>
/// <param name="InvalidArguments">第 1 列。</param>
/// <param name="Unexpected">第 2 列。</param>
/// <param name="RefreshFailure">第 3 列：<c>--refresh</c> 步驟 1、2 失敗的原因代碼。</param>
/// <param name="Read">第 4 列以後用的讀取結果；第 1、2 列可以沒有。</param>
/// <param name="RefreshTimedOut">第 6 列。</param>
/// <param name="Now">命令列的現在時間（牆鐘，和狀態表的時間比）。</param>
internal sealed record CliFacts(bool InvalidArguments, bool Unexpected, string? RefreshFailure, StatusRead? Read,
    bool RefreshTimedOut, DateTimeOffset Now);
