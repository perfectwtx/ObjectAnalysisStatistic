using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// 针对新增强化（流式解析、分块并行、规则校验、报告导出、并发控制）的测试。
/// </summary>
public class NewFeatureTests
{
    // ──────────────────────────────────────────────
    // 解析器边界情况
    // ──────────────────────────────────────────────

    [Fact]
    public void ParseJsonl_EmptyFile_ReturnsEmpty()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            var result = FileParsers.ParseByFormat(tempFile, "jsonl", new()).ToList();
            Assert.Empty(result);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseJsonl_InvalidLine_ThrowsWithLineNumber()
    {
        string tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "{\"Id\": 1}\n{broken json}\n");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                FileParsers.ParseByFormat(tempFile, "jsonl", new()).ToList());
            Assert.Contains("第 2 行", ex.Message);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseCsv_HeaderOnly_ReturnsEmpty()
    {
        string tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
        File.WriteAllText(tempFile, "Id,Name,Age\r\n");
        try
        {
            var result = FileParsers.ParseByFormat(tempFile, "csv", new()).ToList();
            Assert.Empty(result);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseXml_DuplicateElementKeys_Aggregated()
    {
        string tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "<root><item><Id>1</Id><Id>2</Id><Name>A</Name></item></root>");
        try
        {
            var result = FileParsers.ParseByFormat(tempFile, "xml", new()).ToList();
            Assert.Single(result);
            // 同名重复子元素聚合为列表，避免后者覆盖前者导致数据丢失
            Assert.Equal(new List<object?> { "1", "2" }, result[0]["Id"]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseYaml_NumericLookingScalar_StaysString()
    {
        string tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "- Id: 1\n  Age: 25\n");
        try
        {
            var result = FileParsers.ParseByFormat(tempFile, "yaml", new()).ToList();
            Assert.Equal("25", result[0]["Age"]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseByFormat_UppercaseFormat_IsAccepted()
    {
        string tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "{\"Id\": 1}");
        try
        {
            var result = FileParsers.ParseByFormat(tempFile, "JSON", new()).ToList();
            Assert.Single(result);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseByFormat_UnknownFormat_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FileParsers.ParseByFormat("x.txt", "toml", new()));
        Assert.Contains("不支持的文件格式", ex.Message);
    }

    // ──────────────────────────────────────────────
    // 流式 JSONL + 分块并行
    // ──────────────────────────────────────────────

    [Fact]
    public void Analyze_LazyJsonlStreaming_ProducesCorrectResults()
    {
        string tempFile = Path.GetTempFileName();
        var lines = Enumerable.Range(0, 25000)
            .Select(i => $"{{\"Id\": {i}, \"Even\": {(i % 2 == 0 ? "true" : "false")}}}");
        File.WriteAllText(tempFile, string.Join('\n', lines));
        try
        {
            // 不物化，直接传入惰性流式枚举
            var result = Analyze(FileParsers.ParseByFormat(tempFile, "jsonl", new()));

            Assert.Equal(25000, result.TotalObjects);
            Assert.Equal(25000, result.TotalUniqueObjects);
            Assert.Empty(result.DuplicateObjects);
            Assert.Equal(25000, result.FieldStatistics.First(f => f.FieldName == "Id").Count);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Analyze_MaxDegreeOfParallelismOne_MatchesDefault()
    {
        var objects = Enumerable.Range(0, 5000)
            .Select(i => new Dictionary<string, object?>
            {
                { "Id", (long)(i % 100) },
                { "Value", (long)(i % 10) }
            })
            .ToList();

        var single = Analyze(objects, null, new ParallelOptions { MaxDegreeOfParallelism = 1 });
        var multi = Analyze(objects, null, new ParallelOptions { MaxDegreeOfParallelism = 8 });

        Assert.Equal(single.TotalObjects, multi.TotalObjects);
        Assert.Equal(single.TotalUniqueObjects, multi.TotalUniqueObjects);
        Assert.Equal(single.DuplicateObjects.Count, multi.DuplicateObjects.Count);

        foreach (var f in single.FieldStatistics)
        {
            var other = multi.FieldStatistics.First(x => x.FieldName == f.FieldName);
            Assert.Equal(f.Count, other.Count);
            Assert.Equal(f.ValueCounts, other.ValueCounts);
        }
    }

    // ──────────────────────────────────────────────
    // 规则枚举校验
    // ──────────────────────────────────────────────

    [Fact]
    public void Analyze_EnumRule_FlagsViolations()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Gender", "Male" } },
            new() { { "Gender", "Unknown" } },
            new() { { "Gender", "Female" } },
            new() { { "Gender", 5 } }
        };

        var rules = new AnalysisRules();
        rules.Fields["Gender"] = new FieldRule
        {
            EnumValues = new List<JsonElement>
            {
                JsonSerializer.Deserialize<JsonElement>("\"Male\""),
                JsonSerializer.Deserialize<JsonElement>("\"Female\"")
            }
        };

        var result = Analyze(objects, rules);
        var gender = result.FieldStatistics.First(f => f.FieldName == "Gender");

        Assert.Equal("Male", gender.AppliedRule?.EnumValues?[0].GetString());
        Assert.Contains("Unknown", gender.EnumViolations);
        Assert.Contains("5", gender.EnumViolations);
        Assert.DoesNotContain("Male", gender.EnumViolations);
    }

    [Fact]
    public void Analyze_NumericEnumRule_MatchesNumericStrings()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Status", 0 } },
            new() { { "Status", 1 } },
            new() { { "Status", 99 } }
        };

        var rules = new AnalysisRules();
        rules.Fields["Status"] = new FieldRule
        {
            EnumValues = new List<JsonElement>
            {
                JsonSerializer.Deserialize<JsonElement>("0"),
                JsonSerializer.Deserialize<JsonElement>("1")
            }
        };

        var result = Analyze(objects, rules);
        var status = result.FieldStatistics.First(f => f.FieldName == "Status");
        Assert.Equal(new List<string> { "99" }, status.EnumViolations);
    }

    // ──────────────────────────────────────────────
    // primaryType 从 enumValues 自动推导
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("[0,1,2]", "Number")]
    [InlineData("[true,false]", "Boolean")]
    [InlineData("[\"A\",\"B\"]", "String")]
    [InlineData("[0,1,\"Active\"]", "String")] // 混合类型 → String
    public void Analyze_PrimaryType_AutoDerivedFromEnumValues(string enumJson, string expected)
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "f", "x" } }
        };

        var rules = new AnalysisRules();
        rules.Fields["f"] = new FieldRule
        {
            EnumValues = JsonSerializer.Deserialize<List<JsonElement>>(enumJson)
        };

        var result = Analyze(objects, rules);
        var field = result.FieldStatistics.First(x => x.FieldName == "f");
        Assert.Equal(expected, field.PrimaryType);
    }

    [Fact]
    public void Analyze_PrimaryType_ExplicitOverridesAutoDerived()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "f", "x" } }
        };

        var rules = new AnalysisRules();
        rules.Fields["f"] = new FieldRule
        {
            PrimaryType = "Object",
            EnumValues = new List<JsonElement>
            {
                JsonSerializer.Deserialize<JsonElement>("\"A\"")
            }
        };

        var result = Analyze(objects, rules);
        var field = result.FieldStatistics.First(x => x.FieldName == "f");
        Assert.Equal("Object", field.PrimaryType);
    }

    // ──────────────────────────────────────────────
    // 报告导出 + ValueCountsTruncated 序列化
    // ──────────────────────────────────────────────

    [Fact]
    public void FieldStatistic_NotTruncated_OmitsFlagInJson()
    {
        var stat = new FieldStatistic { FieldName = "A", Count = 1 };
        string json = JsonSerializer.Serialize(stat);
        Assert.DoesNotContain("ValueCountsTruncated", json);
    }

    [Fact]
    public void FieldStatistic_Truncated_IncludesFlagInJson()
    {
        var stat = new FieldStatistic { FieldName = "A", Count = 1, ValueCountsTruncated = true };
        string json = JsonSerializer.Serialize(stat);
        Assert.Contains("ValueCountsTruncated", json);
    }

    [Fact]
    public void ReportExporter_SavesJsonReport()
    {
        string output = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        var result = AnalyzeSample();
        try
        {
            ReportExporter.Save(result, output);
            string json = File.ReadAllText(output);
            Assert.Contains("\"FieldStatistics\"", json);
            Assert.Contains("\"TotalObjects\"", json);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void ReportExporter_SavesMarkdownReport()
    {
        string output = Path.ChangeExtension(Path.GetTempFileName(), ".md");
        var result = AnalyzeSample();
        try
        {
            ReportExporter.Save(result, output);
            string md = File.ReadAllText(output);
            Assert.Contains("字段详情", md);
            Assert.Contains("| 字段名", md);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void ReportExporter_SavesCsvReport()
    {
        string output = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
        var result = AnalyzeSample();
        try
        {
            ReportExporter.Save(result, output);
            string csv = File.ReadAllText(output);
            Assert.StartsWith("FieldName,Count,Coverage", csv);
            Assert.Contains("Id,", csv);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void ReportExporter_SavesHtmlReport()
    {
        string output = Path.ChangeExtension(Path.GetTempFileName(), ".html");
        var result = AnalyzeSample();
        try
        {
            ReportExporter.Save(result, output);
            string html = File.ReadAllText(output);
            Assert.Contains("<!DOCTYPE html>", html);
            Assert.Contains("ObjectAnalyzer", html);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void ViolationExporter_ExportsStructuredViolations()
    {
        string output = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Gender", "Bad" } },
            new() { { "Gender", "Good" } }
        };
        var rules = new AnalysisRules();
        rules.Fields["Gender"] = new FieldRule
        {
            EnumValues = new List<JsonElement> { JsonSerializer.Deserialize<JsonElement>("\"Good\"") }
        };
        var result = Analyze(objects, rules);

        try
        {
            ViolationExporter.Save(result, output);
            string json = File.ReadAllText(output);
            Assert.Contains("\"field\"", json);
            Assert.Contains("Bad", json);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static AnalysisResult AnalyzeSample()
    {
        return Analyze(new List<Dictionary<string, object?>>
        {
            new() { { "Id", 1 }, { "Name", "Alice" }, { "Active", true } },
            new() { { "Id", 2 }, { "Name", "Bob" } }
        });
    }
}
