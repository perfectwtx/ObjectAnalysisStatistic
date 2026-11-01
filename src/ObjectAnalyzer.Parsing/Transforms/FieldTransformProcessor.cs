using ObjectAnalyzer.Analysis.Pipeline;
using System.Collections.Concurrent;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字段选择、值转换与过滤处理器。
/// 支持：仅统计指定字段（field selection）、按规则文件对字段值做转换（JWT 解码 / AES 解密 / Base64 / URL 解码），
/// 以及按过滤表达式筛选对象（--where / 规则文件 filter）；过滤在字段选择与值转换之后执行。
/// 转换失败时保留原始值，并记录失败次数到 FieldStatistic.TransformFailureCount。
/// </summary>
public sealed partial class FieldTransformProcessor : IRecordTransformer
{
    private readonly Dictionary<string, FieldTransform> _transforms = new();
    private readonly HashSet<string>? _selectedFields;
    private readonly ConcurrentDictionary<string, long> _failures = new();
    private readonly FieldFilter? _filter;

    /// <summary>
    /// 基于规则文件构建处理器。selectedFields 优先级高于 <c>runtime.selectedFields</c>；
    /// filterExpression 优先级高于 <c>runtime.filter</c>（为空字符串时回退到 runtime.filter）。
    /// </summary>
    public FieldTransformProcessor(AnalysisRules? rules, IEnumerable<string>? selectedFields = null, string? filterExpression = null)
    {
        var explicitSelection = selectedFields as IReadOnlyCollection<string>;
        if (explicitSelection is { Count: > 0 })
        {
            _selectedFields = new HashSet<string>(explicitSelection.Select(x => FieldPath.Parse(x).ToString()), StringComparer.Ordinal);
        }
        else if (TryGetSelectedFields(rules?.Runtime) is { Count: > 0 } ruleFields)
        {
            _selectedFields = new HashSet<string>(ruleFields.Select(x => FieldPath.Parse(x).ToString()), StringComparer.Ordinal);
        }

        string? ruleFilter = TryGetString(rules?.Runtime, "filter");
        string? filter = string.IsNullOrWhiteSpace(filterExpression) ? ruleFilter : filterExpression;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            _filter = new FieldFilter(filter);
        }

        if (rules != null)
        {
            foreach (var (field, rule) in rules.Fields)
            {
                if (rule.Transform == null) continue;
                ValidateTransform(field, rule.Transform);
                _transforms[FieldPath.Parse(field).ToString()] = rule.Transform;
            }
        }
    }

    private static List<string>? TryGetSelectedFields(System.Text.Json.Nodes.JsonObject? runtime)
    {
        if (runtime?["selectedFields"] is System.Text.Json.Nodes.JsonArray arr)
        {
            return arr.Select(n => n?.GetValue<string>() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        }
        return null;
    }

    private static string? TryGetString(System.Text.Json.Nodes.JsonObject? runtime, string key)
    {
        if (runtime?[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s;
        }
        return null;
    }

    public bool HasSelectionOrTransforms => _selectedFields != null || _transforms.Count > 0 || _filter != null;

    /// <summary>
    /// 包装对象序列：惰性应用字段选择、值转换与过滤（保持流式，不物化）。
    /// </summary>
    public IEnumerable<Dictionary<string, object?>> Wrap(IEnumerable<Dictionary<string, object?>> objects)
    {
        if (!HasSelectionOrTransforms)
            return objects;
        return TransformAndSelect(objects);
    }

    private bool IsSelectedKey(string key) => IsSelectedKey(_selectedFields, key);

    private IEnumerable<Dictionary<string, object?>> TransformAndSelect(
        IEnumerable<Dictionary<string, object?>> objects)
    {
        foreach (var obj in objects)
        {
            var result = new Dictionary<string, object?>(obj.Count);
            foreach (var kv in obj)
            {
                if (_selectedFields != null && !IsSelectedKey(kv.Key))
                    continue;

                if (_transforms.TryGetValue(kv.Key, out var transform)
                    && TryApply(transform, kv.Value, out var newValue))
                {
                    // parse: 将转换结果解析为 JSON 并展开为嵌套字段（如 payload.city / payload[0]）
                    if (TryUnwrapJson(kv.Key, transform, newValue, result, _selectedFields))
                        continue;
                    result[kv.Key] = newValue;
                }
                else
                {
                    // 无转换，或转换失败（保留原始值并记录失败）
                    if (transform != null)
                        _failures.AddOrUpdate(kv.Key, 1, (_, c) => c + 1);
                    result[kv.Key] = kv.Value;
                }
            }

            // 过滤在字段选择与值转换之后执行
            if (_filter != null && !_filter.Evaluate(result))
                continue;

            yield return result;
        }
    }

    /// <summary>
    /// 分析完成后，将转换失败次数与转换类型回填到统计结果。
    /// </summary>
    public void ApplyFailures(AnalysisResult result)
    {
        foreach (var stat in result.FieldStatistics)
        {
            if (_failures.TryGetValue(stat.FieldName, out long count))
                stat.TransformFailureCount = count;
            if (_transforms.TryGetValue(stat.FieldName, out var transform))
                stat.TransformType = transform.Type;
        }
    }

    private static bool IsSelectedKey(HashSet<string>? selectedFields, string key)
    {
        if (selectedFields is null) return true;
        var path = FieldPath.Parse(key);
        return selectedFields.Any(selected => path.IsPrefixOf(FieldPath.Parse(selected)));
    }

    private static bool HasNestedSelection(HashSet<string> selectedFields, string key)
    {
        var path = FieldPath.Parse(key);
        return selectedFields.Any(selected => FieldPath.Parse(selected) is var child
            && child.Segments.Length > path.Segments.Length && path.IsPrefixOf(child));
    }

    /// <summary>供测试与诊断使用的字段名集合（排序后）。</summary>
    internal IReadOnlyCollection<string>? GetSelectedFields() => _selectedFields;

    internal IReadOnlyDictionary<string, FieldTransform> GetTransforms() => _transforms;

    /// <summary>供测试与诊断使用的过滤表达式（null 表示未启用过滤）。</summary>
    internal FieldFilter? GetFilter() => _filter;
}
