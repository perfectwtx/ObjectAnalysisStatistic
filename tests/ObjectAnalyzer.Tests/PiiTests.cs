using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// PII 识别 / 打码遮蔽 / 报告脱敏 / 数据安全合规等级报告测试。
/// </summary>
public class PiiTests
{
    private static readonly List<Dictionary<string, object?>> PiiSample = new()
    {
        new()
        {
            { "Phone", "13812341234" },
            { "IdCard", "110101199003077528" },
            { "BankCard", "6222021234567890" },
            { "Name", "张三" },
            { "Email", "alice@example.com" }
        },
        new()
        {
            { "Phone", "13900001111" },
            { "IdCard", "110101199003077529" },
            { "BankCard", "6222021234567891" },
            { "Name", "李四" },
            { "Email", "bob@example.org" }
        }
    };

    // ──────────────────────────────────────────────
    // 语义识别
    // ──────────────────────────────────────────────

    [Fact]
    public void Analyze_DetectsPiiSemanticTypes()
    {
        var result = Analyze(PiiSample);

        Assert.Equal("Phone", Stat(result, "Phone").SemanticType);
        Assert.Equal("IdCard", Stat(result, "IdCard").SemanticType);
        Assert.Equal("BankCard", Stat(result, "BankCard").SemanticType);
        Assert.Equal("Name", Stat(result, "Name").SemanticType);
        Assert.Equal("Email", Stat(result, "Email").SemanticType);
    }

