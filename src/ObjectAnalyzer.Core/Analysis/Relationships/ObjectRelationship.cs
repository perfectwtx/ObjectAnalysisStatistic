namespace ObjectAnalyzer.Analysis;

/// <summary>一次数据对象中的嵌套关系统计。</summary>
public sealed class ObjectRelationship
{
    /// <summary>关系所在字段路径，例如 customer.address。</summary>
    public string Path { get; set; } = "";

    /// <summary>关系类型：Object 表示单个嵌套对象，Collection 表示对象集合。</summary>
    public string Kind { get; set; } = "";

    /// <summary>发现该关系的记录数。</summary>
    public long OccurrenceCount { get; set; }

    /// <summary>目标对象非空记录数。</summary>
    public long NonNullCount { get; set; }

    /// <summary>目标对象中发现的字段数量（去重后的字段名数量）。</summary>
    public int TargetFieldCount { get; set; }

    /// <summary>目标对象字段名摘要，最多保留 50 个字段。</summary>
    public List<string> TargetFields { get; set; } = new();
}
