using Microsoft.Data.Sqlite;

namespace ObjectAnalyzer.Tests;

public class DatabaseReaderTests
{
    [Fact]
    public void ReadRows_NormalizesSqliteValues()
    {
        string dbPath = CreateTempDb();
        try
        {
            var rows = DatabaseReader.ReadRows(
                $"Data Source={dbPath};Pooling=False", DatabaseProvider.Sqlite, "SELECT * FROM users").ToList();

            Assert.Equal(3, rows.Count);

            var first = rows[0];
            Assert.Equal(1L, first["id"]);
            Assert.Equal("Alice", first["name"]);
            Assert.Equal(1L, first["active"]);     // SQLite INTEGER -> long
            Assert.Equal(9.5m, first["score"]);    // 数值统一为无损 CLR 表示
            Assert.Equal(new byte[] { 65, 66, 67 }, Assert.IsType<byte[]>(first["photo"])); // 保留二进制类型

            // DATETIME affinity columns read back as DateTime, normalized to ISO-8601 string
            string created = Assert.IsType<string>(first["created_at"]);
            Assert.StartsWith("2026-08-08T12:00:00", created);

            Assert.Equal(2L, rows[1]["id"]);
            Assert.Equal(0L, rows[1]["active"]);
            Assert.Equal(8L, rows[1]["score"]);

            // NULL row kept intact
            Assert.Equal(3L, rows[2]["id"]);
            Assert.Null(rows[2]["name"]);
            Assert.Null(rows[2]["created_at"]);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public void Analyze_ConsumesDatabaseRowsLikeFileRows()
    {
        string dbPath = CreateTempDb();
        try
        {
            var rows = DatabaseReader.ReadRows(
                $"Data Source={dbPath};Pooling=False", DatabaseProvider.Sqlite, "SELECT * FROM users").ToList();

            var result = ObjectAnalyzer.Analysis.ObjectAnalyzer.Analyze(rows);

            Assert.Equal(3, result.TotalObjects);

            var idStat = result.FieldStatistics.First(f => f.FieldName == "id");
            Assert.Equal("Number", idStat.PrimaryType);
            Assert.Equal(2.0, idStat.AvgNumber);

            var nameStat = result.FieldStatistics.First(f => f.FieldName == "name");
            Assert.Equal("String", nameStat.PrimaryType);
            Assert.Equal(1.0, nameStat.Coverage); // SELECT 每行都含该列 → 覆盖率 100%（第 3 行为 null）
            Assert.True(nameStat.NullAppeared);

            var activeStat = result.FieldStatistics.First(f => f.FieldName == "active");
            Assert.Equal("Number", activeStat.PrimaryType);

            var createdStat = result.FieldStatistics.First(f => f.FieldName == "created_at");
            Assert.Equal("DateTime", createdStat.SemanticType); // ISO-8601 string recognized as DateTime
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=mydb", DatabaseProvider.Postgres)]
    [InlineData("Server=localhost;Uid=root;Pwd=123", DatabaseProvider.MySql)]
    [InlineData("Server=mysql.internal;Port=3306;Database=shop", DatabaseProvider.MySql)]
    [InlineData("Data Source=test.db", DatabaseProvider.Sqlite)]
    [InlineData("Filename=C:\\data\\archive.sqlite", DatabaseProvider.Sqlite)]
    [InlineData("Data Source=:memory:", DatabaseProvider.Sqlite)]
    [InlineData("Server=.;Integrated Security=true;Database=mydb", DatabaseProvider.SqlServer)]
    [InlineData("Server=localhost;Initial Catalog=mydb;User Id=sa;Password=x", DatabaseProvider.SqlServer)]
    public void DetectProvider_InfersFromConnectionStringKeywords(string connectionString, DatabaseProvider expected)
    {
        Assert.Equal(expected, DatabaseReader.ResolveProvider(null, connectionString));
    }

    [Fact]
    public void ResolveProvider_ExplicitProviderWinsOverDetection()
    {
        Assert.Equal(DatabaseProvider.Postgres, DatabaseReader.ResolveProvider("postgresql", "Data Source=test.db"));
        Assert.Equal(DatabaseProvider.MySql, DatabaseReader.ResolveProvider("maria", "Server=.;Initial Catalog=db"));
        Assert.Equal(DatabaseProvider.Sqlite, DatabaseReader.ResolveProvider("sqlite", "Server=.;Initial Catalog=db"));
        Assert.Equal(DatabaseProvider.SqlServer, DatabaseReader.ResolveProvider("mssql", "Server=localhost;Uid=root"));
    }

    [Fact]
    public void ParseProvider_RejectsUnknownProvider()
    {
        var ex = Assert.Throws<ArgumentException>(() => DatabaseReader.ParseProvider("oracle"));
        Assert.Contains("postgres", ex.Message);
        Assert.Throws<InvalidOperationException>(() => DatabaseReader.ResolveProvider(null, "Database=mydb"));
    }

    private static string CreateTempDb()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"oa_db_test_{Guid.NewGuid():N}.db");
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE users (
                    id INTEGER PRIMARY KEY,
                    name TEXT,
                    active INTEGER,
                    score REAL,
                    photo BLOB,
                    created_at DATETIME
                );
                INSERT INTO users VALUES
                    (1, 'Alice', 1, 9.5, X'414243', '2026-08-08T12:00:00Z'),
                    (2, 'Bob', 0, 8.0, X'', '2026-08-09T15:30:00Z'),
                    (3, NULL, 1, 7.25, NULL, NULL);
                """;
            command.ExecuteNonQuery();
        }
        return dbPath;
    }
}