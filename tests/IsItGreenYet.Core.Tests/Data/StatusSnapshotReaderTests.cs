using System.Diagnostics;
using System.Text;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Core.Tests.Data;

/// <summary>命令列讀狀態表（第二階段 spec §10.4）：只讀、不建檔、不改名，鎖定與損毀都回結果種類而不是丟例外。</summary>
public class StatusSnapshotReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 2, 0, 0, TimeSpan.Zero);

    /// <summary>鎖定測試用的逾時；斷言耗時在這個值加 <see cref="LockedSlack"/> 之內。</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan LockedSlack = TimeSpan.FromSeconds(2);

    private static readonly StatusSnapshotRow Row = new(
        WrittenAt: Now,
        HeartbeatAt: Now.AddSeconds(1),
        StoppedAt: Now.AddSeconds(30),
        AppVersion: "0.2.0+test",
        StatusJson: """{"schemaVersion":1,"overall":"success"}""",
        RefreshStartedSeq: 7,
        RefreshDoneSeq: 6,
        RefreshPartial: true);

    [Fact]
    public void 沒有檔案回NoDatabase而且不建檔()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;

        var result = StatusSnapshotReader.Read(databasePath, Tuning.CliDbBusyTimeout);

        Assert.Equal(new StatusReadResult(StatusReadKind.NoDatabase, null), result);
        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
    }

    [Fact]
    public void 第一階段的資料庫回NoTable()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        PhaseOneDatabase.Create(databasePath);

        var result = StatusSnapshotReader.Read(databasePath, Tuning.CliDbBusyTimeout);

        // 只讀：不會順手補建表
        Assert.Equal(new StatusReadResult(StatusReadKind.NoTable, null), result);
        Assert.Null(Scalar(databasePath, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'status_snapshot';"));
    }

    [Fact]
    public void 空的狀態表回NoRow()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        DatabaseInitializer.Ensure(databasePath);

        var result = StatusSnapshotReader.Read(databasePath, Tuning.CliDbBusyTimeout);

        Assert.Equal(new StatusReadResult(StatusReadKind.NoRow, null), result);
    }

    [Fact]
    public void 有資料回Ok()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        DatabaseInitializer.Ensure(databasePath);
        new CacheRepository(databasePath).SaveStatusSnapshot(Row);

        var result = StatusSnapshotReader.Read(databasePath, Tuning.CliDbBusyTimeout);

        // status_json 原樣交回，由命令列解析
        Assert.Equal(new StatusReadResult(StatusReadKind.Ok, Row), result);
    }

    [Fact]
    public void schema版本不符回SchemaMismatch()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        DatabaseInitializer.Ensure(databasePath);
        new CacheRepository(databasePath).SaveStatusSnapshot(Row);
        Execute(databasePath, "UPDATE schema_version SET version = $v;", ("$v", DatabaseInitializer.CurrentSchemaVersion + 1));

        var result = StatusSnapshotReader.Read(databasePath, Tuning.CliDbBusyTimeout);

        Assert.Equal(new StatusReadResult(StatusReadKind.SchemaMismatch, null), result);
    }

    [Fact]
    public void App持有寫入交易時仍讀得到最後提交的值()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        DatabaseInitializer.Ensure(databasePath);
        new CacheRepository(databasePath).SaveStatusSnapshot(Row);

        using var writer = Open(databasePath);
        using var transaction = writer.BeginTransaction();
        using (var update = writer.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE status_snapshot SET status_json = '{}', refresh_done_seq = 99;";
            update.ExecuteNonQuery();
        }

        var result = StatusSnapshotReader.Read(databasePath, ShortTimeout);

        // 第二階段 spec §11.2 的實驗結果：WAL 下唯讀讀取不會被寫入交易擋住，讀到的是最後一次已提交的值
        Assert.Equal(new StatusReadResult(StatusReadKind.Ok, Row), result);
    }

    [Fact]
    public void 寫入鎖住超過逾時回Locked()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        DatabaseInitializer.Ensure(databasePath);
        new CacheRepository(databasePath).SaveStatusSnapshot(Row);
        SqliteConnection.ClearAllPools();

        // WAL 模式下 BEGIN EXCLUSIVE 擋不住讀取；locking_mode=EXCLUSIVE 的連線一旦寫入就獨占整個檔案，讀取會一直拿到 BUSY。
        using var locker = Open(databasePath);
        using (var pragma = locker.CreateCommand())
        {
            pragma.CommandText = "PRAGMA locking_mode = EXCLUSIVE;";
            pragma.ExecuteNonQuery();
        }

        using var transaction = locker.BeginTransaction();
        using (var update = locker.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE status_snapshot SET refresh_done_seq = 99;";
            update.ExecuteNonQuery();
        }

        var watch = Stopwatch.StartNew();
        var result = StatusSnapshotReader.Read(databasePath, ShortTimeout);
        watch.Stop();

        Assert.Equal(new StatusReadResult(StatusReadKind.Locked, null), result);
        Assert.True(watch.Elapsed < ShortTimeout + LockedSlack, $"耗時 {watch.Elapsed}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 損毀的檔案回Corrupt而且不改名(bool keepHeader)
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        byte[] content;
        if (keepHeader)
        {
            // 檔頭正確、第 1 頁（sqlite_schema）其餘內容是垃圾：讀表時才回 SQLITE_CORRUPT。
            DatabaseInitializer.Ensure(databasePath);
            SqliteConnection.ClearAllPools();
            const int sqliteHeaderBytes = 100;
            const int garbageBytes = 3000;
            content = File.ReadAllBytes(databasePath);
            for (var i = sqliteHeaderBytes; i < sqliteHeaderBytes + garbageBytes && i < content.Length; i++)
            {
                content[i] = 0xFF;
            }

            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                File.Delete(databasePath + suffix);
            }
        }
        else
        {
            // 根本不是 SQLite 檔：SQLITE_NOTADB。
            content = Encoding.UTF8.GetBytes("這不是 SQLite 檔，是使用者誤蓋上去的垃圾內容");
        }

        File.WriteAllBytes(databasePath, content);

        var result = StatusSnapshotReader.Read(databasePath, Tuning.CliDbBusyTimeout);

        // 絕不改名或重建（第二階段 spec §10.4），原檔原封不動
        Assert.Equal(new StatusReadResult(StatusReadKind.Corrupt, null), result);
        Assert.Equal(content, File.ReadAllBytes(databasePath));
        Assert.Empty(Directory.GetFiles(workspace.Root, "cache.db.corrupt-*"));
    }

    [Theory]
    [InlineData(5000, 5)]
    [InlineData(1200, 2)]
    [InlineData(1000, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-3000, 1)]
    public void 連線字串是唯讀不共用而且逾時無條件進位到整數秒(int milliseconds, int expectedSeconds)
    {
        // DefaultTimeout 是整數秒，0 代表不逾時，所以最少 1 秒。
        var builder = new SqliteConnectionStringBuilder(
            StatusSnapshotReader.BuildConnectionString("cache.db", TimeSpan.FromMilliseconds(milliseconds)));

        Assert.Equal("cache.db", builder.DataSource);
        Assert.Equal(SqliteOpenMode.ReadOnly, builder.Mode);
        Assert.False(builder.Pooling);
        Assert.Equal(expectedSeconds, builder.DefaultTimeout);
    }

    [Fact]
    public void 逾時超過整數範圍時取整數上限()
    {
        var builder = new SqliteConnectionStringBuilder(StatusSnapshotReader.BuildConnectionString("cache.db", TimeSpan.MaxValue));

        Assert.Equal(int.MaxValue, builder.DefaultTimeout);
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string databasePath, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static object? Scalar(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
