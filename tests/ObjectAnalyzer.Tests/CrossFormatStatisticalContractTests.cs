using System.Text.Json;
using Microsoft.Data.Sqlite;
using MiniExcelLibs;
using ObjectAnalyzer.Data;

namespace ObjectAnalyzer.Tests;

public sealed class CrossFormatStatisticalContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oa-format-contract-" + Guid.NewGuid().ToString("N"));
    public CrossFormatStatisticalContractTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData("json")]
    [InlineData("jsonl")]
    [InlineData("csv")]
    [InlineData("xml")]
    [InlineData("yaml")]
    [InlineData("excel")]
    [InlineData("sqlite")]
    public void StringFixture_PreservesTypedValuesAndStatistics(string format) => Compare(format, numeric: false);

    [Theory]
    [InlineData("jsonl")]
    [InlineData("csv")]
    [InlineData("excel")]
    [InlineData("sqlite")]
    public void NumericFormats_AgreeWithoutChangingTextOnlyFormatSemantics(string format) => Compare(format, numeric: true);

    private void Compare(string format, bool numeric)
    {
        string stem = numeric ? "numbers" : "strings";
        var readOptions = new SourceReadOptions { CsvInferNumbers = numeric };
        var expected = DataSources.File(Fixture(stem + ".json"), "json").Read(readOptions).ToArray();
        var objects = expected.Select(record => record.ToObjectDictionary()).ToArray();
        IAnalysisDataSource source;
        string path = Path.Combine(_directory, "data." + (format == "excel" ? "xlsx" : format));
        if (format == "excel") { MiniExcel.SaveAs(path, objects); source = DataSources.File(path, "excel"); }
        else if (format == "sqlite")
        {
            string connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open(); using var command = connection.CreateCommand();
                string kind = numeric ? "NUMERIC" : "TEXT";
                command.CommandText = $"CREATE TABLE fixture (ordinal INTEGER, id {kind}, amount {kind}, label TEXT)";
                command.ExecuteNonQuery();
                for (int i = 0; i < objects.Length; i++)
                {
                    command.Parameters.Clear();
                    command.CommandText = "INSERT INTO fixture VALUES ($ordinal, $id, $amount, $label)";
                    command.Parameters.AddWithValue("$ordinal", i);
                    foreach (var pair in objects[i]) command.Parameters.AddWithValue("$" + pair.Key, pair.Value!);
                    command.ExecuteNonQuery();
                }
            }
            source = DataSources.Database(connectionString, DatabaseProvider.Sqlite, "SELECT id, amount, label FROM fixture ORDER BY ordinal");
        }
        else if (!numeric) source = DataSources.File(Fixture("strings." + format), format);
        else
        {
            string data = format == "jsonl" ? string.Join('\n', objects.Select(row => JsonSerializer.Serialize(row)))
                : "id,amount,label\n1,1.25,\"alpha, \"\"quoted\"\"\"\n2,-2.5,中文\n1,1.25,\"alpha, \"\"quoted\"\"\"\n";
            File.WriteAllText(path, data);
            source = DataSources.File(path, format);
        }
        var actual = source.Read(readOptions).ToArray();
        Assert.Equal(expected.Select(record => JsonSerializer.Serialize(record.Fields)), actual.Select(record => JsonSerializer.Serialize(record.Fields)));
        var expectedStats = StatisticalContractTests.Accumulate(objects, 1).Complete();
        var actualStats = StatisticalContractTests.Accumulate(actual.Select(record => record.ToObjectDictionary()), 1).Complete();
        StatisticalContractTests.AssertExact(expectedStats, actualStats);
    }
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Contracts", name);
}
