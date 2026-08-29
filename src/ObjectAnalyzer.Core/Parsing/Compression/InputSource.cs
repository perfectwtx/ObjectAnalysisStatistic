using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ICSharpCode.SharpZipLib.BZip2;

namespace ObjectAnalyzer.Parsing;

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
