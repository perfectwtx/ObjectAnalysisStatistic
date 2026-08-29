namespace ObjectAnalyzer.Analysis;

internal sealed class FieldFilterLogicalNode : FieldFilterNode
{
    public bool IsAnd;
    public FieldFilterNode Left = null!;
    public FieldFilterNode Right = null!;

    public override bool Evaluate(Dictionary<string, object?> obj) =>
        IsAnd ? Left.Evaluate(obj) && Right.Evaluate(obj)
              : Left.Evaluate(obj) || Right.Evaluate(obj);

    public override string ToString() =>
        $"({Left} {(IsAnd ? "AND" : "OR")} {Right})";
}
