using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// P6.2：增量分析 / 断点续跑快照包装。
/// 在 AnalysisResult 之外增加"源文件位置"信息，使 AnalysisRunner 能从源文件中间继续分析。
///
/// 使用场景：
/// - <c>--checkpoint &lt;PATH&gt;</c>：每 N 条记录落盘一次，崩溃后可接续；
/// - <c>--resume &lt;CHECKPOINT&gt;</c>：从快照恢复，定位到源文件位置继续分析；
/// - <c>--baseline &lt;JSON&gt;</c>：把历史 AnalysisResult JSON 作为基线，本次只分析新数据后合并。
///
/// 文件格式：NDJSON-of-1（顶层 JSON 对象）—— 与现有 <c>--output ... .json</c> 报告格式互不干扰。
/// Schema: object-analyzer.snapshot/v2
/// </summary>
public sealed class AnalyzerSnapshot
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "object-analyzer.snapshot/v2";

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>源文件路径（绝对或相对，resume 时校验是否一致）。</summary>
    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = "";

    /// <summary>源文件格式（jsonl/csv/json/excel/yaml/xml/db/...）。</summary>
    [JsonPropertyName("sourceFormat")]
    public string SourceFormat { get; set; } = "";

    /// <summary>已处理位置（行号 / 字节偏移 / 数组索引 / Excel 行号 等，按 positionKind 解读）。</summary>
    [JsonPropertyName("sourcePosition")]
    public long SourcePosition { get; set; }

    /// <summary>位置的语义："line" / "byte" / "row" / "arrayIndex"。</summary>
    [JsonPropertyName("positionKind")]
    public string PositionKind { get; set; } = "line";

    /// <summary>源文件头部 SHA-256 摘要前 16 字节（hex，32 字符）用于 resume 时校验文件未变更。</summary>
    [JsonPropertyName("sourceFingerprint")]
    public string SourceFingerprint { get; set; } = "";

    /// <summary>已分析聚合结果。</summary>
    [JsonPropertyName("result")]
    public AnalysisResult Result { get; set; } = new();

    // ── 序列化 / 反序列化 ──

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true
    };

    public void SaveTo(string path)
    {
        // 原子写入：先写临时文件，再重命名，避免半写状态被 resume
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var temp = path + ".tmp";
        using (var fs = File.Create(temp))
        {
            JsonSerializer.Serialize(fs, this, WriteOptions);
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    public static AnalyzerSnapshot LoadFrom(string path)
    {
        using var fs = File.OpenRead(path);
        var snapshot = JsonSerializer.Deserialize<AnalyzerSnapshot>(fs, ReadOptions)
            ?? throw new InvalidDataException($"无法从 {path} 解析 AnalyzerSnapshot（反序列化结果为 null）");
        if (snapshot.Schema != "object-analyzer.snapshot/v2")
        {
            throw new InvalidDataException(
                $"AnalyzerSnapshot schema 不兼容：'{snapshot.Schema}'，期望 'object-analyzer.snapshot/v2'。旧版值身份与路径无法无损迁移，请重新分析数据源。");
        }
        return snapshot;
    }
}
