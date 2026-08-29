using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace ObjectAnalyzer.Parsing;

/// <summary>
/// 支持的数据库类型。
/// </summary>
public enum DatabaseProvider
{
    Postgres,
    MySql,
    Sqlite,
    SqlServer
}
