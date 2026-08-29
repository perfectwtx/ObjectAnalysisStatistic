namespace ObjectAnalyzer.Analysis;

internal readonly struct FieldFilterToken(FieldFilterTokenKind kind, string? text = null, double? number = null)
{
    public FieldFilterTokenKind Kind { get; } = kind;
    public string? Text { get; } = text;
    public double? Number { get; } = number;
}
