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

        var states = new Dictionary<string, RelationshipState>(StringComparer.Ordinal);
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
            string path = depth == 0 ? FieldPath.Parse(pair.Key).ToString() : FieldPath.Parse(prefix).AppendProperty(pair.Key).ToString();
            if (pair.Value is null) continue;
            object? value = pair.Value;
            if (value is RecordValue typedValue)
            {
                CollectTypedValue(typedValue, path, states, depth);
                continue;
            }

            if (TryGetDictionary(value, out var nested))
            {
                Add(states, path, "Object", nested.Keys);
                CollectObject(nested, path, states, depth + 1);
                continue;
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                bool foundObject = false;
                foreach (var element in enumerable)
                {
                    if (!TryGetDictionary(element, out var nestedElement)) continue;
                    foundObject = true;
                    Add(states, path, "Collection", nestedElement.Keys);
                    CollectObject(nestedElement, path, states, depth + 1);
                }

                if (foundObject) continue;
            }
        }
    }

    private static void CollectTypedValue(RecordValue value, string path, Dictionary<string, RelationshipState> states, int depth)
    {
        if (depth >= MaxDepth) return;
        if (value.Kind == RecordValueKind.Object) CollectTypedObject(value, path, "Object", states, depth);
        else if (value.Kind == RecordValueKind.Array)
            foreach (var item in value.Items)
                if (item.Kind == RecordValueKind.Object) CollectTypedObject(item, path, "Collection", states, depth);
    }

    private static void CollectTypedObject(RecordValue value, string path, string kind, Dictionary<string, RelationshipState> states, int depth)
    {
        Add(states, path, kind, value.Properties.Keys);
        foreach (var property in value.Properties)
            CollectTypedValue(property.Value, FieldPath.Parse(path).AppendProperty(property.Key).ToString(), states, depth + 1);
    }

    private static void Add(
        Dictionary<string, RelationshipState> states,
        string path,
        string kind,
        IEnumerable<string> targetFields)
    {
        string key = $"{kind}\n{path}";
        if (!states.TryGetValue(key, out var state))
        {
            state = new RelationshipState(path, kind);
            states[key] = state;
        }

        state.OccurrenceCount++;
        state.NonNullCount++;
        foreach (var field in targetFields)
        {
            state.AddTargetField(field);
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
        private readonly HashSet<string> _targetFields = new(StringComparer.Ordinal);

        public RelationshipState(string path, string kind)
        {
            Path = path;
            Kind = kind;
        }

        public string Path { get; }
        public string Kind { get; }
        public long OccurrenceCount { get; set; }
        public long NonNullCount { get; set; }
        public IReadOnlyCollection<string> TargetFields => _targetFields;

        public void AddTargetField(string field)
        {
            if (_targetFields.Count < MaxTargetFields)
                _targetFields.Add(field);
        }

        public ObjectRelationship ToResult() => new()
        {
            Path = Path,
            Kind = Kind,
            OccurrenceCount = OccurrenceCount,
            NonNullCount = NonNullCount,
            TargetFieldCount = _targetFields.Count,
            TargetFields = _targetFields.OrderBy(x => x, StringComparer.Ordinal).ToList()
        };
    }
}
