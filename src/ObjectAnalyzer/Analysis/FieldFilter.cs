using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字段过滤表达式：按字段值筛选需要纳入统计的对象。
/// 支持运算符 == != &gt; &lt; &gt;= &lt;= in not in like not like；
/// 组合关键字 AND / OR 与括号（大小写不敏感）。
/// 示例: "Age > 18 AND (Gender == 'Male' OR Status in [1,2]) OR Name like '张%'"
/// </summary>
public sealed class FieldFilter
{
    private readonly Node _root;

    public FieldFilter(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("过滤表达式不能为空。", nameof(expression));

        var tokens = Lex(expression);
        if (tokens.Count == 0)
            throw new FormatException($"过滤表达式无效: {expression}");

        int pos = 0;
        _root = Parse(expression, tokens, ref pos);
    }

    /// <summary>对单个对象求值过滤条件。</summary>
    public bool Evaluate(Dictionary<string, object?> obj) => _root.Evaluate(obj);

    public override string ToString() => _root.ToString() ?? "";

    // ──────────────────────────────────────────────
    // 词法分析
    // ──────────────────────────────────────────────

    private enum TokenKind
    {
        Identifier, Number, String, True, False, Null,
        OpEqual, OpNotEqual, OpGreater, OpLess, OpGreaterEqual, OpLessEqual,
        In, Not, Like, And, Or,
        LParen, RParen, LBracket, RBracket, Comma, End
    }

    private readonly struct Token
    {
        public Token(TokenKind kind, string? text = null, double? number = null)
        {
            Kind = kind;
            Text = text;
            Number = number;
        }

        public TokenKind Kind { get; }
        public string? Text { get; }
        public double? Number { get; }
    }

