using MiniExcelLibs;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// valueParsers.header 表头行语义测试：
/// 0 = 无表头（首行即数据，source 为列字母）；1（默认）= 首行表头；N ≥ 2 = 第 N 行是表头。
/// excel-json 每个 source 独立声明自己的表头行（同一张表的不同列可以有不同的表头行）；
/// 通用 excel/csv 解析是整表概念，取 valueParsers 里首个声明的值。
/// </summary>
public class ValueParserHeaderTests
{
    private static ValueParserConfig Parser(string source, string parseType, int? header) =>
        new() { Source = source, ParseType = parseType, Header = header };

    // 复刻用户样本的结构：A/B 列的表头在第 1 行，C 列的表头 "Age" 在第 3 行，数据从第 4 行开始。
    // printHeader:false + 首行含全部列键 → 生成无表头行的 xlsx，行号可控。
    private static string WriteMixedHeaderExcel()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            // MiniExcel 写入要求每行都含全部列键；缺失的单元格用 null（空单元格）
            new() { ["A"] = "Line", ["B"] = 1, ["C"] = null },                            // 行 1：A/B 的表头行
            new() { ["A"] = "{\"body\":\"a1\"}", ["B"] = 2, ["C"] = null },               // 行 2：A/B 数据
            new() { ["A"] = "{\"body\":\"a2\"}", ["B"] = 3, ["C"] = "Age" },              // 行 3：Age 的表头行
            new() { ["A"] = "{\"body\":\"a3\"}", ["B"] = 4, ["C"] = "{\"Age\":\"18\"}" }, // 行 4：Age 数据开始
            new() { ["A"] = "{\"body\":\"a4\"}", ["B"] = null, ["C"] = "{\"Age\":\"20\"}" }, // 行 5：B 缺失（稀疏）
            new() { ["A"] = null, ["B"] = null, ["C"] = "{\"Age\":\"22\"}" }              // 行 6：仅 C
        };
        string path = Path.ChangeExtension(Path.GetTempFileName(), ".xlsx");
        MiniExcel.SaveAs(path, rows, printHeader: false);
        return path;
    }

    [Fact]
    public void ExcelJson_PerSourceHeaderRows_BindIndependently()
    {
        string path = WriteMixedHeaderExcel();
        try
        {
            var records = FileParsers.ParseByFormat(path, "excel-json", new ParseOptions
            {
                Flatten = true,
                ValueParsers = new[]
                {
                    Parser("Line", "json", 1), // A 列：表头在第 1 行
                    Parser("Age", "auto", 3),  // C 列：表头在第 3 行，数据从第 4 行开始
                    Parser("B", "auto", 0)     // B 列：无表头，首行即数据
                }
            }).ToList();

            Assert.Equal(6, records.Count);

            // 行 1：只有 B 出数（header=0 首行即数据——旧行为会把它当表头整体跳过）
            Assert.Equal(1L, records[0]["B"]);
            Assert.False(records[0].ContainsKey("Line.body"));

            // 行 2：Line 开始出数；行 3 是 Age 的表头行，不产生 Age 数据
            Assert.Equal("a1", records[1]["Line.body"]);
            Assert.Equal(2L, records[1]["B"]);
            Assert.Equal(3L, records[2]["B"]);
            Assert.False(records[2].ContainsKey("Age.Age"));

            // 行 4 起：三列齐出
            Assert.Equal("a3", records[3]["Line.body"]);
            Assert.Equal(4L, records[3]["B"]);
            Assert.Equal("18", records[3]["Age.Age"]);

            // 稀疏行：B 缺失则该 source 不出现在记录里
            Assert.Equal("a4", records[4]["Line.body"]);
            Assert.False(records[4].ContainsKey("B"));
            Assert.Equal("20", records[4]["Age.Age"]);

            // 仅 C 的行
            Assert.Equal("22", records[5]["Age.Age"]);
            Assert.False(records[5].ContainsKey("Line.body"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExcelJson_HeaderTitleNotFound_ThrowsWithRowAndTitle()
    {
        string path = WriteMixedHeaderExcel();
        try
        {
            // 找不到标题显式报错，绝不静默跳过——避免「配置了却不生效」
            var ex = Assert.Throws<InvalidDataException>(() =>
                FileParsers.ParseByFormat(path, "excel-json", new ParseOptions
                {
                    ValueParsers = new[] { Parser("Nope", "auto", 2) }
                }).ToList());

            Assert.Contains("第 2 行", ex.Message);
            Assert.Contains("Nope", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExcelJson_HeaderRowBeyondSheet_Throws()
    {
        string path = WriteMixedHeaderExcel();
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                FileParsers.ParseByFormat(path, "excel-json", new ParseOptions
                {
                    ValueParsers = new[] { Parser("Age", "auto", 100) }
                }).ToList());

            Assert.Contains("第 100 行", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExcelJson_HeaderZeroWithNonLetterSource_ThrowsWithHint()
    {
        string path = WriteMixedHeaderExcel();
        try
        {
            // header=0 时 source 必须是列字母；写了标题文本说明想用的是「表头行号」语义
            var ex = Assert.Throws<InvalidDataException>(() =>
                FileParsers.ParseByFormat(path, "excel-json", new ParseOptions
                {
                    ValueParsers = new[] { Parser("Age", "auto", 0) }
                }).ToList());

            Assert.Contains("header=0", ex.Message);
            Assert.Contains("header 设为表头所在行号", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ──────────────────────────────────────────────
    // 通用 excel/csv：整表级表头行号
    // ──────────────────────────────────────────────

    [Fact]
    public void Csv_SheetHeaderAtRowN_SkipsBeforeAndRenamesKeys()
    {
        string path = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
        File.WriteAllText(path, "junk1,junk2\r\nName,Age\r\nAlice,30\r\nBob,41\r\n");
        try
        {
            var records = FileParsers.ParseByFormat(path, "csv", new ParseOptions
            {
                ValueParsers = new[] { Parser("Age", "auto", 2) } // 第 2 行才是表头
            }).ToList();

            Assert.Equal(2, records.Count);
            Assert.Equal("Alice", records[0]["Name"]);
            Assert.Equal(30L, records[0]["Age"]); // CsvInferNumbers 默认推断数值
            Assert.Equal("Bob", records[1]["Name"]);
            Assert.Equal(41L, records[1]["Age"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExcelJson_OverCsv_PerSourceHeaderAtRowN_Works()
    {
        // 用户实际链路：CSV + valueParsers 规则 → 切换 excel-json 专用解析（只输出声明的 source）。
        // 注意 MiniExcel 的 CSV 读取以首行列数为准，各行列数必须一致，这里用单列
        string path = Path.ChangeExtension(Path.GetTempFileName(), ".csv");
        File.WriteAllText(path, "junk\r\nAge\r\n\"{\"\"Age\"\":\"\"18\"\"}\"\r\n\"{\"\"Age\"\":\"\"20\"\"}\"\r\n");
        try
        {
            var records = FileParsers.ParseByFormat(path, "excel-json", new ParseOptions
            {
                Flatten = true,
                ValueParsers = new[] { Parser("Age", "auto", 2) }
            }).ToList();

            Assert.Equal(2, records.Count);
            Assert.Equal("18", records[0]["Age.Age"]);
            Assert.Equal("20", records[1]["Age.Age"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
