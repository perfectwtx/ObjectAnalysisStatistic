using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// C# 源码 → AnalysisRules 提取器（Roslyn SyntaxWalker，仅静态解析，不编译、不参与分析管线）。
/// 映射：属性/公共字段声明类型 → primaryType（数值基元 → Number、bool → Boolean、
/// DateTime/DateTimeOffset/DateOnly/TimeOnly → DateTime、string/Guid/char → String）；
/// 文件内枚举 → enumValues（成员名）；非空值类型与 [Required] → required；
/// [Range] → minValue/maxValue、[RegularExpression] → pattern、[JsonPropertyName] → 字段键；
/// 属性初始化器字面量 → defaultValue。复杂类型（集合/嵌套 POCO）跳过并记录警告。
/// </summary>
public static class RulesFromCSharp
{
    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal)
    {
        "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte", "float", "double", "decimal"
    };
    private static readonly HashSet<string> StringTypes = new(StringComparer.Ordinal) { "string", "String", "Guid", "char" };
    private static readonly HashSet<string> DateTimeTypes = new(StringComparer.Ordinal)
    {
        "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly"
    };

    public static AnalysisRules Extract(string sourceCode, string? className = null, List<string>? warnings = null)
    {
        warnings ??= new List<string>();
        var root = CSharpSyntaxTree.ParseText(sourceCode).GetCompilationUnitRoot();

        var enums = root.DescendantNodes().OfType<EnumDeclarationSyntax>()
            .ToDictionary(e => e.Identifier.Text, e => e.Members.Select(m => m.Identifier.Text).ToList());

        var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax)
            .ToList();
        if (types.Count == 0)
            throw new InvalidOperationException("源码中未找到可用的类（class / record）声明。");

        TypeDeclarationSyntax target;
        if (!string.IsNullOrEmpty(className))
        {
            target = types.FirstOrDefault(t => t.Identifier.Text == className)
                ?? throw new InvalidOperationException(
                    $"源码中未找到名为 {className} 的类。可用: {string.Join(", ", types.Select(t => t.Identifier.Text).Distinct())}");
        }
        else if (types.Count == 1)
        {
            target = types[0];
        }
        else
        {
            throw new InvalidOperationException(
                $"源码包含多个类，请用 --class 指定目标: {string.Join(", ", types.Select(t => t.Identifier.Text).Distinct())}");
        }

        var rules = new AnalysisRules();
        foreach (var prop in target.Members.OfType<PropertyDeclarationSyntax>())
        {
            ProcessMember(prop.Identifier.Text, prop.Type, prop.AttributeLists, prop.Modifiers,
                prop.Initializer?.Value, prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true,
                enums, rules.Fields, warnings);
        }
        foreach (var field in target.Members.OfType<FieldDeclarationSyntax>())
        {
            bool isPublic = field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
            bool isConst = field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword));
            foreach (var variable in field.Declaration.Variables)
            {
                ProcessMember(variable.Identifier.Text, field.Declaration.Type, field.AttributeLists,
                    field.Modifiers, variable.Initializer?.Value,
                    isPublic && !isConst && !field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)),
                    enums, rules.Fields, warnings);
            }
        }
        return rules;
    }

    private static void ProcessMember(
        string memberName,
        TypeSyntax typeSyntax,
        SyntaxList<AttributeListSyntax> attributeLists,
        SyntaxTokenList modifiers,
        ExpressionSyntax? initializer,
        bool isVisible,
        Dictionary<string, List<string>> enums,
        Dictionary<string, FieldRule> fields,
        List<string> warnings)
    {
        if (!isVisible || modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            return;

        string typeText = typeSyntax.ToString().Trim();
        string? key = null, pattern = null;
        double? minValue = null, maxValue = null;
        bool requiredAttr = false;

        foreach (var attrList in attributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();
                var simpleName = name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
                var argArray = attr.ArgumentList?.Arguments.ToArray() ?? Array.Empty<AttributeArgumentSyntax>();
                switch (simpleName)
                {
                    case "JsonPropertyName":
                        if (argArray.Length > 0 && argArray[0].Expression is LiteralExpressionSyntax keyLit
                            && keyLit.IsKind(SyntaxKind.StringLiteralExpression))
                            key = keyLit.Token.ValueText;
                        break;
                    case "Required":
                        requiredAttr = true;
                        break;
                    case "RegularExpression":
                        if (argArray.Length > 0 && argArray[0].Expression is LiteralExpressionSyntax patLit
                            && patLit.IsKind(SyntaxKind.StringLiteralExpression))
                            pattern = patLit.Token.ValueText;
                        break;
                    case "Range":
                        if (argArray.Length == 2)
                        {
                            double? lo = TryGetNumeric(argArray[0].Expression);
                            double? hi = TryGetNumeric(argArray[1].Expression);
                            // 兼容 (typeof(...), "min", "max") 字符串形态
                            if (lo is null && argArray[0].Expression is LiteralExpressionSyntax loLit
                                && loLit.IsKind(SyntaxKind.StringLiteralExpression)
                                && double.TryParse(loLit.Token.ValueText, out var loParsed))
                                lo = loParsed;
                            if (hi is null && argArray[1].Expression is LiteralExpressionSyntax hiLit
                                && hiLit.IsKind(SyntaxKind.StringLiteralExpression)
                                && double.TryParse(hiLit.Token.ValueText, out var hiParsed))
                                hi = hiParsed;
                            minValue = lo;
                            maxValue = hi;
                        }
                        break;
                }
            }
        }

        var mapped = MapType(typeText, enums, out bool isNullable, out var enumValues);
        if (mapped is null)
        {
            warnings.Add($"{memberName}（{typeText}）为复杂类型，无法静态映射，已跳过。");
            return;
        }

        var rule = new FieldRule { PrimaryType = mapped };
        if (enumValues is { Count: > 0 })
            rule.EnumValues = enumValues.Select(v => System.Text.Json.JsonSerializer.SerializeToElement(v)).ToList();
        bool isValueType = NumericTypes.Contains(Normalize(typeText)) || Normalize(typeText) is "bool" || DateTimeTypes.Contains(Normalize(typeText)) || enumValues is not null;
        if (requiredAttr || (isValueType && !isNullable))
            rule.Required = true;
        if (minValue.HasValue) rule.MinValue = minValue;
        if (maxValue.HasValue) rule.MaxValue = maxValue;
        if (!string.IsNullOrEmpty(pattern)) rule.Pattern = pattern;

        if (initializer is not null)
        {
            var defaultValue = TryGetLiteral(initializer);
            if (defaultValue is not null)
                rule.DefaultValue = defaultValue;
            else
                warnings.Add($"{memberName} 的初始化器不是字面量，defaultValue 未生成。");
        }

        fields[key ?? memberName] = rule;
    }

    private static string Normalize(string typeText)
    {
        var t = typeText.Trim();
        if (t.EndsWith('?')) t = t[..^1].Trim();
        if (t.StartsWith("Nullable<", StringComparison.Ordinal) && t.EndsWith('>')) t = t["Nullable<".Length..^1].Trim();
        var dot = t.LastIndexOf('.');
        return dot >= 0 ? t[(dot + 1)..] : t;
    }

    private static string? MapType(
        string typeText,
        Dictionary<string, List<string>> enums,
        out bool isNullable,
        out List<string>? enumValues)
    {
        isNullable = false;
        enumValues = null;
        var t = typeText.Trim();
        if (t.EndsWith('?'))
        {
            isNullable = true;
            t = t[..^1].Trim();
        }
        if (t.StartsWith("Nullable<", StringComparison.Ordinal) && t.EndsWith('>'))
        {
            isNullable = true;
            t = t["Nullable<".Length..^1].Trim();
        }
        var simple = Normalize(t);
        if (simple.StartsWith("System.", StringComparison.Ordinal))
            simple = simple["System.".Length..];

        if (NumericTypes.Contains(simple)) return "Number";
        if (simple is "bool" or "Boolean") return "Boolean";
        if (StringTypes.Contains(simple)) return "String";
        if (DateTimeTypes.Contains(simple)) return "DateTime";
        if (enums.TryGetValue(simple, out var values))
        {
            enumValues = values;
            return "String";
        }
        return null;
    }

    /// <summary>从表达式提取字面量默认值：数值（含负号）、字符串、true/false、null。</summary>
    private static object? TryGetLiteral(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax lit)
        {
            if (lit.IsKind(SyntaxKind.NumericLiteralExpression)) return lit.Token.Value;
            if (lit.IsKind(SyntaxKind.StringLiteralExpression)) return lit.Token.ValueText;
            if (lit.IsKind(SyntaxKind.TrueLiteralExpression)) return true;
            if (lit.IsKind(SyntaxKind.FalseLiteralExpression)) return false;
            if (lit.IsKind(SyntaxKind.NullLiteralExpression)) return null;
        }
        if (expression is PrefixUnaryExpressionSyntax unary
            && unary.IsKind(SyntaxKind.UnaryMinusExpression)
            && unary.Operand is LiteralExpressionSyntax num
            && num.IsKind(SyntaxKind.NumericLiteralExpression)
            && num.Token.Value is IConvertible convertible)
        {
            return -convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static double? TryGetNumeric(ExpressionSyntax expression) => TryGetLiteral(expression) as IConvertible
        is null
            ? null
            : Convert.ToDouble(TryGetLiteral(expression), System.Globalization.CultureInfo.InvariantCulture);
}
