using System.Collections.Generic;
using System.IO;
using System.Linq;
using ObjectAnalyzer.Web.Components.Pages;

namespace ObjectAnalyzer.Web.Tests;

/// <summary>
/// 数据预览单元格格式化的回归测试：嵌套对象/数组应以 JSON 展示，而不是类型名。
/// </summary>
public class PreviewFormattingTests
{
    private static List<Dictionary<string, object?>> ParseNested(string content)
    {
        string temp = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        File.WriteAllText(temp, content);
        try
        {
            // Flatten=false：保留嵌套结构，与用户关闭"展平嵌套"时的路径一致
            return global::ObjectAnalyzer.FileParsers.ParseByFormat(temp, "json", new global::ObjectAnalyzer.ParseOptions { Flatten = false }).ToList();
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [Fact]
    public void NestedObject_FormatsAsJson()
    {
        var rows = ParseNested("[{\"profile\":{\"name\":\"张三\",\"age\":30}},{\"profile\":{\"name\":\"李四\",\"age\":25}}]");
        var row = rows[0];

        string rendered = Home.FormatPreviewValue(row["profile"]);

        Assert.StartsWith("{", rendered);
        Assert.Contains("张三", rendered);
        Assert.DoesNotContain("Dictionary", rendered);
        Assert.DoesNotContain("System.Collections", rendered);
    }

    [Fact]
    public void NestedList_FormatsAsJson()
    {
        var rows = ParseNested("[{\"tags\":[\"a\",\"b\",\"c\"]},{\"tags\":[\"x\",\"y\"]}]");
        var row = rows[0];

        string rendered = Home.FormatPreviewValue(row["tags"]);

        Assert.StartsWith("[", rendered);
        Assert.Contains("a", rendered);
        Assert.DoesNotContain("List", rendered);
        Assert.DoesNotContain("System.Collections", rendered);
    }

    [Fact]
    public void ListOfObjects_FormatsAsJson()
    {
        var rows = ParseNested("[{\"items\":[{\"id\":1},{\"id\":2}]},{\"items\":[{\"id\":3}]}]");
        var row = rows[0];

        string rendered = Home.FormatPreviewValue(row["items"]);

        Assert.StartsWith("[", rendered);
        Assert.DoesNotContain("List", rendered);
        Assert.DoesNotContain("System.Collections", rendered);
    }

    [Fact]
    public void Scalars_KeepReadableFormat()
    {
        Assert.Equal("null", Home.FormatPreviewValue(null));
        Assert.Equal("123", Home.FormatPreviewValue(123L));
        Assert.Equal("True", Home.FormatPreviewValue(true));
        Assert.Equal("hello", Home.FormatPreviewValue("hello"));

        // 长字符串/长 JSON 截断到 80 字符
        Assert.Equal(80, Home.FormatPreviewValue(new string('x', 90)).Length);
        var bigList = new List<object?>(Enumerable.Range(0, 40).Select(i => (object)i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(80, Home.FormatPreviewValue(bigList).Length);
    }

    [Fact]
    public void MissingField_RendersEmptyCell()
    {
        var row = new Dictionary<string, object?> { { "name", "张三" } };

        // 字段未出现过 → 空
        Assert.Equal("", Home.FormatPreviewCell(row, "age"));

        // 字段出现过 → 正常格式化
        Assert.Equal("张三", Home.FormatPreviewCell(row, "name"));

        // 字段出现过但值为 null → 显示 "null"
        var explicitNull = new Dictionary<string, object?> { { "age", null } };
        Assert.Equal("null", Home.FormatPreviewCell(explicitNull, "age"));
    }
}
