using System.Globalization;
using System.Text;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tests.Auth;
using IsItGreenYet.Core.Tests.Engine;
using IsItGreenYet.Core.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Core.Tests.Data;

public class DatabaseInitializerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 2, 3, 4, TimeSpan.Zero);

    /// <summary>P1 §14.3 的 13 張表加上第二階段 spec §10.2 的三張，依名稱排序。</summary>
    private static readonly string[] AllTables =
    [
        "account", "duration_samples", "http_cache", "notified", "pending_reruns", "pr_seed", "pull_requests",
        "repo_overrides", "repo_permissions", "repos", "run_attempts", "runs", "schema_version", "status_snapshot",
        "watched_runs", "workflows",
    ];

    /// <summary>清單 A、B 與探測 A、B 的第 1 頁（快取鍵是完整網址），加上 Link header 給的第 2 頁。</summary>
    private static readonly string[] RepoListCacheUrls =
    [
        Tuning.ApiBaseUrl + GitHubApi.RepoListUrl(collaborator: false, probe: false),
        Tuning.ApiBaseUrl + GitHubApi.RepoListUrl(collaborator: true, probe: false),
        Tuning.ApiBaseUrl + GitHubApi.RepoListUrl(collaborator: false, probe: true),
        Tuning.ApiBaseUrl + GitHubApi.RepoListUrl(collaborator: true, probe: true),
        Tuning.ApiBaseUrl + "/user/repos?affiliation=owner%2Corganization_member&per_page=100&page=2",
    ];

    private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    private const string LatestBackupSuffix = ".corrupt-latest.bak";

    /// <summary>docs/behavior.zh-TW.md 與 spec §14.3 寫的數字，故意不用 <see cref="Tuning.MaxCorruptBackups"/>：常數改了，文件也要跟著改。</summary>
    private const int DocumentedNumberedBackups = 5;

    [Fact]
    public void 首次建立時會產生資料庫檔且不視為復原()
    {
        using var workspace = new TempWorkspace();

        var result = DatabaseInitializer.Ensure(workspace.Paths.CacheDbPath);

        Assert.False(result.RecoveredFromCorruption);
        Assert.Null(result.BackupPath);
        Assert.True(File.Exists(workspace.Paths.CacheDbPath));
    }

    [Fact]
    public void 目錄不存在時會自動建立()
    {
        using var workspace = new TempWorkspace();
        var nested = Path.Combine(workspace.Root, "a", "b", "cache.db");

        DatabaseInitializer.Ensure(nested);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void 對既有資料庫重複初始化不會遺失資料()
    {
        using var workspace = new TempWorkspace();
        DatabaseInitializer.Ensure(workspace.Paths.CacheDbPath);
        var repository = new CacheRepository(workspace.Paths.CacheDbPath, new ManualTimeProvider(Start));
        repository.SaveAccount(new AccountInfo(42, "octocat"));
        repository.SetOverride(1001, new RepoOverride(Muted: true, ForceIncluded: false));

        var result = DatabaseInitializer.Ensure(workspace.Paths.CacheDbPath);

        Assert.False(result.RecoveredFromCorruption);
        Assert.Equal(new AccountInfo(42, "octocat"), repository.GetAccount());
        Assert.True(repository.GetOverrides()[1001].Muted);
    }

    [Fact]
    public void 資料庫檔損毀時會改名保留原檔並重建可用的空白資料庫()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        File.WriteAllText(databasePath, "這不是 SQLite 檔，是使用者誤蓋上去的垃圾內容", Encoding.UTF8);

        var result = DatabaseInitializer.Ensure(databasePath);

        Assert.True(result.RecoveredFromCorruption);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Contains("這不是 SQLite 檔", File.ReadAllText(result.BackupPath!, Encoding.UTF8), StringComparison.Ordinal);

        // 重建後必須真的能用
        var repository = new CacheRepository(databasePath, new ManualTimeProvider(Start));
        repository.SaveAccount(new AccountInfo(42, "octocat"));
        Assert.Equal(42, repository.GetAccount()!.UserId);
    }

    [Fact]
    public void 改名時WAL等附屬檔跟著主檔一起搬走()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        var backupPath = databasePath + ".corrupt-20261024100304.bak";
        File.WriteAllText(databasePath, "主檔", Encoding.UTF8);
        File.WriteAllText(databasePath + "-wal", "wal", Encoding.UTF8);
        File.WriteAllText(databasePath + "-journal", "journal", Encoding.UTF8);

        DatabaseInitializer.MoveWithSidecars(databasePath, backupPath);

        Assert.Equal("主檔", File.ReadAllText(backupPath, Encoding.UTF8));
        Assert.Equal("wal", File.ReadAllText(backupPath + "-wal", Encoding.UTF8));
        Assert.Equal("journal", File.ReadAllText(backupPath + "-journal", Encoding.UTF8));
        // 不存在的附屬檔直接略過
        Assert.False(File.Exists(backupPath + "-shm"));
        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-journal"));
    }

    /// <summary>
    /// 主檔最後才離開路徑：附屬檔搬不動時 cache.db 還在，下一次重試搬的還是這份資料，不會是路徑上新建的空檔。
    /// 編號備份與 latest 共用這個方法。目的地放資料夾讓 File.Move 丟例外，不靠 Windows 的檔案共用規則。
    /// </summary>
    [Fact]
    public void 附屬檔搬不動時主檔留在原地()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        var backupPath = databasePath + ".corrupt-20261024100304.bak";
        File.WriteAllText(databasePath, "主檔", Encoding.UTF8);
        File.WriteAllText(databasePath + "-wal", "wal", Encoding.UTF8);
        Directory.CreateDirectory(backupPath + "-wal");

        Assert.ThrowsAny<Exception>(() => DatabaseInitializer.MoveWithSidecars(databasePath, backupPath));

        Assert.Equal("主檔", File.ReadAllText(databasePath, Encoding.UTF8));
        Assert.Equal("wal", File.ReadAllText(databasePath + "-wal", Encoding.UTF8));
        Assert.False(File.Exists(backupPath));
    }

    [Fact]
    public void 損毀時舊的WAL等附屬檔不會留在重建的資料庫旁()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        File.WriteAllText(databasePath, "垃圾主檔", Encoding.UTF8);
        string[] suffixes = ["-wal", "-shm", "-journal"];
        foreach (var suffix in suffixes)
        {
            File.WriteAllText(databasePath + suffix, "垃圾" + suffix, Encoding.UTF8);
        }

        var result = DatabaseInitializer.Ensure(databasePath);
        SqliteConnection.ClearAllPools();

        // 無效的殘檔可能已被 SQLite 自己清掉；沒清掉的必須跟著備份檔改名，不能留在原處
        Assert.True(result.RecoveredFromCorruption);
        foreach (var suffix in suffixes)
        {
            var leftover = databasePath + suffix;
            Assert.False(File.Exists(leftover) && File.ReadAllText(leftover, Encoding.UTF8) == "垃圾" + suffix, leftover);
        }

        Assert.Equal(DatabaseInitializer.CurrentSchemaVersion, ReadSchemaVersions(databasePath).Single());
    }

    [Fact]
    public void 備份檔名用TimeProvider的本地時間()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        File.WriteAllText(databasePath, "垃圾內容", Encoding.UTF8);
        // 02:03:04Z 在台北是 10:03:04
        var time = new ManualTimeProvider(Start, ManualTimeProvider.Taipei);

        var result = DatabaseInitializer.Ensure(databasePath, time);

        Assert.Equal(databasePath + ".corrupt-20261024100304.bak", result.BackupPath);
    }

    [Fact]
    public void 同一秒內再次損毀時備份檔名不衝突也不覆蓋舊備份()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        var time = new ManualTimeProvider(Start);

        File.WriteAllText(databasePath, "第一次的垃圾", Encoding.UTF8);
        var first = DatabaseInitializer.Ensure(databasePath, time);
        SqliteConnection.ClearAllPools();
        File.Delete(databasePath);
        File.WriteAllText(databasePath, "第二次的垃圾", Encoding.UTF8);
        var second = DatabaseInitializer.Ensure(databasePath, time);

        Assert.True(second.RecoveredFromCorruption);
        Assert.NotEqual(first.BackupPath, second.BackupPath);
        Assert.Equal("第一次的垃圾", File.ReadAllText(first.BackupPath!, Encoding.UTF8));
        Assert.Equal("第二次的垃圾", File.ReadAllText(second.BackupPath!, Encoding.UTF8));
    }

    [Fact]
    public void 檔頭正確但內容毀損時也會復原()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        DatabaseInitializer.Ensure(databasePath);
        SqliteConnection.ClearAllPools();
        // 保留 SQLite 的 100 bytes 檔頭，把第 1 頁其餘內容（sqlite_schema 所在）蓋成垃圾。
        const int sqliteHeaderBytes = 100;
        const int garbageBytes = 3000;
        var bytes = File.ReadAllBytes(databasePath);
        for (var i = sqliteHeaderBytes; i < sqliteHeaderBytes + garbageBytes && i < bytes.Length; i++)
        {
            bytes[i] = 0xFF;
        }

        File.WriteAllBytes(databasePath, bytes);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            File.Delete(databasePath + suffix);
        }

        var result = DatabaseInitializer.Ensure(databasePath);

        Assert.True(result.RecoveredFromCorruption);
        Assert.Equal(DatabaseInitializer.CurrentSchemaVersion, ReadSchemaVersions(databasePath).Single());
    }

    [Fact]
    public void 執行中發現損毀時Recover改名保留原檔並重建空白資料庫()
    {
        // Ensure 不做完整檢查，內容的損毀要到讀寫時才發現；那時 MonitorEngine 依留檔規則呼叫 SetAside 或 Discard，再 Rebuild（spec §15）；Recover 等於 SetAside 加 Rebuild。
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        DatabaseInitializer.Ensure(databasePath);
        var repository = new CacheRepository(databasePath, new ManualTimeProvider(Start));
        repository.SaveAccount(new AccountInfo(42, "octocat"));
        var time = new ManualTimeProvider(Start, ManualTimeProvider.Taipei);

        var result = DatabaseInitializer.Recover(databasePath, time);

        Assert.True(result.RecoveredFromCorruption);
        Assert.Equal(databasePath + ".corrupt-20261024100304.bak", result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Null(repository.GetAccount());
        Assert.Equal(DatabaseInitializer.CurrentSchemaVersion, ReadSchemaVersions(databasePath).Single());
    }

    [Fact]
    public void 不是損毀的開啟錯誤直接丟出且不改名()
    {
        using var workspace = new TempWorkspace();
        // 路徑被資料夾佔住：SQLite 回 CANTOPEN，不是檔案壞掉，不能把它當損毀改名。
        var databasePath = workspace.Paths.CacheDbPath;
        Directory.CreateDirectory(databasePath);

        Assert.Throws<SqliteException>(() => DatabaseInitializer.Ensure(databasePath));

        Assert.True(Directory.Exists(databasePath));
        Assert.Empty(Directory.GetFiles(workspace.Root, "cache.db.corrupt-*"));
    }

    [Fact]
    public void 路徑為空字串時丟出參數例外()
    {
        Assert.Throws<ArgumentException>(() => DatabaseInitializer.Ensure("   "));
    }

    [Fact]
    public void 建立spec列出的所有資料表()
    {
        using var workspace = new TempWorkspace();

        DatabaseInitializer.Ensure(workspace.Paths.CacheDbPath);

        Assert.Equal(AllTables, ReadTableNames(workspace.Paths.CacheDbPath));
    }

    [Fact]
    public void 第一階段的資料庫開啟後補建三張表而且版本仍是1()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        PhaseOneDatabase.Create(databasePath);
        var repository = new CacheRepository(databasePath, new ManualTimeProvider(Start));
        repository.SaveAccount(new AccountInfo(42, "octocat"));
        Assert.Equal(AllTables.Except(PhaseOneDatabase.PhaseTwoTables), ReadTableNames(databasePath));

        var result = DatabaseInitializer.Ensure(databasePath);

        // 新增資料表不升 schema 版本（第二階段 spec §10.2），第一階段的資料原封不動。
        Assert.False(result.RecoveredFromCorruption);
        Assert.Equal(AllTables, ReadTableNames(databasePath));
        Assert.Equal([1], ReadSchemaVersions(databasePath));
        Assert.Equal(new AccountInfo(42, "octocat"), repository.GetAccount());
    }

    [Fact]
    public void 第一次建立repo_permissions時刪掉repo清單的快取()
    {
        // 第一階段存下的 ETag 只會換來 304，讀不到 permissions；建表的那一次刪掉清單 A、B 所有分頁的快取（第二階段 spec §4.1）。
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        PhaseOneDatabase.Create(databasePath);
        var repository = new CacheRepository(databasePath, new ManualTimeProvider(Start));
        foreach (var url in RepoListCacheUrls)
        {
            repository.PutCache(new HttpCacheEntry(url, "W/\"list\"", null, [2001]));
        }

        DatabaseInitializer.Ensure(databasePath);

        Assert.All(RepoListCacheUrls, url => Assert.Null(repository.Get(url)));
    }

    [Fact]
    public void 第二次初始化不再刪repo清單的快取()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;

        // 全新的資料庫：repo_permissions 在這一次建立，之後只判斷這一次。
        DatabaseInitializer.Ensure(databasePath);
        var repository = new CacheRepository(databasePath, new ManualTimeProvider(Start));
        foreach (var url in RepoListCacheUrls)
        {
            repository.PutCache(new HttpCacheEntry(url, "W/\"list\"", null, [2001]));
        }

        DatabaseInitializer.Ensure(databasePath);

        Assert.All(RepoListCacheUrls, url => Assert.Equal("W/\"list\"", repository.Get(url)?.ETag));
    }

    [Fact]
    public void 其他網址的快取不受影響()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        PhaseOneDatabase.Create(databasePath);
        var repository = new CacheRepository(databasePath, new ManualTimeProvider(Start));
        string[] others =
        [
            Tuning.ApiBaseUrl + "/user",
            Tuning.ApiBaseUrl + "/repos/octo-org/sample-app/actions/runs?per_page=30",
            Tuning.ApiBaseUrl + "/repos/octo-org/sample-app/pulls?state=open&per_page=100",
            // 開頭同樣是 /user/repos，但不是 repo 清單
            Tuning.ApiBaseUrl + "/user/repository_invitations?per_page=100",
        ];
        foreach (var url in others.Concat(RepoListCacheUrls))
        {
            repository.PutCache(new HttpCacheEntry(url, "W/\"keep\"", null, [1]));
        }

        DatabaseInitializer.Ensure(databasePath);

        Assert.All(others, url => Assert.Equal("W/\"keep\"", repository.Get(url)?.ETag));
        Assert.All(RepoListCacheUrls, url => Assert.Null(repository.Get(url)));
    }

    [Fact]
    public void 重複初始化後schema_version仍只有一筆且為目前版本()
    {
        using var workspace = new TempWorkspace();

        DatabaseInitializer.Ensure(workspace.Paths.CacheDbPath);
        DatabaseInitializer.Ensure(workspace.Paths.CacheDbPath);
        DatabaseInitializer.Ensure(workspace.Paths.CacheDbPath);

        Assert.Equal([DatabaseInitializer.CurrentSchemaVersion], ReadSchemaVersions(workspace.Paths.CacheDbPath));
    }

    [Fact]
    public void journal_mode是WAL()
    {
        using var workspace = new TempWorkspace();

        DatabaseInitializer.Ensure(workspace.Paths.CacheDbPath);

        using var connection = Open(workspace.Paths.CacheDbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string)command.ExecuteScalar()!);
    }

    // ── 備份的上限與丟棄（spec §14.3、§15）─────────────────────────────

    [Fact]
    public void 編號備份未滿上限時SetAside用編號檔名()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        var existing = AddNumberedBackups(databasePath, Tuning.MaxCorruptBackups - 1);
        File.WriteAllText(databasePath, "這次的損毀檔", Encoding.UTF8);
        File.WriteAllText(databasePath + "-wal", "這次的 wal", Encoding.UTF8);

        var backup = DatabaseInitializer.SetAside(databasePath, new ManualTimeProvider(Start, ManualTimeProvider.Taipei));

        Assert.Equal(databasePath + ".corrupt-20261024100304.bak", backup);
        Assert.Equal("這次的損毀檔", File.ReadAllText(backup!, Encoding.UTF8));
        Assert.Equal("這次的 wal", File.ReadAllText(backup + "-wal", Encoding.UTF8));
        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(LatestBackup(databasePath)));
        Assert.Equal(Tuning.MaxCorruptBackups, DatabaseInitializer.CountBackups(databasePath));
        AssertUnchanged(existing);
    }

    /// <remarks>latest 不算名額：編號還有空位時不能動它。</remarks>
    [Fact]
    public void 編號備份未滿而且已有latest時用編號檔名latest不動()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        var latest = LatestBackup(databasePath);
        var existing = AddNumberedBackups(databasePath, Tuning.MaxCorruptBackups - 1);
        File.WriteAllText(latest, "舊的 latest", Encoding.UTF8);
        File.WriteAllText(databasePath, "這次的損毀檔", Encoding.UTF8);

        var backup = DatabaseInitializer.SetAside(databasePath, new ManualTimeProvider(Start));

        Assert.NotEqual(latest, backup);
        Assert.Equal("這次的損毀檔", File.ReadAllText(backup!, Encoding.UTF8));
        Assert.Equal("舊的 latest", File.ReadAllText(latest, Encoding.UTF8));
        Assert.Equal(Tuning.MaxCorruptBackups, DatabaseInitializer.CountBackups(databasePath));
        AssertUnchanged(existing);
    }

    [Fact]
    public void 編號備份滿額時改名到latest再一次就覆寫而且舊的附屬檔被清掉()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        var latest = LatestBackup(databasePath);
        var time = new ManualTimeProvider(Start);
        var existing = AddNumberedBackups(databasePath, Tuning.MaxCorruptBackups);

        File.WriteAllText(databasePath, "第一次", Encoding.UTF8);
        File.WriteAllText(databasePath + "-wal", "第一次 wal", Encoding.UTF8);
        File.WriteAllText(databasePath + "-shm", "第一次 shm", Encoding.UTF8);
        Assert.Equal(latest, DatabaseInitializer.SetAside(databasePath, time));
        Assert.Equal("第一次", File.ReadAllText(latest, Encoding.UTF8));
        Assert.Equal("第一次 wal", File.ReadAllText(latest + "-wal", Encoding.UTF8));
        Assert.Equal("第一次 shm", File.ReadAllText(latest + "-shm", Encoding.UTF8));

        // 這一次沒有 -wal、-shm：latest 舊的同名附屬檔要清掉，舊 WAL 不能配上新的主檔。
        File.WriteAllText(databasePath, "第二次", Encoding.UTF8);
        File.WriteAllText(databasePath + "-journal", "第二次 journal", Encoding.UTF8);
        Assert.Equal(latest, DatabaseInitializer.SetAside(databasePath, time));

        Assert.Equal("第二次", File.ReadAllText(latest, Encoding.UTF8));
        Assert.Equal("第二次 journal", File.ReadAllText(latest + "-journal", Encoding.UTF8));
        Assert.False(File.Exists(latest + "-wal"));
        Assert.False(File.Exists(latest + "-shm"));
        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(databasePath + "-journal"));
        Assert.Equal(Tuning.MaxCorruptBackups, DatabaseInitializer.CountBackups(databasePath));
        AssertUnchanged(existing);
    }

    [Fact]
    public void 升級前已有8份編號備份時一份都不刪只動latest()
    {
        const int backupsBeforeUpgrade = 8;
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        var existing = AddNumberedBackups(databasePath, backupsBeforeUpgrade);
        File.WriteAllText(databasePath, "升級後第一次", Encoding.UTF8);

        var backup = DatabaseInitializer.SetAside(databasePath, new ManualTimeProvider(Start));

        Assert.Equal(LatestBackup(databasePath), backup);
        Assert.Equal("升級後第一次", File.ReadAllText(backup!, Encoding.UTF8));
        Assert.Equal(backupsBeforeUpgrade, DatabaseInitializer.CountBackups(databasePath));
        AssertUnchanged(existing);
    }

    [Theory]
    [InlineData("cache.db.corrupt-20261024100000.bak")]
    [InlineData("cache.db.corrupt-20261024100000-2.bak")]
    [InlineData("cache.db.corrupt-20261024100000-12.bak")]
    public void CountBackups只算程式會產生的兩種檔名形狀(string fileName)
    {
        using var workspace = new TempWorkspace();
        File.WriteAllText(workspace.PathTo(fileName), "備份", Encoding.UTF8);

        Assert.Equal(1, DatabaseInitializer.CountBackups(workspace.Paths.CacheDbPath));
    }

    [Theory]
    [InlineData("cache.db.corrupt-重要.bak")]
    [InlineData("cache.db.corrupt-20261024100000 - 複本.bak")]
    [InlineData("cache.db.corrupt-20261024100000.bak.bak")]
    [InlineData("cache.db.corrupt-20261024100000.bak-wal")]
    [InlineData("cache.db.corrupt-20261024100000-2.bak-shm")]
    [InlineData("cache.db.corrupt-latest.bak")]
    [InlineData("cache.db.corrupt-2026102410000.bak")]
    [InlineData("cache.db.corrupt-202610241000000.bak")]
    [InlineData("cache.db.corrupt-２０２６１０２４１０００００.bak")]
    [InlineData("cache.db.corrupt-20261024100000-.bak")]
    [InlineData("xcache.db.corrupt-20261024100000.bak")]
    [InlineData("cacheXdb.corrupt-20261024100000.bak")]
    [InlineData("settings.json.broken-20261024100000.bak")]
    public void CountBackups不算其他檔名(string fileName)
    {
        using var workspace = new TempWorkspace();
        File.WriteAllText(workspace.PathTo(fileName), "不是程式留的編號備份", Encoding.UTF8);

        Assert.Equal(0, DatabaseInitializer.CountBackups(workspace.Paths.CacheDbPath));
    }

    [Fact]
    public void CountBackups不算資料夾而且資料夾不存在時是0()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.PathTo("cache.db.corrupt-20261024100000.bak"));

        Assert.Equal(0, DatabaseInitializer.CountBackups(workspace.Paths.CacheDbPath));
        Assert.Equal(0, DatabaseInitializer.CountBackups(Path.Combine(workspace.Root, "不存在", "cache.db")));
    }

    [Fact]
    public void 沒有編號備份時不可丟棄()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        DatabaseInitializer.Ensure(databasePath);

        Assert.False(DatabaseInitializer.IsDiscardable(databasePath));
    }

    [Fact]
    public void 只有latest或使用者自己命名的備份時不可丟棄()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        DatabaseInitializer.Ensure(databasePath);
        File.WriteAllText(LatestBackup(databasePath), "latest", Encoding.UTF8);
        File.WriteAllText(databasePath + ".corrupt-重要.bak", "使用者自己命名", Encoding.UTF8);

        Assert.False(DatabaseInitializer.IsDiscardable(databasePath));
    }

    [Fact]
    public void repo_overrides有資料列時不可丟棄()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        DatabaseInitializer.Ensure(databasePath);
        new CacheRepository(databasePath, new ManualTimeProvider(Start)).SetOverride(1001, new RepoOverride(Muted: true, ForceIncluded: false));
        AddNumberedBackups(databasePath, 1);

        Assert.True(DatabaseInitializer.HasOverrideRows(databasePath));
        Assert.False(DatabaseInitializer.IsDiscardable(databasePath));
    }

    [Fact]
    public void 有編號備份而且repo_overrides沒有資料列時可以丟棄()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        DatabaseInitializer.Ensure(databasePath);
        AddNumberedBackups(databasePath, 1);

        Assert.False(DatabaseInitializer.HasOverrideRows(databasePath));
        Assert.True(DatabaseInitializer.IsDiscardable(databasePath));
    }

    [Fact]
    public void repo_overrides壞掉讀不到時可以丟棄()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        DatabaseInitializer.Ensure(databasePath);
        new CacheRepository(databasePath, new ManualTimeProvider(Start)).SetOverride(1001, new RepoOverride(Muted: true, ForceIncluded: false));
        AddNumberedBackups(databasePath, 1);
        MonitorEngineReviewTests.CorruptTable(databasePath, "repo_overrides");

        Assert.False(DatabaseInitializer.HasOverrideRows(databasePath));
        Assert.True(DatabaseInitializer.IsDiscardable(databasePath));
    }

    [Fact]
    public void repo_overrides不存在或檔案不是SQLite時可以丟棄()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        DatabaseInitializer.Ensure(databasePath);
        MonitorEngineReviewTests.Execute(databasePath, "DROP TABLE repo_overrides;");
        AddNumberedBackups(databasePath, 1);
        Assert.True(DatabaseInitializer.IsDiscardable(databasePath));

        SqliteConnection.ClearAllPools();
        DatabaseInitializer.Discard(databasePath);
        File.WriteAllText(databasePath, "這不是 SQLite 檔", Encoding.UTF8);
        Assert.True(DatabaseInitializer.IsDiscardable(databasePath));
    }

    [Fact]
    public void 資料庫檔不存在時可以丟棄而且不會建立檔案()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        AddNumberedBackups(databasePath, 1);

        Assert.True(DatabaseInitializer.IsDiscardable(databasePath));

        Assert.False(File.Exists(databasePath));
        Assert.All(SidecarSuffixes, suffix => Assert.False(File.Exists(databasePath + suffix)));
    }

    [Fact]
    public void Discard只刪主檔與它的附屬檔()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        var latest = LatestBackup(databasePath);
        var kept = AddNumberedBackups(databasePath, Tuning.MaxCorruptBackups);
        File.WriteAllText(latest, "latest", Encoding.UTF8);
        File.WriteAllText(latest + "-wal", "latest wal", Encoding.UTF8);
        File.WriteAllText(databasePath + ".corrupt-重要.bak", "使用者自己命名", Encoding.UTF8);
        kept[latest] = File.ReadAllBytes(latest);
        kept[latest + "-wal"] = File.ReadAllBytes(latest + "-wal");
        kept[databasePath + ".corrupt-重要.bak"] = File.ReadAllBytes(databasePath + ".corrupt-重要.bak");
        File.WriteAllText(databasePath, "損毀檔", Encoding.UTF8);
        foreach (var suffix in SidecarSuffixes)
        {
            File.WriteAllText(databasePath + suffix, "附屬檔", Encoding.UTF8);
        }

        DatabaseInitializer.Discard(databasePath);

        Assert.False(File.Exists(databasePath));
        Assert.All(SidecarSuffixes, suffix => Assert.False(File.Exists(databasePath + suffix)));
        AssertUnchanged(kept);
    }

    [Fact]
    public void Discard對不存在的檔不丟例外而且部分成功後重試可以接著做()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;

        DatabaseInitializer.Discard(databasePath);

        // 上一次主檔刪掉了、附屬檔沒刪成：這一次接著刪。
        File.WriteAllText(databasePath + "-wal", "殘留", Encoding.UTF8);
        DatabaseInitializer.Discard(databasePath);
        Assert.False(File.Exists(databasePath + "-wal"));
    }

    [Fact]
    public void 主檔不存在時SetAside回null清掉殘留附屬檔接著Rebuild可用()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        foreach (var suffix in SidecarSuffixes)
        {
            File.WriteAllText(databasePath + suffix, "殘留", Encoding.UTF8);
        }

        var backup = DatabaseInitializer.SetAside(databasePath, new ManualTimeProvider(Start));
        DatabaseInitializer.Rebuild(databasePath);

        Assert.Null(backup);
        Assert.Empty(Directory.GetFiles(workspace.Root, "cache.db.corrupt-*"));
        Assert.Equal(DatabaseInitializer.CurrentSchemaVersion, ReadSchemaVersions(databasePath).Single());
    }

    /// <remarks>開著的檔案沒有允許刪除就不能改名或刪除，這是 Windows 的檔案共用規則，所以只在 Windows 上執行。</remarks>
    [WindowsFact]
    public void 別的程式開著cache_db時SetAside與Discard都丟例外而且檔案不動()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        var time = new ManualTimeProvider(Start);
        var kept = AddNumberedBackups(databasePath, 1);
        File.WriteAllText(databasePath, "損毀檔", Encoding.UTF8);

        using (new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.ThrowsAny<IOException>(() => DatabaseInitializer.SetAside(databasePath, time));
            Assert.ThrowsAny<IOException>(() => DatabaseInitializer.Discard(databasePath));
        }

        Assert.Equal("損毀檔", File.ReadAllText(databasePath, Encoding.UTF8));
        Assert.Equal(1, DatabaseInitializer.CountBackups(databasePath));
        AssertUnchanged(kept);
    }

    /// <remarks>同上，只在 Windows 上執行。主檔與附屬檔要一起搬：有一個搬不動時什麼都不動。</remarks>
    [WindowsFact]
    public void 別的程式開著WAL時SetAside丟例外而且主檔沒有被搬走() => AssertHeldSidecarBlocksSetAside("-wal", existingBackups: 0);

    /// <remarks>同上，只在 Windows 上執行。</remarks>
    [WindowsFact]
    public void 編號備份滿額時別的程式開著WAL也丟例外而且主檔沒有被搬走() => AssertHeldSidecarBlocksSetAside("-wal", Tuning.MaxCorruptBackups);

    /// <remarks>同上，只在 Windows 上執行。WAL 排在前面，沒有先檢查的話它會先被搬走，和主檔分開。</remarks>
    [WindowsFact]
    public void 別的程式開著shm時SetAside丟例外而且WAL留在主檔旁() => AssertHeldSidecarBlocksSetAside("-shm", existingBackups: 0);

    /// <remarks>同上，只在 Windows 上執行。</remarks>
    [WindowsFact]
    public void 編號備份滿額時別的程式開著shm也丟例外而且WAL留在主檔旁() => AssertHeldSidecarBlocksSetAside("-shm", Tuning.MaxCorruptBackups);

    /// <remarks>同上，只在 Windows 上執行。主檔搬不動的話，附屬檔先搬就會和主檔分開。</remarks>
    [WindowsFact]
    public void 未滿額時別的程式開著cache_db就丟例外而且附屬檔留在主檔旁()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        File.WriteAllText(databasePath, "損毀檔", Encoding.UTF8);
        File.WriteAllText(databasePath + "-wal", "它的 wal", Encoding.UTF8);

        using (new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.ThrowsAny<IOException>(() => DatabaseInitializer.SetAside(databasePath, new ManualTimeProvider(Start)));
        }

        Assert.Equal("損毀檔", File.ReadAllText(databasePath, Encoding.UTF8));
        Assert.Equal("它的 wal", File.ReadAllText(databasePath + "-wal", Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(workspace.Root, "cache.db.corrupt-*"));
    }

    /// <param name="heldSuffix">被別的程式開著的附屬檔。</param>
    private static void AssertHeldSidecarBlocksSetAside(string heldSuffix, int existingBackups)
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        var kept = AddNumberedBackups(databasePath, existingBackups);
        File.WriteAllText(databasePath, "損毀檔", Encoding.UTF8);
        File.WriteAllText(databasePath + "-wal", "它的 wal", Encoding.UTF8);
        File.WriteAllText(databasePath + "-shm", "它的 shm", Encoding.UTF8);

        using (new FileStream(databasePath + heldSuffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.ThrowsAny<IOException>(() => DatabaseInitializer.SetAside(databasePath, new ManualTimeProvider(Start)));
        }

        Assert.Equal("損毀檔", File.ReadAllText(databasePath, Encoding.UTF8));
        Assert.Equal("它的 wal", File.ReadAllText(databasePath + "-wal", Encoding.UTF8));
        Assert.Equal("它的 shm", File.ReadAllText(databasePath + "-shm", Encoding.UTF8));
        Assert.False(File.Exists(LatestBackup(databasePath)));
        Assert.Equal(existingBackups, DatabaseInitializer.CountBackups(databasePath));
        AssertUnchanged(kept);
    }

    /// <remarks>同上，只在 Windows 上執行。主檔先搬走的話，cache.db 已不在、latest 卻只換了一半，下一次重試可能拿新建的空檔蓋掉 latest。</remarks>
    [WindowsFact]
    public void 滿額時別的程式開著latest就丟例外而且什麼都不動() => AssertHeldFileBlocksLatest(LatestBackupSuffix);

    /// <remarks>同上，只在 Windows 上執行。</remarks>
    [WindowsFact]
    public void 滿額時別的程式開著latest的WAL就丟例外而且什麼都不動() => AssertHeldFileBlocksLatest(LatestBackupSuffix + "-wal");

    /// <remarks>同上，只在 Windows 上執行。</remarks>
    [WindowsFact]
    public void 滿額時別的程式開著latest的shm就丟例外而且什麼都不動() => AssertHeldFileBlocksLatest(LatestBackupSuffix + "-shm");

    /// <remarks>同上，只在 Windows 上執行。</remarks>
    [WindowsFact]
    public void 滿額時別的程式開著latest的journal就丟例外而且什麼都不動() => AssertHeldFileBlocksLatest(LatestBackupSuffix + "-journal");

    /// <remarks>同上，只在 Windows 上執行。主檔搬不動時 latest 舊的附屬檔也不能先刪掉。</remarks>
    [WindowsFact]
    public void 滿額時別的程式開著cache_db就丟例外而且latest的附屬檔不動() => AssertHeldFileBlocksLatest(string.Empty);

    /// <param name="heldSuffix">接在資料庫路徑後面，就是被別的程式開著的檔。</param>
    private static void AssertHeldFileBlocksLatest(string heldSuffix)
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        var latest = LatestBackup(databasePath);
        var kept = AddNumberedBackups(databasePath, Tuning.MaxCorruptBackups);
        File.WriteAllText(databasePath, "損毀檔", Encoding.UTF8);
        File.WriteAllText(latest, "舊的 latest", Encoding.UTF8);
        foreach (var suffix in SidecarSuffixes)
        {
            File.WriteAllText(latest + suffix, "舊的" + suffix, Encoding.UTF8);
        }

        using (new FileStream(databasePath + heldSuffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.ThrowsAny<IOException>(() => DatabaseInitializer.SetAside(databasePath, new ManualTimeProvider(Start)));
        }

        Assert.Equal("損毀檔", File.ReadAllText(databasePath, Encoding.UTF8));
        Assert.Equal("舊的 latest", File.ReadAllText(latest, Encoding.UTF8));
        foreach (var suffix in SidecarSuffixes)
        {
            Assert.Equal("舊的" + suffix, File.ReadAllText(latest + suffix, Encoding.UTF8));
        }

        Assert.Equal(Tuning.MaxCorruptBackups, DatabaseInitializer.CountBackups(databasePath));
        AssertUnchanged(kept);
    }

    /// <remarks>唯讀的 latest 蓋不過去：檢查要在清掉它舊的附屬檔之前擋下來。Unix 的 root 不受唯讀限制，只在 Windows 上執行。</remarks>
    [WindowsFact]
    public void 滿額時latest是唯讀就丟例外而且什麼都不動() => AssertReadOnlyBlocksLatest(LatestBackupSuffix);

    /// <remarks>同上，只在 Windows 上執行。</remarks>
    [WindowsFact]
    public void 滿額時latest的附屬檔是唯讀就丟例外而且什麼都不動() => AssertReadOnlyBlocksLatest(LatestBackupSuffix + "-shm");

    /// <param name="readOnlySuffix">接在資料庫路徑後面，就是唯讀的檔。</param>
    private static void AssertReadOnlyBlocksLatest(string readOnlySuffix)
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        var latest = LatestBackup(databasePath);
        var kept = AddNumberedBackups(databasePath, Tuning.MaxCorruptBackups);
        File.WriteAllText(databasePath, "損毀檔", Encoding.UTF8);
        File.WriteAllText(latest, "舊的 latest", Encoding.UTF8);
        foreach (var suffix in SidecarSuffixes)
        {
            File.WriteAllText(latest + suffix, "舊的" + suffix, Encoding.UTF8);
        }

        File.SetAttributes(databasePath + readOnlySuffix, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => DatabaseInitializer.SetAside(databasePath, new ManualTimeProvider(Start)));

            Assert.Equal("損毀檔", File.ReadAllText(databasePath, Encoding.UTF8));
            Assert.Equal("舊的 latest", File.ReadAllText(latest, Encoding.UTF8));
            foreach (var suffix in SidecarSuffixes)
            {
                Assert.Equal("舊的" + suffix, File.ReadAllText(latest + suffix, Encoding.UTF8));
            }

            AssertUnchanged(kept);
        }
        finally
        {
            // 唯讀的檔刪不掉，TempWorkspace 收尾時會失敗。
            File.SetAttributes(databasePath + readOnlySuffix, FileAttributes.Normal);
        }
    }

    [Fact]
    public void 反覆用垃圾檔呼叫Ensure超過上限次數時編號備份停在上限總共最多多一份latest()
    {
        const int extraRounds = 3;
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;
        var time = new ManualTimeProvider(Start);
        string? first = null;

        for (var round = 0; round < Tuning.MaxCorruptBackups + extraRounds; round++)
        {
            SqliteConnection.ClearAllPools();
            File.WriteAllText(databasePath, "第 " + round.ToString(CultureInfo.InvariantCulture) + " 次的垃圾", Encoding.UTF8);

            var result = DatabaseInitializer.Ensure(databasePath, time);

            Assert.True(result.RecoveredFromCorruption);
            Assert.NotNull(result.BackupPath);
            first ??= result.BackupPath;
            Assert.Equal(DatabaseInitializer.CurrentSchemaVersion, ReadSchemaVersions(databasePath).Single());
        }

        Assert.Equal(DocumentedNumberedBackups, DatabaseInitializer.CountBackups(databasePath));
        Assert.Equal(DocumentedNumberedBackups + 1, Directory.GetFiles(workspace.Root, "cache.db.corrupt-*.bak").Length);
        Assert.Equal("第 0 次的垃圾", File.ReadAllText(first!, Encoding.UTF8));
        var lastRound = (Tuning.MaxCorruptBackups + extraRounds - 1).ToString(CultureInfo.InvariantCulture);
        Assert.Equal("第 " + lastRound + " 次的垃圾", File.ReadAllText(LatestBackup(databasePath), Encoding.UTF8));
    }

    [Fact]
    public void 時鐘撥回同一秒時不覆蓋舊的編號備份()
    {
        using var workspace = new TempWorkspace();
        var databasePath = workspace.Paths.CacheDbPath;

        File.WriteAllText(databasePath, "第一份", Encoding.UTF8);
        var first = DatabaseInitializer.SetAside(databasePath, new ManualTimeProvider(Start));
        File.WriteAllText(databasePath, "第二份", Encoding.UTF8);
        var second = DatabaseInitializer.SetAside(databasePath, new ManualTimeProvider(Start.AddHours(1)));

        // 時鐘撥回第一份的那一秒。
        File.WriteAllText(databasePath, "第三份", Encoding.UTF8);
        var third = DatabaseInitializer.SetAside(databasePath, new ManualTimeProvider(Start));

        Assert.Equal(3, new[] { first, second, third }.Distinct().Count());
        Assert.Equal("第一份", File.ReadAllText(first!, Encoding.UTF8));
        Assert.Equal("第二份", File.ReadAllText(second!, Encoding.UTF8));
        Assert.Equal("第三份", File.ReadAllText(third!, Encoding.UTF8));
        Assert.Equal(3, DatabaseInitializer.CountBackups(databasePath));
    }

    private static string LatestBackup(string databasePath) => databasePath + LatestBackupSuffix;

    /// <summary>放 <paramref name="count"/> 份編號備份（各帶一個 -wal），內容各不相同。</summary>
    /// <returns>每個檔案的路徑與位元組，給 <see cref="AssertUnchanged"/> 比對。</returns>
    private static Dictionary<string, byte[]> AddNumberedBackups(string databasePath, int count)
    {
        var files = new Dictionary<string, byte[]>();
        for (var number = 0; number < count; number++)
        {
            var backup = databasePath + ".corrupt-" + Start.AddDays(-1).AddMinutes(number).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".bak";
            foreach (var path in new[] { backup, backup + "-wal" })
            {
                files[path] = Encoding.UTF8.GetBytes(Path.GetFileName(path));
                File.WriteAllBytes(path, files[path]);
            }
        }

        return files;
    }

    private static void AssertUnchanged(Dictionary<string, byte[]> files)
    {
        foreach (var (path, bytes) in files)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        return connection;
    }

    private static List<int> ReadSchemaVersions(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version;";
        using var reader = command.ExecuteReader();
        var versions = new List<int>();
        while (reader.Read())
        {
            versions.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
        }

        return versions;
    }

    private static List<string> ReadTableNames(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
