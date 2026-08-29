using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字段值转换配置。类型: jwt / aes / sm4 / sm2 / base64 / url。
/// </summary>
public class FieldTransform
{
    /// <summary>转换类型: jwt, aes, sm4, sm2, base64, url。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>JWT: 指定要提取的 claim 名（如 "sub" / "user_id"）。缺省返回整个 payload JSON。</summary>
    [JsonPropertyName("claim")]
    public string? Claim { get; set; }

    /// <summary>AES/SM4: 解密密钥（编码由 format 决定）。</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>AES/SM4: 初始化向量 IV（编码由 format 决定；ECB 模式无需）。</summary>
    [JsonPropertyName("iv")]
    public string? Iv { get; set; }

    /// <summary>AES/SM4: 工作模式 cbc（默认）或 ecb。</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>AES/SM4: 填充方式 pkcs7（默认）或 none。</summary>
    [JsonPropertyName("padding")]
    public string? Padding { get; set; }

    /// <summary>AES/SM4: Key 与 IV 统一编码 base64（默认）或 hex。</summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>
    /// SM2 专用：私钥（Base64 编码的 PKCS#8 或 hex 编码的 32 字节私钥整数）。
    /// decrypt 模式必需。
    /// </summary>
    [JsonPropertyName("privateKey")]
    public string? PrivateKey { get; set; }

    /// <summary>
    /// SM2 专用：公钥（Base64 编码的 X.509 SubjectPublicKeyInfo 或 hex 编码的 04||X||Y 未压缩点）。
    /// verify 模式必需。
    /// </summary>
    [JsonPropertyName("publicKey")]
    public string? PublicKey { get; set; }

    /// <summary>
    /// SM2 verify 模式：待验证签名（Base64 或 hex，与 <see cref="Format"/> 一致）。
    /// 同一规则下所有记录都用同一签名批量校验；如需逐行签名请使用专用校验流程。
    /// </summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    /// <summary>
    /// 转换后再解析并展开为嵌套字段（当前支持 "json"）：
    /// 将转换结果当作 JSON 文本解析，对象按字段名前缀展开（如 payload.city / payload.age），
    /// 数组按下标展开（如 payload[0]）；非合法 JSON 时保留原始转换结果。可省略 type（仅展开）。
    /// </summary>
    [JsonPropertyName("parse")]
    public string? Parse { get; set; }
}
