using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// 字段过滤功能测试：--where 过滤表达式引擎（FieldFilter）与 FieldTransformProcessor 集成。
/// </summary>
public class FieldFilterTests
{
    private static readonly string[] ExpectedFilteredNames = { "A", "C" };

    private static Dictionary<string, object?> Obj(object? age = null, string? name = null, string? status = null)
    {
        var d = new Dictionary<string, object?>();
        if (age != null) d["Age"] = age;
        if (name != null) d["Name"] = name;
        if (status != null) d["Status"] = status;
        return d;
    }

    // ──────────────────────────────────────────────
    // 基础比较运算符
    // ──────────────────────────────────────────────

    [Fact]
    public void Equal_Numeric_ComparesWeakly()
    {
        var filter = new FieldFilter("Age == 20");
        Assert.True(filter.Evaluate(Obj(age: 20)));
        Assert.True(filter.Evaluate(Obj(age: "20"))); // 字符串数字也匹配
        Assert.False(filter.Evaluate(Obj(age: 21)));
    }

    [Fact]
    public void Equal_String_MatchesValue()
    {
        var filter = new FieldFilter("Name == 'Alice'");
        Assert.True(filter.Evaluate(Obj(name: "Alice")));
        Assert.False(filter.Evaluate(Obj(name: "Bob")));
    }

    [Fact]
    public void NotEqual_Works()
    {
        var filter = new FieldFilter("Status != 'active'");
        Assert.True(filter.Evaluate(Obj(status: "disabled")));
        Assert.False(filter.Evaluate(Obj(status: "active")));
    }

    [Fact]
    public void GreaterThan_WithInt()
    {
        var filter = new FieldFilter("Age > 18");
        Assert.True(filter.Evaluate(Obj(age: 19)));
        Assert.False(filter.Evaluate(Obj(age: 18)));
        Assert.False(filter.Evaluate(Obj(age: 10)));
    }

    [Fact]
    public void LessThanEqual_Works()
    {
        var filter = new FieldFilter("Age <= 25");
        Assert.True(filter.Evaluate(Obj(age: 25)));
        Assert.True(filter.Evaluate(Obj(age: 20)));
        Assert.False(filter.Evaluate(Obj(age: 26)));
    }

    [Fact]
    public void GreaterEqual_WithNegativeNumber()
    {
        var filter = new FieldFilter("Age >= -5");
        Assert.True(filter.Evaluate(Obj(age: 0)));
        Assert.True(filter.Evaluate(Obj(age: -5)));
        Assert.False(filter.Evaluate(Obj(age: -6)));
    }

    // ──────────────────────────────────────────────
    // in / not in
    // ──────────────────────────────────────────────

    [Fact]
    public void In_List_MatchesAnyItem()
    {
        var filter = new FieldFilter("Status in ['active', 'pending']");
        Assert.True(filter.Evaluate(Obj(status: "active")));
        Assert.True(filter.Evaluate(Obj(status: "pending")));
        Assert.False(filter.Evaluate(Obj(status: "disabled")));
    }

    [Fact]
    public void In_EmptyList_MatchesNothing()
    {
        var filter = new FieldFilter("Status in []");
        Assert.False(filter.Evaluate(Obj(status: "active")));
    }

    [Fact]
    public void NotIn_Works()
    {
        var filter = new FieldFilter("Status not in ['active']");
        Assert.True(filter.Evaluate(Obj(status: "disabled")));
        Assert.False(filter.Evaluate(Obj(status: "active")));
    }

    // ──────────────────────────────────────────────
    // like / not like
    // ──────────────────────────────────────────────

    [Fact]
    public void Like_WildcardMatchesCaseInsensitive()
    {
        var filter = new FieldFilter("Name like 'zhang%'");
        Assert.True(filter.Evaluate(Obj(name: "ZhangSan")));
        Assert.True(filter.Evaluate(Obj(name: "ZHANGSAN")));
        Assert.False(filter.Evaluate(Obj(name: "LiSi")));
    }

    [Fact]
    public void Like_Underscore_MatchesSingleChar()
    {
        var filter = new FieldFilter("Name like 'A_ice'");
        Assert.True(filter.Evaluate(Obj(name: "Alice")));
        Assert.False(filter.Evaluate(Obj(name: "Allic")));
    }

    [Fact]
    public void NotLike_Works()
    {
        var filter = new FieldFilter("Name not like 'zhang%'");
        Assert.True(filter.Evaluate(Obj(name: "LiSi")));
        Assert.False(filter.Evaluate(Obj(name: "ZhangSan")));
    }

