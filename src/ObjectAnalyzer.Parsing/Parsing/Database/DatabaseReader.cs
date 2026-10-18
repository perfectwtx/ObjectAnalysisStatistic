using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace ObjectAnalyzer.Parsing;

/// <summary>
/// 数据库直连 (Live DB Ingestion)：通过 Connection String + SQL 查询直连
/// PostgreSQL / MySQL / SQLite / SQL Server，利用 DbDataReader 惰性流式读取行，
/// 无需先将数据库导出为 CSV/JSON 文件即可进行实时结构与质量分析。
/// 每行通过统一值模型归一化，保留精确数值、日期、Guid 和二进制的类型身份，
/// 从而可直接复用 ObjectAnalyzer 分析管道与 Schema 推断。
/// </summary>
public static class DatabaseReader
{
    private static readonly string[] SqliteExtensions = { ".db", ".db3", ".sqlite", ".sqlite3", ".sdb", ".sb3" };

    public static string DisplayName(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.Postgres => "PostgreSQL",
        DatabaseProvider.MySql => "MySQL",
        DatabaseProvider.Sqlite => "SQLite",
        DatabaseProvider.SqlServer => "SQL Server",
        _ => "Unknown"
    };

    /// <summary>解析用户提供的 --provider 字符串（大小写不敏感），非法值抛 ArgumentException。</summary>
    public static DatabaseProvider ParseProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("未指定数据库类型，请使用 --provider postgres|mysql|sqlite|sqlserver。");

        return provider.Trim().ToLowerInvariant() switch
        {
            "postgres" or "postgresql" or "pg" or "npgsql" => DatabaseProvider.Postgres,
            "mysql" or "maria" or "mariadb" => DatabaseProvider.MySql,
            "sqlite" or "sqlite3" => DatabaseProvider.Sqlite,
            "sqlserver" or "mssql" or "sql-server" or "tsql" => DatabaseProvider.SqlServer,
            _ => throw new ArgumentException($"不支持的数据库类型: '{provider}'。可用: postgres, mysql, sqlite, sqlserver。")
        };
    }

    /// <summary>
    /// 解析数据源类型：--provider 显式指定时优先；否则尽力根据连接字符串关键字推断。
    /// 推断规则（最佳努力，建议显式指定 --provider）：
    /// Filename= → SQLite；Host= → PostgreSQL；Uid= → MySQL；
    /// Trusted_Connection=/Integrated Security=（Windows 集成验证）→ SQL Server；
    /// Initial Catalog= → SQL Server；Server=/Data Source= 且为 *.db/*.sqlite 文件 → SQLite；
    /// Port=3306 → MySQL；其余默认 SQL Server。
    /// </summary>
    public static DatabaseProvider ResolveProvider(string? provider, string connectionString)
    {
        if (!string.IsNullOrWhiteSpace(provider))
            return ParseProvider(provider);

        string? detected = DetectProvider(connectionString);
        if (detected == null)
            throw new InvalidOperationException(
                "无法根据连接字符串自动推断数据库类型，请显式指定 --provider postgres|mysql|sqlite|sqlserver。");
        return ParseProvider(detected);
    }

    /// <summary>从连接字符串推断数据库类型，无法识别时返回 null。</summary>
    public static string? DetectProvider(string connectionString)
    {
        var map = ParseConnectionString(connectionString);

        if (map.ContainsKey("Filename"))
            return "sqlite";
        if (map.ContainsKey("Host") || map.ContainsKey("HostAddr"))
            return "postgres";
        if (map.ContainsKey("Uid"))
            return "mysql";
        if (map.ContainsKey("Trusted_Connection") || map.ContainsKey("Integrated Security"))
            return "sqlserver";
        if (map.ContainsKey("Initial Catalog"))
            return "sqlserver";

        string? ds = GetAny(map, "Server", "Data Source", "DataSource", "Address", "Addr");
        if (ds != null)
        {
            if (IsSqliteDataSource(ds))
                return "sqlite";
            if (map.TryGetValue("Port", out string? port) && port == "3306")
                return "mysql";
            return "sqlserver";
        }

        return null;
    }

    /// <summary>
    /// 惰性流式读取 SQL 查询结果。仅在枚举期间保持数据库连接；
    /// 每个数据库值被归一化为对象字典中的标量（数值→long/double、布尔→bool、
    /// DateTime→ISO-8601 字符串、Guid→字符串、二进制→Base64、其余→字符串）。
    /// </summary>
    public static IEnumerable<Dictionary<string, object?>> ReadRows(
        string connectionString, DatabaseProvider provider, string query, CancellationToken cancellationToken = default)
        => DataSources.Database(connectionString, provider, query).Read(new SourceReadOptions(), cancellationToken)
            .Select(record => record.ToObjectDictionary());

    internal static DbConnection CreateConnection(string connectionString, DatabaseProvider provider)
    {
        return provider switch
        {
            DatabaseProvider.Postgres => new NpgsqlConnection(connectionString),
            DatabaseProvider.MySql => new MySqlConnection(connectionString),
            DatabaseProvider.Sqlite => new SqliteConnection(connectionString),
            DatabaseProvider.SqlServer => new SqlConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "未知的数据库类型。")
        };
    }

    private static Dictionary<string, string> ParseConnectionString(string connectionString)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(connectionString))
            return map;

        foreach (string part in connectionString.Split(';'))
        {
            string trimmed = part.Trim();
            if (trimmed.Length == 0)
                continue;
            int eq = trimmed.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = trimmed.Substring(0, eq).Trim();
            string val = trimmed.Substring(eq + 1).Trim();
            if (key.Length == 0)
                continue;
            map[key] = val;
        }
        return map;
    }

    private static string? GetAny(Dictionary<string, string> map, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (map.TryGetValue(key, out string? val))
                return val;
        }
        return null;
    }

    private static bool IsSqliteDataSource(string dataSource)
    {
        string value = dataSource.Trim();
        if (value == ":memory:")
            return true;
        string ext = Path.GetExtension(value);
        if (ext.Length == 0)
            return false;
        foreach (string sqliteExt in SqliteExtensions)
        {
            if (ext.Equals(sqliteExt, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
