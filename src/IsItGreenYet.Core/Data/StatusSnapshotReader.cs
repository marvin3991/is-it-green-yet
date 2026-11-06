using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Core.Data;

/// <summary>命令列讀狀態表的結果種類（第二階段 spec §10.4、§10.6）。</summary>
public enum StatusReadKind
{
    /// <summary>讀到那一列。</summary>
    Ok,

    /// <summary>cache.db 不存在（App 從沒執行過）。</summary>
    NoDatabase,

    /// <summary>沒有 status_snapshot 表（第一階段的 DB，或 App 還在建立資料庫）。</summary>
    NoTable,

    /// <summary>表在，但 App 還沒寫過。</summary>
    NoRow,

    /// <summary>別的連線佔著鎖，重試到逾時仍讀不到。</summary>
    Locked,

    /// <summary>檔案損毀（SQLITE_CORRUPT、SQLITE_NOTADB）。</summary>
    Corrupt,

    /// <summary>schema_version 不是這個版本認得的值。</summary>
    SchemaMismatch,
}

/// <param name="Row">只有 <see cref="StatusReadKind.Ok"/> 時有值。</param>
public sealed record StatusReadResult(StatusReadKind Kind, StatusSnapshotRow? Row);

/// <summary>
/// 命令列用的唯讀讀取（第二階段 spec §10.4）：<c>Mode=ReadOnly</c>、<c>Pooling=False</c>，一個讀取交易讀完就關閉。
/// 檔案不存在時不建立；絕不呼叫 <see cref="DatabaseInitializer.Ensure(string)"/> 或 Recover，損毀時也不改名、不重建。
/// </summary>
/// <remarks>
/// cache.db 是 WAL 模式，唯讀連線可能在旁邊建立 <c>-wal</c>、<c>-shm</c> 附屬檔（第二階段 spec §11.2）。
/// 鎖定、損毀以外的 SqliteException（例如磁碟錯誤、開不了檔）與資料內容的格式錯誤（時間欄位不是 ISO 8601 時的 FormatException）原樣丟出；
/// 命令列把它們當成讀 DB 失敗（§10.6 第 4 列 db_error），不是未預期的例外。
/// </remarks>
public static class StatusSnapshotReader
{
    /// <summary>
    /// Microsoft.Data.Sqlite 的 DefaultTimeout 是整數秒，0 代表不逾時（一直重試），所以至少 1 秒。
    /// </summary>
    private const int MinTimeoutSeconds = 1;

    private const string SchemaVersionTable = "schema_version";
    private const string StatusSnapshotTable = "status_snapshot";

    /// <param name="busyTimeout">遇到鎖定時重試的上限（命令列用 <see cref="Tuning.CliDbBusyTimeout"/>），無條件進位到整數秒。</param>
    public static StatusReadResult Read(string databasePath, TimeSpan busyTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        // 唯讀模式本來就不會建檔；先查是為了回明確的結果，而不是 SQLITE_CANTOPEN。
        if (!File.Exists(databasePath))
        {
            return new StatusReadResult(StatusReadKind.NoDatabase, null);
        }

        try
        {
            using var connection = new SqliteConnection(BuildConnectionString(databasePath, busyTimeout));
            connection.Open();

            // 延遲交易（BEGIN）：第一個 SELECT 才開始讀取交易，之後的查詢都看同一個已提交的版本；唯讀連線不能 BEGIN IMMEDIATE。
            using var transaction = connection.BeginTransaction(deferred: true);
            return ReadInTransaction(connection, transaction);
        }
        catch (SqliteException ex) when (DatabaseInitializer.IsBusy(ex))
        {
            return new StatusReadResult(StatusReadKind.Locked, null);
        }
        catch (SqliteException ex) when (DatabaseInitializer.IsCorruption(ex))
        {
            return new StatusReadResult(StatusReadKind.Corrupt, null);
        }
        catch (SqliteException) when (!File.Exists(databasePath))
        {
            // 檢查之後、開啟之前檔案被移走（例如 App 正在把損毀檔改名）：當成沒有資料庫。
            return new StatusReadResult(StatusReadKind.NoDatabase, null);
        }
    }

    internal static string BuildConnectionString(string databasePath, TimeSpan busyTimeout) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = TimeoutSeconds(busyTimeout),
        }.ToString();

    /// <summary>無條件進位到整數秒，範圍限制在 [<see cref="MinTimeoutSeconds"/>, int.MaxValue]。</summary>
    private static int TimeoutSeconds(TimeSpan busyTimeout) =>
        (int)Math.Clamp(Math.Ceiling(busyTimeout.TotalSeconds), MinTimeoutSeconds, int.MaxValue);

    /// <summary>
    /// 依序判斷：schema 版本不符 → 沒有表 → 沒有列。schema_version 表不存在或還沒有資料列（App 正在建立資料庫）時不算不符，
    /// 交給後面的判斷（多半是 <see cref="StatusReadKind.NoTable"/>）。
    /// </summary>
    private static StatusReadResult ReadInTransaction(SqliteConnection connection, SqliteTransaction transaction)
    {
        var tables = ExistingTables(connection, transaction);

        if (tables.Contains(SchemaVersionTable) && HasOtherSchemaVersion(connection, transaction))
        {
            return new StatusReadResult(StatusReadKind.SchemaMismatch, null);
        }

        if (!tables.Contains(StatusSnapshotTable))
        {
            return new StatusReadResult(StatusReadKind.NoTable, null);
        }

        return CacheRepository.ReadStatusSnapshot(connection, transaction) is { } row
            ? new StatusReadResult(StatusReadKind.Ok, row)
            : new StatusReadResult(StatusReadKind.NoRow, null);
    }

    private static HashSet<string> ExistingTables(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ($schemaVersion, $statusSnapshot);";
        command.Parameters.AddWithValue("$schemaVersion", SchemaVersionTable);
        command.Parameters.AddWithValue("$statusSnapshot", StatusSnapshotTable);

        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>有任何一列不是目前的版本（包含 NULL）就算不符。</summary>
    private static bool HasOtherSchemaVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM schema_version WHERE version IS NOT $version LIMIT 1;";
        command.Parameters.AddWithValue("$version", DatabaseInitializer.CurrentSchemaVersion);
        return command.ExecuteScalar() is not null;
    }
}
