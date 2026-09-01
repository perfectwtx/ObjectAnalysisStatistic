using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Utilities.Encoders;

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
    /// 基于规则文件构建处理器。selectedFields 优先级高于 <see cref="AnalysisRuntime.SelectedFields"/>；
    /// filterExpression 优先级高于 <see cref="AnalysisRuntime.Filter"/>（为空字符串时回退到 runtime.filter）。
    /// </summary>
    public FieldTransformProcessor(AnalysisRules? rules, IEnumerable<string>? selectedFields = null, string? filterExpression = null)
    {
        var explicitSelection = selectedFields as IReadOnlyCollection<string>;
        if (explicitSelection is { Count: > 0 })
        {
            _selectedFields = new HashSet<string>(explicitSelection, StringComparer.OrdinalIgnoreCase);
        }
        else if (rules?.Runtime?.SelectedFields is { Count: > 0 } ruleFields)
        {
            _selectedFields = new HashSet<string>(ruleFields, StringComparer.OrdinalIgnoreCase);
        }

        string? filter = string.IsNullOrWhiteSpace(filterExpression) ? rules?.Runtime?.Filter : filterExpression;
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
                "sm4" => ApplySm4(transform, str),
                "sm2" => ApplySm2(transform, str),
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

    // ── SM4 解密（国密对称算法，128 位块 / 128 位密钥，GM/T 0002-2014）──
    private static string ApplySm4(FieldTransform transform, string cipherText)
    {
        if (transform.Key == null)
            throw new InvalidDataException("SM4 转换缺少 key 配置。");

        byte[] key = DecodeBytes(transform.Key, transform.Format);
        if (key.Length != 16)
            throw new InvalidDataException($"SM4 密钥长度必须为 16 字节（128 位），当前 {key.Length} 字节。");

        byte[] cipher;
        try
        {
            cipher = DecodeBytes(cipherText, transform.Format);
        }
        catch (FormatException)
        {
            // format 指定的解码方式失败时按 base64 重试（密文本身就是 base64 字符串）
            cipher = Convert.FromBase64String(cipherText);
        }

        IBlockCipher engine = new SM4Engine();
        IBlockCipher mode;
        bool isEcb = string.Equals(transform.Mode, "ecb", StringComparison.OrdinalIgnoreCase);
        if (isEcb)
        {
            mode = engine;
        }
        else
        {
            if (transform.Iv == null)
                throw new InvalidDataException("SM4 CBC 模式需要提供 iv。");
            byte[] iv = DecodeBytes(transform.Iv, transform.Format);
            if (iv.Length != 16)
                throw new InvalidDataException($"SM4 IV 长度必须为 16 字节，当前 {iv.Length} 字节。");
            mode = new CbcBlockCipher(engine);
        }

        PaddedBufferedBlockCipher cipher2;
        if (string.Equals(transform.Padding, "none", StringComparison.OrdinalIgnoreCase))
        {
            cipher2 = new PaddedBufferedBlockCipher(mode, new ZeroBytePadding());
            // ZeroBytePadding 在解密时不会去掉末尾 0；改用无填充处理后手动 trim
        }
        else
        {
            cipher2 = new PaddedBufferedBlockCipher(mode, new Pkcs7Padding());
        }

        var keyParam = new KeyParameter(key);
        ICipherParameters cipherParams = keyParam;
        if (!isEcb)
        {
            byte[] ivBytes = DecodeBytes(transform.Iv!, transform.Format);
            cipherParams = new ParametersWithIV(keyParam, ivBytes);
        }

        cipher2.Init(false, cipherParams);
        byte[] output = new byte[cipher2.GetOutputSize(cipher.Length)];
        int len = cipher2.ProcessBytes(cipher, 0, cipher.Length, output, 0);
        len += cipher2.DoFinal(output, len);

        // PKCS7 填充已被 Pkcs7Padding 剥离；ZeroBytePadding 需要手动去掉尾部 0
        if (string.Equals(transform.Padding, "none", StringComparison.OrdinalIgnoreCase))
        {
            int newLen = len;
            while (newLen > 0 && output[newLen - 1] == 0) newLen--;
            len = newLen;
        }

        return Encoding.UTF8.GetString(output, 0, len);
    }

    // ── SM2 转换（国密非对称算法，GM/T 0003-2014）：decrypt 私钥解公钥密文 / verify 公钥验签 ──
    private static string ApplySm2(FieldTransform transform, string cipherText)
    {
        string sm2Mode = string.IsNullOrWhiteSpace(transform.Mode) ? "decrypt" : transform.Mode.Trim().ToLowerInvariant();
        if (sm2Mode == "decrypt")
            return ApplySm2Decrypt(transform, cipherText);
        if (sm2Mode == "verify")
            return ApplySm2Verify(transform, cipherText);
        throw new InvalidDataException($"SM2 mode 不支持: {transform.Mode}（支持 decrypt / verify）。");
    }

    private static string ApplySm2Decrypt(FieldTransform transform, string cipherText)
    {
        if (transform.PrivateKey == null)
            throw new InvalidDataException("SM2 decrypt 缺少 privateKey 配置。");

        ECPrivateKeyParameters privKey = LoadSm2PrivateKey(transform.PrivateKey, transform.Format);
        byte[] cipher;
        try
        {
            cipher = DecodeBytes(cipherText, transform.Format);
        }
        catch (FormatException)
        {
            cipher = Convert.FromBase64String(cipherText);
        }

        var engine = new SM2Engine();
        engine.Init(false, privKey);
        byte[] plain = engine.ProcessBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plain);
    }

    private static string ApplySm2Verify(FieldTransform transform, string plainText)
    {
        if (transform.PublicKey == null || transform.Signature == null)
            throw new InvalidDataException("SM2 verify 缺少 publicKey / signature 配置。");

        ECPublicKeyParameters pubKey = LoadSm2PublicKey(transform.PublicKey, transform.Format);
        byte[] sig = DecodeBytes(transform.Signature, transform.Format);
        byte[] data = Encoding.UTF8.GetBytes(plainText);

        var signer = new SM2Signer();
        signer.Init(false, pubKey);
        signer.BlockUpdate(data, 0, data.Length);
        bool valid = signer.VerifySignature(sig);
        return valid ? "valid" : "invalid";
    }

    private static ECPrivateKeyParameters LoadSm2PrivateKey(string keyMaterial, string? format)
    {
        // 优先尝试：1) hex 32 字节私钥整数；2) PEM/DER PKCS#8
        string trimmed = keyMaterial.Trim();
        bool isHex = string.Equals(format, "hex", StringComparison.OrdinalIgnoreCase);

        if (isHex || LooksLikeHex(trimmed))
        {
            byte[] bytes = isHex ? Hex.Decode(trimmed) : Hex.Decode(trimmed);
            if (bytes.Length != 32)
                throw new InvalidDataException($"SM2 私钥 hex 长度必须为 32 字节，当前 {bytes.Length}。");
            var d = new BigInteger(1, bytes);
            return new ECPrivateKeyParameters(d, GetSm2DomainParameters());
        }

        // Base64：先尝试 DER (PKCS#8 PrivateKeyInfo)，失败则回退到裸 32 字节
        try
        {
            var (priv, _) = ReadPemPair(trimmed, expectPrivate: true);
            return priv ?? throw new InvalidDataException("PEM 中未找到 SM2 私钥。");
        }
        catch
        {
            byte[] bytes = Convert.FromBase64String(trimmed);
            if (bytes.Length != 32)
                throw new InvalidDataException($"SM2 私钥（base64 裸）长度必须为 32 字节，当前 {bytes.Length}。");
            var d = new BigInteger(1, bytes);
            return new ECPrivateKeyParameters(d, GetSm2DomainParameters());
        }
    }

    private static ECPublicKeyParameters LoadSm2PublicKey(string keyMaterial, string? format)
    {
        string trimmed = keyMaterial.Trim();
        bool isHex = string.Equals(format, "hex", StringComparison.OrdinalIgnoreCase);

        if (isHex || LooksLikeHex(trimmed))
        {
            byte[] bytes = isHex ? Hex.Decode(trimmed) : Hex.Decode(trimmed);
            // 04 || X (32) || Y (32)
            if (bytes.Length == 65 && bytes[0] == 0x04)
            {
                var x = new BigInteger(1, bytes.AsSpan(1, 32).ToArray());
                var y = new BigInteger(1, bytes.AsSpan(33, 32).ToArray());
                var curve = GetSm2DomainParameters();
                return new ECPublicKeyParameters(curve.Curve.CreatePoint(x, y), curve);
            }
            if (bytes.Length == 64)
            {
                var x = new BigInteger(1, bytes.AsSpan(0, 32).ToArray());
                var y = new BigInteger(1, bytes.AsSpan(32, 32).ToArray());
                var curve = GetSm2DomainParameters();
                return new ECPublicKeyParameters(curve.Curve.CreatePoint(x, y), curve);
            }
            throw new InvalidDataException($"SM2 公钥 hex 长度必须为 64 字节（裸 X||Y）或 65 字节（04||X||Y），当前 {bytes.Length}。");
        }

        // Base64：先尝试 DER (X.509 SubjectPublicKeyInfo)，失败则回退到裸 04||X||Y
        try
        {
            var (_, pub) = ReadPemPair(trimmed, expectPrivate: false);
            return pub ?? throw new InvalidDataException("PEM 中未找到 SM2 公钥。");
        }
        catch
        {
            byte[] bytes = Convert.FromBase64String(trimmed);
            if (bytes.Length == 65 && bytes[0] == 0x04)
            {
                var x = new BigInteger(1, bytes.AsSpan(1, 32).ToArray());
                var y = new BigInteger(1, bytes.AsSpan(33, 32).ToArray());
                var curve = GetSm2DomainParameters();
                return new ECPublicKeyParameters(curve.Curve.CreatePoint(x, y), curve);
            }
            throw new InvalidDataException($"SM2 公钥（base64）需为 65 字节未压缩点 (04||X||Y)，当前 {bytes.Length}。");
        }
    }

    private static ECDomainParameters GetSm2DomainParameters()
    {
        // sm2p256v1 曲线参数（GM/T 0003-2014 附录 A）
        X9ECParameters x9 = ECNamedCurveTable.GetByName("sm2p256v1")
            ?? throw new InvalidOperationException("BouncyCastle 未注册 sm2p256v1 曲线。");
        return new ECDomainParameters(x9.Curve, x9.G, x9.N, x9.H, x9.GetSeed());
    }

    private static bool LooksLikeHex(string s)
    {
        if (s.Length == 0) return false;
        if (s.Length % 2 != 0) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    private static (ECPrivateKeyParameters? priv, ECPublicKeyParameters? pub) ReadPemPair(string material, bool expectPrivate)
    {
        var reader = new PemReader(new System.IO.StringReader(material));
        object obj = reader.ReadObject();
        if (obj == null) throw new InvalidDataException("PEM 输入为空。");

        if (obj is AsymmetricCipherKeyPair pair)
        {
            return (pair.Private as ECPrivateKeyParameters, pair.Public as ECPublicKeyParameters);
        }
        if (obj is ECPrivateKeyParameters ecPriv)
        {
            return (ecPriv, null);
        }
        if (obj is ECPublicKeyParameters ecPub)
        {
            return (null, ecPub);
        }
        if (obj is AsymmetricKeyParameter akp)
        {
            // 私钥 PrivateKeyInfo 走 AsymmetricCipherKeyPair 分支，这里兜底 PKCS#8 私钥
            if (akp is ECPrivateKeyParameters p) return (p, null);
            if (akp is ECPublicKeyParameters pb) return (null, pb);
        }
        throw new InvalidDataException($"PEM 类型不被识别: {obj.GetType().Name}（expectPrivate={expectPrivate}）。");
    }

    /// <summary>供测试与诊断使用的字段名集合（排序后）。</summary>
    internal IReadOnlyCollection<string>? GetSelectedFields() => _selectedFields;

    internal IReadOnlyDictionary<string, FieldTransform> GetTransforms() => _transforms;

    /// <summary>供测试与诊断使用的过滤表达式（null 表示未启用过滤）。</summary>
    internal FieldFilter? GetFilter() => _filter;
}
