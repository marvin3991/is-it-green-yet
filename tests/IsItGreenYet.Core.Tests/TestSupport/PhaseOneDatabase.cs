using IsItGreenYet.Core.Data;
using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Core.Tests.TestSupport;

/// <summary>
/// 第一階段的 cache.db：照目前的方式建好，再拿掉第二階段新增的三張表（第二階段 spec §10.2），
/// 剩下的 13 張表就是第一階段建的樣子（第二階段只加表，不改既有的欄位）。
/// </summary>
internal static class PhaseOneDatabase
{
    public static readonly string[] PhaseTwoTables = ["pending_reruns", "repo_permissions", "status_snapshot"];

    public static void Create(string databasePath)
    {
        DatabaseInitializer.Ensure(databasePath);

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        foreach (var table in PhaseTwoTables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS {table};";
            command.ExecuteNonQuery();
        }
    }
}
