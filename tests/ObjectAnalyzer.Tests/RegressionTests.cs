namespace ObjectAnalyzer.Tests;

public class RegressionTests
{
    [Fact]
    public void Analyze_HashNoAmbiguity_ObjectsWithSeparatorInValueAreNotDuplicates()
    {
        // 回归测试：规范化哈希曾因分隔符 "="、"&" 出现在值中而产生碰撞，
        // 导致 {"a":"b&c=d"} 与 {"a":"b","c":"d"} 被误判为重复。
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "a", "b&c=d" } },
            new() { { "a", "b" }, { "c", "d" } }
        };

        var result = Analyze(objects);

        Assert.Equal(2, result.TotalObjects);
        Assert.Equal(2, result.TotalUniqueObjects);
        Assert.Empty(result.DuplicateObjects);
    }

    [Fact]
    public void Analyze_HashNoAmbiguity_FieldNameWithEqualsSignIsNotDuplicated()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "a=b", "c" } },
            new() { { "a", "b" } }
        };

        var result = Analyze(objects);

        Assert.Equal(2, result.TotalUniqueObjects);
        Assert.Empty(result.DuplicateObjects);
    }

    [Fact]
    public void Analyze_StringNullLiteral_IsNotTreatedAsNullDefault()
    {
        // 回归测试：值为字符串 "null" 不应被推断为 null 默认值
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Name", "null" } },
            new() { { "Name", "Alice" } }
        };

        var result = Analyze(objects);

        var stat = result.FieldStatistics.First(f => f.FieldName == "Name");
        Assert.DoesNotContain("Null", stat.TypeCounts.Keys);
        Assert.False(stat.NullAppeared);
        Assert.NotEqual("null", stat.DefaultValueRepresentation);
    }

    [Fact]
    public void Analyze_TrulyNullValue_StillInfersNullDefault()
    {
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Name", (string?)null } },
            new() { { "Name", "Alice" } }
        };

        var result = Analyze(objects);

        var stat = result.FieldStatistics.First(f => f.FieldName == "Name");
        Assert.True(stat.NullAppeared);
        Assert.Equal("null", stat.DefaultValueRepresentation);
    }

    [Fact]
    public void Analyze_UnserializableValue_DoesNotAbortAnalysis()
    {
        // 回归测试：并行循环内的序列化曾对不可序列化值直接抛异常导致整个分析中断
        var badValue = Array.Empty<System.Reflection.MemberInfo>();
        var objects = new List<Dictionary<string, object?>>
        {
            new() { { "Id", 1 }, { "Data", badValue } },
            new() { { "Id", 1 }, { "Data", badValue } }
        };

        var result = Analyze(objects);

        Assert.Equal(2, result.TotalObjects);
        Assert.Single(result.DuplicateObjects);
    }

    [Fact]
    public void CsvReport_PreventsFormulaInjection()
    {
        // 回归测试：以 = + - @ 开头的单元格前加单引号，避免 Excel 公式注入
        var field = new FieldStatistic
        {
            FieldName = "=HYPERLINK(\"http://evil\")",
            Count = 1,
            Coverage = 1.0,
            PrimaryType = "String",
            TypeCounts = { ["String"] = 1 },
            ValueCounts = { ["x"] = 1 }
        };
        var result = new AnalysisResult
        {
            TotalObjects = 1,
            TotalUniqueObjects = 1,
            FieldStatistics = { field }
        };

        string outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csv");
        try
        {
            CsvReportExporter.Save(result, outputPath);
            string csv = File.ReadAllText(outputPath);
            Assert.Contains("'=HYPERLINK", csv);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void JsonParser_InvalidJson_ThrowsFriendlyError()
    {
        // 回归测试：非法 JSON 应抛出 InvalidDataException 而非裸 JsonException
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not valid json");

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                FileParsers.ParseByFormat(path, "json", new ParseOptions()).ToList());
            Assert.Contains("JSON", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Analyze_TotalDuplicateGroups_CountsAllGroups()
    {
        // 回归测试：TotalDuplicateGroups 统计所有重复组，不受 DuplicateObjects 存储上限影响
        var objects = new List<Dictionary<string, object?>>();
        for (int i = 0; i < 3; i++) objects.Add(new() { { "A", 1 }, { "B", 2 } }); // 组1 x3
        for (int i = 0; i < 2; i++) objects.Add(new() { { "A", 5 }, { "B", 6 } }); // 组2 x2
        objects.Add(new() { { "A", 9 }, { "B", 10 } });                            // 唯一

        var result = Analyze(objects);

        Assert.Equal(6, result.TotalObjects);
        Assert.Equal(3, result.TotalUniqueObjects);
        Assert.Equal(2, result.TotalDuplicateGroups);
        Assert.Equal(2, result.DuplicateObjects.Count);
    }

    [Fact]
    public void Analyze_OverflowDoesNotConsumeValueSlot()
    {
        // 回归测试：[Other values] 哨兵键不应占用 1000 个唯一值追踪名额
        const int distinct = 1001;
        var objects = new List<Dictionary<string, object?>>(distinct + 1);
        for (int i = 0; i < distinct; i++)
        {
            objects.Add(new() { { "F", i.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
        }
        // 让第 1001 个值出现两次，保证超出上限时能稳定触发裁剪
        objects.Add(new() { { "F", "overflow" } });

        var result = Analyze(objects);
        var stat = result.FieldStatistics.First(f => f.FieldName == "F");

        Assert.True(stat.ValueCountsTruncated);
        Assert.Equal(2, stat.OtherValueCount);
        // 仅保留真实值，溢出计数不占用值名额。
        Assert.Equal(1000, stat.ValueCounts.Count);
    }
}
