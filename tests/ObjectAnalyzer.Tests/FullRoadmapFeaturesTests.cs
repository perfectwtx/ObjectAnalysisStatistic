using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

        var result = Analyze(objects);
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

        var result = Analyze(objects);

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

        var result = Analyze(objects);

        // C# (Roslyn AST 确保带有 { } 大括号)
        string csCode = SchemaExporter.GenerateCSharp(result, "TestEntity");
        Assert.Contains("public record TestEntity", csCode);
        Assert.Contains("{", csCode);
        Assert.Contains("}", csCode);
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

    [Fact]
    public void SchemaExporter_UnflattensNestedDotPathsAndArrayIndices()
    {
        // 模拟 FlattenJson 之后的字段名（address.city / tags[0] / skills.primary）
        var objects = new List<Dictionary<string, object?>>
        {
            new() {
                { "id", 1L }, { "name", "Alice" },
                { "address.city", "Beijing" }, { "address.district", "Haidian" },
                { "tags[0]", "C#" }, { "tags[1]", "Dev" },
                { "skills.primary", "Python" }
            },
            new() {
                { "id", 2L }, { "name", "Bob" },
                { "address.city", "Shanghai" },
                { "tags[0]", "Java" }, { "tags[1]", "Ops" }
            }
        };

        var result = Analyze(objects);

        string csCode = SchemaExporter.GenerateCSharp(result, "GeneratedSchema");
        // 嵌套对象被还原为独立 DTO record
        Assert.Contains("public record AddressDto", csCode);
        Assert.Contains("public string City", csCode);
        Assert.Contains("public string District", csCode);
        Assert.Contains("public AddressDto Address", csCode);
        // 数组索引被还原为强类型数组
        Assert.Contains("public string[] Tags", csCode);
        // 覆盖率不足的对象成为可空类型
        Assert.Contains("public SkillsDto? Skills", csCode);
        // 回归：不应再生成扁平化名称
        Assert.DoesNotContain("Address_city", csCode);
        Assert.DoesNotContain("Tags_0_", csCode);

        string tsCode = SchemaExporter.GenerateTypeScript(result, "GeneratedSchema");
        Assert.Contains("export interface AddressDto", tsCode);
        Assert.Contains("tags: string[];", tsCode);
        Assert.Contains("skills?: SkillsDto;", tsCode);

        string jsonSchema = SchemaExporter.GenerateJsonSchema(result);
        Assert.Contains("\"address\"", jsonSchema);
        Assert.Contains("\"tags\"", jsonSchema);
        Assert.Contains("\"type\": \"array\"", jsonSchema);
    }

    [Fact]
    public void SchemaExporter_ArrayOfObjectsBecomesTypedDtoArray()
    {
        // tags[0].name / tags[0].hex 应还原为 TagsItemDto[] 元素
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "id", 1L }, { "tags[0].name", "red" }, { "tags[0].hex", "#FF0000" } },
            new() { { "id", 2L }, { "tags[0].name", "blue" }, { "tags[0].hex", "#0000FF" } }
        };

        var result = Analyze(objects);

        string csCode = SchemaExporter.GenerateCSharp(result, "GeneratedSchema");
        Assert.Contains("public TagsItemDto[] Tags", csCode);
        Assert.Contains("public record TagsItemDto", csCode);
        Assert.Contains("public string Name", csCode);
        Assert.Contains("public string Hex", csCode);

        string tsCode = SchemaExporter.GenerateTypeScript(result, "GeneratedSchema");
        Assert.Contains("tags: TagsItemDto[];", tsCode);
        Assert.Contains("export interface TagsItemDto", tsCode);
    }

    [Fact]
    public void SchemaExporter_GeneratesSqlDdlWithDialectsConstraintsAndNullability()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() {
                { "id", 1L }, { "name", "Alice" }, { "active", true },
                { "price", 9.99 }, { "status", "active" },
                { "tags[0]", "C#" }, { "profile.age", 30L },
                { "created_at", "2026-08-08T12:00:00Z" }
            },
            new() {
                { "id", 3L }, { "name", "Bob" }, { "active", false },
                { "price", 19.99 }, { "status", "disabled" },
                { "profile.age", 31L }, { "created_at", "2026-08-09T15:30:00Z" }
            }
        };

        var result = Analyze(objects);
        // 通过规则为 status 提供混合类型枚举值（含引号转义场景）
        result.FieldStatistics.First(f => f.FieldName == "status").AppliedRule = new FieldRule
        {
            EnumValues = new List<JsonElement>
            {
                JsonDocument.Parse("\"active\"").RootElement.Clone(),
                JsonDocument.Parse("\"disabled\"").RootElement.Clone(),
                JsonDocument.Parse("\"O'Brien\"").RootElement.Clone()
            }
        };

        // PostgreSQL (双引号标识符、JSONB、DOUBLE PRECISION、BOOLEAN)
        string pg = SchemaExporter.GenerateSql(result, "postgres", "users");
        Assert.Contains("CREATE TABLE \"users\" (", pg);
        Assert.Contains("\"id\" BIGINT NOT NULL", pg);
        Assert.Contains("\"name\" TEXT NOT NULL", pg);
        Assert.Contains("\"active\" BOOLEAN NOT NULL", pg);
        Assert.Contains("\"price\" DOUBLE PRECISION NOT NULL", pg);
        Assert.Contains("\"created_at\" TIMESTAMP NOT NULL", pg);
        Assert.Contains("\"profile\" JSONB", pg);
        Assert.Contains("\"tags\" JSONB", pg);
        Assert.Contains("CHECK (\"status\" IN ('active', 'disabled', 'O''Brien'))", pg);

        // MySQL (反引号标识符、JSON、DOUBLE、TINYINT(1))
        string my = SchemaExporter.GenerateSql(result, "mysql", "users");
        Assert.Contains("`id` BIGINT NOT NULL", my);
        Assert.Contains("`active` TINYINT(1) NOT NULL", my);
        Assert.Contains("`price` DOUBLE NOT NULL", my);
        Assert.Contains("`tags` JSON", my);

        // SQLite (INTEGER / TEXT)
        string sq = SchemaExporter.GenerateSql(result, "sqlite", "users");
        Assert.Contains("\"id\" INTEGER NOT NULL", sq);
        Assert.Contains("\"name\" TEXT NOT NULL", sq);
        Assert.Contains("\"price\" REAL NOT NULL", sq);

        // SQL Server (方括号标识符、BIT、FLOAT、DATETIME2、UNIQUEIDENTIFIER、NVARCHAR(MAX))
        string ss = SchemaExporter.GenerateSql(result, "sqlserver", "users");
        Assert.Contains("CREATE TABLE [users] (", ss);
        Assert.Contains("[id] BIGINT NOT NULL", ss);
        Assert.Contains("[active] BIT NOT NULL", ss);
        Assert.Contains("[price] FLOAT NOT NULL", ss);
        Assert.Contains("[created_at] DATETIME2 NOT NULL", ss);
        Assert.Contains("[tags] NVARCHAR(MAX)", ss);
        Assert.Contains("[name] NVARCHAR(MAX) NOT NULL", ss);
        Assert.Contains("CHECK ([status] IN ('active', 'disabled', 'O''Brien'))", ss);
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
