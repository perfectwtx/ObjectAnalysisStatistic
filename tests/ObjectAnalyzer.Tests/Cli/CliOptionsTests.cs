using System.CommandLine;
using ObjectAnalyzer.Cli;

namespace ObjectAnalyzer.Tests.Cli;

public sealed class CliOptionsTests
{
    [Fact]
    public void Read_AppliesDocumentedDefaults()
    {
        CliArguments cli = CliTestParser.Parse();

        Assert.Null(cli.Input);
        Assert.Null(cli.Output);
        Assert.Null(cli.Rules);
        Assert.False(cli.NoFlatten);
        Assert.False(cli.AllowUnknownRules);
        Assert.False(cli.NoCsvNumberInference);
        Assert.Equal(5, cli.MaxValues);
        Assert.Equal(1000, cli.MaxTrackedValues);
        Assert.Equal(-1, cli.MaxParallel);
        Assert.Equal(0, cli.SampleReservoir);
        Assert.Equal(0, cli.SampleStep);
        Assert.Null(cli.FailOnPiiLevel);
        Assert.False(cli.PiiOff);
        Assert.True(cli.AllowBreakingSchemaChanges);
        Assert.Equal("anonymous", cli.Operator);
        Assert.Equal("soft", cli.MaxMemoryMode);
        Assert.Equal(0, cli.MaxMemoryMB);
        Assert.False(cli.NoProgress);
        Assert.False(cli.PreflightOnly);
        Assert.False(cli.Yes);
    }

    [Fact]
    public void Read_MapsAliasesAndExplicitValues()
    {
        using var directory = new TempDirectory();
        string input = directory.File("input.jsonl");
        string output = directory.File("report.html");
        string rules = directory.File("rules.json");
        string violations = directory.File("violations.json");
        string quality = directory.File("quality.json");
        string piiReport = directory.File("pii.json");

        CliArguments cli = CliTestParser.Parse(
            "-i", input,
            "-o", output,
            "-r", rules,
            "-F", "name,age",
            "-w", "age > 18",
            "-p", "3",
            "-m", "7",
            "--max-tracked-values", "50",
            "-v", violations,
            "-q", quality,
            "-s", "csharp:out.cs",
            "--sample-step", "10",
            "--connection", "Host=localhost;Database=test",
            "--query", "SELECT * FROM users",
            "--provider", "postgres",
            "--pii-report", piiReport,
            "--fail-on-pii-level", "b",
            "--fail-on-outlier-ratio", "0.25",
            "--export-schema-snapshot", "snapshot.json",
            "--diff-schema", "baseline.json",
            "--schema-diff-report", "diff.json",
            "--allow-breaking-schema-changes", "false",
            "--root-path", "$.data[*]",
            "--record-path", "$.payload",
            "--audit-log", "audit.ndjson",
            "--operator", "alice",
            "--checkpoint", "checkpoint.json",
            "--resume", "checkpoint.json",
            "--baseline", "batch.json",
            "--max-memory", "512",
            "--max-memory-mode", "HARD",
            "--no-flatten",
            "--no-csv-number-inference",
            "--allow-unknown-rules",
            "--fail-on-violations",
            "--no-progress",
            "--preflight-only",
            "-y");

        Assert.Equal(input, cli.Input!.FullName);
        Assert.Equal(output, cli.Output!.FullName);
        Assert.Equal(rules, cli.Rules!.FullName);
        Assert.Equal(violations, cli.Violations!.FullName);
        Assert.Equal(quality, cli.Quality!.FullName);
        Assert.Equal(piiReport, cli.PiiReport!.FullName);
        Assert.Equal("name,age", cli.Fields);
        Assert.Equal("age > 18", cli.Where);
        Assert.Equal(3, cli.MaxParallel);
        Assert.Equal(7, cli.MaxValues);
        Assert.Equal(50, cli.MaxTrackedValues);
        Assert.Equal("csharp:out.cs", cli.ExportSchema);
        Assert.Equal(10, cli.SampleStep);
        Assert.Equal("Host=localhost;Database=test", cli.Connection);
        Assert.Equal("SELECT * FROM users", cli.Query);
        Assert.Equal("postgres", cli.Provider);
        Assert.Equal("b", cli.FailOnPiiLevel);
        Assert.Equal(0.25, cli.FailOnOutlierRatio);
        Assert.Equal("snapshot.json", cli.SchemaSnapshot);
        Assert.Equal("baseline.json", cli.DiffSchema);
        Assert.Equal("diff.json", cli.SchemaDiffReport);
        Assert.False(cli.AllowBreakingSchemaChanges);
        Assert.Equal("$.data[*]", cli.RootPath);
        Assert.Equal("$.payload", cli.RecordPath);
        Assert.Equal("audit.ndjson", cli.AuditLog);
        Assert.Equal("alice", cli.Operator);
        Assert.Equal("checkpoint.json", cli.Checkpoint);
        Assert.Equal("checkpoint.json", cli.Resume);
        Assert.Equal("batch.json", cli.Baseline);
        Assert.Equal(512, cli.MaxMemoryMB);
        Assert.Equal("HARD", cli.MaxMemoryMode);
        Assert.True(cli.NoFlatten);
        Assert.True(cli.NoCsvNumberInference);
        Assert.True(cli.AllowUnknownRules);
        Assert.True(cli.FailOnViolations);
        Assert.True(cli.NoProgress);
        Assert.True(cli.PreflightOnly);
        Assert.True(cli.Yes);
    }

    [Fact]
    public void Parse_UnknownOption_IsReported()
    {
        var options = new CliOptions();
        var root = new RootCommand("test");
        options.AddTo(root);

        ParseResult result = root.Parse(["--not-a-real-option"]);

        Assert.NotEmpty(result.Errors);
    }
}

internal sealed class TempDirectory : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "oa-cli-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(_path);

    public string File(string name) => Path.Combine(_path, name);

    public void Dispose()
    {
        try { Directory.Delete(_path, recursive: true); }
        catch (IOException) { }
    }
}
