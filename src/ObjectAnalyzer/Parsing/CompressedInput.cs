using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ICSharpCode.SharpZipLib.BZip2;

namespace ObjectAnalyzer.Parsing;

/// <summary>
/// 压缩输入透明解包层。根据文件扩展名自动识别 gzip / bzip2 / zip 压缩，
/// 返回一个惰性、可释放的“解压后内容流”，使下游解析器完全无需感知压缩。
///
/// 设计目标（贴合超大文件场景）：
/// - 流式透传：解压后的数据由解析器边读边处理，不整体落盘、不整体物化。
///   因此 gzip/bzip2 包裹的 JSONL / CSV 等行级流式格式，在 50GB+ 级别仍内存有界。
/// - 仅当内层格式需要随机访问（目前为 .xlsx）时，才将解压内容缓冲到内存以保证可寻址。
/// - 所有底层句柄（文件流、解压流、ZipArchive）统一在 <see cref="InputSource.Dispose"/> 中释放。
/// </summary>
public enum CompressionType
{
    None,
    GZip,
    BZip2,
    Zip
}

/// <summary>
/// 根据文件扩展名探测最外层压缩类型，并剥离压缩扩展名以还原“内层路径”。
/// 扩展名无法判定时回退到魔数字节嗅探（1F 8B = gzip、42 5A 68 = bzip2、50 4B = zip 容器，
/// 但带 [Content_Types].xml 的 Office Open XML 包——如 .xlsx——不视为待解压容器）。
/// </summary>
public static class CompressionDetector
{
    private static readonly HashSet<string> _compressionExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gz", ".bz2", ".zip"
    };

    /// <summary>明确的“数据内容”扩展名：命中时不做魔数嗅探（避免 .xlsx 等 PK 头文件被误判为 zip 包裹）。</summary>
    private static readonly HashSet<string> _knownDataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonl", ".ndjson", ".csv", ".tsv",
        ".xlsx", ".xls", ".xlsm", ".xml", ".yaml", ".yml",
        ".txt", ".log"
    };

    // 魔数签名
    private const int MaxMagicLength = 4;
    private static readonly byte[] _gzipMagic = { 0x1F, 0x8B };
    private static readonly byte[] _bzip2Magic = { 0x42, 0x5A, 0x68 }; // "BZh"

    public static bool IsCompressionExtension(string ext) =>
        !string.IsNullOrEmpty(ext) && _compressionExt.Contains(ext);

    /// <summary>按最后一个扩展名判定最外层压缩类型。</summary>
    public static CompressionType Detect(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".gz": return CompressionType.GZip;
            case ".bz2": return CompressionType.BZip2;
            case ".zip": return CompressionType.Zip;
        }

        string ext = Path.GetExtension(path);
        bool knownDataExt = HasKnownDataExtension(ext);
        // 扩展名未声明压缩时按魔数兜底嗅探：
        // - 未知/无扩展名 → 完整嗅探（含 zip 容器；Office Open XML 包除外）
        // - 已知数据扩展名（.json/.csv 等）→ 仅嗅探 gzip/bzip2 魔数。文本数据格式不可能以
        //   1F 8B / BZh 开头，命中即说明是被误命名的压缩文件；PK 头不嗅探，避免把 .xlsx 误判为 zip 包裹。
        return SniffByMagic(path, probeZip: !knownDataExt);
    }

    /// <summary>是否为工具已认知的数据内容扩展名（非压缩壳）。</summary>
    public static bool HasKnownDataExtension(string pathOrExt)
    {
        string ext = Path.HasExtension(pathOrExt) && pathOrExt.StartsWith('.')
            ? pathOrExt
            : Path.GetExtension(pathOrExt);
        return !string.IsNullOrEmpty(ext) && _knownDataExtensions.Contains(ext);
    }

    /// <summary>
    /// 判断文件是否为 Office Open XML 包（zip 容器且含 [Content_Types].xml，如 .xlsx）。
    /// 用于未知扩展名场景的内容级格式识别：此类文件应按 Excel 解析，而非作为待解压数据容器。
    /// </summary>
    public static bool LooksLikeOfficeOpenXmlPackage(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            if (ReadAtLeast(fs, header, 4) < 4 || header[0] != (byte)'P' || header[1] != (byte)'K'
                || header[2] != 0x03 || header[3] != 0x04)
            {
                return false;
            }

            fs.Position = 0;
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
            return zip.Entries.Any(e =>
                e.FullName.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>剥离最外层压缩扩展名（如 <c>data.json.gz</c> → <c>data.json</c>），用于内层格式探测。</summary>
    public static string StripCompression(string path)
    {
        if (Detect(path) == CompressionType.None)
            return path;
        // Path.ChangeExtension(path, null) 仅去掉最后一个扩展名
        return Path.ChangeExtension(path, null);
    }

    /// <summary>
    /// 反复剥离 gzip / bzip2 后缀（<c>logs.csv.gz</c> → <c>logs.csv</c>），用于 zip 条目等无完整路径语义的名称。
    /// 不处理内层再嵌套 zip 的场景。
    /// </summary>
    public static string StripGzipBzip2Chain(string name)
    {
        string current = name;
        while (true)
        {
            CompressionType layer = DetectGzipBzip2Suffix(current);
            if (layer == CompressionType.None)
                break;
            // 去掉末尾后缀：".gz" 3 字符 / ".bz2" 4 字符
            current = current[..^(layer == CompressionType.GZip ? 3 : 4)];
        }

        return current;
    }

    /// <summary>仅按名称后缀判定 gzip / bzip2 层；其它（含 .zip）返回 None。</summary>
    public static CompressionType DetectGzipBzip2Suffix(string name)
    {
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            return CompressionType.GZip;
        if (name.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase))
            return CompressionType.BZip2;
        return CompressionType.None;
    }

    private static bool TryGetGzipBzip2Suffix(string name, out string? suffix)
    {
        suffix = null;
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            suffix = ".gz";
            return true;
        }

        if (name.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase))
        {
            suffix = ".bz2";
            return true;
        }

        return false;
    }

    /// <summary>读取文件头并按魔数判定压缩类型；文件不存在或头过短时返回 None。</summary>
    /// <param name="probeZip">是否将 PK 头判定为 zip 容器（对已知数据扩展名关闭，避免误判 xlsx）。</param>
    private static CompressionType SniffByMagic(string path, bool probeZip)
    {
        if (!File.Exists(path))
            return CompressionType.None;

        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[MaxMagicLength];
            int read = ReadAtLeast(fs, header, _bzip2Magic.Length); // 至少读到 3 字节即可判定 gzip/bzip2
            if (read >= _gzipMagic.Length && header[.._gzipMagic.Length].SequenceEqual(_gzipMagic))
                return CompressionType.GZip;
            if (read >= _bzip2Magic.Length && header[.._bzip2Magic.Length].SequenceEqual(_bzip2Magic))
                return CompressionType.BZip2;
            if (probeZip && read >= 4 && header[0] == (byte)'P' && header[1] == (byte)'K'
                && header[2] == 0x03 && header[3] == 0x04)
            {
                // PK 头：区分真正的数据 zip 容器与 Office Open XML 包（后者按 Excel 原生解析）
                return LooksLikeOfficeOpenXmlPackage(path) ? CompressionType.None : CompressionType.Zip;
            }

            return CompressionType.None;
        }
        catch (IOException)
        {
            return CompressionType.None;
        }
    }

    private static int ReadAtLeast(FileStream fs, Span<byte> buffer, int minBytes)
    {
        int total = 0;
        while (total < minBytes)
        {
            int n = fs.Read(buffer[total..]);
            if (n <= 0)
                break;
            total += n;
        }

        return total;
    }
}

