using System.Globalization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字段过滤表达式：按字段值筛选需要纳入统计的对象。
/// 支持运算符 == != &gt; &lt; &gt;= &lt;= in not in like not like；
/// 组合关键字 AND / OR 与括号（大小写不敏感）。
/// 示例: "Age > 18 AND (Gender == 'Male' OR Status in [1,2]) OR Name like '张%'"
/// </summary>
public sealed class FieldFilter(string expression)
{
    private readonly FieldFilterNode _root = BuildRoot(expression);

    /// <summary>对单个对象求值过滤条件。</summary>
    public bool Evaluate(Dictionary<string, object?> obj) => _root.Evaluate(obj);

    public override string ToString() => _root.ToString() ?? "";

    private static FieldFilterNode BuildRoot(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("过滤表达式不能为空。", nameof(expression));

        var tokens = Lex(expression);
        if (tokens.Count == 0)
            throw new FormatException($"过滤表达式无效: {expression}");

        int pos = 0;
        return Parse(expression, tokens, ref pos);
    }

    private static List<FieldFilterToken> Lex(string expr)
    {
        var tokens = new List<FieldFilterToken>();
        int i = 0;
        int len = expr.Length;

        while (i < len)
        {
            char c = expr[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '(') { tokens.Add(new FieldFilterToken(FieldFilterTokenKind.LParen)); i++; continue; }
            if (c == ')') { tokens.Add(new FieldFilterToken(FieldFilterTokenKind.RParen)); i++; continue; }
            if (c == '[') { tokens.Add(new FieldFilterToken(FieldFilterTokenKind.LBracket)); i++; continue; }
            if (c == ']') { tokens.Add(new FieldFilterToken(FieldFilterTokenKind.RBracket)); i++; continue; }
            if (c == ',') { tokens.Add(new FieldFilterToken(FieldFilterTokenKind.Comma)); i++; continue; }

            if (c == '=' && i + 1 < len && expr[i + 1] == '=')
            { tokens.Add(new FieldFilterToken(FieldFilterTokenKind.OpEqual)); i += 2; continue; }
            if (c == '!' && i + 1 < len && expr[i + 1] == '=')
            { tokens.Add(new FieldFilterToken(FieldFilterTokenKind.OpNotEqual)); i += 2; continue; }
            if (c == '>' && i + 1 < len && expr[i + 1] == '=')
            { tokens.Add(new FieldFilterToken(FieldFilterTokenKind.OpGreaterEqual)); i += 2; continue; }
            if (c == '<' && i + 1 < len && expr[i + 1] == '=')
            { tokens.Add(new FieldFilterToken(FieldFilterTokenKind.OpLessEqual)); i += 2; continue; }
            if (c == '>') { tokens.Add(new FieldFilterToken(FieldFilterTokenKind.OpGreater)); i++; continue; }
            if (c == '<') { tokens.Add(new FieldFilterToken(FieldFilterTokenKind.OpLess)); i++; continue; }

            if (c == '\'' || c == '"')
            {
                char quote = c;
                int start = ++i;
                while (i < len && expr[i] != quote) i++;
                if (i >= len)
                    throw new FormatException($"字符串常量缺少结束引号: {expr}");
                string value = expr[start..i];
                i++; // 跳过结束引号
                tokens.Add(new FieldFilterToken(FieldFilterTokenKind.String, value));
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < len && char.IsDigit(expr[i + 1])))
            {
                int start = i;
                if (c == '-') i++;
                while (i < len && (char.IsDigit(expr[i]) || expr[i] == '.')) i++;
                string numText = expr[start..i];
                if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                    throw new FormatException($"无效的数字常量: {numText}");
                tokens.Add(new FieldFilterToken(FieldFilterTokenKind.Number, number: num));
                continue;
            }

            // 字段名（允许 a.b.c、tags[0] 等扁平化名称）
            int idStart = i;
            while (i < len && !IsBoundary(expr[i])) i++;
            while (i < len && expr[i] == '[')
            {
                int j = i + 1;
                while (j < len && char.IsDigit(expr[j])) j++;
                if (j < len && expr[j] == ']') i = j + 1;
                else break;
            }
            if (i == idStart)
                throw new FormatException($"无法解析的字符 '{c}' (位置 {idStart})");

            string ident = expr[idStart..i];
            FieldFilterTokenKind kind = ident.ToLowerInvariant() switch
            {
                "and" => FieldFilterTokenKind.And,
                "or" => FieldFilterTokenKind.Or,
                "in" => FieldFilterTokenKind.In,
                "not" => FieldFilterTokenKind.Not,
                "like" => FieldFilterTokenKind.Like,
                "true" => FieldFilterTokenKind.True,
                "false" => FieldFilterTokenKind.False,
                "null" => FieldFilterTokenKind.Null,
                _ => FieldFilterTokenKind.Identifier
            };
            tokens.Add(kind == FieldFilterTokenKind.Identifier
                ? new FieldFilterToken(FieldFilterTokenKind.Identifier, ident)
                : new FieldFilterToken(kind));
            continue;
        }

