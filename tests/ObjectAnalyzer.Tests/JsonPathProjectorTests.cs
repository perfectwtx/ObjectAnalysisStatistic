using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// JSONPath 投影器单元测试：覆盖语法预检、根路径、记录路径、组合用法、空集、标量与数组等场景。
/// 不依赖 AnalysisRunner（避免 AnsiConsole 干扰），可与其它测试并行。
/// </summary>
public class JsonPathProjectorTests
{
    // ──────────────────────────────────────────────
    // 语法预检
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("$")]
    [InlineData("$.data")]
    [InlineData("$.data[*]")]
    [InlineData("$..price")]
    [InlineData("$[?(@.status==\"active\")]")]
    public void TryValidate_AcceptsRfc9535Expressions(string expression)
    {
        // 不应抛错
        JsonPathProjector.TryValidate(expression);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("data")]
    [InlineData("$.[invalid")]
    [InlineData("$??")]
    public void TryValidate_RejectsInvalidExpressions(string expression)
    {
        Assert.Throws<InvalidOperationException>(() => JsonPathProjector.TryValidate(expression));
    }

    [Fact]
    public void TryValidate_ErrorMessage_ContainsOriginalExpression()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JsonPathProjector.TryValidate("$.[bad"));
        Assert.Contains("$.[bad", ex.Message);
    }

    // ──────────────────────────────────────────────
    // ApplyRootPath：作用于文件根
    // ──────────────────────────────────────────────

    [Fact]
    public void ApplyRootPath_ArrayWildcard_FlattensToOneRecordPerElement()
    {
        var root = JsonNode.Parse("""
        {
          "data": [
            { "id": 1, "name": "Alice" },
            { "id": 2, "name": "Bob" },
            { "id": 3, "name": "Carol" }
          ],
          "meta": { "page": 1 }
        }
        """)!;

        var records = JsonPathProjector.ApplyRootPath(root, "$.data[*]");

        Assert.Equal(3, records.Count);
        Assert.Equal("Alice", records[0]["name"]);
        Assert.Equal("Bob", records[1]["name"]);
        Assert.Equal("Carol", records[2]["name"]);
    }

    [Fact]
    public void ApplyRootPath_ObjectMatch_ReturnsSingleRecord()
    {
        var root = JsonNode.Parse("""
        {
          "user": { "id": 42, "email": "alice@example.com" },
          "sessionId": "abc"
        }
        """)!;

        var records = JsonPathProjector.ApplyRootPath(root, "$.user");

        Assert.Single(records);
        Assert.Equal(42L, AsInt64(records[0]["id"]));
        Assert.Equal("alice@example.com", records[0]["email"]);
    }

    [Fact]
    public void ApplyRootPath_RecursiveDescent_PicksUpNestedObjects()
    {
        var root = JsonNode.Parse("""
        {
          "items": [
            { "name": "a", "details": { "price": 10 } },
            { "name": "b", "details": { "price": 20 } }
          ]
        }
        """)!;

        var records = JsonPathProjector.ApplyRootPath(root, "$..details");

        Assert.Equal(2, records.Count);
        Assert.Equal(10L, AsInt64(records[0]["price"]));
        Assert.Equal(20L, AsInt64(records[1]["price"]));
    }

    [Fact]
    public void ApplyRootPath_ScalarMatch_ReturnsEmpty()
    {
        var root = JsonNode.Parse("""{ "count": 42, "label": "hello" }""")!;

        var records = JsonPathProjector.ApplyRootPath(root, "$.count");

        Assert.Empty(records);
    }

    [Fact]
    public void ApplyRootPath_NoMatch_ReturnsEmpty()
    {
        var root = JsonNode.Parse("""{ "a": 1 }""")!;

        var records = JsonPathProjector.ApplyRootPath(root, "$.doesNotExist");

        Assert.Empty(records);
    }

    [Fact]
    public void ApplyRootPath_NullRoot_ReturnsEmpty()
    {
        var records = JsonPathProjector.ApplyRootPath(null, "$.anything");
        Assert.Empty(records);
    }

    [Fact]
    public void ApplyRootPath_FilterSelector_AppliesCondition()
    {
        var root = JsonNode.Parse("""
        [
          { "id": 1, "status": "active" },
          { "id": 2, "status": "inactive" },
          { "id": 3, "status": "active" }
        ]
        """)!;

        var records = JsonPathProjector.ApplyRootPath(root, "$[?(@.status==\"active\")]");

        Assert.Equal(2, records.Count);
        Assert.Equal(1L, AsInt64(records[0]["id"]));
        Assert.Equal(3L, AsInt64(records[1]["id"]));
    }

    // ──────────────────────────────────────────────
    // ApplyRecordPath：作用于每条记录
    // ──────────────────────────────────────────────

    [Fact]
    public void ApplyRecordPath_ObjectField_ReturnsReplacedRecord()
    {
        var record = new Dictionary<string, object?>
        {
            ["event"] = "click",
            ["payload"] = new Dictionary<string, object?>
            {
                ["x"] = 100L,
                ["y"] = 200L
            }
        };

        var records = JsonPathProjector.ApplyRecordPath(record, "$.payload");

        Assert.Single(records);
        Assert.Equal(100L, AsInt64(records[0]["x"]));
        Assert.Equal(200L, AsInt64(records[0]["y"]));
        Assert.False(records[0].ContainsKey("event"));
    }

    [Fact]
    public void ApplyRecordPath_ArrayField_FansOutMultipleRecords()
    {
        var record = new Dictionary<string, object?>
        {
            ["batch"] = "B1",
            ["items"] = new List<object?>
            {
                new Dictionary<string, object?> { ["sku"] = "A", ["qty"] = 2L },
                new Dictionary<string, object?> { ["sku"] = "B", ["qty"] = 5L }
            }
        };

        var records = JsonPathProjector.ApplyRecordPath(record, "$.items[*]");

        Assert.Equal(2, records.Count);
        Assert.Equal("A", records[0]["sku"]);
        Assert.Equal(2L, AsInt64(records[0]["qty"]));
        Assert.Equal("B", records[1]["sku"]);
    }

    [Fact]
    public void ApplyRecordPath_NoMatch_ReturnsEmpty()
    {
        var record = new Dictionary<string, object?>
        {
            ["a"] = 1
        };

        var records = JsonPathProjector.ApplyRecordPath(record, "$.missing");

        Assert.Empty(records);
    }

    [Fact]
    public void ApplyRecordPath_PreservesNumericTypes()
    {
        var record = new Dictionary<string, object?>
        {
            ["payload"] = new Dictionary<string, object?>
            {
                ["i"] = 42,
                ["d"] = 3.14,
                ["b"] = true
            }
        };

        var records = JsonPathProjector.ApplyRecordPath(record, "$.payload");

        Assert.Equal(42L, AsInt64(records[0]["i"]));
        Assert.Equal(3.14, (double)records[0]["d"]!, 5);
        Assert.Equal(true, records[0]["b"]);
    }

    [Fact]
    public void ApplyRecordPath_HandlesJsonElementValues()
    {
        // 模拟 JSONL 解析器产物：value 是 JsonElement 而非 Dictionary
        var record = new Dictionary<string, object?>
        {
            ["event"] = "click",
            ["payload"] = JsonDocument.Parse("""{"x":100,"y":200}""").RootElement
        };

        var records = JsonPathProjector.ApplyRecordPath(record, "$.payload");

        Assert.Single(records);
        Assert.Equal(100L, AsInt64(records[0]["x"]));
        Assert.Equal(200L, AsInt64(records[0]["y"]));
    }

    [Fact]
    public void ApplyRecordPath_HandlesNestedArrays()
    {
        var record = new Dictionary<string, object?>
        {
            ["tags"] = new List<object?> { "a", "b", "c" }
        };

        // 数组里是字符串，ApplyRecordPath 在 JsonArray 分支只对 JsonObject 入记录；这里期望 0 条
        var records = JsonPathProjector.ApplyRecordPath(record, "$.tags[*]");
        Assert.Empty(records);
    }

    /// <summary>
    /// 将任意对象值（long / int / JsonElement / boxed）规范化为 long；用于跨类型的字段值断言。
    /// </summary>
    private static long AsInt64(object? value)
    {
        if (value is null) return 0;
        return value switch
        {
            long l => l,
            int i => i,
            JsonElement el => el.GetInt64(),
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
        };
    }
}