/// <summary>
/// 已打开的（可能经解压的）输入源。持有解压内容流与压缩类型信息，并负责释放底层资源。
/// 通过 <see cref="Open"/> 工厂创建，枚举结束后应 <c>Dispose</c>。
/// </summary>
public sealed class InputSource : IDisposable
{
    private readonly Stream _content;
    private readonly List<IDisposable> _owners; // ZipArchive、bzip2 中间流等需随 InputSource 一起释放的句柄
    private readonly List<string>? _otherEntries; // zip 中未被选中的其它数据条目（用于提示）
    private bool _disposed;

    private InputSource(Stream content, IEnumerable<IDisposable>? owners, string format, CompressionType compression, string? entryName, List<string>? otherEntries)
    {
        _content = content;
        _owners = owners?.ToList() ?? new List<IDisposable>();
        Format = format;
        Compression = compression;
        EntryName = entryName;
        _otherEntries = otherEntries;
    }

    /// <summary>解压后的内容流（已定位到起始位置）。</summary>
    public Stream Content => _content;

    /// <summary>探测出的内层格式名（如 json / jsonl / csv / excel / xml / yaml）。</summary>
    public string Format { get; }

    /// <summary>最外层压缩类型。</summary>
    public CompressionType Compression { get; }

    /// <summary>zip 场景被选中的条目名（其它压缩类型为空）。</summary>
    public string? EntryName { get; }

