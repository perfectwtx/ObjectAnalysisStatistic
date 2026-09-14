namespace ObjectAnalyzer.Analysis;

/// <summary>影响分析等级。</summary>
public enum ImpactLevel
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>影响分析来源类型。</summary>
public enum ImpactType
{
    SchemaChange,
    ParentObject,
    ChildNode,
    RelatedObject
}

/// <summary>单个受影响节点。</summary>
public sealed record ImpactedNode(
    string Path,
    string Kind,
    ImpactType Type,
    ImpactLevel Level,
    string Reason);

/// <summary>Schema 变更沿对象关系和数据血缘传播后的影响分析结果。</summary>
public sealed record ImpactAnalysis(
    IReadOnlyList<ImpactedNode> Nodes,
    ImpactLevel HighestLevel,
    int BreakingChangeCount);

/// <summary>基于 Schema Diff 与数据血缘计算影响范围。</summary>
public static class ImpactAnalysisBuilder
{
    public static ImpactAnalysis Build(SchemaDiff? diff, DataLineage? lineage)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var changedPaths = new Dictionary<string, ImpactLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in diff.RemovedFields)
            changedPaths[field.Path] = Max(changedPaths.GetValueOrDefault(field.Path), ImpactLevel.Critical);

        foreach (var field in diff.ChangedFields)
            changedPaths[field.Path] = Max(changedPaths.GetValueOrDefault(field.Path), field.Breaking ? ImpactLevel.High : ImpactLevel.Medium);

        foreach (var field in diff.AddedFields)
            changedPaths[field.Path] = Max(changedPaths.GetValueOrDefault(field.Path), ImpactLevel.Low);

        var nodes = new List<ImpactedNode>();
        foreach (var change in changedPaths)
        {
            var level = change.Value;
            var type = level == ImpactLevel.Low ? ImpactType.SchemaChange : ImpactType.SchemaChange;
            var reason = level switch
            {
                ImpactLevel.Critical => "字段已删除，属于直接破坏性影响。",
                ImpactLevel.High => "字段存在破坏性 Schema 变更。",
                ImpactLevel.Medium => "字段 Schema 发生变化，需要检查下游兼容性。",
                _ => "字段新增，当前为低风险结构变化。"
            };
            nodes.Add(new ImpactedNode(change.Key, "Field", type, level, reason));
        }

        if (lineage is not null)
        {
            foreach (var node in lineage.Nodes)
            {
                foreach (var change in changedPaths)
                {
                    if (string.Equals(node.Path, change.Key, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (IsDescendant(node.Path, change.Key))
                    {
                        nodes.Add(new ImpactedNode(
                            node.Path,
                            node.Kind,
                            ImpactType.ChildNode,
                            Lower(change.Value),
                            $"父级字段 {change.Key} 发生 Schema 变化，该节点位于其下游血缘路径。"));
                        break;
                    }

                    if (IsDescendant(change.Key, node.Path))
                    {
                        nodes.Add(new ImpactedNode(
                            node.Path,
                            node.Kind,
                            ImpactType.ParentObject,
                            Lower(change.Value),
                            $"子级字段 {change.Key} 发生 Schema 变化，该节点是其父级对象。"));
                        break;
                    }
                }
            }
        }

        var distinct = nodes
            .GroupBy(x => $"{x.Type}\n{x.Path}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Level).First())
            .OrderByDescending(x => x.Level)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var highest = distinct.Count == 0 ? ImpactLevel.Low : distinct.Max(x => x.Level);
        return new ImpactAnalysis(distinct, highest, diff.RemovedFields.Count + diff.ChangedFields.Count(x => x.Breaking));
    }

    private static bool IsDescendant(string path, string parent)
        => path.StartsWith(parent + ".", StringComparison.OrdinalIgnoreCase);

    private static ImpactLevel Lower(ImpactLevel level)
        => level switch
        {
            ImpactLevel.Critical => ImpactLevel.High,
            ImpactLevel.High => ImpactLevel.Medium,
            ImpactLevel.Medium => ImpactLevel.Low,
            _ => ImpactLevel.Low
        };

    private static ImpactLevel Max(ImpactLevel left, ImpactLevel right)
        => left > right ? left : right;
}
