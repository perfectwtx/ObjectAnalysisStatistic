namespace ObjectAnalyzer.Analysis;

/// <summary>把对象关系摘要转换为可查询的数据血缘图。</summary>
public static class DataLineageBuilder
{
    public static DataLineage Build(IEnumerable<ObjectRelationship> relationships)
    {
        ArgumentNullException.ThrowIfNull(relationships);

        var source = relationships
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .GroupBy(x => $"{x.Kind}\n{x.Path}", StringComparer.Ordinal)
            .Select(x => x.OrderByDescending(r => r.OccurrenceCount).First())
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ToList();

        var nodes = source
            .Select(x => new DataLineageNode(
                x.Path,
                x.Kind,
                FieldPath.Parse(x.Path).Segments.Length,
                x.OccurrenceCount,
                x.TargetFields.ToList()))
            .ToList();

        var edges = new List<DataLineageEdge>();
        var nodePaths = new HashSet<string>(nodes.Select(x => x.Path), StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            var path = FieldPath.Parse(node.Path);
            if (path.Segments.Length <= 1)
                continue;

            var parent = path.Parent.ToString();
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

                var fieldPath = FieldPath.Parse(relationship.Path).AppendProperty(field).ToString();
                if (nodePaths.Add(fieldPath))
                {
                    nodes.Add(new DataLineageNode(
                        fieldPath,
                        "Field",
                        FieldPath.Parse(fieldPath).Segments.Length,
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
