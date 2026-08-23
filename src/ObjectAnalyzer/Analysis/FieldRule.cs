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

    /// <summary>
    /// 自定义 PII 规则：将业务敏感字段（即使语义类型未被内置识别，如 internalId / authToken）
    /// 声明为敏感字段，并指定打码格式。命中后该字段在一切报告输出前都会被自动打码。
    /// </summary>
    [JsonPropertyName("pii")]
    public PiiRule? Pii { get; set; }
}

/// <summary>
/// 自定义 PII 规则：声明业务敏感字段及其打码格式。
/// 示例：
/// <code>
/// "fields": {
///   "internalId": {
///     "pii": { "label": "内部标识", "highSeverity": false, "mask": { "keepPrefix": 2, "keepSuffix": 2 } }
///   },
///   "authToken": { "pii": { "mask": { "maskAll": true } } }
/// }
/// </code>
/// </summary>
public class PiiRule
{
    /// <summary>敏感分类标签，用于合规报告展示（如 "内部标识"）。缺省为 "业务敏感字段"。</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>是否属于高敏感（默认 true），影响合规报告中的字段排序。</summary>
    [JsonPropertyName("highSeverity")]
    public bool HighSeverity { get; set; } = true;

    /// <summary>打码格式；缺省时使用全量打码（maskAll=true）。</summary>
    [JsonPropertyName("mask")]
    public PiiMaskConfig? Mask { get; set; }
}

/// <summary>
/// 自定义打码格式：保留前缀/后缀字符，其余以 maskChar 打码；或整值打码。
/// 规则：优先 maskAll（整值打码）；否则按 keepPrefix/keepSuffix 保留前后缀，中间用 maskChar 填充。
/// </summary>
public class PiiMaskConfig
{
    /// <summary>保留前缀字符数（默认 0）。</summary>
    [JsonPropertyName("keepPrefix")]
    public int KeepPrefix { get; set; }

    /// <summary>保留后缀字符数（默认 0）。</summary>
    [JsonPropertyName("keepSuffix")]
    public int KeepSuffix { get; set; }

    /// <summary>打码字符（默认 *）。</summary>
    [JsonPropertyName("maskChar")]
    public string MaskChar { get; set; } = "*";

    /// <summary>是否整值打码（忽略 keepPrefix/keepSuffix，整个值替换为 maskChar）。</summary>
    [JsonPropertyName("maskAll")]
    public bool MaskAll { get; set; }
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

    /// <summary>AES: 解密密钥（编码由 format 决定）。</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>AES: 初始化向量 IV（编码由 format 决定；ECB 模式无需）。</summary>
    [JsonPropertyName("iv")]
    public string? Iv { get; set; }

    /// <summary>AES: 工作模式 cbc（默认）或 ecb。</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>AES: 填充方式 pkcs7（默认）或 none。</summary>
    [JsonPropertyName("padding")]
    public string? Padding { get; set; }

    /// <summary>AES: Key 与 IV 统一编码 base64（默认）或 hex。</summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>
    /// 转换后再解析并展开为嵌套字段（当前支持 "json"）：
    /// 将转换结果当作 JSON 文本解析，对象按字段名前缀展开（如 payload.city / payload.age），
    /// 数组按下标展开（如 payload[0]）；非合法 JSON 时保留原始转换结果。可省略 type（仅展开）。
    /// </summary>
    [JsonPropertyName("parse")]
    public string? Parse { get; set; }
}

/// <summary>
/// 值解析规则：针对字段/列内容按 parseType 决定是否和如何解析 JSON，适用于 Excel / CSV / 其他来源的值级解析。
/// </summary>
public class ValueParserConfig
{
    /// <summary>来源字段/列名（如 "A"、"payload"）。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    /// <summary>解析类型：none / auto / json / json-string。</summary>
    [JsonPropertyName("parseType")]
    public string ParseType { get; set; } = "auto";

    /// <summary>是否对该来源解析出的对象/数组做扁平化。null 表示沿用全局 flatten 配置。</summary>
    [JsonPropertyName("flatten")]
    public bool? Flatten { get; set; }

    /// <summary>表头模式：1（默认）首行值为列名，source/字段前缀用表头文字；0 无表头，列以字母 A/B/C 命名，source/字段前缀用列字母。</summary>
    [JsonPropertyName("header")]
    public int? Header { get; set; }
}

/// <summary>
/// 分析规则配置，包含所有字段规则的集合。
/// </summary>
public class AnalysisRules
{
    /// <summary>全局 JSON 展开行为：true 时扁平化嵌套对象，false 时保留对象树。CLI 的 --no-flatten 优先级更高。</summary>
    [JsonPropertyName("flatten")]
    public bool? Flatten { get; set; }

    [JsonPropertyName("fields")]
    public Dictionary<string, FieldRule> Fields { get; set; } = new();

    /// <summary>仅统计这些字段（逗号分隔）。优先级低于 CLI 的 --fields 选项。</summary>
    [JsonPropertyName("selectedFields")]
    public List<string>? SelectedFields { get; set; }

    /// <summary>全局过滤条件表达式（如 "Age &gt; 18 AND Gender in ['男','女']"）。优先级低于 CLI 的 --where 选项。</summary>
    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    /// <summary>通用值解析器：按 source + parseType 决定是否解析值，适用于 Excel / JSON / 任意输入来源。</summary>
    [JsonPropertyName("valueParsers")]
    public List<ValueParserConfig>? ValueParsers { get; set; }
}
