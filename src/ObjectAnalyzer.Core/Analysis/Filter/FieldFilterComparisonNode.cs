using System.Globalization;
using System.Text.RegularExpressions;

namespace ObjectAnalyzer.Analysis;

internal sealed class FieldFilterComparisonNode : FieldFilterNode
{
    public string Field = "";
    public FieldFilterCmpOp Op;
    public FieldFilterOperand Right = null!;

    public override bool Evaluate(Dictionary<string, object?> obj)
    {
        bool present = obj.TryGetValue(Field, out object? value);
        if (!present) value = null;

        return Op switch
        {
            FieldFilterCmpOp.Equal => WeakEqual(value, Right),
            FieldFilterCmpOp.NotEqual => !WeakEqual(value, Right),
            FieldFilterCmpOp.In => Right.List?.Any(item => WeakEqual(value, item)) ?? false,
            FieldFilterCmpOp.NotIn => !(Right.List?.Any(item => WeakEqual(value, item)) ?? false),
            FieldFilterCmpOp.Like => LikeMatch(value, Right),
            FieldFilterCmpOp.NotLike => !LikeMatch(value, Right),
            _ => NumericCompare(value, Right) switch
            {
                > 0 => Op is FieldFilterCmpOp.Greater or FieldFilterCmpOp.GreaterEqual,
                < 0 => Op is FieldFilterCmpOp.Less or FieldFilterCmpOp.LessEqual,
                _ => Op is FieldFilterCmpOp.GreaterEqual or FieldFilterCmpOp.LessEqual
            }
        };
    }

    public override string ToString()
    {
        string op = Op switch
        {
            FieldFilterCmpOp.Equal => "==", FieldFilterCmpOp.NotEqual => "!=",
            FieldFilterCmpOp.Greater => ">", FieldFilterCmpOp.Less => "<",
            FieldFilterCmpOp.GreaterEqual => ">=", FieldFilterCmpOp.LessEqual => "<=",
            FieldFilterCmpOp.In => "in", FieldFilterCmpOp.NotIn => "not in",
            FieldFilterCmpOp.Like => "like", FieldFilterCmpOp.NotLike => "not like",
            _ => "?"
        };
        return $"{Field} {op} {Right}";
    }

    // ──────────────────────────────────────────────
    // 求值辅助：弱类型比较 / 数值比较 / like 匹配
    // ──────────────────────────────────────────────

    private static bool TryGetNumber(object? value, out double num)
    {
        num = 0;
        if (value == null) return false;
        if (value is RecordValue typed) return typed.TryGetDouble(out num);
        if (value is double d) { num = d; return true; }
        if (value is float f) { num = f; return true; }
        if (value is int i) { num = i; return true; }
        if (value is long l) { num = l; return true; }
        if (value is short s) { num = s; return true; }
        if (value is byte b) { num = b; return true; }
        if (value is uint ui) { num = ui; return true; }
        if (value is ulong ul) { num = ul; return true; }
        if (value is decimal m) { num = (double)m; return true; }
        if (value is string str && double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        { num = parsed; return true; }
        return false;
    }

    private static bool WeakEqual(object? value, FieldFilterOperand operand)
    {
        if (operand.IsNull)
            return value == null;
        if (operand.Bool.HasValue)
            return value is bool b && b == operand.Bool.Value;
        if (operand.Number.HasValue)
            return TryGetNumber(value, out double vnum)
                && Math.Abs(vnum - operand.Number.Value) < 1e-12;
        return value?.ToString() == operand.Text;
    }

    /// <summary>比较结果: 正数（更大）、负数（更小）、0（相等）。数值不适用时按字符串比较。</summary>
    private static int NumericCompare(object? value, FieldFilterOperand operand)
    {
        if (TryGetNumber(value, out double vnum)
            && (operand.Number.HasValue || operand.IsNull))
        {
            if (operand.IsNull) return vnum == 0 ? 0 : (vnum > 0 ? 1 : -1);
            return vnum.CompareTo(operand.Number!.Value);
        }
        string left = value?.ToString() ?? "";
        string right = operand.Text ?? operand.Number?.ToString(CultureInfo.InvariantCulture) ?? "";
        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static bool LikeMatch(object? value, FieldFilterOperand operand)
    {
        if (value == null || operand.Text == null)
            return false;
        string pattern = "^" + Regex.Escape(operand.Text)
            .Replace("%", ".*")
            .Replace("_", ".") + "$";
        return Regex.IsMatch(value.ToString()!, pattern, RegexOptions.IgnoreCase);
    }
}