    // ──────────────────────────────────────────────
    // 逻辑组合：AND / OR / 括号 / 优先级
    // ──────────────────────────────────────────────

    [Fact]
    public void And_RequiresAllConditions()
    {
        var filter = new FieldFilter("Age > 18 AND Status == 'active'");
        Assert.True(filter.Evaluate(Obj(age: 30, status: "active")));
        Assert.False(filter.Evaluate(Obj(age: 16, status: "active")));
        Assert.False(filter.Evaluate(Obj(age: 30, status: "disabled")));
    }

    [Fact]
    public void Or_MatchesEither()
    {
        var filter = new FieldFilter("Age > 18 OR Status == 'vip'");
        Assert.True(filter.Evaluate(Obj(age: 30)));
        Assert.True(filter.Evaluate(Obj(status: "vip")));
        Assert.False(filter.Evaluate(Obj(age: 10, status: "normal")));
    }

    [Fact]
    public void Parentheses_ChangePrecedence()
    {
        // AND 优先于 OR：不加括号时解析为 (Age>18 AND Status=='x') OR Name=='n'
        var withoutParen = new FieldFilter("Age > 18 AND Status == 'x' OR Name == 'n'");
        var withParen = new FieldFilter("Age > 18 AND (Status == 'x' OR Name == 'n')");

        var obj = Obj(age: 10, name: "n", status: "y");
        Assert.True(withoutParen.Evaluate(obj));  // FALSE OR TRUE = TRUE
        Assert.False(withParen.Evaluate(obj));    // FALSE AND TRUE = FALSE

        Assert.True(withParen.Evaluate(Obj(age: 30, name: "n"))); // TRUE AND TRUE
    }

    [Fact]
    public void Mixed_Keywords_AreCaseInsensitive()
    {
        var filter = new FieldFilter("Age > 18 and Status IN ['active'] or Name like 'zh%'");
        Assert.True(filter.Evaluate(Obj(age: 30, status: "active")));
        Assert.True(filter.Evaluate(Obj(name: "Zhang")));
        Assert.False(filter.Evaluate(Obj(age: 10, status: "disabled", name: "Li")));
    }

    // ──────────────────────────────────────────────
    // 字段缺失 / null / 布尔
    // ──────────────────────────────────────────────

    [Fact]
    public void MissingField_EqualNull_Matches()
    {
        var filter = new FieldFilter("Age == null");
        Assert.True(filter.Evaluate(Obj(name: "Alice"))); // Age 缺失视为 null
        Assert.False(filter.Evaluate(Obj(age: 20)));
    }

    [Fact]
    public void MissingField_NotEqualsString_MatchesAsNull()
    {
        // 缺失字段视为 null；null != '' 成立（弱类型非严格比较）
        var filter = new FieldFilter("Name != ''");
        Assert.True(filter.Evaluate(Obj(age: 20)));
        Assert.False(filter.Evaluate(Obj(name: "")));
        Assert.True(filter.Evaluate(Obj(name: "Bob")));
    }

    [Fact]
    public void Bool_Operand_ComparesStrictly()
    {
        var filter = new FieldFilter("Flag == true");
        Assert.True(filter.Evaluate(new Dictionary<string, object?> { { "Flag", true } }));
        Assert.False(filter.Evaluate(new Dictionary<string, object?> { { "Flag", false } }));
        Assert.False(filter.Evaluate(new Dictionary<string, object?> { { "Flag", "true" } }));
    }

    // ──────────────────────────────────────────────
    // 非法表达式
    // ──────────────────────────────────────────────

    [Fact]
    public void EmptyExpression_Throws()
    {
        Assert.Throws<ArgumentException>(() => new FieldFilter("   "));
    }

    [Theory]
    [InlineData("Age >")]
    [InlineData("Age == 1 AND")]
    [InlineData("(Age > 18")]              // 缺少右括号
    [InlineData("Age > 18)")]              // 多余右括号
    [InlineData("Name like")]              // like 缺操作数
    [InlineData("Age > > 5")]              // 连续运算符
    public void InvalidExpression_ThrowsFormatException(string expr)
    {
        Assert.Throws<FormatException>(() => new FieldFilter(expr));
    }

    // ──────────────────────────────────────────────
    // FieldTransformProcessor 集成
    // ──────────────────────────────────────────────

    [Fact]
    public void Processor_FilterExpression_FiltersObjects()
    {
        var processor = new FieldTransformProcessor(null, filterExpression: "Age >= 18");

        var wrapped = processor.Wrap(new List<Dictionary<string, object?>>
        {
            Obj(age: 30, name: "A"),
            Obj(age: 15, name: "B"),
            Obj(age: 18, name: "C")
        }).ToList();

        Assert.Equal(2, wrapped.Count);
        Assert.Equal(ExpectedFilteredNames, wrapped.Select(d => d["Name"]));
    }

