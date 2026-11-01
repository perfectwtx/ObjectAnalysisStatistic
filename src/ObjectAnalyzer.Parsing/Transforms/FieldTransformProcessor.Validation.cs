using ObjectAnalyzer.Analysis.Pipeline;
using System.Text.Json;

namespace ObjectAnalyzer.Analysis;

public sealed partial class FieldTransformProcessor
{
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
                ValidateAesOrSm4(field, type, transform);
                break;
            case "sm4":
                {
                    ValidateAesOrSm4(field, type, transform);
                    bool isEcb = string.Equals(transform.Mode, "ecb", StringComparison.OrdinalIgnoreCase);
                    if (!isEcb && string.IsNullOrEmpty(transform.Iv))
                        throw new InvalidDataException($"字段 [{field}] 的 SM4 CBC 模式需要提供 iv。");
                    if (!isEcb && !string.IsNullOrEmpty(transform.Iv))
                    {
                        byte[] iv = DecodeBytes(transform.Iv, transform.Format);
                        if (iv.Length != 16)
                            throw new InvalidDataException($"字段 [{field}] 的 SM4 IV 长度必须为 16 字节，当前 {iv.Length} 字节。");
                    }
                    break;
                }
            case "sm2":
                {
                    string sm2Mode = string.IsNullOrWhiteSpace(transform.Mode) ? "decrypt" : transform.Mode.Trim().ToLowerInvariant();
                    if (sm2Mode == "decrypt")
                    {
                        if (string.IsNullOrEmpty(transform.PrivateKey))
                            throw new InvalidDataException($"字段 [{field}] 的 SM2 decrypt 模式缺少 privateKey 配置。");
                    }
                    else if (sm2Mode == "verify")
                    {
                        if (string.IsNullOrEmpty(transform.PublicKey))
                            throw new InvalidDataException($"字段 [{field}] 的 SM2 verify 模式缺少 publicKey 配置。");
                        if (string.IsNullOrEmpty(transform.Signature))
                            throw new InvalidDataException($"字段 [{field}] 的 SM2 verify 模式缺少 signature 配置。");
                    }
                    else
                    {
                        throw new InvalidDataException($"字段 [{field}] 的 SM2 mode 不支持: {transform.Mode}（支持 decrypt / verify）。");
                    }
                    if (!string.IsNullOrWhiteSpace(transform.Format)
                        && transform.Format.Trim() != "base64"
                        && transform.Format.Trim() != "hex")
                        throw new InvalidDataException($"字段 [{field}] 的 SM2 format 不支持: {transform.Format}（支持 base64 / hex）。");
                    break;
                }
            default:
                throw new InvalidDataException($"字段 [{field}] 的转换类型不支持: {transform.Type}（支持 jwt / aes / sm4 / sm2 / base64 / url）。");
        }
    }

    private static void ValidateAesOrSm4(string field, string type, FieldTransform transform)
    {
        if (string.IsNullOrEmpty(transform.Key))
            throw new InvalidDataException($"字段 [{field}] 的 {type.ToUpperInvariant()} 转换缺少 key 配置。");
        if (!string.IsNullOrWhiteSpace(transform.Format)
            && transform.Format.Trim() != "base64"
            && transform.Format.Trim() != "hex")
            throw new InvalidDataException($"字段 [{field}] 的 {type.ToUpperInvariant()} format 不支持: {transform.Format}（支持 base64 / hex）。");
        byte[] key = DecodeBytes(transform.Key, transform.Format);
        // AES key 可能是 16/24/32（AES-128/192/256），不强制单一长度
        if (type == "sm4" && key.Length != 16)
            throw new InvalidDataException($"字段 [{field}] 的 SM4 密钥长度必须为 16 字节（128 位），当前 {key.Length} 字节。");
        if (type == "aes" && key.Length != 16 && key.Length != 24 && key.Length != 32)
            throw new InvalidDataException($"字段 [{field}] 的 AES 密钥长度必须为 16/24/32 字节，当前 {key.Length} 字节。");
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
                    string name = FieldPath.Parse(prefix).AppendProperty(property.Name).ToString();
                    FlattenJsonWithSelection(property.Value, name, target, selectedFields);
                }
                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    string name = FieldPath.Parse(prefix).AppendIndex(index).ToString();
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

    private static object? ConvertJsonElementValue(JsonElement element) => RecordValue.FromJsonElement(element).ToObject();
}
