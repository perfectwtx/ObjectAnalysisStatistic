using System.Globalization;

namespace ObjectAnalyzer.Analysis;

internal sealed class FieldFilterOperand
{
    public double? Number;
    public string? Text;
    public bool? Bool;
    public bool IsNull;
    public List<FieldFilterOperand>? List;

    public static FieldFilterOperand OfList(List<FieldFilterOperand> items) => new() { List = items };

    public override string ToString()
    {
        if (List != null) return "[" + string.Join(", ", List) + "]";
        if (IsNull) return "null";
        if (Number.HasValue) return Number.Value.ToString(CultureInfo.InvariantCulture);
        if (Bool.HasValue) return Bool.Value ? "true" : "false";
        return $"'{Text}'";
    }
}
