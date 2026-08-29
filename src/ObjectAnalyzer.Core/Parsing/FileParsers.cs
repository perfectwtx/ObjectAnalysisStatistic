using System.IO.Compression;
using ObjectAnalyzer.Parsing.Parsers;

namespace ObjectAnalyzer.Parsing;

/// <summary>
/// Multi-format file parsers. Each parser reads a file and returns
/// a collection of property dictionaries for analysis.
/// All parsers stream results lazily via yield return to ensure minimal memory footprint.
/// </summary>
public static class FileParsers
{
    // ─────────────────────────────────────────────
    //  Parser registry — add new formats here
    // ─────────────────────────────────────────────

    private static readonly List<IFileParser> _parsers = new()
    {
        new JsonFileParser(),
        new JsonlFileParser(),
        new ExcelCsvFileParser(),
        new XmlFileParser(),
        new YamlFileParser(),
        new ExcelJsonFileParser(),
    };

    /// <summary>
    /// Detects the format name from a file extension.
    /// 支持压缩包裹：gzip/bzip2 取内层扩展名；zip 取首个数据条目的扩展名。
    /// </summary>
    public static string DetectFormat(string path)
    {
        CompressionType ct = CompressionDetector.Detect(path);
        if (ct == CompressionType.None)
        {
            // 内容级兜底：未知扩展名但实为 Office Open XML 包（如被改名的 .xlsx）→ 按 Excel 解析
            if (!CompressionDetector.HasKnownDataExtension(path) &&
                CompressionDetector.LooksLikeOfficeOpenXmlPackage(path))
            {
                return "excel";
            }

            return FormatFromExtension(Path.GetExtension(path));
        }

        if (ct == CompressionType.Zip)
            return DetectZipFormat(path);

        // gzip / bzip2：内层扩展名决定格式
        return FormatFromExtension(Path.GetExtension(CompressionDetector.StripCompression(path)));
    }

    /// <summary>
    /// 仅按扩展名（已剥离压缩层）推断格式名；供 <see cref="InputSource"/> 复用，也可在测试中直接校验。
    /// </summary>
    public static string FormatFromExtension(string ext)
    {
        ext = ext.ToLowerInvariant();

        // 明确优先返回 Excel（.xlsx）而不是 CSV；CSV 与 Excel 共享同一解析器，但 .xlsx 的语义应走 excel 分支。
        if (ext == ".xlsx")
            return "excel";

        foreach (var parser in _parsers)
        {
            if (parser.SupportedExtensions.Contains(ext))
                return parser.SupportedFormats[0];
        }
        // Fallbacks
        if (ext is ".txt" or ".ndjson") return "jsonl";
        return "json";
    }

    /// <summary>
    /// 打开 zip 中央目录，按确定性规则选取数据条目并以其（剥离嵌套 gzip/bzip2 后缀的）扩展名推断格式
    /// （探测失败则退化为 json）。
    /// </summary>
    private static string DetectZipFormat(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            var entry = InputSource.PickZipEntry(zip, out _);
            if (entry == null)
                return "json";
            string innerName = CompressionDetector.StripGzipBzip2Chain(entry.FullName);
            return FormatFromExtension(Path.GetExtension(innerName));
        }
        catch
        {
            return "json";
        }
    }

    /// <summary>
    /// Parses a file by format name using the parser registry.
    /// Format names are matched case-insensitively.
    /// 自动处理 gzip / bzip2 / zip 压缩包裹：在枚举期间惰性解压并持有输入源，枚举结束后释放。
    /// </summary>
    public static IEnumerable<Dictionary<string, object?>> ParseByFormat(string path, string format, ParseOptions options)
    {
        if (string.IsNullOrWhiteSpace(format))
            throw new InvalidOperationException("未指定文件格式。");

        string normalized = format.Trim().ToLowerInvariant();
        foreach (var parser in _parsers)
        {
            if (parser.SupportedFormats.Contains(normalized))
                return new DecompressingEnumerable(path, parser, options);
        }
        throw new InvalidOperationException($"不支持的文件格式: {format}");
    }

}
