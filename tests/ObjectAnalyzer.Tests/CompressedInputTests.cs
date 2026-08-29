using System.IO.Compression;
using ICSharpCode.SharpZipLib.BZip2;

namespace ObjectAnalyzer.Tests;

/// <summary>
/// 验证压缩文件直读：gzip / bzip2 / zip 包裹的 json / jsonl / csv 能被透明解压并正确解析。
/// 每个用例在临时目录生成压缩包，再用 FileParsers 入口解析，断言行数/字段正确。
/// </summary>
[Collection(AnalysisRunnerSequentialGroup.Name)]
public class CompressedInputTests
{
    // ── 压缩包构造辅助 ──

    private static string WriteGZip(string innerName, string content)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(innerName, ".gz"));
        using var fs = File.Create(path);
        using (var gz = new GZipStream(fs, CompressionMode.Compress, leaveOpen: true))
        using (var w = new StreamWriter(gz))
        {
            w.Write(content);
        }
        return path;
    }

    private static string WriteBZip2(string innerName, string content)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(innerName, ".bz2"));
        using var fs = File.Create(path);
        using (var bz = new BZip2OutputStream(fs))
        using (var w = new StreamWriter(bz))
        {
            w.Write(content);
        }
        return path;
    }

    private static string WriteZip(string fileName, string content, params (string Name, string Content)[] others)
    {
        // fileName 形如 "data.json.zip"；zip 内部条目名取去掉 .zip 后缀的主名，
        // 以便 DetectFormat / PickZipEntry 能据扩展名正确推断格式。
        string path = Path.Combine(Path.GetTempPath(), fileName);
        if (File.Exists(path)) File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddZipEntry(zip, fileName.Replace(".zip", ""), content);
            foreach (var (name, c) in others)
                AddZipEntry(zip, name, c);
        }
        return path;
    }

    private static void AddZipEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var w = new StreamWriter(entry.Open());
        w.Write(content);
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var p in paths)
            if (File.Exists(p)) File.Delete(p);
    }

    // ── gzip ──

    [Fact]
    public void GZip_Jsonl_StreamsThrough()
    {
        string path = WriteGZip("data.jsonl.gz",
            "{\"Id\":1,\"Name\":\"Alice\"}\n{\"Id\":2,\"Name\":\"Bob\"}\n");
        try
        {
            Assert.Equal("jsonl", FileParsers.DetectFormat(path));
            var rows = FileParsers.ParseByFormat(path, "jsonl", new() { Flatten = true }).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(1L, rows[0]["Id"]);
            Assert.Equal("Bob", rows[1]["Name"]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void GZip_Json_ArrayParses()
    {
        string path = WriteGZip("data.json.gz",
            "[{\"Id\":1,\"Name\":\"Alice\"},{\"Id\":2,\"Name\":\"Bob\"}]");
        try
        {
            Assert.Equal("json", FileParsers.DetectFormat(path));
            var rows = FileParsers.ParseByFormat(path, "json", new() { Flatten = true }).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal("Alice", rows[0]["Name"]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── bzip2 ──

    [Fact]
    public void BZip2_Json_Parses()
    {
        string path = WriteBZip2("data.json.bz2",
            "[{\"Id\":1,\"Name\":\"Alice\"},{\"Id\":2,\"Name\":\"Bob\"}]");
        try
        {
            Assert.Equal("json", FileParsers.DetectFormat(path));
            var rows = FileParsers.ParseByFormat(path, "json", new() { Flatten = true }).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal("Bob", rows[1]["Name"]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── zip ──

    [Fact]
    public void Zip_SingleJsonEntry_Parses()
    {
        string path = WriteZip("data.json.zip", "[{\"Id\":1,\"Name\":\"Alice\"}]");
        try
        {
            Assert.Equal("json", FileParsers.DetectFormat(path));
            var rows = FileParsers.ParseByFormat(path, "json", new() { Flatten = true }).ToList();
            Assert.Single(rows);
            Assert.Equal("Alice", rows[0]["Name"]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Zip_CsvEntry_Parses()
    {
        string path = WriteZip("data.csv.zip", "Name,Age\r\nAlice,30\r\nBob,25\r\n");
        try
        {
            // 项目既有约定：.csv 与 .xlsx 共用解析器，DetectFormat 统一返回 "excel"
            Assert.Equal("excel", FileParsers.DetectFormat(path));
            var rows = FileParsers.ParseByFormat(path, "csv", new() { Flatten = true }).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal("Bob", rows[1]["Name"]);
            Assert.Equal("25", rows[1]["Age"]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Zip_MultipleDataEntries_PicksFirstDeterministically()
    {
        // 两个数据条目：a.csv 与 b.json，确定性选择字典序首个（a.csv）
        string path = WriteZip("multi.zip", "Name\r\nAlice\r\n",
            ("a.csv", "Name,Age\r\nAlice,30\r\n"),
            ("b.json", "[{\"Id\":1}]"));
        try
        {
            // 选中 a.csv；既有约定 .csv → "excel"（共用解析器）
            Assert.Equal("excel", FileParsers.DetectFormat(path));
            var rows = FileParsers.ParseByFormat(path, "csv", new() { Flatten = true }).ToList();
            Assert.Single(rows);
            Assert.Equal("Alice", rows[0]["Name"]);

            // 通过 InputSource 直接验证条目选择与 OtherEntries
            using var source = InputSource.Open(path);
            Assert.Equal(CompressionType.Zip, source.Compression);
            Assert.Equal("excel", source.Format);
            Assert.Contains("b.json", source.OtherEntries);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Zip_OnlyDirectory_ThrowsClearError()
    {
        string path = Path.Combine(Path.GetTempPath(), "empty.zip");
        if (File.Exists(path)) File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("folder/"); // 仅目录条目
        }
        try
        {
            Assert.Throws<InvalidDataException>(() => InputSource.Open(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 探测逻辑 ──

    [Fact]
    public void CompressionDetector_StripsOuterExtension()
    {
        Assert.Equal(CompressionType.GZip, CompressionDetector.Detect("x.json.gz"));
        Assert.Equal(CompressionType.BZip2, CompressionDetector.Detect("x.json.bz2"));
        Assert.Equal(CompressionType.Zip, CompressionDetector.Detect("x.json.zip"));
        Assert.Equal(CompressionType.None, CompressionDetector.Detect("x.json"));

        Assert.Equal("x.json", CompressionDetector.StripCompression("x.json.gz"));
        // Path.ChangeExtension(path, null) 仅剥离最后一个扩展名
        Assert.Equal("data.csv", CompressionDetector.StripCompression("data.csv.gz"));

        // 内层扩展名决定格式（先剥离压缩层再取扩展名）
        Assert.Equal("json", FileParsers.FormatFromExtension(Path.GetExtension(CompressionDetector.StripCompression("x.json.gz"))));
        Assert.Equal("jsonl", FileParsers.FormatFromExtension(Path.GetExtension(CompressionDetector.StripCompression("x.jsonl.bz2"))));
    }

    // ── 非压缩回归 ──

    [Fact]
    public void PlainFile_StillParses_Unchanged()
    {
        string path = Path.Combine(Path.GetTempPath(), "plain.json");
        File.WriteAllText(path, "[{\"Id\":1,\"Name\":\"Alice\"}]");
        try
        {
            Assert.Equal(CompressionType.None, CompressionDetector.Detect(path));
            var rows = FileParsers.ParseByFormat(path, "json", new() { Flatten = true }).ToList();
            Assert.Single(rows);
            Assert.Equal("Alice", rows[0]["Name"]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void InputSource_Dispose_ReleasesHandles()
    {
        string path = WriteGZip("dispose.jsonl.gz", "{\"Id\":1}\n");
        try
        {
            InputSource source;
            using (source = InputSource.Open(path))
            {
                var rows = FileParsers.ParseByFormat(path, "jsonl", new() { Flatten = true }).ToList();
                Assert.Single(rows);
            }
            // 释放后仍可删除文件（句柄已关闭），验证无句柄泄漏
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 增强：gzip CSV（不可寻址流嗅探）与 gzip xlsx（内存缓冲保证可寻址）──

    [Fact]
    public void GZip_Csv_StreamsThrough_NonSeekableSniffing()
    {
        string path = WriteGZip("data.csv.gz", "Name,Age\r\nAlice,30\r\nBob,25\r\n");
        try
        {
            Assert.Equal("excel", FileParsers.DetectFormat(path)); // 项目约定 .csv → "excel"（共用解析器）
            var rows = FileParsers.ParseByFormat(path, "excel", new() { Flatten = true }).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal("Alice", rows[0]["Name"]);
            Assert.Equal(25L, Convert.ToInt64(rows[1]["Age"], System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void GZip_Xlsx_BufferedAndParses()
    {
        string path = Path.Combine(Path.GetTempPath(), "book.data.xlsx.gz");
        try
        {
            byte[] xlsx = BuildXlsx();
            using (var fs = File.Create(path))
            using (var gz = new GZipStream(fs, CompressionMode.Compress, leaveOpen: true))
            {
                gz.Write(xlsx);
            }

            Assert.Equal("excel", FileParsers.DetectFormat(path));
            var rows = FileParsers.ParseByFormat(path, "excel", new() { Flatten = true }).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal("Bob", rows[1]["Name"]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 增强：zip 内嵌套 gzip/bzip2 条目 ──

    [Fact]
    public void Zip_NestedGzipCsvEntry_Parses()
    {
        string path = Path.Combine(Path.GetTempPath(), "bundle.zip");
        if (File.Exists(path)) File.Delete(path);
        byte[] gzCsv = CompressToGZipBytes("Name,Age\r\nAlice,30\r\nBob,25\r\n");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("logs.csv.gz");
            using var s = entry.Open();
            s.Write(gzCsv);
        }

        try
        {
            Assert.Equal("excel", FileParsers.DetectFormat(path));
            var rows = FileParsers.ParseByFormat(path, "excel", new() { Flatten = true }).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal("Bob", rows[1]["Name"]);

            using var source = InputSource.Open(path);
            Assert.Equal(CompressionType.Zip, source.Compression);
            Assert.Equal("logs.csv.gz", source.EntryName);
            Assert.Equal("excel", source.Format);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 增强：魔数兜底探测 ──

    [Fact]
    public void MagicByte_NoExtension_GzipDetected()
    {
        string path = Path.Combine(Path.GetTempPath(), "payload.bin"); // 无认知扩展名，实为 gzip 的 JSONL
        try
        {
            byte[] payload = CompressToGZipBytes("{\"Id\":1,\"Name\":\"Alice\"}\n{\"Id\":2}\n");
            File.WriteAllBytes(path, payload);

            Assert.Equal(CompressionType.GZip, CompressionDetector.Detect(path));
            var rows = FileParsers.ParseByFormat(path, "jsonl", new() { Flatten = true }).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal("Alice", rows[0]["Name"]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void MagicByte_RenamedXlsx_AnalyzedAsExcel_NotAsZipContainer()
    {
        string path = Path.Combine(Path.GetTempPath(), "report.dat"); // 未知扩展名，实为 xlsx
        try
        {
            File.WriteAllBytes(path, BuildXlsx());

            // 不被误判为 zip 数据容器，且内容级识别回退为 excel 格式
            Assert.Equal(CompressionType.None, CompressionDetector.Detect(path));
            Assert.True(CompressionDetector.LooksLikeOfficeOpenXmlPackage(path));
            Assert.Equal("excel", FileParsers.DetectFormat(path));

            var rows = FileParsers.ParseByFormat(path, "excel", new() { Flatten = true }).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal("Alice", rows[0]["Name"]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── 增强：端到端集成（AnalysisRunner 完整管线 + 解压源释放）──

    [Fact]
    public void AnalysisRunner_EndToEnd_GzipJsonl_ExitsZero()
    {
        string path = WriteGZip("pipeline.jsonl.gz",
            "{\"Id\":1,\"Name\":\"Alice\"}\n{\"Id\":2,\"Name\":\"Bob\"}\n{\"Id\":3}\n");
        try
        {
            int exit = ObjectAnalyzer.Analysis.AnalysisRunner.Run(
                new ObjectAnalyzer.Analysis.AnalysisOptions { InputPath = path });
            Assert.Equal(0, exit);

            // 管线结束后文件句柄应全部释放，可立即删除
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 二进制压缩/构造辅助 ──

    private static byte[] CompressToGZipBytes(string content)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
        using (var w = new StreamWriter(gz))
        {
            w.Write(content);
        }

        return ms.ToArray();
    }

    private static byte[] BuildXlsx()
    {
        var rows = new[]
        {
            new { Name = "Alice", Age = 30 },
            new { Name = "Bob", Age = 25 }
        };
        using var ms = new MemoryStream();
        MiniExcelLibs.MiniExcel.SaveAs(ms, rows, printHeader: true);
        ms.Position = 0;
        return ms.ToArray();
    }
}
