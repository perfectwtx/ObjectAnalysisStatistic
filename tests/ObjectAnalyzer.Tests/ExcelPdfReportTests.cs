using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MiniExcelLibs;

namespace ObjectAnalyzer.Tests;

public class ExcelPdfReportTests
{
    private static List<Dictionary<string, object?>> Sample()
    {
        return new()
        {
            new() { ["id"] = 1, ["name"] = "Alice", ["score"] = 92.5, ["category"] = "A" },
            new() { ["id"] = 2, ["name"] = "Bob", ["score"] = 78.0, ["category"] = "B" },
            new() { ["id"] = 3, ["name"] = "Charlie", ["score"] = 88.5, ["category"] = "A" },
            new() { ["id"] = 4, ["name"] = "Diana", ["score"] = 65.0, ["category"] = "C" },
        };
    }

    [Fact]
    public void ExcelReport_GeneratesMultiSheetWorkbook()
    {
        var result = Analyze(Sample());
        var temp = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.xlsx");
        try
        {
            ExcelReportExporter.Save(result, temp);

            Assert.True(File.Exists(temp), "Excel 文件应当被创建");
            var info = new FileInfo(temp);
            Assert.True(info.Length > 1000, "Excel 文件大小应当 > 1KB");

            // 用 MiniExcel 读回，验证 sheet 结构
            var sheets = MiniExcel.GetSheetNames(temp).ToList();
            Assert.Contains("摘要", sheets);
            Assert.Contains("字段统计", sheets);
            Assert.Contains("重复对象", sheets);
            Assert.Contains("违规清单", sheets);

            // 读摘要 sheet 验证关键行
            var summaryRows = MiniExcel.Query(temp, sheetName: "摘要").ToList();
            Assert.NotEmpty(summaryRows);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [Fact]
    public void ExcelReport_FieldStatsSheet_HasExpectedColumns()
    {
        var result = Analyze(Sample());
        var temp = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.xlsx");
        try
        {
            ExcelReportExporter.Save(result, temp);
            // 不使用表头模式读取：第 1 行就是字段名行
            var rows = MiniExcel.Query(
                temp,
                sheetName: "字段统计",
                useHeaderRow: false).ToList();
            Assert.True(rows.Count >= 2, "字段统计 sheet 至少应包含表头行 + 1 条数据");

            // 第 1 行（表头）应当包含 字段名 / 样本数 / 覆盖率 等
            var header = rows[0] as IDictionary<string, object>;
            Assert.NotNull(header);
            Assert.True(header!.ContainsKey("A"), "应当存在 A 列");
            // 把每列的值收集成 key 集合，便于检查
            var headerValues = header.Values.Select(v => v?.ToString() ?? "").ToHashSet();
            Assert.Contains("字段名", headerValues);
            Assert.Contains("样本数", headerValues);
            Assert.Contains("覆盖率", headerValues);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [Fact]
    public void PdfReport_GeneratesValidPdfFile()
    {
        var result = Analyze(Sample());
        var temp = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.pdf");
        try
        {
            PdfReportExporter.Save(result, temp);

            Assert.True(File.Exists(temp), "PDF 文件应当被创建");
            var info = new FileInfo(temp);
            Assert.True(info.Length > 1000, "PDF 文件大小应当 > 1KB");

            // 验证文件头是 PDF magic number
            byte[] header = new byte[5];
            using (var fs = File.OpenRead(temp))
            {
                fs.ReadExactly(header, 0, 5);
            }
            Assert.Equal((byte)'%', header[0]);
            Assert.Equal((byte)'P', header[1]);
            Assert.Equal((byte)'D', header[2]);
            Assert.Equal((byte)'F', header[3]);
            Assert.Equal((byte)'-', header[4]);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [Fact]
    public void ReportExporter_RoutesByExtension()
    {
        var result = Analyze(Sample());

        // xlsx 路由
        var xlsxPath = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.xlsx");
        try
        {
            ReportExporter.Save(result, xlsxPath);
            Assert.True(File.Exists(xlsxPath));
        }
        finally { if (File.Exists(xlsxPath)) File.Delete(xlsxPath); }

        // pdf 路由
        var pdfPath = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.pdf");
        try
        {
            ReportExporter.Save(result, pdfPath);
            Assert.True(File.Exists(pdfPath));
        }
        finally { if (File.Exists(pdfPath)) File.Delete(pdfPath); }
    }
}
