using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// Schema 快照中单个字段的规范化描述。足够保留 diff 所需信息：
/// 类型、语义、nullable、coverage、数值/字符串/时间范围、枚举候选、类型分布、异常值指标。
/// </summary>
public sealed class SchemaSnapshotField
{
    /// <summary>字段路径（扁平化后，如 "user.address.city" / "tags[0]"）。</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>主导类型（Number/String/Boolean/Array/Object/Null 等）。</summary>
    [JsonPropertyName("primaryType")]
    public string PrimaryType { get; set; } = "Unknown";

    /// <summary>语义类型（DateTime/UUID/Email/IPv4/URL/Phone/MacAddress/...），无则 null。</summary>
    [JsonPropertyName("semanticType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SemanticType { get; set; }

    /// <summary>是否观察到 null 出现（与 "required/optional" 关联：出现 null ⇒ 不是必填）。</summary>
    [JsonPropertyName("nullable")]
    public bool Nullable { get; set; }

    /// <summary>字段在所有对象中的覆盖率 (0.0~1.0)。</summary>
    [JsonPropertyName("coverage")]
    public double Coverage { get; set; }

    /// <summary>各类型出现次数（主导类型之外的次要类型；可能为空）。</summary>
    [JsonPropertyName("typeDistribution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Dictionary<string, long> TypeDistribution { get; set; } = new();

    /// <summary>数值范围下界，类型为 Number 时填写。</summary>
    [JsonPropertyName("minNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinNumber { get; set; }

    /// <summary>数值范围上界。</summary>
    [JsonPropertyName("maxNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxNumber { get; set; }

    /// <summary>字符串长度下界（无数值字段时为 null）。</summary>
    [JsonPropertyName("minLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinLength { get; set; }

    /// <summary>字符串长度上界。</summary>
    [JsonPropertyName("maxLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; set; }

    /// <summary>最小时间戳（统一为 UTC），非时间字段时为 null。</summary>
    [JsonPropertyName("minDateTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? MinDateTime { get; set; }

    /// <summary>最大时间戳（UTC）。</summary>
    [JsonPropertyName("maxDateTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? MaxDateTime { get; set; }

    /// <summary>枚举候选值（来自规则文件 enumValues，或近似推断的前 N 个高频值）。</summary>
    [JsonPropertyName("enumValues")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? EnumValues { get; set; }

    /// <summary>异常值方法（"iqr" 等），流式样本数不足时为 null。</summary>
    [JsonPropertyName("outlierMethod")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutlierMethod { get; set; }

    /// <summary>异常值比例（0.0~1.0）。</summary>
    [JsonPropertyName("outlierRatio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OutlierRatio { get; set; }

    /// <summary>唯一值数量（精确或 HyperLogLog 近似）。</summary>
    [JsonPropertyName("distinctCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DistinctCount { get; set; }
}
