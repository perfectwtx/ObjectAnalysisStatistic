using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Tests;

/// <summary>Release only this database's idle connections; never clear unrelated parallel tests' pools.</summary>
internal static class SqliteTestDatabase
{
    public static void Delete(string path)
    {
        using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        SqliteConnection.ClearPool(pool);
        foreach (var file in new[] { path, path + "-shm", path + "-wal" })
            File.Delete(file); // Missing files are harmless; permission/lock failures must fail the test.
    }
}
