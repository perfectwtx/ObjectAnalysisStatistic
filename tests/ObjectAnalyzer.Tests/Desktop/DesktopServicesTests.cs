using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Desktop.Services;

namespace ObjectAnalyzer.Tests.Desktop;

public sealed class DesktopServicesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oa-desktop-" + Guid.NewGuid().ToString("N"));

    public DesktopServicesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private string DatabasePath => Path.Combine(_directory, "object-analyzer.db");

    [Fact]
    public async Task InitializeAsync_CreatesDatabaseAndHoldsExclusiveLease()
    {
        using var first = new DesktopPlatformService(DatabasePath);
        await first.InitializeAsync();

        Assert.True(File.Exists(DatabasePath));
        Assert.True(File.Exists(DatabasePath + ".host.lock"));

        using var competing = new DesktopPlatformService(DatabasePath);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => competing.InitializeAsync());
        Assert.Contains("独占", error.Message);

        first.Dispose();

        using var next = new DesktopPlatformService(DatabasePath);
        await next.InitializeAsync();
    }

    [Fact]
    public void LoadResult_DeserializesReport()
    {
        string path = Path.Combine(_directory, "report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new AnalysisResult { TotalObjects = 2 }));

        AnalysisResult result = LocalAnalysisService.LoadResult(path);

        Assert.Equal(2, result.TotalObjects);
    }

    [Fact]
    public void LoadResult_RejectsEmptyPayload()
    {
        string path = Path.Combine(_directory, "empty.json");
        File.WriteAllText(path, "null");

        Assert.Throws<InvalidDataException>(() => LocalAnalysisService.LoadResult(path));
    }

    [Fact]
    public async Task AnalyzeAsync_WritesJsonAndUserReports()
    {
        string input = Path.Combine(_directory, "sample.jsonl");
        await File.WriteAllTextAsync(input, "{\"id\":1,\"name\":\"a\"}\n{\"id\":2,\"name\":\"b\"}\n");
        string jsonReport = Path.Combine(_directory, "report.json");
        string userReport = Path.Combine(_directory, "report.csv");
        var output = new List<string>();
        var progress = new List<long>();

        using var platform = new DesktopPlatformService(DatabasePath);
        LocalAnalysisResult result = await LocalAnalysisService.AnalyzeAsync(
            input,
            jsonReport,
            userReport,
            rulesPath: null,
            new DesktopAnalysisSettings(),
            output.Add,
            progress.Add,
            platform,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Result);
        Assert.Equal(2, result.Result.TotalObjects);
        Assert.True(File.Exists(jsonReport));
        Assert.True(File.Exists(userReport));
        Assert.Contains(output, line => line.Contains("已导出用户格式报告", StringComparison.Ordinal));
        Assert.NotEmpty(progress);
    }
}
