using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// Versioned resumable calculation state and source cursor. Written after a successful run;
/// a report cannot be used to reconstruct this state. CLI resume uses physical JSONL lines;
/// Web batch snapshots deliberately carry no source cursor.
/// </summary>
public sealed class AnalyzerSnapshot
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "object-analyzer.snapshot/v3";

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

    /// <summary>已处理行前缀的 SHA-256（hex，64 字符），允许追加，拒绝修改历史内容。</summary>
    [JsonPropertyName("sourceFingerprint")]
    public string SourceFingerprint { get; set; } = "";

    /// <summary>Complete resumable calculation state; reports are not resumable state.</summary>
    [JsonPropertyName("state")]
    public AnalysisStateSnapshot State { get; set; } = new();

    /// <summary>Compatibility read projection; never serialized or used to restore computation.</summary>
    [JsonIgnore]
    public AnalysisResult Result => State.Restore().Complete();

    /// <summary>Pipeline configuration identity, excluding source location and output options.</summary>
    [JsonPropertyName("executionSignature")]
    public string? ExecutionSignature { get; set; }

    // ── 序列化 / 反序列化 ──

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        MaxDepth = 512,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IncludeFields = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        MaxDepth = 512,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true
    };

    public void SaveTo(string path)
    {
        if (Schema != "object-analyzer.snapshot/v3" || SourcePosition < 0) throw new InvalidDataException("Invalid checkpoint schema or cursor.");
        // 原子写入：先写临时文件，再重命名，避免半写状态被 resume
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _ = State.Restore();
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = File.Create(temp)) JsonSerializer.Serialize(fs, this, WriteOptions);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static AnalyzerSnapshot LoadFrom(string path)
    {
        using var fs = File.OpenRead(path);
        using var document = JsonDocument.Parse(fs, new JsonDocumentOptions { MaxDepth = 512 });
        if (!document.RootElement.TryGetProperty("schema", out var schema) || schema.GetString() != "object-analyzer.snapshot/v3"
            || !document.RootElement.TryGetProperty("state", out _))
            throw new InvalidDataException("AnalyzerSnapshot schema incompatible; v3 calculation state is required. Re-analyze legacy reports/checkpoints.");
        var snapshot = document.RootElement.Deserialize<AnalyzerSnapshot>(ReadOptions)
            ?? throw new InvalidDataException($"无法从 {path} 解析 AnalyzerSnapshot（反序列化结果为 null）");
        if (snapshot.Schema != "object-analyzer.snapshot/v3")
        {
            throw new InvalidDataException(
                $"AnalyzerSnapshot schema 不兼容：'{snapshot.Schema}'，期望 'object-analyzer.snapshot/v3'。旧版值身份与路径无法无损迁移，请重新分析数据源。");
        }
        if (snapshot.SourcePosition < 0) throw new InvalidDataException("Negative source cursor.");
        _ = snapshot.State.Restore();
        return snapshot;
    }
}
