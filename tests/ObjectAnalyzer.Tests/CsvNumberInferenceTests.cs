using ObjectAnalyzer.Parsing;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// CSV 数值推断。
/// <para>CSV 无类型信息，MiniExcel 取出的值一律是 string，而类型判定只看 CLR 类型，
/// 于是 CSV 的数值列 primaryType 永远是 String，均值/标准差/分位数/相关性一整套数值剖析失效。
/// 这里补上推断，让 CSV 追平 JSON。</para>
/// <para>推断刻意保守：误转会让报告里的值失真，漏转只是维持现状，两者代价不对等，
/// 所以凡有歧义（前导零、超长数字、科学计数法、千分位、带单位）一律保持字符串。</para>
/// </summary>
public class CsvNumberInferenceTests
{
    /// <summary>把单列 CSV 解析成值序列（走公开的 ParseByFormat，覆盖真实解析路径）。</summary>
    private static List<object?> ParseColumn(string csv, bool infer = true)
    {
        string path = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
        File.WriteAllText(path, csv);
        try
        {
            return FileParsers.ParseByFormat(path, "csv", new ParseOptions { CsvInferNumbers = infer })
                .Select(r => r.GetValueOrDefault("v"))
                .ToList();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static object? Single(string value, bool infer = true)
    {
        var parsed = ParseColumn($"v\r\n{value}\r\n", infer);
        Assert.Single(parsed);
        return parsed[0];
    }

    [Fact]
    public void Integer_BecomesLong()
    {
        Assert.Equal(30L, Single("30"));
        Assert.Equal(0L, Single("0"));
    }

    [Fact]
    public void Decimal_BecomesDouble()
    {
        Assert.Equal(3.14d, Single("3.14"));
    }

    [Fact]
    public void Negative_IsAccepted()
    {
        Assert.Equal(-7L, Single("-7"));
        Assert.Equal(-2.5d, Single("-2.5"));
    }

    [Fact]
    public void LeadingZero_StaysString()
    {
        // 007 / 0123 是编码（工号、邮编）不是数字，转成 7 会丢掉前导零
        Assert.Equal("007", Single("007"));
        Assert.Equal("0123", Single("0123"));
    }

    [Fact]
    public void LongDigitString_StaysString()
    {
        // 18 位 ID：double 只有 ~15-17 位有效数字，转过去必然丢精度
        Assert.Equal("123456789012345678", Single("123456789012345678"));
    }

    [Fact]
    public void ScientificNotation_StaysString()
    {
        // 1E5 既可能是 100000，也可能是物料编码——无法判断就别动
        Assert.Equal("1E5", Single("1E5"));
    }

    [Fact]
    public void ThousandsSeparator_And_Unit_StayString()
    {
        // 含逗号 / 尾随小数点的值必须用引号包起来，否则 MiniExcel 会按逗号拆成多列
        Assert.Equal("1,234", Single("\"1,234\""));
        Assert.Equal("42kg", Single("42kg"));
        Assert.Equal(" 42", Single("\" 42\""));   // 前后空白不猜
        Assert.Equal("12.", Single("\"12.\""));   // 尾随小数点不是常规写法
    }

    [Fact]
    public void Empty_And_Text_Unchanged()
    {
        // 空值要用引号包成 ""，否则 MiniExcel 跳过整行拿不到任何记录
        Assert.Equal("", Single("\"\""));
        Assert.Equal("Alice", Single("Alice"));
    }

    [Fact]
    public void MixedColumn_TypedPerValue()
    {
        // 同一列里数字归数字、文本归文本，TypeCounts 会同时记到 Number 与 String
        var values = ParseColumn("v\r\n30\r\nunknown\r\n25\r\n");
        Assert.Equal(new object?[] { 30L, "unknown", 25L }, values);
    }

    [Fact]
    public void Disabled_By_Option()
    {
        // 关掉开关后回到纯字符串行为
        Assert.Equal("30", Single("30", infer: false));
        Assert.Equal("3.14", Single("3.14", infer: false));
    }

    [Fact]
    public void NumericCsvColumn_AnalyzesAsNumber()
    {
        // 真正的目的：CSV 数值列能拿到数值剖析（极值/均值/分位数），而不是只有字符串指标。
        // 推断前这里 primaryType 是 "String"，MinNumber/MaxNumber/AvgNumber 全为 null。
        string path = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
        File.WriteAllText(path, "age,score\r\n30,88.5\r\n45,72.25\r\n28,95\r\n");
        try
        {
            var data = FileParsers.ParseByFormat(path, "csv", new ParseOptions()).ToList();
            var result = ObjectAnalyzer.Analysis.ObjectAnalyzer.Analyze(data);

            var age = result.FieldStatistics.Single(f => f.FieldName == "age");
            Assert.Equal("Number", age.PrimaryType);
            Assert.Equal(28d, age.MinNumber!.Value);
            Assert.Equal(45d, age.MaxNumber!.Value);
            Assert.NotNull(age.AvgNumber);

            // 小数列同样是 Number
            var score = result.FieldStatistics.Single(f => f.FieldName == "score");
            Assert.Equal("Number", score.PrimaryType);
            Assert.Equal(72.25d, score.MinNumber!.Value);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Inference_EnablesCorrelation()
    {
        // 相关性的前置条件是「至少两个 primaryType == Number 且 count >= 3 的字段」，
        // 推断开启前 CSV 永远满足不了，矩阵恒为 null
        string path = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
        File.WriteAllText(path, "age,score\r\n30,88\r\n45,72\r\n28,95\r\n60,60\r\n35,80\r\n");
        try
        {
            var data = FileParsers.ParseByFormat(path, "csv", new ParseOptions()).ToList();
            var result = ObjectAnalyzer.Analysis.ObjectAnalyzer.Analyze(
                data, features: new ObjectAnalyzer.Analysis.AnalysisFeatures { EnableCorrelation = true });

            Assert.NotNull(result.CorrelationMatrix);
            Assert.Equal(2, result.CorrelationMatrix!.Fields.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
