namespace ObjectAnalyzer.Data;

/// <summary>One normalization boundary for JSON, object graphs and typed callers.</summary>
public static class RecordNormalizer
{
    public static IReadOnlyDictionary<FieldPath, RecordValue> Normalize(RecordValue record, bool flatten = true)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Kind != RecordValueKind.Object) throw new ArgumentException("A record must be an object.", nameof(record));
        var fields = new Dictionary<FieldPath, RecordValue>();
        foreach (var property in record.Properties)
        {
            var path = FieldPath.Property(property.Key);
            if (flatten) FlattenInto(property.Value, path, fields);
            else fields.Add(path, property.Value);
        }
        return fields;
    }

    public static IReadOnlyDictionary<FieldPath, RecordValue> Flatten(RecordValue value, FieldPath prefix)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(prefix);
        var fields = new Dictionary<FieldPath, RecordValue>();
        FlattenInto(value, prefix, fields);
        return fields;
    }

    private static void FlattenInto(RecordValue value, FieldPath path, Dictionary<FieldPath, RecordValue> fields)
    {
        if (value.Kind == RecordValueKind.Object && value.Properties.Count > 0)
        {
            foreach (var pair in value.Properties) FlattenInto(pair.Value, path.AppendProperty(pair.Key), fields);
        }
        else if (value.Kind == RecordValueKind.Array && value.Items.Length > 0)
        {
            for (int i = 0; i < value.Items.Length; i++) FlattenInto(value.Items[i], path.AppendIndex(i), fields);
        }
        else fields.Add(path, value);
    }
}
