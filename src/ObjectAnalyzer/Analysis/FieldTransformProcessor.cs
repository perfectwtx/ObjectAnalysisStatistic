using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ObjectAnalyzer.Parsing;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字段选择、值转换与过滤处理器。
/// 支持：仅统计指定字段（field selection）、按规则文件对字段值做转换（JWT 解码 / AES 解密 / Base64 / URL 解码），
/// 以及按过滤表达式筛选对象（--where / 规则文件 filter）；过滤在字段选择与值转换之后执行。
/// 转换失败时保留原始值，并记录失败次数到 FieldStatistic.TransformFailureCount。
/// </summary>
public sealed class FieldTransformProcessor
{
    private readonly Dictionary<string, FieldTransform> _transforms = new();
    private readonly HashSet<string>? _selectedFields;
    private readonly ConcurrentDictionary<string, long> _failures = new();
    private readonly FieldFilter? _filter;

    /// <summary>
    /// 基于规则文件构建处理器。selectedFields 优先级高于 rules.SelectedFields；
    /// filterExpression 优先级高于 rules.Filter（为空字符串时回退到 rules.Filter）。
    /// </summary>
    public FieldTransformProcessor(AnalysisRules? rules, IEnumerable<string>? selectedFields = null, string? filterExpression = null)
    {
        var explicitSelection = selectedFields as IReadOnlyCollection<string>;
        if (explicitSelection is { Count: > 0 })
        {
            _selectedFields = new HashSet<string>(explicitSelection, StringComparer.OrdinalIgnoreCase);
        }
        else if (rules?.SelectedFields is { Count: > 0 } ruleFields)
        {
            _selectedFields = new HashSet<string>(ruleFields, StringComparer.OrdinalIgnoreCase);
        }

        string? filter = string.IsNullOrWhiteSpace(filterExpression) ? rules?.Filter : filterExpression;
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
                _transforms[field] = rule.Transform;
            }
        }
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

    private bool IsSelectedKey(string key)
    {
        if (_selectedFields == null)
            return true;

        foreach (var selected in _selectedFields)
        {
            if (string.Equals(selected, key, StringComparison.OrdinalIgnoreCase))
                return true;

            if (selected.StartsWith(key + ".", StringComparison.OrdinalIgnoreCase)
                || selected.StartsWith(key + "[", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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

    private static void ValidateTransform(string field, FieldTransform transform)
    {
        if (!string.IsNullOrWhiteSpace(transform.Parse)
            && !string.Equals(transform.Parse.Trim(), "json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"字段 [{field}] 的 parse 模式不支持: {transform.Parse}（当前支持 json）。");
        }

        string type = transform.Type.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(type))
        {
            // 无转换类型时仅允许 parse 展开（透传原值再解析）
            if (!string.IsNullOrWhiteSpace(transform.Parse))
                return;
            throw new InvalidDataException($"字段 [{field}] 缺少 transform.type 配置。");
        }

        switch (type)
        {
            case "jwt":
            case "base64":
            case "url":
                break;
            case "aes":
                if (string.IsNullOrEmpty(transform.Key))
                    throw new InvalidDataException($"字段 [{field}] 的 AES 转换缺少 key 配置。");
                if (!string.IsNullOrWhiteSpace(transform.Format)
                    && transform.Format.Trim() != "base64"
                    && transform.Format.Trim() != "hex")
                    throw new InvalidDataException($"字段 [{field}] 的 AES format 不支持: {transform.Format}（支持 base64 / hex）。");
                break;
            default:
                throw new InvalidDataException($"字段 [{field}] 的转换类型不支持: {transform.Type}（支持 jwt / aes / base64 / url）。");
        }
    }

    /// <summary>
    /// 将转换结果按 parse 模式解析并展开到目标字典。
    /// 成功（含解析失败回退）返回 true；无 parse 配置或值非字符串时返回 false 由调用方原样保留。
    /// </summary>
    private static bool TryUnwrapJson(
        string field, FieldTransform transform, object? value, Dictionary<string, object?> target, HashSet<string>? selectedFields)
    {
        if (value is not string text
            || !string.Equals(transform.Parse?.Trim(), "json", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(text);
            FlattenJsonWithSelection(doc.RootElement, field, target, selectedFields);
            return true;
        }
        catch (JsonException)
        {
            // 非合法 JSON：保留原始转换结果字符串
            if (selectedFields == null || IsSelectedKey(selectedFields, field))
                target[field] = text;
            return true;
        }
    }

    private static void FlattenJsonWithSelection(JsonElement element, string prefix, Dictionary<string, object?> target, HashSet<string>? selectedFields)
    {
        if (selectedFields != null && !IsSelectedKey(selectedFields, prefix))
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    string name = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
                    FlattenJsonWithSelection(property.Value, name, target, selectedFields);
                }
                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    string name = $"{prefix}[{index}]";
                    FlattenJsonWithSelection(item, name, target, selectedFields);
                    index++;
                }
                break;

            case JsonValueKind.String:
                if (selectedFields == null || HasNestedSelection(selectedFields, prefix))
                {
                    string text = element.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        try
                        {
                            using var nestedDoc = JsonDocument.Parse(text);
                            FlattenJsonWithSelection(nestedDoc.RootElement, prefix, target, selectedFields);
                            return;
                        }
                        catch (JsonException)
                        {
                            // 不是合法嵌套 JSON，保留原始字符串
                        }
                    }
                }

                if (selectedFields == null || IsSelectedKey(selectedFields, prefix))
                    target[prefix] = element.GetString();
                break;

            default:
                if (selectedFields == null || IsSelectedKey(selectedFields, prefix))
                    target[prefix] = ConvertJsonElementValue(element);
                break;
        }
    }

    private static bool IsSelectedKey(HashSet<string>? selectedFields, string key)
    {
        if (selectedFields == null)
            return true;

        foreach (var selected in selectedFields)
        {
            if (string.Equals(selected, key, StringComparison.OrdinalIgnoreCase))
                return true;

            if (selected.StartsWith(key + ".", StringComparison.OrdinalIgnoreCase)
                || selected.StartsWith(key + "[", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasNestedSelection(HashSet<string> selectedFields, string key)
    {
        foreach (var selected in selectedFields)
        {
            if (selected.StartsWith(key + ".", StringComparison.OrdinalIgnoreCase)
                || selected.StartsWith(key + "[", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static object? ConvertJsonElementValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var property in element.EnumerateObject())
                    dict[property.Name] = ConvertJsonElementValue(property.Value);
                return dict;

            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                    list.Add(ConvertJsonElementValue(item));
                return list;

            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.Number:
                if (element.TryGetInt64(out long l)) return l;
                if (element.TryGetDouble(out double d)) return d;
                return element.GetRawText();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Null:
                return null;

            default:
                return element.GetRawText();
        }
    }

    private static bool TryApply(FieldTransform transform, object? value, out object? result)
    {
        result = value;
        if (value is not string str)
            return true; // 非字符串值不做转换，也不计为失败

        try
        {
            result = transform.Type.Trim().ToLowerInvariant() switch
            {
                "jwt" => ApplyJwt(transform, str),
                "aes" => ApplyAes(transform, str),
                "base64" => ApplyBase64(str),
                "url" => ApplyUrl(str),
                _ => str
            };
            return true;
        }
        catch
        {
            result = value; // 保留原始值
            return false;
        }
    }

    // ── JWT 解码：提取 payload（或指定 claim）──
    private static string ApplyJwt(FieldTransform transform, string jwt)
    {
        string[] parts = jwt.Split('.');
        if (parts.Length < 2)
            throw new InvalidDataException("JWT 格式无效（需至少 header.payload）。");

        string payload = Base64UrlDecode(parts[1]);
        if (string.IsNullOrWhiteSpace(transform.Claim))
            return payload;

        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty(transform.Claim, out var claim))
            throw new InvalidDataException($"JWT payload 中不存在 claim: {transform.Claim}");

        return claim.ValueKind switch
        {
            JsonValueKind.String => claim.GetString() ?? "",
            JsonValueKind.Number => claim.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => claim.GetRawText()
        };
    }

    private static string Base64UrlDecode(string input)
    {
        string s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    // ── AES 解密 ──
    private static string ApplyAes(FieldTransform transform, string cipherText)
    {
        CipherMode mode = string.Equals(transform.Mode, "ecb", StringComparison.OrdinalIgnoreCase)
            ? CipherMode.ECB
            : CipherMode.CBC;
        PaddingMode padding = string.Equals(transform.Padding, "none", StringComparison.OrdinalIgnoreCase)
            ? PaddingMode.None
            : PaddingMode.PKCS7;

        byte[] key = DecodeBytes(transform.Key!, transform.Format);
        byte[] cipher = Convert.FromBase64String(cipherText);

        using var aes = Aes.Create();
        aes.Mode = mode;
        aes.Padding = padding;
        aes.Key = key;
        if (mode == CipherMode.CBC)
        {
            if (string.IsNullOrEmpty(transform.Iv))
                throw new InvalidDataException("CBC 模式需要提供 iv。");
            aes.IV = DecodeBytes(transform.Iv, transform.Format);
        }

        using var decryptor = aes.CreateDecryptor();
        byte[] plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DecodeBytes(string value, string? format)
    {
        return string.Equals(format, "hex", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromHexString(value)
            : Convert.FromBase64String(value);
    }

    // ── Base64 解码 ──
    private static string ApplyBase64(string input) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(input));

    // ── URL 解码 ──
    private static string ApplyUrl(string input) =>
        System.Net.WebUtility.UrlDecode(input);

    /// <summary>供测试与诊断使用的字段名集合（排序后）。</summary>
    internal IReadOnlyCollection<string>? GetSelectedFields() => _selectedFields;

    internal IReadOnlyDictionary<string, FieldTransform> GetTransforms() => _transforms;

    /// <summary>供测试与诊断使用的过滤表达式（null 表示未启用过滤）。</summary>
    internal FieldFilter? GetFilter() => _filter;
}