    /// <summary>zip 中含多个数据条目时，返回未被选中的其它条目名。</summary>
    public IReadOnlyList<string> OtherEntries => (IReadOnlyList<string>?)_otherEntries ?? Array.Empty<string>();

    /// <summary>
    /// 打开输入文件并按扩展名选择解压策略。返回的对象在分析枚举期间保持打开，结束后释放。
    /// </summary>
    public static InputSource Open(string path)
    {
        CompressionType type = CompressionDetector.Detect(path);
        if (type == CompressionType.None)
        {
            // 普通文件：直接以可读流打开（可寻址，无需缓冲）
            var fs = File.OpenRead(path);
            return new InputSource(fs, null, FileParsers.FormatFromExtension(Path.GetExtension(path)), type, null, null);
        }

        if (type == CompressionType.GZip)
        {
            var fs = File.OpenRead(path);
            var gzip = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: false);
            string innerExt = Path.GetExtension(CompressionDetector.StripCompression(path));
            string format = FileParsers.FormatFromExtension(innerExt);
            Stream content = MaterializeIfExcel(gzip, format);
            return new InputSource(content, null, format, type, null, null);
        }

        if (type == CompressionType.BZip2)
        {
            var fs = File.OpenRead(path);
            var bz2 = new BZip2InputStream(fs)
            {
                IsStreamOwner = false // 由 InputSource 负责释放底层文件流
            };
            string innerExt = Path.GetExtension(CompressionDetector.StripCompression(path));
            string format = FileParsers.FormatFromExtension(innerExt);
            Stream content = MaterializeIfExcel(bz2, format);
            // BZip2InputStream 不级联释放底层文件流，显式将文件流登记为 owner 释放
            return new InputSource(content, new IDisposable[] { fs }, format, type, null, null);
        }

