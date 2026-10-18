using System.Text.Json;
using ObjectAnalyzer.Analysis.Gates;
using ObjectAnalyzer.Reporting;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// W4 注册表对拍：导出注册表（IResultExporter）与门禁注册表（IQualityGate）的
/// 路由 / 优先级 / 兜底行为。注册新实现不改核心——这是本工作流的验收标准。
/// </summary>
public class RegistryTests : IDisposable
{
    private static readonly int[] s_expectedGateOrder = [3, 4, 5, 6, 7];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oa-registry-tests", Guid.NewGuid().ToString("N"));

    public RegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        RegistryTestHarness.ResetExporters();
        RegistryTestHarness.ResetGates();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── 导出注册表 ──

    [Theory]
    [InlineData("report.html")]
    [InlineData("report.htm")]
    [InlineData("report.md")]
    [InlineData("report.csv")]
    [InlineData("report.xlsx")]
    [InlineData("report.pdf")]
    [InlineData("report.json")]
    [InlineData("report")] // 无扩展名 → JSON 兜底
    public void ReportExporter_RoutesByExtension_CreatesReportFile(string fileName)
    {
        string path = Path.Combine(_dir, fileName);

        ReportExporter.Save(new AnalysisResult(), path);

        Assert.True(File.Exists(path), $"路由 {fileName} 失败：未产出报告文件");
        if (fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return; // 二进制格式，不做前缀断言
        string content = File.ReadAllText(path);
        Assert.True(content.Length > 0, "报告文件不应为空");
    }

    [Fact]
    public void ReportExporter_JsonFallback_ProducesParsableResult()
    {
        string path = Path.Combine(_dir, "no-extension-report");

        ReportExporter.Save(new AnalysisResult { TotalObjects = 7 }, path);

        Assert.True(File.Exists(path));
        Assert.Equal(7, JsonSerializer.Deserialize<AnalysisResult>(File.ReadAllText(path))!.TotalObjects);
    }

    [Fact]
    public void ReportExporter_CustomExporter_TakesPrecedenceOverBuiltins()
    {
        var probe = new ProbeExporter();
        ReportExporter.Register(probe);
        string path = Path.Combine(_dir, "report.csv");

        ReportExporter.Save(new AnalysisResult(), path);

        // probe 的 CanHandle 对所有路径返回 true → 拦截在内置 csv 之前
        Assert.Equal(path, probe.HandledPath);
        Assert.False(File.Exists(path), "探测实现不应真正落盘");
    }

    private sealed class ProbeExporter : IResultExporter
    {
        public string? HandledPath { get; private set; }
        public string Format => "probe";
        public bool CanHandle(string outputPath) => true;
        public void Save(AnalysisResult result, string outputPath) => HandledPath = outputPath;
    }

    // ── 门禁注册表 ──

    [Fact]
    public void QualityGateRegistry_BuiltInGates_InExitCodeOrder()
    {
        var codes = QualityGateRegistry.Gates.Select(g => g.ExitCode).ToList();

        Assert.Equal(s_expectedGateOrder, codes);
        Assert.All(QualityGateRegistry.Gates, g => Assert.False(string.IsNullOrWhiteSpace(g.FailureMessage)));
    }

    [Fact]
    public void QualityGateRegistry_FirstTriggeredGateWins()
    {
        var result = new AnalysisResult
        {
            QualityViolations = new List<FieldViolation> { new() { Check = "enum", Count = 1 } }
        };
        var options = new AnalysisOptions
        {
            Gates = { FailOnViolations = true, FailOnPiiLevel = "A", FailOnOutlierRatio = 0 }
        };

        var (exitCode, message) = QualityGateRegistry.Evaluate(result, options);

        Assert.Equal(3, exitCode);
        Assert.Equal("数据质量门禁未通过", message);
    }

    [Fact]
    public void QualityGateRegistry_AllPass_ReturnsZero()
    {
        var (exitCode, message) = QualityGateRegistry.Evaluate(new AnalysisResult(), new AnalysisOptions());

        Assert.Equal(0, exitCode);
        Assert.Null(message);
    }

    [Fact]
    public void QualityGateRegistry_CustomGate_IsEvaluatedFirst()
    {
        QualityGateRegistry.Register(new AlwaysFailGate());

        var (exitCode, message) = QualityGateRegistry.Evaluate(new AnalysisResult(), new AnalysisOptions());

        Assert.Equal(42, exitCode);
        Assert.Equal("自定义门禁未通过", message);
    }

    private sealed class AlwaysFailGate : IQualityGate
    {
        public string Name => "自定义";
        public int ExitCode => 42;
        public string FailureMessage => "自定义门禁未通过";
        public GateVerdict Check(AnalysisResult result, AnalysisOptions options, ObjectAnalyzer.Abstractions.IAnalysisConsole? console = null) => new(true);
    }
}

/// <summary>
/// 测试辅助：把两个注册表重置回内置状态，避免自定义实现泄漏到其它测试。
/// </summary>
internal static class RegistryTestHarness
{
    public static void ResetExporters()
    {
        var field = typeof(ReportExporter).GetField("s_exporters",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var list = (List<IResultExporter>)field!.GetValue(null)!;
        list.RemoveAll(e => e.Format == "probe");
    }

    public static void ResetGates()
    {
        var field = typeof(QualityGateRegistry).GetField("s_gates",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var list = (List<IQualityGate>)field!.GetValue(null)!;
        list.RemoveAll(g => g.ExitCode == 42);
    }
}
