using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// 产品化测试：数据质量报告 (P1) 与各报告导出器 (P0) 的快照回读断言。
/// </summary>
public class ProductizationTests
{
    private static AnalysisResult BuildCleanResult()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Id", 1L }, { "Name", "Alice" } },
            new() { { "Id", 2L }, { "Name", "Bob" } }
        };
        return Analyze(objects);
    }

    private static AnalysisResult BuildResultWithViolations()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Gender", "Male" } },
            new() { { "Gender", "Unknown" } },
            new() { { "Gender", "Female" } }
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

        return Analyze(objects, rules);
    }

    // ──────────────────────────────────────────────
    // 数据质量报告构建
    // ──────────────────────────────────────────────

    [Fact]
    public void DataQualityReport_CleanData_Passes()
    {
        var report = DataQualityReport.Build(BuildCleanResult());

        Assert.True(report.Passed);
        Assert.Equal(2, report.TotalFields);
        Assert.Equal(0, report.FieldsWithViolations);
        Assert.Equal(0, report.TotalViolationCount);
        Assert.Equal(0, report.ViolationRate);
    }

    [Fact]
    public void DataQualityReport_WithViolations_ReportsCorrectSummary()
    {
        var report = DataQualityReport.Build(BuildResultWithViolations());

        Assert.False(report.Passed);
        Assert.Equal(1, report.FieldsWithViolations);
        Assert.Equal(1, report.TotalViolationCount);
        Assert.Equal(1, report.TotalFields);
        Assert.Equal(1.0, report.ViolationRate);

        var gender = report.Fields.Single(f => f.Field == "Gender");
        Assert.True(gender.HasRule);
        Assert.Single(gender.Violations);
        Assert.Equal("Unknown", gender.Violations[0]);
    }

    // ──────────────────────────────────────────────
    // 数据质量报告 JSON 快照回读
    // ──────────────────────────────────────────────

    [Fact]
    public void QualityExporter_SavesStructuredJsonSnapshot()
    {
        string temp = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            QualityExporter.Save(BuildResultWithViolations(), temp);

            string content = File.ReadAllText(temp);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            Assert.False(root.GetProperty("Passed").GetBoolean());
            Assert.Equal(1, root.GetProperty("FieldsWithViolations").GetInt32());
            Assert.Equal(1, root.GetProperty("TotalViolationCount").GetInt32());
            Assert.Equal(1, root.GetProperty("Fields").GetArrayLength());
            Assert.Equal("Gender", root.GetProperty("Fields")[0].GetProperty("Field").GetString());
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [Fact]
    public void QualityExporter_SavesSnapshotForCleanData()
    {
        string temp = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            QualityExporter.Save(BuildCleanResult(), temp);

            string content = File.ReadAllText(temp);
            using var doc = JsonDocument.Parse(content);
            Assert.True(doc.RootElement.GetProperty("Passed").GetBoolean());
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // ──────────────────────────────────────────────
    // 报告导出器快照回读 (JSON / HTML / MD / CSV)
    // ──────────────────────────────────────────────

    [Fact]
    public void ReportExporter_JsonSnapshot_ContainsCoreSections()
    {
        string temp = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            ReportExporter.Save(BuildResultWithViolations(), temp);

            string content = File.ReadAllText(temp);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            Assert.True(root.GetProperty("TotalObjects").GetInt32() > 0);
            Assert.True(root.GetProperty("FieldStatistics").GetArrayLength() > 0);
            Assert.True(root.TryGetProperty("DuplicateObjects", out _));
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [Fact]
    public void ReportExporter_MarkdownSnapshot_ContainsQualitySectionAndFields()
    {
        string temp = Path.ChangeExtension(Path.GetTempFileName(), ".md");
        try
        {
            ReportExporter.Save(BuildResultWithViolations(), temp);

            string content = File.ReadAllText(temp);
            Assert.Contains("数据质量", content);
            Assert.Contains("Gender", content);
            Assert.Contains("枚举违规", content);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [Fact]
    public void ReportExporter_HtmlSnapshot_ContainsDocumentAndFieldData()
    {
        string temp = Path.ChangeExtension(Path.GetTempFileName(), ".html");
        try
        {
            ReportExporter.Save(BuildResultWithViolations(), temp);

            string content = File.ReadAllText(temp);
            Assert.Contains("<!DOCTYPE html>", content, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Gender", content);
            Assert.Contains("数据质量", content);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [Fact]
    public void ReportExporter_CsvSnapshot_HasHeaderAndDataRows()
    {
        string temp = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
        try
        {
            ReportExporter.Save(BuildCleanResult(), temp);

            string content = File.ReadAllText(temp);
            Assert.Contains("FieldName,Count,Coverage,PrimaryType", content);
            Assert.Contains("Name", content);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
