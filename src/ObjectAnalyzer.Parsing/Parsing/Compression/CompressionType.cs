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
