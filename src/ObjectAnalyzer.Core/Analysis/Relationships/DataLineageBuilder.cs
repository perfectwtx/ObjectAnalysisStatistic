namespace ObjectAnalyzer.Analysis;

/// <summary>把对象关系摘要转换为可查询的数据血缘图。</summary>
public static class DataLineageBuilder
{
    public static DataLineage Build(IEnumerable<ObjectRelationship> relationships)
    {
        ArgumentNullException.ThrowIfNull(relationships);

        var source = relationships
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .GroupBy(x => $"{x.Kind}\n{x.Path}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderByDescending(r => r.OccurrenceCount).First())
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nodes = source
            .Select(x => new DataLineageNode(
                x.Path,
                x.Kind,
                x.Path.Count(c => c == '.') + 1,
                x.OccurrenceCount,
                x.TargetFields.ToList()))
            .ToList();

        var edges = new List<DataLineageEdge>();
        var nodePaths = new HashSet<string>(nodes.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            var lastSeparator = node.Path.LastIndexOf('.');
            if (lastSeparator < 0)
                continue;

            var parent = node.Path[..lastSeparator];
            if (nodePaths.Contains(parent))
            {
                edges.Add(new DataLineageEdge(
                    parent,
                    node.Path,
                    node.Kind == "Collection" ? "ContainsCollection" : "Contains"));
            }
        }

        foreach (var relationship in source)
        {
            foreach (var field in relationship.TargetFields)
            {
                if (string.IsNullOrWhiteSpace(field))
                    continue;

                var fieldPath = $"{relationship.Path}.{field}";
                if (nodePaths.Add(fieldPath))
                {
                    nodes.Add(new DataLineageNode(
                        fieldPath,
                        "Field",
                        fieldPath.Count(c => c == '.') + 1,
                        relationship.OccurrenceCount,
                        Array.Empty<string>()));
                }

                edges.Add(new DataLineageEdge(
                    relationship.Path,
                    fieldPath,
                    "ContainsField"));
            }
        }

        return new DataLineage(nodes, edges);
    }
}
