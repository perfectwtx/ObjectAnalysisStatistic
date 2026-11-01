using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ObjectAnalyzer.Analysis;

public sealed partial class FieldTransformProcessor
{
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
}
