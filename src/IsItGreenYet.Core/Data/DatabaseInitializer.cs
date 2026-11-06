using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Core.Data;

/// <param name="RecoveredFromCorruption">原本的資料庫檔無法開啟，已重建空白資料庫；有沒有改名保留看 <paramref name="BackupPath"/>。</param>
/// <param name="BackupPath">損毀檔被改名後的完整路徑；沒有發生復原，或復原時沒有留備份（主檔已經不在）時為 null。</param>
public sealed record DatabaseInitResult(bool RecoveredFromCorruption, string? BackupPath);

/// <summary>
/// 建立與升級 cache.db（spec §14.3），檔案損毀時改名保留後重建（同 SAM）。
/// 留檔規則：編號備份（<c>cache.db.corrupt-&lt;時間戳&gt;[-序號].bak</c>）最多 <see cref="Tuning.MaxCorruptBackups"/> 份，滿了改名到固定檔名 <c>cache.db.corrupt-latest.bak</c>，
/// 覆寫上一次的這個檔（<see cref="SetAside"/>）。程式絕不刪、不覆寫任何編號備份；只會刪目前路徑的 cache.db 與它的附屬檔（<see cref="Discard"/>，只有持續損毀的重試會用），
/// 或主檔已經不在時留下的附屬檔（<see cref="SetAside"/>），或覆寫 latest 與它的附屬檔。
/// </summary>
public static class DatabaseInitializer
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>損毀檔改名用的時間格式：<c>cache.db.corrupt-&lt;yyyyMMddHHmmss&gt;.bak</c>（spec §14.3）。</summary>
    private const string BackupTimestampFormat = "yyyyMMddHHmmss";

    /// <summary>編號備份滿額之後用的固定後綴：<c>cache.db.corrupt-latest.bak</c>，每次覆寫。</summary>
    private const string LatestBackupSuffix = ".corrupt-latest.bak";

    /// <summary>
    /// 程式產生的編號備份，資料庫檔名之後的部分：<c>.corrupt-&lt;時間戳&gt;.bak</c> 或 <c>.corrupt-&lt;時間戳&gt;-&lt;序號&gt;.bak</c>（<see cref="NextBackupPath"/>）。
    /// 只認 ASCII 數字；<c>\z</c> 擋掉 <c>.bak-wal</c> 這類附屬檔。使用者自己命名的檔案與 latest 都不算。
    /// </summary>
    private static readonly string NumberedBackupNameSuffix = $@"\.corrupt-[0-9]{{{BackupTimestampFormat.Length}}}(-[0-9]+)?\.bak\z";

    // SQLite 主要結果碼（https://www.sqlite.org/rescode.html）。只有這兩種代表檔案內容壞了；
    // 被鎖住、無法開啟、磁碟錯誤等都不是損毀，不能把使用者的資料庫改名。
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    // 同上來源：別的連線佔著鎖（BUSY）或同一個程序內的表鎖（LOCKED）。重試到逾時仍拿不到時丟出。
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    /// <summary>repo 清單（E2，清單 A／B 與探測 A／B）快取鍵的共同開頭，第一次建立 repo_permissions 時刪除（第二階段 spec §4.1）。</summary>
    private const string RepoListCachePrefix = Tuning.ApiBaseUrl + "/user/repos?";

    /// <summary>擴充結果碼的低 8 位元就是主要結果碼。</summary>
    private const int PrimaryResultCodeMask = 0xFF;

    /// <summary>同一秒內第二次損毀時，備份檔名從這個序號開始加。</summary>
    private const int FirstDuplicateBackupNumber = 2;

    private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    /// <summary>
    /// 確保資料庫可用。檔案不存在就建立；檔案損毀就改名保留後重建（<see cref="Recover"/>），這條路不刪主檔與編號備份，滿額時覆寫 latest、清掉它舊的附屬檔。
    /// 備份檔名用系統時間；測試請改用可注入時間的多載。
    /// </summary>
    public static DatabaseInitResult Ensure(string databasePath) => Ensure(databasePath, TimeProvider.System);

    /// <param name="time">只用來產生損毀備份檔名（本地時間）。</param>
    public static DatabaseInitResult Ensure(string databasePath, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(time);

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            CreateSchema(databasePath);
            return new DatabaseInitResult(false, null);
        }
        catch (SqliteException ex) when (IsCorruption(ex) && File.Exists(databasePath))
        {
            // 走到這裡代表檔案存在但不是可用的 SQLite 資料庫（或內容毀損）。
            // 保留原檔供日後搶救，重建一個空白資料庫讓程式還能用；快取丟了只會重抓。
            return Recover(databasePath, time);
        }
    }

    /// <summary>
    /// 把損毀的資料庫改名保留（<see cref="SetAside"/>），重建空白資料庫（<see cref="Rebuild"/>）。
    /// Ensure 不做完整檢查，內容的損毀要到讀寫時才發現，那時由 MonitorEngine 依留檔規則呼叫 <see cref="SetAside"/> 或 <see cref="Discard"/>，再呼叫 <see cref="Rebuild"/>（spec §15）。
    /// </summary>
    /// <param name="time">只用來產生備份檔名（本地時間）。</param>
    internal static DatabaseInitResult Recover(string databasePath, TimeProvider time)
    {
        var backupPath = SetAside(databasePath, time);
        Rebuild(databasePath);
        return new DatabaseInitResult(true, backupPath);
    }

    /// <summary>
    /// 把損毀的資料庫連同附屬檔改名保留：編號備份未滿 <see cref="Tuning.MaxCorruptBackups"/> 份時用編號檔名，滿了就改名到 latest，覆寫上一次的 latest。
    /// 任何一步失敗都丟例外（例如別的程式開著）：MonitorEngine 退避後重試，App 啟動時（<see cref="Ensure(string)"/>）顯示錯誤後結束。
    /// </summary>
    /// <param name="time">只用來產生備份檔名（本地時間）。</param>
    /// <returns>備份的完整路徑；主檔已經不在、沒有東西可以保留時回 null。</returns>
    internal static string? SetAside(string databasePath, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(time);

        // 連線池裡閒置的連線還抓著檔案握柄，不釋放就改不了名。
        SqliteConnection.ClearAllPools();

        // 上一次改名成功、CreateSchema 失敗而且沒留下新檔：只剩殘留的附屬檔，留著會讓重建再次失敗。
        if (!File.Exists(databasePath))
        {
            DeleteSidecars(databasePath);
            return null;
        }

        // 先確認搬得動：別的程式開著時什麼都不動，附屬檔不會和主檔分開。
        ThrowIfSidecarInUse(databasePath);
        ThrowIfInUse(databasePath);

        if (CountBackups(databasePath) >= Tuning.MaxCorruptBackups)
        {
            return OverwriteLatest(databasePath);
        }

        var backupPath = NextBackupPath(databasePath, time.GetLocalNow());
        MoveWithSidecars(databasePath, backupPath);
        return backupPath;
    }

    /// <summary>重建空白資料庫。呼叫前舊檔要已經離開這個路徑（<see cref="SetAside"/> 或 <see cref="Discard"/>）。</summary>
    internal static void Rebuild(string databasePath) => CreateSchema(databasePath);

    /// <summary>
    /// 磁碟上有幾份程式產生的編號備份（<see cref="NumberedBackupNameSuffix"/>）。latest、使用者自己命名的檔案、備份的附屬檔都不算。資料夾不存在時是 0。
    /// </summary>
    internal static int CountBackups(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (directory is null || !Directory.Exists(directory))
        {
            return 0;
        }

        // Regex.IsMatch 的靜態多載會快取解析結果，同一個資料庫路徑不會每個檔案重新解析一次。
        var pattern = @"\A" + Regex.Escape(Path.GetFileName(databasePath)) + NumberedBackupNameSuffix;
        return Directory.EnumerateFiles(directory).Count(file => Regex.IsMatch(Path.GetFileName(file), pattern));
    }

    /// <summary>
    /// 用唯讀、不進連線池的連線查 repo_overrides 有沒有資料列（靜音與強制納入，spec §14.3）。不會建立主檔；主檔存在時 SQLite 可能在旁邊留下 -shm 與空的 -wal，之後改名或刪除時會一起處理。
    /// 檔案不存在、表不存在、損毀、鎖定等任何例外都回 false：呼叫端只有在記憶體旗標說「這個程序自己建的而且沒設過」時才問，這裡只是多一層確認。
    /// </summary>
    internal static bool HasOverrideRows(string databasePath)
    {
        try
        {
            // 遇到鎖定不多等（實際是連線字串的下限 1 秒）：判斷不出來就當沒有，接著刪檔也會因為別的程式開著而失敗、走退避。
            using var connection = new SqliteConnection(StatusSnapshotReader.BuildConnectionString(databasePath, TimeSpan.Zero));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM repo_overrides LIMIT 1;";
            return command.ExecuteScalar() is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 這份損毀的資料庫可以不留檔直接丟棄：磁碟上至少已有 1 份編號備份，而且讀不到 repo_overrides 的資料列。
    /// 呼叫端只有在記憶體旗標說「這個程序自己建的而且沒設過」時才問它（MonitorEngine）。
    /// </summary>
    internal static bool IsDiscardable(string databasePath) => CountBackups(databasePath) > 0 && !HasOverrideRows(databasePath);

    /// <summary>
    /// 刪掉目前路徑的資料庫與它的附屬檔，不動任何備份。對不存在的檔不丟例外，部分成功後重試可以接著做；別的程式開著時丟例外，由呼叫端退避後重試。
    /// </summary>
    internal static void Discard(string databasePath)
    {
        // 連線池裡閒置的連線還抓著檔案握柄，不釋放就刪不掉。
        SqliteConnection.ClearAllPools();
        File.Delete(databasePath);
        DeleteSidecars(databasePath);
    }

    /// <summary>
    /// WAL / journal 殘檔改名為同名加後綴，主檔最後才改名為 <paramref name="backupPath"/>；殘檔會讓重建再次失敗。
    /// 主檔最後才走：別的程式在檢查之後才開著附屬檔時 cache.db 還在，下一次重試搬的還是這份資料，不會把路徑上新建的空檔留成備份或蓋掉 latest。
    /// 主檔在檢查之後才搬不動時，已經搬走的附屬檔會和它分開，只損失 WAL：編號路徑留下孤兒附屬檔；latest 在下一次重試之前是舊主檔配新 WAL，重試時清掉這份 WAL（舊主檔本來就要被蓋掉）。
    /// 無效的殘檔通常在開啟失敗或關閉連線時已被 SQLite 自己清掉，這裡搬走的是它留下來的。
    /// </summary>
    /// <param name="overwrite">主檔的目的地已有舊檔時覆寫（latest）。附屬檔一律覆寫。</param>
    internal static void MoveWithSidecars(string databasePath, string backupPath, bool overwrite = false)
    {
        foreach (var suffix in SidecarSuffixes)
        {
            var sidecar = databasePath + suffix;
            if (File.Exists(sidecar))
            {
                File.Move(sidecar, backupPath + suffix, overwrite: true);
            }
        }

        File.Move(databasePath, backupPath, overwrite);
    }

    internal static string BuildConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
        }.ToString();

    /// <remarks>MonitorEngine 也用它判斷執行中讀寫時遇到的 SqliteException 是不是損毀。</remarks>
    internal static bool IsCorruption(SqliteException ex) =>
        (ex.SqliteErrorCode & PrimaryResultCodeMask) is SqliteCorrupt or SqliteNotADatabase;

    /// <remarks>命令列（<see cref="StatusSnapshotReader"/>）用它分辨「鎖定逾時」與其他讀取錯誤。</remarks>
    internal static bool IsBusy(SqliteException ex) =>
        (ex.SqliteErrorCode & PrimaryResultCodeMask) is SqliteBusy or SqliteLocked;

    /// <summary>同一秒內再次損毀時不覆蓋前一份備份，改加序號。</summary>
    private static string NextBackupPath(string databasePath, DateTimeOffset localNow)
    {
        var stamp = localNow.ToString(BackupTimestampFormat, CultureInfo.InvariantCulture);
        var candidate = $"{databasePath}.corrupt-{stamp}.bak";
        for (var number = FirstDuplicateBackupNumber; File.Exists(candidate); number++)
        {
            candidate = $"{databasePath}.corrupt-{stamp}-{number}.bak";
        }

        return candidate;
    }

    /// <summary>
    /// 檔案存在時要能獨占打開，否則丟例外、什麼都不動。比 File.Move 嚴格：允許刪除的握柄（FileShare.Delete）搬得動也會擋下來，放開之後下一次重試才搬。
    /// </summary>
    /// <param name="access">要被蓋掉的目的地用 <see cref="FileAccess.ReadWrite"/>，唯讀的檔也擋下來；來源用 <see cref="FileAccess.Read"/>，唯讀的檔照樣搬得動。</param>
    private static void ThrowIfInUse(string path, FileAccess access = FileAccess.Read)
    {
        if (File.Exists(path))
        {
            using var exclusive = new FileStream(path, FileMode.Open, access, FileShare.None);
        }
    }

    /// <summary>每個現存的附屬檔都要能獨占打開，否則丟例外、什麼都不動。</summary>
    private static void ThrowIfSidecarInUse(string databasePath, FileAccess access = FileAccess.Read)
    {
        foreach (var suffix in SidecarSuffixes)
        {
            ThrowIfInUse(databasePath + suffix, access);
        }
    }

    /// <summary>
    /// 主檔與附屬檔改名到 latest。主檔與它的附屬檔呼叫端確認過；這裡先確認 latest 與它的附屬檔蓋得過去，再清掉舊的附屬檔（舊 WAL 不能配上新主檔）。
    /// </summary>
    private static string OverwriteLatest(string databasePath)
    {
        var latest = databasePath + LatestBackupSuffix;
        ThrowIfInUse(latest, FileAccess.ReadWrite);
        ThrowIfSidecarInUse(latest, FileAccess.ReadWrite);
        DeleteSidecars(latest);

        MoveWithSidecars(databasePath, latest, overwrite: true);

        return latest;
    }

    private static void DeleteSidecars(string databasePath)
    {
        foreach (var suffix in SidecarSuffixes)
        {
            File.Delete(databasePath + suffix);
        }
    }

    private static void CreateSchema(string databasePath)
    {
        using var connection = new SqliteConnection(BuildConnectionString(databasePath));
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            // 先做一次真正會讀到 header 的操作，損毀檔會在這裡就丟出 SqliteException。
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteScalar();
        }

        // 欄位對照 spec §14.3；時間一律是 UTC ISO 8601 文字，整數 id 都是 int64。
        // repos.name 與 runs.path 不在 spec 的「主要欄位」裡，但 RepoInfo／RunInfo 需要它們才能完整往返。
        // 最後兩張是第二階段 spec §10.2 新增的表：第一階段的 DB 第一次被開啟時補建，不升 schema 版本；
        // pending_reruns 多一個 repo_id（計畫的差異表第 2 列）。repo_permissions 另外建，見 CreatePermissionsTable。
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS account (
                id         INTEGER PRIMARY KEY CHECK (id = 1),
                user_id    INTEGER NOT NULL,
                login      TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS repos (
                repo_id              INTEGER PRIMARY KEY,
                full_name            TEXT NOT NULL,
                name                 TEXT NOT NULL,
                owner_id             INTEGER NOT NULL,
                owner_login          TEXT NOT NULL,
                owner_type           TEXT NOT NULL,
                category             TEXT NOT NULL,
                is_fork              INTEGER NOT NULL,
                is_archived          INTEGER NOT NULL,
                is_disabled          INTEGER NOT NULL,
                is_private           INTEGER NOT NULL,
                default_branch       TEXT NOT NULL,
                html_url             TEXT NOT NULL,
                pushed_at            TEXT NULL,
                workflows_total      INTEGER NULL,
                workflows_active     INTEGER NULL,
                workflows_checked_at TEXT NULL,
                skip_reason          TEXT NULL,
                problem_note         TEXT NULL,
                last_success_at      TEXT NULL,
                missing_rounds       INTEGER NOT NULL DEFAULT 0,
                baseline_t0          TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS workflows (
                repo_id         INTEGER NOT NULL,
                workflow_id     INTEGER NOT NULL,
                name            TEXT NOT NULL,
                path            TEXT NOT NULL,
                state           TEXT NOT NULL,
                html_url        TEXT NOT NULL,
                seed_checked_at TEXT NULL,
                seed_result     TEXT NULL,
                PRIMARY KEY (repo_id, workflow_id)
            );

            CREATE TABLE IF NOT EXISTS repo_overrides (
                repo_id        INTEGER PRIMARY KEY,
                muted          INTEGER NOT NULL,
                force_included INTEGER NOT NULL,
                updated_at     TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS http_cache (
                url        TEXT PRIMARY KEY NOT NULL,
                etag       TEXT NULL,
                next_url   TEXT NULL,
                item_ids   TEXT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS runs (
                run_id                 INTEGER PRIMARY KEY,
                repo_id                INTEGER NOT NULL,
                workflow_id            INTEGER NOT NULL,
                workflow_name          TEXT NULL,
                path                   TEXT NOT NULL,
                run_number             INTEGER NOT NULL,
                run_attempt            INTEGER NOT NULL,
                display_title          TEXT NOT NULL,
                event                  TEXT NOT NULL,
                status                 TEXT NULL,
                conclusion             TEXT NULL,
                head_branch            TEXT NULL,
                head_sha               TEXT NOT NULL,
                head_repo_id           INTEGER NOT NULL,
                head_repo_full_name    TEXT NULL,
                head_repo_html_url     TEXT NULL,
                actor_id               INTEGER NULL,
                actor_login            TEXT NULL,
                triggering_actor_login TEXT NULL,
                pr_numbers             TEXT NOT NULL,
                run_started_at         TEXT NULL,
                created_at             TEXT NOT NULL,
                updated_at             TEXT NOT NULL,
                html_url               TEXT NOT NULL,
                in_latest_page         INTEGER NOT NULL DEFAULT 0,
                gone                   INTEGER NOT NULL DEFAULT 0,
                fetched_at             TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_runs_repo ON runs(repo_id);

            CREATE TABLE IF NOT EXISTS run_attempts (
                run_id                INTEGER NOT NULL,
                run_attempt           INTEGER NOT NULL,
                status                TEXT NULL,
                conclusion            TEXT NULL,
                run_started_at        TEXT NULL,
                updated_at            TEXT NOT NULL,
                completed_observed_at TEXT NULL,
                PRIMARY KEY (run_id, run_attempt)
            );

            CREATE TABLE IF NOT EXISTS duration_samples (
                run_id           INTEGER NOT NULL,
                run_attempt      INTEGER NOT NULL,
                repo_id          INTEGER NOT NULL,
                workflow_id      INTEGER NOT NULL,
                duration_seconds REAL NOT NULL,
                completed_at     TEXT NOT NULL,
                PRIMARY KEY (run_id, run_attempt)
            );
            CREATE INDEX IF NOT EXISTS idx_duration_samples_workflow
                ON duration_samples(repo_id, workflow_id, completed_at);

            CREATE TABLE IF NOT EXISTS pull_requests (
                repo_id             INTEGER NOT NULL,
                number              INTEGER NOT NULL,
                head_repo_id        INTEGER NULL,
                head_repo_full_name TEXT NULL,
                head_ref            TEXT NOT NULL,
                head_sha            TEXT NOT NULL,
                html_url            TEXT NOT NULL,
                fetched_at          TEXT NOT NULL,
                PRIMARY KEY (repo_id, number)
            );

            CREATE TABLE IF NOT EXISTS pr_seed (
                repo_id    INTEGER NOT NULL,
                head_sha   TEXT NOT NULL,
                checked_at TEXT NOT NULL,
                PRIMARY KEY (repo_id, head_sha)
            );

            CREATE TABLE IF NOT EXISTS notified (
                run_id      INTEGER NOT NULL,
                run_attempt INTEGER NOT NULL,
                conclusion  TEXT NOT NULL,
                kind        TEXT NULL,
                notified_at TEXT NOT NULL,
                suppressed  INTEGER NOT NULL,
                PRIMARY KEY (run_id, run_attempt, conclusion)
            );

            CREATE TABLE IF NOT EXISTS watched_runs (
                run_id     INTEGER PRIMARY KEY,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS status_snapshot (
                id                  INTEGER PRIMARY KEY CHECK (id = 1),
                written_at          TEXT NOT NULL,
                heartbeat_at        TEXT NOT NULL,
                stopped_at          TEXT NULL,
                app_version         TEXT NOT NULL,
                status_json         TEXT NOT NULL,
                refresh_started_seq INTEGER NOT NULL,
                refresh_done_seq    INTEGER NOT NULL,
                refresh_partial     INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS pending_reruns (
                run_id       INTEGER PRIMARY KEY,
                repo_id      INTEGER NOT NULL,
                prev_attempt INTEGER NOT NULL,
                requested_at TEXT NOT NULL,
                notify       INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        CreatePermissionsTable(connection);

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT COUNT(*) FROM schema_version;";
        var rows = Convert.ToInt64(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (rows == 0)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO schema_version (version) VALUES ($v);";
            insert.Parameters.AddWithValue("$v", CurrentSchemaVersion);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 第一次建立 repo_permissions 時（建立前先查這張表在不在），同一個交易刪掉 repo 清單所有分頁的快取（第二階段 spec §4.1「升級時的一次性重抓」）：
    /// 第一階段存下的 ETag 只會換來 304，讀不到 permissions；刪掉之後下一次抓清單就會拿到完整回應。表已經存在就什麼都不做。
    /// </summary>
    /// <remarks>
    /// 快取鍵是完整網址（GitHubClient），清單 A、B、探測與 Link 給的第 2 頁以後都以 <see cref="RepoListCachePrefix"/> 開頭。
    /// 用 substr 逐字比對，同 CacheRepository 的前綴刪除。
    /// </remarks>
    private static void CreatePermissionsTable(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'repo_permissions';";
            if (exists.ExecuteScalar() is not null)
            {
                transaction.Commit();
                return;
            }
        }

        using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS repo_permissions (
                repo_id    INTEGER NOT NULL,
                user_id    INTEGER NOT NULL,
                can_push   INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (repo_id, user_id)
            );

            DELETE FROM http_cache WHERE substr(url, 1, length($prefix)) = $prefix;
            """;
        create.Parameters.AddWithValue("$prefix", RepoListCachePrefix);
        create.ExecuteNonQuery();

        transaction.Commit();
    }
}
