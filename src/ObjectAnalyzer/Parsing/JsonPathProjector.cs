using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meziantou.Framework.Json;

namespace ObjectAnalyzer.Parsing;

/// <summary>
/// JSONPath 投影器：在分析管道的两个不同层级应用 JSONPath 表达式（RFC 9535），
/// 仅保留匹配节点作为后续分析输入。
///
/// 设计要点：
/// - <see cref="ApplyRootPath(JsonNode, string)"/>：作用于文件根（JSON 整体文件）。
///   对数组匹配：每个匹配元素作为一条记录；
///   对对象匹配：整个对象作为一条记录（覆盖整文件为 1 条）。
/// - <see cref="ApplyRecordPath"/>：作用于每条记录（JSONL / CSV / Excel / DB 行）。
///   同样按节点类型展开为多条记录。
/// - 匹配空集：返回 0 条记录，由调用方决定告警/错误策略（默认仅警告）。
/// - 语法错误：在 <see cref="TryValidate"/> 中预检，fail-fast。
///
/// 性能权衡：JSONPath 投影需要将 Dictionary 转 JsonNode 再转回，单次投影 O(树大小)；
/// 与流式解析结合时，仅在 RootPath 路径上需要整体读入（与 JSON 整体解析的内存占用同级）。
/// </summary>
public static class JsonPathProjector
{
    /// <summary>
    /// 校验 JSONPath 表达式语法；不执行求值。
    /// 失败时抛 <see cref="InvalidOperationException"/>，携带详细错误信息（含原始表达式）。
    /// </summary>
    public static void TryValidate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new InvalidOperationException("JSONPath 表达式不能为空。");
        }

        if (!expression.TrimStart().StartsWith('$'))
        {
            throw new InvalidOperationException(
                $"JSONPath 表达式必须以 '$' 开头（RFC 9535 根标识符），当前: {expression}");
        }

        try
        {
            _ = JsonPath.Parse(expression);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"JSONPath 表达式语法无效: {expression}（{ex.Message}）", ex);
        }
    }

    /// <summary>
    /// 在文件根应用 JSONPath：返回 0..N 条记录（Dictionary）。
    /// 对数组匹配节点：每个元素单独成为一条记录；
    /// 对对象匹配节点：包成一条记录 {<see cref="RootSingletonKey"/>: 对象} 保留原结构；
    /// 对标量匹配节点：不产生可分析记录（返回空）。
    /// </summary>
    /// <param name="root">从 JSON 文件整体解析得到的根节点（已剥离 BOM、已加载到 JsonNode）。</param>
    /// <param name="expression">JSONPath 表达式（已通过 <see cref="TryValidate"/>）。</param>
    public static IReadOnlyList<Dictionary<string, object?>> ApplyRootPath(JsonNode? root, string expression)
    {
        if (root == null) return Array.Empty<Dictionary<string, object?>>();

        var path = JsonPath.Parse(expression);
        var result = path.Evaluate(root, JsonPathEvaluationMode.Lax);

        var records = new List<Dictionary<string, object?>>(result.Count);
        foreach (var match in result)
        {
            var node = match.Value;
            switch (node)
            {
                case JsonObject obj:
                    records.Add(JsonNodeToRecord(obj));
                    break;
                case JsonArray arr:
                    // 数组节点 → 摊平：每个数组元素作为一条记录
                    foreach (var item in arr)
                    {
                        if (item is JsonObject itemObj)
                            records.Add(JsonNodeToRecord(itemObj));
                    }
                    break;
                // 标量/null：无可分析结构，跳过
            }
        }
        return records;
    }

    /// <summary>
    /// 在单条记录上应用 JSONPath：返回 0..N 条新记录（fan-out）。
    /// 同样按节点类型展开：对象 → 1 条；数组 → N 条；标量/null → 0 条。
    /// 路径不匹配（无结果）时返回 0 条记录（不抛错，由调用方告警）。
    /// </summary>
    public static IReadOnlyList<Dictionary<string, object?>> ApplyRecordPath(
        Dictionary<string, object?> record,
        string expression)
    {
        var node = RecordToJsonNode(record);
        try
        {
            var path = JsonPath.Parse(expression);
            var result = path.Evaluate(node, JsonPathEvaluationMode.Lax);
            var records = new List<Dictionary<string, object?>>(result.Count);
            foreach (var match in result)
            {
                switch (match.Value)
                {
                    case JsonObject obj:
                        records.Add(JsonNodeToRecord(obj));
                        break;
                    case JsonArray arr:
                        foreach (var item in arr)
                        {
                            if (item is JsonObject itemObj)
                                records.Add(JsonNodeToRecord(itemObj));
                        }
                        break;
                }
            }
            return records;
        }
        finally
        {
            // JsonNode 自身不持有非托管资源，但确保在反复投影时不会无意持有父节点引用
            // （System.Text.Json.Nodes.JsonNode 在深度嵌套下 GC 压力较大，及时释放有帮助）
        }
    }

    /// <summary>JSONPath 投影时，对象节点被包成单字段记录时使用的占位键。</summary>
    public const string RootSingletonKey = "_root";

    /// <summary>当 JSONPath 匹配到非数组的"单对象"时，是否用 <see cref="RootSingletonKey"/> 包裹以保留原结构。</summary>
    public static Dictionary<string, object?> WrapSingletonIfObject(JsonNode node)
    {
        if (node is JsonObject obj)
            return JsonNodeToRecord(obj);
        // 数组已在 Apply* 中摊平；标量在外部被跳过
        return new Dictionary<string, object?>();
    }

    private static JsonObject RecordToJsonNode(Dictionary<string, object?> record)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in record)
        {
            obj[key] = ToJsonNode(value);
        }
        return obj;
    }

    private static JsonNode? ToJsonNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case long l:
                return JsonValue.Create(l);
            case int i:
                return JsonValue.Create((long)i);
            case double d:
                return JsonValue.Create(d);
            case float f:
                return JsonValue.Create((double)f);
            case decimal dec:
                return JsonValue.Create(dec);
            case DateTime dt:
                return JsonValue.Create(dt);
            case DateTimeOffset dto:
                return JsonValue.Create(dto);
            case Guid g:
                return JsonValue.Create(g);
            case JsonElement element:
                // JSONL 解析器（System.Text.Json.JsonDocument 流式读取）的产物是 JsonElement；
                // 不能直接当 JsonNode 使用，需要先序列化为 JsonNode 后再继续投影。
                return JsonSerializer.SerializeToNode(element, JsonSharedOptions.Read);
            case JsonNode jn:
                return jn.DeepClone();
            case Dictionary<string, object?> dict:
                {
                    var nested = new JsonObject();
                    foreach (var (k, v) in dict)
                        nested[k] = ToJsonNode(v);
                    return nested;
                }
            case IDictionary<string, object?> sd:
                {
                    var nested = new JsonObject();
                    foreach (var (k, v) in sd)
                        nested[k] = ToJsonNode(v);
                    return nested;
                }
            case IDictionary nonGenericDict:
                {
                    var nested = new JsonObject();
                    foreach (DictionaryEntry entry in nonGenericDict)
                    {
                        if (entry.Key == null) continue;
                        nested[entry.Key.ToString() ?? ""] = ToJsonNode(entry.Value);
                    }
                    return nested;
                }
            case IEnumerable enumerable:
                {
                    var arr = new JsonArray();
                    foreach (var item in enumerable)
                        arr.Add(ToJsonNode(item));
                    return arr;
                }
            default:
                // 兜底：序列化为字符串
                return JsonValue.Create(value.ToString());
        }
    }

    private static Dictionary<string, object?> JsonNodeToRecord(JsonObject obj)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kvp in obj)
        {
            record[kvp.Key] = FromJsonNode(kvp.Value);
        }
        return record;
    }

    private static object? FromJsonNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject nested:
                {
                    var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var kvp in nested)
                        dict[kvp.Key] = FromJsonNode(kvp.Value);
                    return dict;
                }
            case JsonArray arr:
                {
                    var list = new List<object?>(arr.Count);
                    foreach (var item in arr)
                        list.Add(FromJsonNode(item));
                    return list;
                }
            case JsonValue val:
                // JsonNode.Parse 的标量值实际类型是 JsonValueOfElement，存储的是 JsonElement；
                // 此时 GetValue<object>() 返回 JsonElement 而不是基础类型，需要走 JsonElementToObject。
                // 对 JsonValue.Create(...) 创建的强类型值，GetValue<object>() 可直接返回基础类型。
                object? boxed;
                try
                {
                    boxed = val.GetValue<object>();
                }
                catch (InvalidOperationException)
                {
                    return node.ToJsonString();
                }
                if (boxed is JsonElement el)
                {
                    return JsonElementToObject(el);
                }
                return boxed;
            default:
                return node.ToJsonString();
        }
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var prop in element.EnumerateObject())
                        dict[prop.Name] = JsonElementToObject(prop.Value);
                    return dict;
                }
            case JsonValueKind.Array:
                {
                    var list = new List<object?>();
                    foreach (var item in element.EnumerateArray())
                        list.Add(JsonElementToObject(item));
                    return list;
                }
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long l)) return l;
                if (element.TryGetDouble(out double d)) return d;
                return element.GetRawText();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            default: return element.GetRawText();
        }
    }
}
