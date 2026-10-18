using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// 字段规则定义，用于指定字段的默认值、主导类型和枚举约束。
/// </summary>
public class FieldRule
{
    /// <summary>Registered custom value-rule configurations, keyed by compiler ID.</summary>
    [JsonPropertyName("checks")]
    public Dictionary<string, JsonElement>? Checks { get; set; }

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

    /// <summary>
    /// 误报逃生口（PII 白名单）：false 时该字段即使命中敏感语义/字段名启发式
    /// （如 name 列存的是产品名）也不打码、不进合规报告。缺省 null = 自动打码。
    /// 优先级高于 pii 自定义规则。
    /// </summary>
    [JsonPropertyName("redact")]
    public bool? Redact { get; set; }

    // ── 质量检查 v2（复用 --violations 导出与 --fail-on-violations 门禁）──

    /// <summary>数值下界（含）：数值出现次数中低于该值的计入范围违规（热路径精确计数）。</summary>
    [JsonPropertyName("minValue")]
    public double? MinValue { get; set; }

    /// <summary>数值上界（含）：高于该值计入范围违规。</summary>
    [JsonPropertyName("maxValue")]
    public double? MaxValue { get; set; }

    /// <summary>必填检查：true 时字段缺失（对象中不出现）或值为 null 均计为违规。</summary>
    [JsonPropertyName("required")]
    public bool? Required { get; set; }

    /// <summary>正则 IsMatch 检查，不自动添加首尾锚点；非空值逐值精确计数，样本有界。</summary>
    [JsonPropertyName("pattern")]
    public string? Pattern { get; set; }

    /// <summary>唯一性检查：非空值出现重复时报告重复次数（未截断时精确；截断时用 HyperLogLog 近似并标记）。</summary>
    [JsonPropertyName("unique")]
    public bool? Unique { get; set; }

    /// <summary>null 率上限（0.0~1.0）：null 与字段缺失合计占比超过该值时违规。</summary>
    [JsonPropertyName("nullRateMax")]
    public double? NullRateMax { get; set; }

    /// <summary>异常值比例上限（0.0~1.0）：IQR 1.5× 判定为异常值的样本占比超过该值时违规（基于对数直方图近似归类）。</summary>
    [JsonPropertyName("maxOutlierRatio")]
    public double? MaxOutlierRatio { get; set; }

    /// <summary>是否配置了任一质量检查维度（用于跳过无关开销）。</summary>
    [JsonIgnore]
    public bool HasQualityChecks =>
        MinValue.HasValue || MaxValue.HasValue || Required == true
        || !string.IsNullOrEmpty(Pattern) || Unique == true || NullRateMax.HasValue
        || MaxOutlierRatio.HasValue || Checks is { Count: > 0 };
}
