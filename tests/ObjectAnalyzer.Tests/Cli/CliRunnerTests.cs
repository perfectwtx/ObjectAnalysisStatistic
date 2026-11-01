using System.Text.Json;
using ObjectAnalyzer.Cli;

namespace ObjectAnalyzer.Tests.Cli;

public sealed class CliRunnerTests
{
    private static int Run(params string[] args) =>
        CliRunner.Run(CliTestParser.Parse(args), new AnalysisCommandRunner(), CancellationToken.None);

    [Fact]
    public void Run_RejectsOutOfRangeParallelism()
    {
        Assert.Equal(1, Run("--parallel", "0"));
        Assert.Equal(1, Run("--parallel", "-2"));
    }

    [Theory]
    [InlineData("--sample-reservoir", "5", "--sample-step", "5")]
    [InlineData("--sample-reservoir", "1")]
    [InlineData("--sample-step", "1")]
    [InlineData("--sample-reservoir", "-1")]
    [InlineData("--sample-step", "-1")]
    public void Run_RejectsInvalidSampling(params string[] args) =>
        Assert.Equal(1, Run(args));

    [Fact]
    public void Run_RequiresConnectionForQuery() =>
        Assert.Equal(1, Run("--query", "SELECT 1"));

    [Fact]
    public void Run_RejectsUnknownPiiLevel() =>
        Assert.Equal(1, Run("--fail-on-pii-level", "D"));

    [Fact]
    public void Run_RejectsPiiOffWithPiiGate() =>
        Assert.Equal(1, Run("--pii-off", "--fail-on-pii-level", "B"));

    [Fact]
    public void Run_RequiresInput() =>
        Assert.Equal(1, Run("--yes"));

    [Fact]
    public void Run_RejectsRootPathForJsonl()
    {
        Assert.Equal(1, Run("-i", "records.jsonl", "--root-path", "$.data[*]"));
        Assert.Equal(1, Run("-i", "records.jsonl.gz", "--root-path", "$.data[*]"));
    }

    [Fact]
    public void Run_RequiresInputForResume() =>
        Assert.Equal(1, Run("--resume", "checkpoint.json"));

    [Fact]
    public void Run_RejectsCheckpointAndResumeOnSameFile()
    {
        string path = Path.GetFullPath("checkpoint.json");
        Assert.Equal(1, Run("-i", "data.json", "--checkpoint", path, "--resume", path));
    }

    [Fact]
    public void Run_RejectsBaselineAndResumeOnSameFile()
    {
        string path = Path.GetFullPath("snapshot.json");
        Assert.Equal(1, Run("-i", "data.json", "--baseline", path, "--resume", path));
    }

    [Fact]
    public void Run_WritesJsonReport()
    {
        using var directory = new TempDirectory();
        string input = directory.File("data.jsonl");
        File.WriteAllText(input, "{\"id\":1,\"name\":\"a\"}\n{\"id\":2,\"name\":\"b\"}\n");
        string output = directory.File("report.json");

        int exitCode = Run("-i", input, "-o", output, "--yes", "--no-progress");

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output));

        using var report = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(2, report.RootElement.GetProperty("TotalObjects").GetInt32());
    }
}
