namespace ObjectAnalyzer.Analysis;

/// <summary>数据血缘节点。</summary>
public sealed record DataLineageNode(
    string Path,
    string Kind,
    int Depth,
    long OccurrenceCount,
    IReadOnlyList<string> TargetFields);

/// <summary>数据血缘父子边。</summary>
public sealed record DataLineageEdge(
    string ParentPath,
    string ChildPath,
    string Kind);

/// <summary>一次分析结果生成的对象结构血缘。</summary>
public sealed record DataLineage(
    IReadOnlyList<DataLineageNode> Nodes,
    IReadOnlyList<DataLineageEdge> Edges);
