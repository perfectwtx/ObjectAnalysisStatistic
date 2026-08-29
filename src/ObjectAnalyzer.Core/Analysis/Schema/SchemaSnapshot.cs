using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// Schema 快照（canonical）：独立于代码生成的、面向 diff 场景的规范化 Schema 描述。
/// 字段顺序按源数据首次出现顺序写入，便于快照间稳定对比。
/// </summary>
public sealed class SchemaSnapshot
{
    /// <summary>快照格式版本（用于快照自身演进兼容）。</summary>
    [JsonPropertyName("snapshotVersion")]
    public string SnapshotVersion { get; set; } = "1.0";

    /// <summary>快照生成时间（UTC）。</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>可选来源标识（输入文件路径、SQL 查询等）。</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>被分析的对象总数。</summary>
    [JsonPropertyName("totalObjects")]
    public long TotalObjects { get; set; }

    /// <summary>字段列表（按首次出现顺序）。</summary>
    [JsonPropertyName("fields")]
    public List<SchemaSnapshotField> Fields { get; set; } = new();
}
