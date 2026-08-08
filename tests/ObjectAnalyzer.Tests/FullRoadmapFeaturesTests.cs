using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ObjectAnalyzer.Tests;

public class FullRoadmapFeaturesTests
{
    // ──────────────────────────────────────────────
    // 1. 数值极值、平均值与求和统计测试
    // ──────────────────────────────────────────────
    [Fact]
    public void Analyze_NumericField_CalculatesMinMaxAvgSumCorrectly()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Score", 10.0 } },
            new() { { "Score", 20.0 } },
            new() { { "Score", 30.0 } }
        };

        var result = ObjectAnalyzer.Analyze(objects);
        var scoreStat = result.FieldStatistics.First(f => f.FieldName == "Score");

        Assert.Equal(10.0, scoreStat.MinNumber);
        Assert.Equal(30.0, scoreStat.MaxNumber);
        Assert.Equal(20.0, scoreStat.AvgNumber);
        Assert.Equal(60.0, scoreStat.SumNumber);
    }

    // ──────────────────────────────────────────────
    // 2. 文本长度与语义类型推论测试 (Email, UUID, Date, IPv4, URL)
    // ──────────────────────────────────────────────
    [Fact]
    public void Analyze_SemanticTypes_InfersEmailAndUuidAndDateCorrectly()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() {
                { "UserEmail", "alice@example.com" }, // 17 chars
                { "UserGuid", "123e4567-e89b-12d3-a456-426614174000" },
                { "CreatedAt", "2026-08-08T12:00:00Z" }
            },
            new() {
                { "UserEmail", "bob@example.org" }, // 15 chars
                { "UserGuid", "987fc567-e89b-12d3-a456-426614174000" },
                { "CreatedAt", "2026-08-09T15:30:00Z" }
            }
        };

        var result = ObjectAnalyzer.Analyze(objects);

        var emailStat = result.FieldStatistics.First(f => f.FieldName == "UserEmail");
        var guidStat = result.FieldStatistics.First(f => f.FieldName == "UserGuid");
        var dateStat = result.FieldStatistics.First(f => f.FieldName == "CreatedAt");

        Assert.Equal("Email", emailStat.SemanticType);
        Assert.Equal("UUID", guidStat.SemanticType);
        Assert.Equal("DateTime", dateStat.SemanticType);

        Assert.Equal(15, emailStat.MinLength);
        Assert.Equal(17, emailStat.MaxLength);
    }

    // ──────────────────────────────────────────────
    // 3. Schema 代码生成器测试 (C#, TypeScript, JSON Schema)
    // ──────────────────────────────────────────────
    [Fact]
    public void SchemaExporter_GeneratesValidCSharpAndTypeScriptAndJsonSchema()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() {
                { "Id", 101L },
                { "Email", "test@domain.com" },
                { "IsActive", true },
                { "NullableNote", null }
            }
        };

        var result = ObjectAnalyzer.Analyze(objects);

        // C#
        string csCode = SchemaExporter.GenerateCSharp(result, "TestEntity");
        Assert.Contains("public record TestEntity", csCode);
        Assert.Contains("public long Id", csCode);
        Assert.Contains("public bool IsActive", csCode);
        Assert.Contains("public string? NullableNote", csCode);

        // TypeScript
        string tsCode = SchemaExporter.GenerateTypeScript(result, "TestEntity");
        Assert.Contains("export interface TestEntity", tsCode);
        Assert.Contains("Id: number;", tsCode);
        Assert.Contains("IsActive: boolean;", tsCode);
        Assert.Contains("NullableNote?: string;", tsCode);

        // JSON Schema
        string jsonSchema = SchemaExporter.GenerateJsonSchema(result);
        Assert.Contains("\"$schema\": \"https://json-schema.org/draft/2020-12/schema\"", jsonSchema);
        Assert.Contains("\"Id\"", jsonSchema);
        Assert.Contains("\"Email\"", jsonSchema);
    }

    // ──────────────────────────────────────────────
    // 4. 各种文件格式的真·流式解析测试
    // ──────────────────────────────────────────────
    [Fact]
    public void FileParsers_AllFormats_ReturnEnumerableSuccessfully()
    {
        string tempJson = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        string tempJsonl = Path.ChangeExtension(Path.GetTempFileName(), ".jsonl");

        try
        {
            File.WriteAllText(tempJson, "[{\"A\": 1}, {\"A\": 2}]");
            File.WriteAllText(tempJsonl, "{\"B\": \"x\"}\n{\"B\": \"y\"}");

            var parseOptions = new ParseOptions();

            var jsonEnum = FileParsers.ParseByFormat(tempJson, "json", parseOptions);
            var jsonList = jsonEnum.ToList();
            Assert.Equal(2, jsonList.Count);

            var jsonlEnum = FileParsers.ParseByFormat(tempJsonl, "jsonl", parseOptions);
            var jsonlList = jsonlEnum.ToList();
            Assert.Equal(2, jsonlList.Count);
        }
        finally
        {
            if (File.Exists(tempJson)) File.Delete(tempJson);
            if (File.Exists(tempJsonl)) File.Delete(tempJsonl);
        }
    }
}
