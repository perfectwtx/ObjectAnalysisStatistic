namespace ObjectAnalyzer.Tests;

/// <summary>
/// JSONPath 投影集成测试：使用 AnalysisRunner 端到端验证 --root-path / --record-path。
/// 这些测试使用 [Collection(AnalysisRunnerSequentialGroup.Name)] 强制串行执行，避免 AnsiConsole 争用。
/// </summary>
[Collection(AnalysisRunnerSequentialGroup.Name)]
public class JsonPathIntegrationTests
{
    [Fact]
    public void RootPath_ExtractsArrayFromWrappedJson()
    {
        // {data: [...], meta: {...}} → 只分析 data 数组
        string input = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        string output = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            File.WriteAllText(input, """
            {
              "meta": { "page": 1, "total": 2 },
              "data": [
                { "id": 1, "label": "alpha" },
                { "id": 2, "label": "beta" }
              ]
            }
            """);

            var options = new AnalysisOptions
            {
                Input = { InputPath = input, RootPath = "$.data[*]" },
                Report = { OutputPath = output },
                Pii = { PiiOff = true }   // 关闭 PII 打码以断言原始值
            };
            Assert.Equal(0, AnalysisRunner.Run(options));

            // 验证输出报告包含 data 字段，且不含 meta.* 顶层字段
            string json = File.ReadAllText(output);
            Assert.Contains("alpha", json);
            Assert.Contains("beta", json);
            // 关键断言：meta.page 不应出现在 data 数组元素内
            Assert.DoesNotContain("\"page\"", json);
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void RecordPath_OnJsonl_ExtractsPayloadSubObject()
    {
        // 每行 {event, payload} → 只分析 payload
        string input = Path.ChangeExtension(Path.GetTempFileName(), ".jsonl");
        string output = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            File.WriteAllText(input, """
            {"event":"click","payload":{"x":100,"y":200}}
            {"event":"hover","payload":{"x":50,"y":75}}
            {"event":"click","payload":{"x":300,"y":400}}
            """);

            var options = new AnalysisOptions
            {
                Input = { InputPath = input, RecordPath = "$.payload" },
                Report = { OutputPath = output },
                Pii = { PiiOff = true }
            };
            Assert.Equal(0, AnalysisRunner.Run(options));

            string json = File.ReadAllText(output);
            Assert.Contains("\"x\"", json);
            Assert.Contains("\"y\"", json);
            // 验证 event 字段被剔除（payload 替换为顶层记录）
            Assert.DoesNotContain("\"event\"", json);
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void RootPath_NoMatch_ReturnsWarningButContinues()
    {
        // 路径匹配空集时应继续分析（0 条记录），返回 0
        string input = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            File.WriteAllText(input, """{ "x": 1, "y": 2 }""");

            var options = new AnalysisOptions
            {
                Input = { InputPath = input, RootPath = "$.doesNotExist[*]" }
            };
            // 空集时仅警告，继续分析 → 退出码 0
            Assert.Equal(0, AnalysisRunner.Run(options));
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
        }
    }

    [Fact]
    public void RecordPath_InvalidSyntax_FailsFast()
    {
        string input = Path.ChangeExtension(Path.GetTempFileName(), ".jsonl");
        try
        {
            File.WriteAllText(input, """{"a":1}""");

            var options = new AnalysisOptions
            {
                Input = { InputPath = input, RecordPath = "$.[invalid" }
            };
            // 语法无效应硬错误退出码 1
            Assert.Equal(1, AnalysisRunner.Run(options));
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
        }
    }

    [Fact]
    public void RootPath_OnJsonl_IsIgnoredWithWarning()
    {
        // JSONL 是流式结构，没有文件根概念，AnalysisRunner 仅警告并忽略 --root-path
        // （CLI 层 Program.cs 会硬错误以提示用户，但库层更宽松）
        string input = Path.ChangeExtension(Path.GetTempFileName(), ".jsonl");
        try
        {
            File.WriteAllText(input, """{"a":1,"b":2}""");

            var options = new AnalysisOptions
            {
                Input = { InputPath = input, RootPath = "$.data" }
            };
            Assert.Equal(0, AnalysisRunner.Run(options));
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
        }
    }

    [Fact]
    public void RootPathAndRecordPath_Combined_AppliesBothLevels()
    {
        // 文件根 JSONPath 提取数组 + 每条记录再 JSONPath 提取子对象
        string input = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        string output = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            File.WriteAllText(input, """
            {
              "envelope": "ignored",
              "events": [
                { "kind": "click", "details": { "x": 10, "y": 20 } },
                { "kind": "hover", "details": { "x": 5,  "y": 8 } }
              ]
            }
            """);

            var options = new AnalysisOptions
            {
                Input = { InputPath = input, RootPath = "$.events[*]", RecordPath = "$.details" },
                Report = { OutputPath = output },
                Pii = { PiiOff = true }
            };
            Assert.Equal(0, AnalysisRunner.Run(options));

            string json = File.ReadAllText(output);
            Assert.Contains("\"x\"", json);
            Assert.Contains("\"y\"", json);
            // kind/details 不应出现（被 details 替换）
            Assert.DoesNotContain("\"kind\"", json);
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
    }
}
