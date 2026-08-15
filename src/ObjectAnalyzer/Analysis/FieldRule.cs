using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字段规则定义，用于指定字段的默认值、主导类型和枚举约束。
/// </summary>
public class FieldRule
{
    /// <summary>指定字段默认值 (覆盖自动推断)。</summary>
    [JsonPropertyName("defaultValue")]
    public object? DefaultValue { get; set; }

    /// <summary>指定主导类型 (覆盖自动检测)。</summary>
    [JsonPropertyName("primaryType")]
    public string? PrimaryType { get; set; }

    /// <summary>枚举约束，支持混合类型 (数值 + 字符串)。</summary>
    [JsonPropertyName("enumValues")]
    public List<JsonElement>? EnumValues { get; set; }

    /// <summary>字段值转换配置（如解密、JWT 转明文、Base64/URL 解码）。</summary>
    [JsonPropertyName("transform")]
    public FieldTransform? Transform { get; set; }
}

/// <summary>
/// 字段值转换配置。类型: jwt / aes / base64 / url。
/// </summary>
public class FieldTransform
{
    /// <summary>转换类型: jwt, aes, base64, url。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>JWT: 指定要提取的 claim 名（如 "sub" / "user_id"）。缺省返回整个 payload JSON。</summary>
    [JsonPropertyName("claim")]
    public string? Claim { get; set; }

    /// <summary>AES: 解密密钥（格式由 KeyFormat 决定）。</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>AES: 初始化向量 IV（格式由 IvFormat 决定；ECB 模式无需）。</summary>
    [JsonPropertyName("iv")]
    public string? Iv { get; set; }

    /// <summary>AES: 工作模式 cbc（默认）或 ecb。</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>AES: 填充方式 pkcs7（默认）或 none。</summary>
    [JsonPropertyName("padding")]
    public string? Padding { get; set; }

    /// <summary>AES: Key 编码 base64（默认）或 hex。</summary>
    [JsonPropertyName("keyFormat")]
    public string? KeyFormat { get; set; }

    /// <summary>AES: IV 编码 base64（默认）或 hex。</summary>
    [JsonPropertyName("ivFormat")]
    public string? IvFormat { get; set; }

    /// <summary>
    /// 转换后再解析并展开为嵌套字段（当前支持 "json"）：
    /// 将转换结果当作 JSON 文本解析，对象按字段名前缀展开（如 payload.city / payload.age），
    /// 数组按下标展开（如 payload[0]）；非合法 JSON 时保留原始转换结果。可省略 type（仅展开）。
    /// </summary>
    [JsonPropertyName("parse")]
    public string? Parse { get; set; }
}

/// <summary>
/// 分析规则配置，包含所有字段规则的集合。
/// </summary>
public class AnalysisRules
{
    [JsonPropertyName("fields")]
    public Dictionary<string, FieldRule> Fields { get; set; } = new();

    /// <summary>仅统计这些字段（逗号分隔）。优先级低于 CLI 的 --fields 选项。</summary>
    [JsonPropertyName("selectedFields")]
    public List<string>? SelectedFields { get; set; }

    /// <summary>全局过滤条件表达式（如 "Age &gt; 18 AND Gender in ['男','女']"）。优先级低于 CLI 的 --where 选项。</summary>
    [JsonPropertyName("filter")]
    public string? Filter { get; set; }
}