        tokens.Add(new FieldFilterToken(FieldFilterTokenKind.End));
        return tokens;
    }

    private static bool IsBoundary(char c) =>
        char.IsWhiteSpace(c) || c is '(' or ')' or ',' or '=' or '!' or '<' or '>' or '\'' or '"';


    private static FieldFilterNode Parse(string expr, List<FieldFilterToken> tokens, ref int pos)
    {
        var node = ParseOr(expr, tokens, ref pos);
        if (tokens[pos].Kind != FieldFilterTokenKind.End)
            throw new FormatException($"存在多余的标记 '{TokenText(tokens[pos])}' (表达式: {expr})");
        return node;
    }

    private static FieldFilterNode ParseOr(string expr, List<FieldFilterToken> tokens, ref int pos)
    {
        var node = ParseAnd(expr, tokens, ref pos);
        while (tokens[pos].Kind == FieldFilterTokenKind.Or)
        {
            pos++;
            var right = ParseAnd(expr, tokens, ref pos);
            node = new FieldFilterLogicalNode { IsAnd = false, Left = node, Right = right };
        }
        return node;
    }

    private static FieldFilterNode ParseAnd(string expr, List<FieldFilterToken> tokens, ref int pos)
    {
        var node = ParseComparison(expr, tokens, ref pos);
        while (tokens[pos].Kind == FieldFilterTokenKind.And)
        {
            pos++;
            var right = ParseComparison(expr, tokens, ref pos);
            node = new FieldFilterLogicalNode { IsAnd = true, Left = node, Right = right };
        }
        return node;
    }

    private static FieldFilterNode ParseComparison(string expr, List<FieldFilterToken> tokens, ref int pos)
    {
        if (tokens[pos].Kind == FieldFilterTokenKind.LParen)
        {
            pos++;
            var inner = ParseOr(expr, tokens, ref pos);
            if (tokens[pos].Kind != FieldFilterTokenKind.RParen)
                throw new FormatException($"期望 ')' 但遇到 '{tokens[pos].Text ?? tokens[pos].Kind.ToString()}' (表达式: {expr})");
            pos++;
            return inner;
        }

        if (tokens[pos].Kind != FieldFilterTokenKind.Identifier)
            throw new FormatException($"期望字段名但遇到 '{TokenText(tokens[pos])}' (表达式: {expr})");
        string field = tokens[pos].Text!;
        pos++;

        FieldFilterCmpOp op = ParseOp(expr, tokens, ref pos);
        FieldFilterOperand right = ParseOperand(expr, tokens, ref pos);

        return new FieldFilterComparisonNode { Field = field, Op = op, Right = right };
    }

    private static FieldFilterCmpOp ParseOp(string expr, List<FieldFilterToken> tokens, ref int pos)
    {
        var kind = tokens[pos].Kind;
        switch (kind)
        {
            case FieldFilterTokenKind.OpEqual: pos++; return FieldFilterCmpOp.Equal;
            case FieldFilterTokenKind.OpNotEqual: pos++; return FieldFilterCmpOp.NotEqual;
            case FieldFilterTokenKind.OpGreater: pos++; return FieldFilterCmpOp.Greater;
            case FieldFilterTokenKind.OpLess: pos++; return FieldFilterCmpOp.Less;
            case FieldFilterTokenKind.OpGreaterEqual: pos++; return FieldFilterCmpOp.GreaterEqual;
            case FieldFilterTokenKind.OpLessEqual: pos++; return FieldFilterCmpOp.LessEqual;
            case FieldFilterTokenKind.In: pos++; return FieldFilterCmpOp.In;
            case FieldFilterTokenKind.Like: pos++; return FieldFilterCmpOp.Like;
            case FieldFilterTokenKind.Not:
                pos++;
                if (tokens[pos].Kind == FieldFilterTokenKind.In) { pos++; return FieldFilterCmpOp.NotIn; }
                if (tokens[pos].Kind == FieldFilterTokenKind.Like) { pos++; return FieldFilterCmpOp.NotLike; }
                throw new FormatException($"'not' 后只能接 'in' 或 'like' (表达式: {expr})");
            default:
                throw new FormatException($"期望比较运算符但遇到 '{TokenText(tokens[pos])}' (表达式: {expr})");
        }
    }

    private static FieldFilterOperand ParseOperand(string expr, List<FieldFilterToken> tokens, ref int pos)
    {
        var kind = tokens[pos].Kind;
        switch (kind)
        {
            case FieldFilterTokenKind.Number:
                var num = tokens[pos].Number!.Value;
                pos++;
                return new FieldFilterOperand { Number = num };
            case FieldFilterTokenKind.String:
                var text = tokens[pos].Text!;
                pos++;
                return new FieldFilterOperand { Text = text };
            case FieldFilterTokenKind.True:
                pos++;
                return new FieldFilterOperand { Bool = true };
            case FieldFilterTokenKind.False:
                pos++;
                return new FieldFilterOperand { Bool = false };
            case FieldFilterTokenKind.Null:
                pos++;
                return new FieldFilterOperand { IsNull = true };
            case FieldFilterTokenKind.LBracket:
            {
                pos++;
                var items = new List<FieldFilterOperand>();
                if (tokens[pos].Kind == FieldFilterTokenKind.RBracket) { pos++; return FieldFilterOperand.OfList(items); }
                while (true)
                {
                    items.Add(ParseOperand(expr, tokens, ref pos));
                    if (tokens[pos].Kind == FieldFilterTokenKind.Comma) { pos++; continue; }
                    if (tokens[pos].Kind == FieldFilterTokenKind.RBracket) { pos++; break; }
                    throw new FormatException($"列表缺少 ']' 或存在非法元素 (表达式: {expr})");
                }
                return FieldFilterOperand.OfList(items);
            }
            default:
                throw new FormatException($"期望操作数但遇到 '{TokenText(tokens[pos])}' (表达式: {expr})");
        }
    }

    private static string TokenText(FieldFilterToken t) => t.Text ?? t.Kind.ToString();

}