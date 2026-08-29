using System.IO.Compression;

namespace ObjectAnalyzer.Parsing;

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
    /// <param name="path">待探测的文件路径。</param>
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
