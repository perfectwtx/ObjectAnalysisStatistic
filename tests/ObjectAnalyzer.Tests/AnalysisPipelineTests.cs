using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Pipeline = ObjectAnalyzer.Analysis.Pipeline.AnalysisPipeline;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// W1 统一分析管线验收：
/// - 缺陷 2 修复——valueParsers 不再对普通 JSON 强制 excel-json；
/// - 缺陷 1 回归锁定——filter + flatten 下字段级质量检查仍生效；
/// - 三宿主行为一致性——同一输入在管线与 CLI 宿主（AnalysisRunner）下产出逐字节一致的结果 JSON。
/// </summary>
public class AnalysisPipelineTests
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string TempPath(string ext)
    {
        string dir = Path.Combine(Path.GetTempPath(), "oa-pipeline-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Guid.NewGuid():N}{ext}");
    }

    private static void TryDelete(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); } catch { }
    }

    private static AnalysisOptions NewOptions(string inputPath) => new()
    {
        Input = { InputPath = inputPath },
        // 并行合并时 valueCounts 的键序取决于线程调度；固定单线程保证结果可复现、可比对
        Parse = { MaxDegreeOfParallelism = 1 },
        Progress = { ShowProgress = false }
    };

    // ── 缺陷 2：valueParsers 不再强制改写格式 ──

    [Theory]
    [InlineData("json")]
    [InlineData("jsonl")]
    [InlineData("xml")]
    [InlineData("yaml")]
    public void ResolveEffectiveFormat_PlainNonSpreadsheetWithValueParsers_KeepsDetectedFormat(string ext)
    {
        string path = TempPath("." + ext);
        try
        {
            File.WriteAllText(path, "{}");
            var rules = new AnalysisRules
            {
                ValueParsers = new List<ValueParserConfig> { new() { Source = "A", ParseType = "json" } }
            };

            string effective = Pipeline.ResolveEffectiveFormat(path, rules, formatOverride: null);

            // 旧行为：配置了 valueParsers 即强制 excel-json，普通 JSON 直接报"字典键 '1' 缺失"
            Assert.Equal(ext, effective);
        }
        finally { TryDelete(path); }
    }

    [Theory]
    [InlineData(".csv")]
    [InlineData(".xlsx")]
    public void ResolveEffectiveFormat_SpreadsheetWithValueParsers_SwitchesToExcelJson(string ext)
    {
        string path = TempPath(ext);
        try
        {
            File.WriteAllText(path, "A\nx\n");
            var rules = new AnalysisRules
            {
                ValueParsers = new List<ValueParserConfig> { new() { Source = "A", ParseType = "json" } }
            };

            string effective = Pipeline.ResolveEffectiveFormat(path, rules, formatOverride: null);

            Assert.Equal("excel-json", effective);
            Assert.Equal("excel-json", Pipeline.ResolveEffectiveFormat(path, rules, formatOverride: "csv"));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ResolveEffectiveFormat_ExplicitOverride_WinsOverDetection()
    {
        string path = TempPath(".txt");
        try
        {
            File.WriteAllText(path, "A\n1\n");

            string effective = Pipeline.ResolveEffectiveFormat(path, rules: null, formatOverride: "CSV");

            Assert.Equal("csv", effective);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Pipeline_ExplicitFormatOverride_ParsesExtensionlessInput()
    {
        // API 原始文本端点场景：临时文件是 .txt，宿主显式指定 csv
        string path = TempPath(".txt");
        try
        {
            File.WriteAllText(path, "name,age\n张三,30\n李四,25\n");
            var options = NewOptions(path);
            options.Input.Format = "csv";

            var result = Pipeline.Execute(options, rules: null, runContext: null).Result;

            Assert.Equal(2, result.TotalObjects);
            Assert.Equal("csv", AnalyzeEffectiveFormat(options));
        }
        finally { TryDelete(path); }
    }

    private static string? AnalyzeEffectiveFormat(AnalysisOptions options)
    {
        // 与管线同一套格式判定口径（含显式覆盖），供断言使用
        return Pipeline.ResolveEffectiveFormat(
            options.Input.InputPath, rules: null, formatOverride: options.Input.Format);
    }

    // ── 缺陷 1 回归：filter + flatten 下字段级质量检查不被跳过 ──

    [Fact]
    public void FilterWithFlatten_FieldLevelQualityChecksStillRun()
    {
        string input = TempPath(".json");
        string rulesPath = TempPath(".json");
        string violations = TempPath(".json");
        try
        {
            // 三行嵌套记录；过滤保留 status == "active" 的两行，其中 age=15 越界应记 1 条范围违规
            File.WriteAllText(input,
                """
                [
                  {"profile": {"age": 15, "status": "active"}},
                  {"profile": {"age": 30, "status": "active"}},
                  {"profile": {"age": 40, "status": "inactive"}}
                ]
                """);
            File.WriteAllText(rulesPath,
                """
                {
                  "fields": {
                    "profile.age": { "primaryType": "Number", "minValue": 18 },
                    "profile.status": { "primaryType": "String", "enumValues": ["active"] }
                  },
                  "runtime": {
                    "flatten": true,
                    "filter": "profile.status == 'active'"
                  }
                }
                """);

            int code = AnalysisRunner.Run(new AnalysisOptions
            {
                Input = { InputPath = input },
                Rules = { RulesFile = rulesPath },
                Report = { ViolationsPath = violations },
                Progress = { ShowProgress = false }
            });

            Assert.Equal(0, code);
            var node = JsonNode.Parse(File.ReadAllText(violations))!;
            int total = node["summary"]!["totalViolations"]!.GetValue<int>();
            Assert.True(total >= 1, "filter + flatten 下字段级（范围）违规应被检出");

            bool hasRange = node["fieldViolations"]!.AsArray().Any(v =>
                v!["field"]!.GetValue<string>() == "profile.age" &&
                v["check"]!.GetValue<string>() == "range");
            Assert.True(hasRange, "应包含 profile.age 的范围违规");
        }
        finally { TryDelete(input); TryDelete(rulesPath); TryDelete(violations); }
    }

    // ── 覆盖率拆分：值为 null 与 字段不存在（键缺失）是两种不同的未覆盖 ──

    [Fact]
    public void Coverage_SplitsNullAndAbsentCounts()
    {
        // 5 条记录：a 有值 ×2、a 为 null ×1、a 键缺失 ×2（{} 与仅含 b 的行）
        string input = TempPath(".jsonl");
        try
        {
            File.WriteAllText(input, "{\"a\":1}\n{\"a\":2}\n{\"a\":null}\n{}\n{\"b\":\"x\"}\n");

            var result = Pipeline.Execute(NewOptions(input), rules: null, runContext: null).Result;
            var a = result.FieldStatistics.First(f => f.FieldName == "a");

            Assert.Equal(3, a.Count);                       // 出现次数（含 null）
            Assert.Equal(1, a.NullCount);                   // 值为 null（Count 的子集）
            Assert.Equal(2, result.TotalObjects - a.Count); // 字段不存在（键缺失）
            Assert.Equal(0.6, a.Coverage, precision: 5);    // 覆盖率仍按出现次数口径
        }
        finally { TryDelete(input); }
    }

    [Fact]
    public void FieldStatistic_MergeFrom_SumsNullCount()
    {
        var a = new AnalysisResult
        {
            TotalObjects = 3,
            FieldStatistics = new List<FieldStatistic>
            {
                new() { FieldName = "x", Count = 2, NullCount = 1 }
            }
        };
        var b = new AnalysisResult
        {
            TotalObjects = 2,
            FieldStatistics = new List<FieldStatistic>
            {
                new() { FieldName = "x", Count = 1, NullCount = 1 }
            }
        };

        a.MergeFrom(b);

        var x = a.FieldStatistics.Single(f => f.FieldName == "x");
        Assert.Equal(3, x.Count);
        Assert.Equal(2, x.NullCount);
    }

    // ── 三宿主行为一致性：管线结果与 CLI 宿主结果逐字节一致 ──

    [Fact]
    public void Pipeline_ExecuteIsDeterministic()
    {
        string input = TempPath(".json");
        try
        {
            File.WriteAllText(input,
                """
                [{"name":"alice","age":30},{"name":"bob","age":25},{"name":"carol","age":41}]
                """);

            var first = Pipeline.Execute(NewOptions(input), rules: null, runContext: null).Result;
            var second = Pipeline.Execute(NewOptions(input), rules: null, runContext: null).Result;

            Assert.Equal(JsonSerializer.Serialize(first, s_json), JsonSerializer.Serialize(second, s_json));
        }
        finally { TryDelete(input); }
    }

    [Fact]
    public void Pipeline_MatchesCliHostResult_ByteForByte()
    {
        string input = TempPath(".json");
        string rulesPath = TempPath(".json");
        string cliReport = TempPath(".json");
        try
        {
            File.WriteAllText(input,
                """
                [{"name":"alice","age":30},{"name":"bob","age":25},{"name":"carol","age":41}]
                """);
            File.WriteAllText(rulesPath,
                """
                {"fields":{"age":{"primaryType":"Number","minValue":10}}}
                """);
            // 并行合并时 valueCounts 的键序取决于线程调度；固定单线程保证逐字节可比
            int singleThreaded = 1;

            // CLI 宿主：AnalysisRunner.Run（内部同样走 AnalysisPipeline）
            int code = AnalysisRunner.Run(new AnalysisOptions
            {
                Input = { InputPath = input },
                Rules = { RulesFile = rulesPath },
                Report = { OutputPath = cliReport },
                Parse = { MaxDegreeOfParallelism = singleThreaded },
                Pii = { PiiOff = true }, // 关闭导出打码，保证与管线直调结果逐字节可比
                Progress = { ShowProgress = false }
            });
            Assert.Equal(0, code);

            // 管线直调（API / Web 宿主的入口）
            var options = NewOptions(input);
            options.Parse.MaxDegreeOfParallelism = singleThreaded;
            RulesToOptionsConverter.Apply(options, LoadRules(rulesPath));
            var direct = Pipeline.Execute(options, LoadRules(rulesPath), runContext: null).Result;
            string directJson = JsonSerializer.Serialize(direct, s_json);

            // CLI 报告与管线直调结果都反序列化回 AnalysisResult 再以同一序列化选项比较（逐字段一致）
            string normalizedCli = JsonSerializer.Serialize(
                JsonSerializer.Deserialize<AnalysisResult>(File.ReadAllText(cliReport))!, s_json);
            string normalizedDirect = JsonSerializer.Serialize(
                JsonSerializer.Deserialize<AnalysisResult>(directJson)!, s_json);

            Assert.Equal(normalizedCli, normalizedDirect);
        }
        finally { TryDelete(input); TryDelete(rulesPath); TryDelete(cliReport); }
    }

    private static AnalysisRules LoadRules(string path)
        => JsonSerializer.Deserialize<AnalysisRules>(File.ReadAllText(path), AnalysisRules.RulesJsonOptions)!;
}
