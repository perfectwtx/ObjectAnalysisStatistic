namespace ObjectAnalyzer.Analysis;

// ──────────────────────────────────────────────
// 语法分析（递归下降）
// ──────────────────────────────────────────────

internal abstract class FieldFilterNode
{
    public abstract bool Evaluate(Dictionary<string, object?> obj);
}
