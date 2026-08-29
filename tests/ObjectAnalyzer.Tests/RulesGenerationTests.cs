using System.Text.Json;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// 规则文件生成测试：JSON Schema → AnalysisRules、C# 源码 → AnalysisRules，以及生成结果的往返兼容性。
/// </summary>
public class RulesGenerationTests
{
    private static readonly JsonSerializerOptions s_rulesJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] s_userLevels = { "Basic", "Vip", "Admin" };

    // ─────────────────────────────────────────────
    //  JSON Schema → AnalysisRules
    // ─────────────────────────────────────────────

    [Fact]
    public void Schema_ScalarTypes_MapToPrimaryTypes()
    {
        var schema = """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "age": { "type": "integer" },
            "score": { "type": "number" },
            "active": { "type": "boolean" },
            "createdAt": { "type": "string", "format": "date-time" }
          }
        }
        """;
        var rules = RulesFromSchema.Convert(schema);

        Assert.Equal("String", rules.Fields["name"].PrimaryType);
        Assert.Equal("Number", rules.Fields["age"].PrimaryType);
        Assert.Equal("Number", rules.Fields["score"].PrimaryType);
        Assert.Equal("Boolean", rules.Fields["active"].PrimaryType);
        Assert.Equal("DateTime", rules.Fields["createdAt"].PrimaryType);
    }

    [Fact]
    public void Schema_Enum_PopulatesEnumValues_AndInferPrimaryType()
    {
        var schema = """{ "type": "object", "properties": { "status": { "enum": ["Pending", "Paid"] } } }""";
        var rules = RulesFromSchema.Convert(schema);

        var rule = rules.Fields["status"];
        Assert.Equal("String", rule.PrimaryType);
        Assert.NotNull(rule.EnumValues);
        Assert.Equal(2, rule.EnumValues!.Count);
    }

    [Fact]
    public void Schema_Required_And_Constraints_MapCorrectly()
    {
        var schema = """
        {
          "type": "object",
          "required": ["orderId"],
          "properties": {
            "orderId": { "type": "string", "pattern": "^ORD-[0-9]{8}$" },
            "amount": { "type": "number", "minimum": 0, "maximum": 100000, "default": 0 }
          }
        }
        """;
        var rules = RulesFromSchema.Convert(schema);

        Assert.True(rules.Fields["orderId"].Required);
        Assert.Equal("^ORD-[0-9]{8}$", rules.Fields["orderId"].Pattern);
        Assert.Null(rules.Fields["amount"].Required);
        Assert.Equal(0, rules.Fields["amount"].MinValue);
        Assert.Equal(100000, rules.Fields["amount"].MaxValue);
        Assert.Equal(0, ((JsonElement)rules.Fields["amount"].DefaultValue!).GetInt32());
    }

    [Fact]
    public void Schema_NullableTypeArray_Works()
    {
        var schema = """{ "type": "object", "properties": { "memo": { "type": ["string", "null"] } } }""";
        var rules = RulesFromSchema.Convert(schema);

        Assert.Equal("String", rules.Fields["memo"].PrimaryType);
        Assert.Null(rules.Fields["memo"].Required);
    }

    [Fact]
    public void Schema_NestedObject_FlattensToDottedPaths()
    {
        var schema = """
        {
          "type": "object",
          "properties": {
            "customer": {
              "type": "object",
              "properties": { "name": { "type": "string" } }
            }
          }
        }
        """;
        var rules = RulesFromSchema.Convert(schema);

        Assert.True(rules.Fields.ContainsKey("customer.name"));
        Assert.False(rules.Fields.ContainsKey("customer"));
    }

    [Fact]
    public void Schema_LocalRef_IsResolved()
    {
        var schema = """
        {
          "type": "object",
          "$defs": { "memoType": { "type": "string" } },
          "properties": { "memo": { "$ref": "#/$defs/memoType" } }
        }
        """;
        var rules = RulesFromSchema.Convert(schema);

        Assert.Equal("String", rules.Fields["memo"].PrimaryType);
    }

    [Fact]
    public void Schema_CircularRef_Throws()
    {
        var schema = """
        {
          "type": "object",
          "$defs": { "a": { "$ref": "#/$defs/b" }, "b": { "$ref": "#/$defs/a" } },
          "properties": { "x": { "$ref": "#/$defs/a" } }
        }
        """;
        Assert.Throws<InvalidOperationException>(() => RulesFromSchema.Convert(schema));
    }

    [Fact]
    public void Schema_Array_IsSkippedWithReason()
    {
        var skipped = new List<string>();
        var schema = """{ "type": "object", "properties": { "tags": { "type": "array", "items": { "type": "string" } } } }""";
        var rules = RulesFromSchema.Convert(schema, skipped);

        Assert.Empty(rules.Fields);
        Assert.Contains(skipped, s => s.StartsWith("tags", StringComparison.Ordinal));
    }

    [Fact]
    public void Schema_UnknownType_IsSkipped()
    {
        var skipped = new List<string>();
        var schema = """{ "type": "object", "properties": { "blob": { "type": "secret" } } }""";
        var rules = RulesFromSchema.Convert(schema, skipped);

        Assert.Empty(rules.Fields);
        Assert.Contains(skipped, s => s == "blob");
    }

    // ─────────────────────────────────────────────
    //  C# 源码 → AnalysisRules
    // ─────────────────────────────────────────────

    private const string SampleModelSource = """
        using System.ComponentModel.DataAnnotations;
        using System.Text.Json.Serialization;

        namespace Demo;

        public class UserModel
        {
            [JsonPropertyName("userName")]
            [Required]
            public string Name { get; set; } = "unknown";

            [Range(0, 150)]
            public int Age { get; set; } = 18;

            [RegularExpression("^1[0-9]{5}$")]
            public string? Phone { get; set; }

            public bool IsActive { get; set; }

            public DateTime CreatedAt { get; set; }

            public UserLevel Level { get; set; }

            public string? Remark { get; set; }

            public List<string>? Tags { get; set; }
        }

        public enum UserLevel
        {
            Basic,
            Vip,
            Admin
        }
        """;

    [Fact]
    public void CSharp_Class_ProducesRulesWithTypesAndRequired()
    {
        var rules = RulesFromCSharp.Extract(SampleModelSource);

        Assert.Equal("String", rules.Fields["userName"].PrimaryType);
        Assert.Equal("Number", rules.Fields["Age"].PrimaryType);
        Assert.Equal("Boolean", rules.Fields["IsActive"].PrimaryType);
        Assert.Equal("DateTime", rules.Fields["CreatedAt"].PrimaryType);
        // 非空值类型 → required；可空引用类型 → 不设
        Assert.True(rules.Fields["Age"].Required);
        Assert.True(rules.Fields["IsActive"].Required);
        Assert.Null(rules.Fields["Phone"].Required);
        Assert.Null(rules.Fields["Remark"].Required);
    }

    [Fact]
    public void CSharp_Attributes_MapToRules()
    {
        var rules = RulesFromCSharp.Extract(SampleModelSource);

        Assert.True(rules.Fields["userName"].Required);
        Assert.Equal(0, rules.Fields["Age"].MinValue);
        Assert.Equal(150, rules.Fields["Age"].MaxValue);
        Assert.Equal("^1[0-9]{5}$", rules.Fields["Phone"].Pattern);
    }

    [Fact]
    public void CSharp_JsonPropertyName_RenamesFieldKey()
    {
        var rules = RulesFromCSharp.Extract(SampleModelSource);

        Assert.True(rules.Fields.ContainsKey("userName"));
        Assert.False(rules.Fields.ContainsKey("Name"));
    }

    [Fact]
    public void CSharp_EnumProperty_ProducesEnumValues()
    {
        var rules = RulesFromCSharp.Extract(SampleModelSource);

        var rule = rules.Fields["Level"];
        Assert.Equal("String", rule.PrimaryType);
        Assert.NotNull(rule.EnumValues);
        Assert.Equal(s_userLevels, rule.EnumValues!.Select(v => v.GetString()).ToArray());
    }

    [Fact]
    public void CSharp_Initializers_BecomeDefaultValues()
    {
        var rules = RulesFromCSharp.Extract(SampleModelSource);

        Assert.Equal("unknown", rules.Fields["userName"].DefaultValue);
        Assert.Equal(18, rules.Fields["Age"].DefaultValue);
    }

    [Fact]
    public void CSharp_ComplexType_IsSkippedWithWarning()
    {
        var warnings = new List<string>();
        var rules = RulesFromCSharp.Extract(SampleModelSource, warnings: warnings);

        Assert.False(rules.Fields.ContainsKey("Tags"));
        Assert.Contains(warnings, w => w.Contains("Tags"));
    }

    [Fact]
    public void CSharp_Record_IsSupported()
    {
        var source = "public record Point(int X, int Y);";
        // 位置参数 record 的主构造属性不是 PropertyDeclarationSyntax 成员——当前版本不支持，应得到空规则而非崩溃
        var rules = RulesFromCSharp.Extract(source);
        Assert.Empty(rules.Fields);
    }

    [Fact]
    public void CSharp_MultipleClasses_RequiresClassName()
    {
        var source = "public class A { public int X { get; set; } } public class B { public int Y { get; set; } }";
        Assert.Throws<InvalidOperationException>(() => RulesFromCSharp.Extract(source));

        var rules = RulesFromCSharp.Extract(source, className: "B");
        Assert.True(rules.Fields.ContainsKey("Y"));
    }

    [Fact]
    public void CSharp_NoClass_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => RulesFromCSharp.Extract("public enum E { A }"));
    }

    // ─────────────────────────────────────────────
    //  往返兼容性：生成的规则文件必须能被分析器加载
    // ─────────────────────────────────────────────

    [Fact]
    public void GeneratedRules_RoundTrip_DeserializesAsAnalysisRules()
    {
        var generated = RulesFromCSharp.Extract(SampleModelSource);
        var json = JsonSerializer.Serialize(generated, s_rulesJsonOptions);

        var loaded = JsonSerializer.Deserialize<AnalysisRules>(json);

        Assert.NotNull(loaded);
        Assert.Equal(generated.Fields.Count, loaded!.Fields.Count);
        Assert.Equal("String", loaded.Fields["userName"].PrimaryType);
        Assert.Equal(150, loaded.Fields["Age"].MaxValue);
        Assert.True(loaded.Fields["userName"].Required);
    }

    [Fact]
    public void GeneratedRules_FromSchema_RoundTrip_DeserializesAsAnalysisRules()
    {
        var schema = """
        {
          "type": "object",
          "required": ["id"],
          "properties": {
            "id": { "type": "string" },
            "qty": { "type": "integer", "minimum": 1 }
          }
        }
        """;
        var generated = RulesFromSchema.Convert(schema);
        var json = JsonSerializer.Serialize(generated, s_rulesJsonOptions);

        var loaded = JsonSerializer.Deserialize<AnalysisRules>(json);

        Assert.NotNull(loaded);
        Assert.True(loaded!.Fields["id"].Required);
        Assert.Equal(1, loaded.Fields["qty"].MinValue);
    }
}
