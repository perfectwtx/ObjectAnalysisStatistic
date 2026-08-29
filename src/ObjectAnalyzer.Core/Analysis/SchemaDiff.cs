using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 两份 Schema 快照的 diff 结果。分类：新增字段、删除字段、变更字段（含类型/coverage/nullable/语义/范围等子项）。
/// </summary>
public sealed class SchemaDiff
{
    /// <summary>Diff 格式版本。</summary>
    [JsonPropertyName("diffVersion")]
    public string DiffVersion { get; set; } = "1.0";

    /// <summary>生成时间（UTC）。</summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>基线快照的来源标识（如有）。</summary>
    [JsonPropertyName("baselineSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaselineSource { get; set; }

    /// <summary>当前快照的来源标识（如有）。</summary>
    [JsonPropertyName("currentSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentSource { get; set; }

    /// <summary>是否包含破坏性变更（删除字段 / 类型变化 / nullable → non-nullable / coverage 大幅下降）。</summary>
    [JsonPropertyName("hasBreakingChanges")]
    public bool HasBreakingChanges { get; set; }

    /// <summary>新增字段列表（current 有、baseline 无）。</summary>
    [JsonPropertyName("addedFields")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SchemaSnapshotField> AddedFields { get; set; } = new();

    /// <summary>删除字段列表（baseline 有、current 无）。属于破坏性变更。</summary>
    [JsonPropertyName("removedFields")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SchemaSnapshotField> RemovedFields { get; set; } = new();

    /// <summary>变更字段列表（同名但属性发生变化）。</summary>
    [JsonPropertyName("changedFields")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<SchemaFieldChange> ChangedFields { get; set; } = new();
}