    private static List<Token> Lex(string expr)
    {
        var tokens = new List<Token>();
        int i = 0;
        int len = expr.Length;

        while (i < len)
        {
            char c = expr[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '(') { tokens.Add(new Token(TokenKind.LParen)); i++; continue; }
            if (c == ')') { tokens.Add(new Token(TokenKind.RParen)); i++; continue; }
            if (c == '[') { tokens.Add(new Token(TokenKind.LBracket)); i++; continue; }
            if (c == ']') { tokens.Add(new Token(TokenKind.RBracket)); i++; continue; }
            if (c == ',') { tokens.Add(new Token(TokenKind.Comma)); i++; continue; }

            if (c == '=' && i + 1 < len && expr[i + 1] == '=')
            { tokens.Add(new Token(TokenKind.OpEqual)); i += 2; continue; }
            if (c == '!' && i + 1 < len && expr[i + 1] == '=')
            { tokens.Add(new Token(TokenKind.OpNotEqual)); i += 2; continue; }
            if (c == '>' && i + 1 < len && expr[i + 1] == '=')
            { tokens.Add(new Token(TokenKind.OpGreaterEqual)); i += 2; continue; }
            if (c == '<' && i + 1 < len && expr[i + 1] == '=')
            { tokens.Add(new Token(TokenKind.OpLessEqual)); i += 2; continue; }
            if (c == '>') { tokens.Add(new Token(TokenKind.OpGreater)); i++; continue; }
            if (c == '<') { tokens.Add(new Token(TokenKind.OpLess)); i++; continue; }

            if (c == '\'' || c == '"')
            {
                char quote = c;
                int start = ++i;
                while (i < len && expr[i] != quote) i++;
                if (i >= len)
                    throw new FormatException($"字符串常量缺少结束引号: {expr}");
                string value = expr[start..i];
                i++; // 跳过结束引号
                tokens.Add(new Token(TokenKind.String, value));
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
                tokens.Add(new Token(TokenKind.Number, number: num));
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
            TokenKind kind = ident.ToLowerInvariant() switch
            {
                "and" => TokenKind.And,
                "or" => TokenKind.Or,
                "in" => TokenKind.In,
                "not" => TokenKind.Not,
                "like" => TokenKind.Like,
                "true" => TokenKind.True,
                "false" => TokenKind.False,
                "null" => TokenKind.Null,
                _ => TokenKind.Identifier
            };
            tokens.Add(kind == TokenKind.Identifier
                ? new Token(TokenKind.Identifier, ident)
                : new Token(kind));
            continue;
        }

        tokens.Add(new Token(TokenKind.End));
        return tokens;
    }

    private static bool IsBoundary(char c) =>
        char.IsWhiteSpace(c) || c is '(' or ')' or ',' or '=' or '!' or '<' or '>' or '\'' or '"';

    // ──────────────────────────────────────────────
    // 语法分析（递归下降）
    // ──────────────────────────────────────────────

    private abstract class Node
    {
        public abstract bool Evaluate(Dictionary<string, object?> obj);
    }

    private sealed class LogicalNode : Node
    {
        public bool IsAnd;
        public Node Left = null!;
        public Node Right = null!;

        public override bool Evaluate(Dictionary<string, object?> obj) =>
            IsAnd ? Left.Evaluate(obj) && Right.Evaluate(obj)
                  : Left.Evaluate(obj) || Right.Evaluate(obj);

        public override string ToString() =>
            $"({Left} {(IsAnd ? "AND" : "OR")} {Right})";
    }

    private enum CmpOp
    {
        Equal, NotEqual, Greater, Less, GreaterEqual, LessEqual,
        In, NotIn, Like, NotLike
    }

    private sealed class ComparisonNode : Node
    {
        public string Field = "";
        public CmpOp Op;
        public Operand Right = null!;

        public override bool Evaluate(Dictionary<string, object?> obj)
        {
            bool present = TryGetValueIgnoreCase(obj, Field, out object? value);
            if (!present) value = null;

            return Op switch
            {
                CmpOp.Equal => WeakEqual(value, Right),
                CmpOp.NotEqual => !WeakEqual(value, Right),
                CmpOp.In => Right.List?.Any(item => WeakEqual(value, item)) ?? false,
                CmpOp.NotIn => !(Right.List?.Any(item => WeakEqual(value, item)) ?? false),
                CmpOp.Like => LikeMatch(value, Right),
                CmpOp.NotLike => !LikeMatch(value, Right),
                _ => NumericCompare(value, Right) switch
                {
                    > 0 => Op is CmpOp.Greater or CmpOp.GreaterEqual,
                    < 0 => Op is CmpOp.Less or CmpOp.LessEqual,
                    _ => Op is CmpOp.GreaterEqual or CmpOp.LessEqual
                }
            };
        }

        public override string ToString()
        {
            string op = Op switch
            {
                CmpOp.Equal => "==", CmpOp.NotEqual => "!=",
                CmpOp.Greater => ">", CmpOp.Less => "<",
                CmpOp.GreaterEqual => ">=", CmpOp.LessEqual => "<=",
                CmpOp.In => "in", CmpOp.NotIn => "not in",
                CmpOp.Like => "like", CmpOp.NotLike => "not like",
                _ => "?"
            };
            return $"{Field} {op} {Right}";
        }
    }

    private sealed class Operand
    {
        public double? Number;
        public string? Text;
        public bool? Bool;
        public bool IsNull;
        public List<Operand>? List;

        public static Operand OfList(List<Operand> items) => new() { List = items };

        public override string ToString()
        {
            if (List != null) return "[" + string.Join(", ", List) + "]";
            if (IsNull) return "null";
            if (Number.HasValue) return Number.Value.ToString(CultureInfo.InvariantCulture);
            if (Bool.HasValue) return Bool.Value ? "true" : "false";
            return $"'{Text}'";
        }
    }

    private static Node Parse(string expr, List<Token> tokens, ref int pos)
    {
        var node = ParseOr(expr, tokens, ref pos);
        if (tokens[pos].Kind != TokenKind.End)
            throw new FormatException($"存在多余的标记 '{TokenText(tokens[pos])}' (表达式: {expr})");
        return node;
    }

    private static Node ParseOr(string expr, List<Token> tokens, ref int pos)
    {
        var node = ParseAnd(expr, tokens, ref pos);
        while (tokens[pos].Kind == TokenKind.Or)
        {
            pos++;
            var right = ParseAnd(expr, tokens, ref pos);
            node = new LogicalNode { IsAnd = false, Left = node, Right = right };
        }
        return node;
    }

    private static Node ParseAnd(string expr, List<Token> tokens, ref int pos)
    {
        var node = ParseComparison(expr, tokens, ref pos);
        while (tokens[pos].Kind == TokenKind.And)
        {
            pos++;
            var right = ParseComparison(expr, tokens, ref pos);
            node = new LogicalNode { IsAnd = true, Left = node, Right = right };
        }
        return node;
    }

    private static Node ParseComparison(string expr, List<Token> tokens, ref int pos)
    {
        if (tokens[pos].Kind == TokenKind.LParen)
        {
            pos++;
            var inner = ParseOr(expr, tokens, ref pos);
            if (tokens[pos].Kind != TokenKind.RParen)
                throw new FormatException($"期望 ')' 但遇到 '{tokens[pos].Text ?? tokens[pos].Kind.ToString()}' (表达式: {expr})");
            pos++;
            return inner;
        }

        if (tokens[pos].Kind != TokenKind.Identifier)
            throw new FormatException($"期望字段名但遇到 '{TokenText(tokens[pos])}' (表达式: {expr})");
        string field = tokens[pos].Text!;
        pos++;

        CmpOp op = ParseOp(expr, tokens, ref pos);
        Operand right = ParseOperand(expr, tokens, ref pos);

        return new ComparisonNode { Field = field, Op = op, Right = right };
    }

    private static CmpOp ParseOp(string expr, List<Token> tokens, ref int pos)
    {
        var kind = tokens[pos].Kind;
        switch (kind)
        {
            case TokenKind.OpEqual: pos++; return CmpOp.Equal;
            case TokenKind.OpNotEqual: pos++; return CmpOp.NotEqual;
            case TokenKind.OpGreater: pos++; return CmpOp.Greater;
            case TokenKind.OpLess: pos++; return CmpOp.Less;
            case TokenKind.OpGreaterEqual: pos++; return CmpOp.GreaterEqual;
            case TokenKind.OpLessEqual: pos++; return CmpOp.LessEqual;
            case TokenKind.In: pos++; return CmpOp.In;
            case TokenKind.Like: pos++; return CmpOp.Like;
            case TokenKind.Not:
                pos++;
                if (tokens[pos].Kind == TokenKind.In) { pos++; return CmpOp.NotIn; }
                if (tokens[pos].Kind == TokenKind.Like) { pos++; return CmpOp.NotLike; }
                throw new FormatException($"'not' 后只能接 'in' 或 'like' (表达式: {expr})");
            default:
                throw new FormatException($"期望比较运算符但遇到 '{TokenText(tokens[pos])}' (表达式: {expr})");
        }
    }

    private static Operand ParseOperand(string expr, List<Token> tokens, ref int pos)
    {
        var kind = tokens[pos].Kind;
        switch (kind)
        {
            case TokenKind.Number:
                var num = tokens[pos].Number!.Value;
                pos++;
                return new Operand { Number = num };
            case TokenKind.String:
                var text = tokens[pos].Text!;
                pos++;
                return new Operand { Text = text };
            case TokenKind.True:
                pos++;
                return new Operand { Bool = true };
            case TokenKind.False:
                pos++;
                return new Operand { Bool = false };
            case TokenKind.Null:
                pos++;
                return new Operand { IsNull = true };
            case TokenKind.LBracket:
            {
                pos++;
                var items = new List<Operand>();
                if (tokens[pos].Kind == TokenKind.RBracket) { pos++; return Operand.OfList(items); }
                while (true)
                {
                    items.Add(ParseOperand(expr, tokens, ref pos));
                    if (tokens[pos].Kind == TokenKind.Comma) { pos++; continue; }
                    if (tokens[pos].Kind == TokenKind.RBracket) { pos++; break; }
                    throw new FormatException($"列表缺少 ']' 或存在非法元素 (表达式: {expr})");
                }
                return Operand.OfList(items);
            }
            default:
                throw new FormatException($"期望操作数但遇到 '{TokenText(tokens[pos])}' (表达式: {expr})");
        }
    }

    private static string TokenText(Token t) => t.Text ?? t.Kind.ToString();

    // ──────────────────────────────────────────────
    // 求值辅助：弱类型比较 / 数值比较 / like 匹配
    // ──────────────────────────────────────────────

    private static bool TryGetNumber(object? value, out double num)
    {
        num = 0;
        if (value == null) return false;
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

    private static bool WeakEqual(object? value, Operand operand)
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

    private static bool TryGetValueIgnoreCase(Dictionary<string, object?> obj, string field, out object? value)
    {
        if (obj.TryGetValue(field, out value))
            return true;
        foreach (var kv in obj)
        {
            if (string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    /// <summary>比较结果: 正数（更大）、负数（更小）、0（相等）。数值不适用时按字符串比较。</summary>
    private static int NumericCompare(object? value, Operand operand)
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

    private static bool LikeMatch(object? value, Operand operand)
    {
        if (value == null || operand.Text == null)
            return false;
        string pattern = "^" + Regex.Escape(operand.Text)
            .Replace("%", ".*")
            .Replace("_", ".") + "$";
        return Regex.IsMatch(value.ToString()!, pattern, RegexOptions.IgnoreCase);
    }
}