        return OpenZip(path);
    }

    /// <summary>
    /// 若内层格式为需要随机访问的 excel（.xlsx），则把前向只读的解压流整体拷贝到内存流以保证可寻址；
    /// 否则直接透传解压流（流式、内存有界）。
    /// </summary>
    private static Stream MaterializeIfExcel(Stream decompressed, string format)
    {
        if (format != "excel")
            return decompressed;

        // .xlsx 基于 zip 随机访问，MiniExcel 需要可寻址流；压缩流通常为前向只读，整体缓冲到内存。
        // xlsx 极少达到 TB 级，且已属边界场景，缓冲代价可接受。
        var ms = new MemoryStream();
        decompressed.CopyTo(ms);
        ms.Position = 0;
        decompressed.Dispose(); // 级联释放底层文件流（leaveOpen:false）
        return ms;
    }

    private static InputSource OpenZip(string path)
    {
        var fs = File.OpenRead(path);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (Exception ex)
        {
            fs.Dispose();
            throw new InvalidDataException($"无法将文件作为 zip 压缩包打开: {ex.Message}", ex);
        }

        List<IDisposable> owners = new() { zip };
        Stream? raw = null;
        try
        {
            var entry = PickZipEntry(zip, out var otherEntries);
            if (entry == null)
            {
                throw new InvalidDataException("zip 压缩包中未找到可读取的数据条目（仅含目录或为空）。");
            }

            raw = entry.Open();

            // 嵌套解压：条目本身是 gzip / bzip2（如 logs.csv.gz、dump.json.bz2）时逐层再包解压流。
            // GZipStream(leaveOpen:false) 会级联关闭内层流；bzip2 不级联，需将内层流登记为 owner。
            string innerName = entry.FullName;
            CompressionType nested;
            while ((nested = CompressionDetector.DetectGzipBzip2Suffix(innerName)) != CompressionType.None)
            {
                if (nested == CompressionType.GZip)
                {
                    raw = new GZipStream(raw, CompressionMode.Decompress, leaveOpen: false);
                    innerName = innerName[..^3];
                }
                else
                {
                    owners.Add(raw); // BZip2InputStream 不级联释放，先登记当前内层流
                    raw = new BZip2InputStream(raw)
                    {
                        IsStreamOwner = false
                    };
                    innerName = innerName[..^4];
                }
            }

            string innerExt = Path.GetExtension(innerName);
            string format = FileParsers.FormatFromExtension(innerExt);

            Stream content;
            if (format == "excel")
            {
                // 缓冲到内存以保证可寻址（.xlsx 基于 zip 随机访问）
                var ms = new MemoryStream();
                raw.CopyTo(ms);
                ms.Position = 0;
                raw.Dispose();
                content = ms;
            }
            else
            {
                // 前向只读流直接透传：JSONL / CSV 等行级格式可流式处理，超大文件内存有界
                content = raw;
            }

            return new InputSource(content, owners, format, CompressionType.Zip, entry.FullName, otherEntries);
        }
        catch
        {
            // 统一兜底：任何失败路径都不得泄漏已打开的流与归档句柄
            try { raw?.Dispose(); } catch { /* 忽略清理异常 */ }
            foreach (var owner in owners)
            {
                try { owner.Dispose(); } catch { /* 忽略清理异常 */ }
            }

            throw;
        }
    }

    /// <summary>
    /// 轻量读取 zip 中央目录，返回确定性选中的数据条目名与未被选中的其它条目（供控制台提示）。
    /// 文件不可读或不是有效 zip 时返回 null。
    /// </summary>
    public static (string Chosen, IReadOnlyList<string> Others)? PreviewZipEntries(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            var entry = PickZipEntry(zip, out var others);
            return entry == null ? null : (entry.FullName, others);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从 zip 中选择用于分析的数据条目：优先选扩展名匹配已知数据格式的条目，
    /// 多个候选时取全名字典序首个（确定性）；返回选中条目与未被选中的其它数据条目名。
    /// </summary>
    internal static ZipArchiveEntry? PickZipEntry(ZipArchive zip, out List<string> otherEntries)
    {
        otherEntries = new List<string>();
        var files = new List<ZipArchiveEntry>();
        foreach (var e in zip.Entries)
        {
            // 跳过目录条目（以 '/' 结尾且文件名为空）
            if (e.FullName.EndsWith('/') && string.IsNullOrEmpty(e.Name))
                continue;
            if (string.IsNullOrEmpty(e.Name))
                continue;
            files.Add(e);
        }

        if (files.Count == 0)
            return null;

        var dataFiles = files.FindAll(e => _dataExtensions.Contains(Path.GetExtension(e.FullName)));
        var candidates = dataFiles.Count > 0 ? dataFiles : files;
        candidates.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));

        var chosen = candidates[0];
        foreach (var e in candidates)
        {
            if (e != chosen)
                otherEntries.Add(e.FullName);
        }
        return chosen;
    }

    private static readonly HashSet<string> _dataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonl", ".ndjson", ".csv", ".xlsx", ".xml", ".yaml", ".yml", ".txt"
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _content.Dispose();
        foreach (var owner in _owners)
        {
            try { owner.Dispose(); } catch { /* 单个句柄释放失败不影响其余清理 */ }
        }

        _owners.Clear();
    }
}