    [Fact]
    public void Processor_RulesFilter_UsedWhenNoCliFilter()
    {
        var rules = new AnalysisRules
        {
            Runtime = JsonNode.Parse("""{"filter":"Age > 18"}""") as JsonObject
        };
        var processor = new FieldTransformProcessor(rules);

        var wrapped = processor.Wrap(new List<Dictionary<string, object?>>
        {
            Obj(age: 30),
            Obj(age: 10)
        }).ToList();

        Assert.Single(wrapped);
        Assert.Equal(30, wrapped[0]["Age"]);
    }

    [Fact]
    public void Processor_CliFilterOverridesRulesFilter()
    {
        var rules = new AnalysisRules { Runtime = JsonNode.Parse("""{"filter":"Age > 100"}""") as JsonObject };
        var processor = new FieldTransformProcessor(rules, filterExpression: "Age > 18");

        var wrapped = processor.Wrap(new List<Dictionary<string, object?>>
        {
            Obj(age: 30),
            Obj(age: 10)
        }).ToList();

        Assert.Single(wrapped);
        Assert.Equal(30, wrapped[0]["Age"]);
    }

    [Fact]
    public void Processor_EmptyCliFilter_FallsBackToRulesFilter()
    {
        var rules = new AnalysisRules { Runtime = JsonNode.Parse("""{"filter":"Age > 18"}""") as JsonObject };
        var processor = new FieldTransformProcessor(rules, filterExpression: "  ");

        var wrapped = processor.Wrap(new List<Dictionary<string, object?>>
        {
            Obj(age: 30),
            Obj(age: 10)
        }).ToList();

        Assert.Single(wrapped);
        Assert.Equal(30, wrapped[0]["Age"]);
    }

    [Fact]
    public void Processor_InvalidFilterExpression_Throws()
    {
        var rules = new AnalysisRules { Runtime = JsonNode.Parse("""{"filter":"Age >"}""") as JsonObject };
        Assert.Throws<FormatException>(() => new FieldTransformProcessor(rules));
    }

    [Fact]
    public void Processor_FilterAppliedAfterTransform()
    {
        // 原始值为 base64，过滤表达式作用于转换后的明文
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "code", Convert.ToBase64String(Encoding.UTF8.GetBytes("SECRET")) } },
            new() { { "code", Convert.ToBase64String(Encoding.UTF8.GetBytes("public")) } }
        };
        var rules = new AnalysisRules();
        rules.Fields["code"] = new FieldRule { Transform = new FieldTransform { Type = "base64" } };

        var processor = new FieldTransformProcessor(rules, filterExpression: "code like 'SEC%'");
        var wrapped = processor.Wrap(objects).ToList();
        Assert.Single(wrapped); // 仅 SECRET 通过过滤

        var result = Analyze(processor.Wrap(objects), rules);
        processor.ApplyFailures(result);

        var stat = result.FieldStatistics.Single();
        Assert.Equal(1, result.TotalObjects);
        Assert.Contains("SECRET", stat.ValueCounts.Keys);
        Assert.DoesNotContain("public", stat.ValueCounts.Keys);
    }

    [Fact]
    public void Analyze_WithFilter_StatsOnlyMatchingObjects()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            Obj(age: 30, status: "active"),
            Obj(age: 10, status: "disabled"),
            Obj(age: 25, status: "active")
        };

        var processor = new FieldTransformProcessor(null, filterExpression: "Status == 'active'");
        var result = Analyze(processor.Wrap(objects));

        Assert.Equal(2, result.TotalObjects);
        var status = result.FieldStatistics.First(f => f.FieldName == "Status");
        Assert.Equal(2, status.ValueCounts["active"]);
    }

    // ──────────────────────────────────────────────
    // 报告中记录过滤条件
    // ──────────────────────────────────────────────

    [Fact]
    public void FilterExpression_OmitsFromReportJsonWhenNull()
    {
        var result = new AnalysisResult { TotalObjects = 1 };
        string json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("FilterExpression", json);
    }

    [Fact]
    public void FilterExpression_IncludesInReportJsonWhenSet()
    {
        var result = new AnalysisResult { TotalObjects = 1, FilterExpression = "age >= 25 AND status in ['active']" };
        string json = JsonSerializer.Serialize(result);
        Assert.Contains("FilterExpression", json);
    }
}