    [Fact]
    public void Analyze_NameSemantic_RequiresNameFieldHint()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "City", "Beijing" }, { "Name", "Alice" } },
            new() { { "City", "Shanghai" }, { "Name", "Bob" } }
        };

        var result = Analyze(objects);

        // "City" 字段携带字母值但不匹配姓名字段提示 → 不应判定为 Name
        Assert.NotEqual("Name", Stat(result, "City").SemanticType);
        Assert.Equal("Name", Stat(result, "Name").SemanticType);
    }

    // ──────────────────────────────────────────────
    // 打码规则
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("13812341234", PiiCategory.Phone, "138****1234")]
    [InlineData("13900001111", PiiCategory.Phone, "139****1111")]
    [InlineData("110101199003077528", PiiCategory.IdCard, "110101********7528")]
    [InlineData("11010119900307752X", PiiCategory.IdCard, "110101********752X")]
    [InlineData("6222021234567890", PiiCategory.BankCard, "6222********7890")]
    [InlineData("张三", PiiCategory.Name, "张*")]
    [InlineData("欧阳娜娜", PiiCategory.Name, "欧**娜")]
    [InlineData("Alice", PiiCategory.Name, "A***e")]
    [InlineData("alice@example.com", PiiCategory.Email, "al***@example.com")]
    [InlineData("bob@example.org", PiiCategory.Email, "bo***@example.org")]
    public void PiiDetector_Mask_ProducesExpectedMaskedValues(string value, PiiCategory category, string expected)
    {
        Assert.Equal(expected, PiiDetector.Mask(value, category));
    }

    [Fact]
    public void PiiDetector_TryMask_NonPiiReturnsNull()
    {
        Assert.Null(PiiDetector.TryMask("abc", null));
        Assert.Null(PiiDetector.TryMask("Beijing", "URL"));
        Assert.Null(PiiDetector.TryMask("2026-08-08T12:00:00Z", "DateTime"));
        Assert.Null(PiiDetector.TryMask("1234", "IPv4"));
    }

    [Fact]
    public void PiiDetector_IsPii_OnlyForKnownCategories()
    {
        Assert.True(PiiDetector.IsPii("Phone"));
        Assert.True(PiiDetector.IsPii("IdCard"));
        Assert.False(PiiDetector.IsPii("UUID"));
        Assert.False(PiiDetector.IsPii("URL"));
        Assert.False(PiiDetector.IsPii(null));
    }

    // ──────────────────────────────────────────────
    // 报告脱敏（redaction）
    // ──────────────────────────────────────────────

    [Fact]
    public void PiiRedactor_MasksValuesWithoutMutatingOriginal()
    {
        var result = Analyze(PiiSample);
        Assert.True(Stat(result, "Phone").ValueCounts.ContainsKey("13812341234"));

        var redacted = PiiRedactor.Redact(result);

        Assert.NotSame(result, redacted);
        // 原始结果未被修改（打码必须生成副本）
        Assert.True(Stat(result, "Phone").ValueCounts.ContainsKey("13812341234"));

        var phone = Stat(redacted, "Phone");
        Assert.Contains("138****1234", phone.ValueCounts.Keys);
        Assert.DoesNotContain("13812341234", phone.ValueCounts.Keys);

        var name = Stat(redacted, "Name");
        Assert.Contains("张*", name.ValueCounts.Keys);
        Assert.DoesNotContain("张三", name.ValueCounts.Keys);
        Assert.DoesNotContain("李四", name.ValueCounts.Keys);
    }

    [Fact]
    public void PiiRedactor_NoPii_ReturnsSameInstance()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Id", 1 }, { "Active", true } }
        };

        var result = Analyze(objects);
        Assert.Same(result, PiiRedactor.Redact(result));
    }

    [Fact]
    public void PiiRedactor_MasksDuplicateObjectJson()
    {
        var objects = new List<Dictionary<string, object?>>();
        for (int i = 0; i < 3; i++)
        {
            objects.Add(new() { { "Phone", "13812341234" }, { "Tag", "dup" } });
        }

        var result = Analyze(objects);
        Assert.Single(result.DuplicateObjects);
        Assert.Contains("13812341234", result.DuplicateObjects[0].ObjectJson);

        var redacted = PiiRedactor.Redact(result);
        Assert.DoesNotContain("13812341234", redacted.DuplicateObjects[0].ObjectJson);
        Assert.Contains("138****1234", redacted.DuplicateObjects[0].ObjectJson);
    }

    [Fact]
    public void ReportExporter_SaveJson_MasksRawPiiValues()
    {
        string output = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            ReportExporter.Save(Analyze(PiiSample), output);
            string json = File.ReadAllText(output);
            Assert.Contains("138****1234", json);
            Assert.DoesNotContain("13812341234", json);
            Assert.DoesNotContain("张三", json);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    // ──────────────────────────────────────────────
    // 数据安全合规等级报告
    // ──────────────────────────────────────────────

    [Fact]
    public void PiiReport_Build_SummarizesPiiFieldsAndLevelB()
    {
        var report = PiiReport.Build(Analyze(PiiSample));

        Assert.Equal(5, report.PiiFieldCount);
        Assert.Equal("B", report.ComplianceLevel);
        Assert.True(report.MaskingApplied);
        Assert.Equal(10, report.PiiValueCount); // 5 字段 × 2 行
        Assert.Equal(3, report.Fields.Count(f => f.HighSeverity)); // 手机号/身份证/银行卡为高危
        Assert.Equal("手机号", report.Fields.First(f => f.Field == "Phone").CategoryLabel);
        Assert.Equal("138****1234", report.Fields.First(f => f.Field == "Phone").MaskedSample);
    }

    [Fact]
    public void PiiReport_Build_CleanData_LevelA()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Id", 1 }, { "Active", true } },
            new() { { "Id", 2 }, { "Active", false } }
        };

        var report = PiiReport.Build(Analyze(objects));

        Assert.Equal("A", report.ComplianceLevel);
        Assert.Equal(0, report.PiiFieldCount);
        Assert.False(report.MaskingApplied);
    }

    [Fact]
    public void PiiExporter_SavesJsonComplianceSnapshot()
    {
        string output = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            PiiExporter.Save(Analyze(PiiSample), output);

            string content = File.ReadAllText(output);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            Assert.Equal("B", root.GetProperty("ComplianceLevel").GetString());
            Assert.True(root.GetProperty("MaskingApplied").GetBoolean());
            Assert.Equal(5, root.GetProperty("PiiFieldCount").GetInt32());
            Assert.Equal(5, root.GetProperty("Fields").GetArrayLength());

            var first = root.GetProperty("Fields")[0];
            Assert.NotNull(first.GetProperty("Field").GetString());
            Assert.NotNull(first.GetProperty("Category").GetString());
            Assert.True(first.TryGetProperty("MaskedSample", out _));
            Assert.DoesNotContain("13812341234", content);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    // ──────────────────────────────────────────────
    // 自定义 PII 规则（rules 声明业务敏感字段 + 打码格式）
    // ──────────────────────────────────────────────

    private static (AnalysisRules rules, List<Dictionary<string, object?>> objects) CustomPiiSample()
    {
        var rules = new AnalysisRules();
        rules.Fields["internalId"] = new FieldRule
        {
            Pii = new PiiRule
            {
                Label = "内部标识",
                HighSeverity = false,
                Mask = new PiiMaskConfig { KeepPrefix = 2, KeepSuffix = 2 }
            }
        };
        rules.Fields["authToken"] = new FieldRule
        {
            Pii = new PiiRule { Mask = new PiiMaskConfig { MaskAll = true } }
        };

        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "internalId", "ID-1001" }, { "authToken", "s3cr3t" } },
            new() { { "internalId", "ID-2002" }, { "authToken", "hunter2" } }
        };
        return (rules, objects);
    }

    [Fact]
    public void PiiRedactor_MasksCustomRuleFields()
    {
        var (rules, objects) = CustomPiiSample();
        var result = Analyze(objects, rules);

        Assert.True(Stat(result, "internalId").ValueCounts.ContainsKey("ID-1001"));
        Assert.True(Stat(result, "authToken").ValueCounts.ContainsKey("s3cr3t"));

        var redacted = PiiRedactor.Redact(result);

        var internalId = Stat(redacted, "internalId");
        Assert.Contains("ID***01", internalId.ValueCounts.Keys);
        Assert.DoesNotContain("ID-1001", internalId.ValueCounts.Keys);
        Assert.DoesNotContain("ID-2002", internalId.ValueCounts.Keys);

        var token = Stat(redacted, "authToken");
        Assert.Contains("******", token.ValueCounts.Keys); // s3cr3t (长度 6)
        Assert.DoesNotContain("s3cr3t", token.ValueCounts.Keys);
        Assert.DoesNotContain("hunter2", token.ValueCounts.Keys);
    }

    [Fact]
    public void PiiDetector_TryGetMaskFormat_CustomRuleWinsOverBuiltin()
    {
        // 即使字段语义恰好匹配内置分类，规则声明的自定义格式也应优先
        var rule = new FieldRule
        {
            Pii = new PiiRule
            {
                Label = "内部标识",
                Mask = new PiiMaskConfig { KeepPrefix = 1, KeepSuffix = 1 }
            }
        };
        var mask = PiiDetector.TryGetMaskFormat("Phone", rule);
        Assert.NotNull(mask);
        Assert.True(mask!.IsCustom);
        Assert.Equal("内部标识", mask.Label);
        Assert.Equal("1*3", mask.Apply("123")); // keep 前缀 1 + 后缀 1 → "1*3"

        // 无规则时回退到内置分类
        var builtin = PiiDetector.TryGetMaskFormat("Phone", null);
        Assert.NotNull(builtin);
        Assert.False(builtin!.IsCustom);
        Assert.Equal("138****1234", builtin.Apply("13812341234"));

        // 非敏感字段（无规则、无语义）返回 null
        Assert.Null(PiiDetector.TryGetMaskFormat(null, null));
        Assert.Null(PiiDetector.TryGetMaskFormat("UUID", new FieldRule()));
    }

    [Fact]
    public void PiiReport_Build_IncludesCustomRuleFields()
    {
        var (rules, objects) = CustomPiiSample();
        var report = PiiReport.Build(Analyze(objects, rules));

        Assert.Equal(2, report.PiiFieldCount);
        Assert.Equal("B", report.ComplianceLevel);
        Assert.True(report.MaskingApplied);

        var internalId = report.Fields.First(f => f.Field == "internalId");
        Assert.Equal("内部标识", internalId.CategoryLabel);
        Assert.False(internalId.HighSeverity);
        Assert.Equal("ID***01", internalId.MaskedSample);

        var token = report.Fields.First(f => f.Field == "authToken");
        Assert.Equal("业务敏感字段", token.CategoryLabel);
        Assert.True(token.HighSeverity);
        Assert.Equal("******", token.MaskedSample);
    }

    [Fact]
    public void ReportExporter_SaveJson_MasksCustomRuleFields()
    {
        var (rules, objects) = CustomPiiSample();
        string output = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            ReportExporter.Save(Analyze(objects, rules), output);
            string json = File.ReadAllText(output);
            Assert.Contains("ID***01", json);
            Assert.DoesNotContain("ID-1001", json);
            Assert.DoesNotContain("s3cr3t", json);
            Assert.DoesNotContain("hunter2", json);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    // ──────────────────────────────────────────────
    // 工具方法
    // ──────────────────────────────────────────────

    private static FieldStatistic Stat(AnalysisResult result, string fieldName)
        => result.FieldStatistics.First(f => f.FieldName == fieldName);
}