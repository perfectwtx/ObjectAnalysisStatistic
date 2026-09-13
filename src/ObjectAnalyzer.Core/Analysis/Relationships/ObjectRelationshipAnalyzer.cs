using System.Collections;

namespace ObjectAnalyzer.Analysis;

/// <summary>从解析后的对象中发现嵌套对象关系。</summary>
public static class ObjectRelationshipAnalyzer
{
    private const int MaxTargetFields = 50;
    private const int MaxDepth = 8;

    /// <summary>
    /// 包装输入序列，在对象被消费时同步收集关系，避免为了关系分析额外物化整个数据集。
    /// </summary>
    public static IEnumerable<Dictionary<string, object?>> Track(
        IEnumerable<Dictionary<string, object?>> source,
        Action<IReadOnlyList<ObjectRelationship>> completed)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(completed);

        var states = new Dictionary<string, RelationshipState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in source)
            {
                CollectObject(item, "", states, 0);
                yield return item;
            }
        }
        finally
        {
            completed(states.Values
                .OrderByDescending(x => x.OccurrenceCount)
                .Select(x => x.ToResult())
                .ToList());
        }
    }

    private static void CollectObject(
        IDictionary<string, object?> obj,
        string prefix,
        Dictionary<string, RelationshipState> states,
        int depth)
    {
        if (depth >= MaxDepth) return;

        foreach (var pair in obj)
        {
            string path = string.IsNullOrEmpty(prefix) ? pair.Key : $"{prefix}.{pair.Key}";
            if (pair.Value is null) continue;

            if (TryGetDictionary(pair.Value, out var nested))
            {
                Add(states, path, "Object", nested);
                CollectObject(nested, path, states, depth + 1);
                continue;
            }

            if (pair.Value is IEnumerable enumerable && pair.Value is not string)
            {
                bool foundObject = false;
                foreach (var element in enumerable)
                {
                    if (!TryGetDictionary(element, out var nestedElement)) continue;
                    foundObject = true;
                    Add(states, path, "Collection", nestedElement);
                    CollectObject(nestedElement, path, states, depth + 1);
                }

                if (foundObject) continue;
            }
        }
    }

    private static void Add(
        Dictionary<string, RelationshipState> states,
        string path,
        string kind,
        IDictionary<string, object?> target)
    {
        string key = $"{kind}\n{path}";
        if (!states.TryGetValue(key, out var state))
        {
            state = new RelationshipState(path, kind);
            states[key] = state;
        }

        state.OccurrenceCount++;
        state.NonNullCount++;
        foreach (var field in target.Keys)
        {
            if (state.TargetFields.Count >= MaxTargetFields) break;
            state.TargetFields.Add(field);
        }
    }

    private static bool TryGetDictionary(object? value, out IDictionary<string, object?> dictionary)
    {
        if (value is Dictionary<string, object?> concrete)
        {
            dictionary = concrete;
            return true;
        }

        if (value is IDictionary<string, object?> typed)
        {
            dictionary = typed;
            return true;
        }

        dictionary = null!;
        return false;
    }

    private sealed class RelationshipState
    {
        private readonly HashSet<string> _targetFields = new(StringComparer.OrdinalIgnoreCase);

        public RelationshipState(string path, string kind)
        {
            Path = path;
            Kind = kind;
        }

        public string Path { get; }
        public string Kind { get; }
        public long OccurrenceCount { get; set; }
        public long NonNullCount { get; set; }
        public List<string> TargetFields => _targetFields.Take(MaxTargetFields).ToList();

        public void AddTargetField(string field) => _targetFields.Add(field);

        public ObjectRelationship ToResult() => new()
        {
            Path = Path,
            Kind = Kind,
            OccurrenceCount = OccurrenceCount,
            NonNullCount = NonNullCount,
            TargetFieldCount = _targetFields.Count,
            TargetFields = TargetFields
        };
    }
}
