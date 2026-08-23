namespace ObjectAnalyzer.Analysis;

using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

public class FieldStatistic
{
    public string FieldName { get; set; } = "";
    public long Count { get; set; }
    public double Coverage { get; set; } // 字段在所有对象中的覆盖率 (0.0 - 1.0)

    /// <remarks>
    /// 由分析器在内部填充，外部调用者应视为只读。
    /// </remarks>
    public Dictionary<string, long> TypeCounts { get; set; } = new();

    /// <remarks>
    /// 由分析器在内部填充，外部调用者应视为只读。
    /// </remarks>
    public Dictionary<string, long> ValueCounts { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ValueCountsTruncated { get; set; } // 唯一值超阈值被裁剪到 [Other values] 桶

    // 数值统计
    public double? MinNumber { get; set; }
    public double? MaxNumber { get; set; }
    public double? AvgNumber { get; set; }
    public double? SumNumber { get; set; }

    /// <summary>样本标准差（Welford 在线算法，n-1 分母）；样本数 &lt; 2 时为 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? StdDevNumber { get; set; }

    /// <summary>近似中位数 P50（可合并 t-digest 草图）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MedianNumber { get; set; }

    /// <summary>近似 P90。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? P90Number { get; set; }

    /// <summary>近似 P95。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? P95Number { get; set; }

    /// <summary>近似 P99。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? P99Number { get; set; }

    /// <summary>对数定宽（2 的幂）直方图分桶，按桶下界升序，跳过空桶；无数值时为 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<NumericHistogramBucket>? Histogram { get; set; }

    // 字符串长度统计
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }

    /// <summary>平均字符串长度（流式累加 / 出现次数）；无字符串值时为 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AvgLength { get; set; }

    /// <summary>
    /// distinct 值计数：唯一值未截断时为精确计数；超过跟踪阈值时为 HyperLogLog 近似估计
    /// （此时 <see cref="DistinctApproximate"/> 为 true）。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DistinctCount { get; set; }

    /// <summary><see cref="DistinctCount"/> 是否为近似值（HyperLogLog 估计）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DistinctApproximate { get; set; }

    // 智能语义推断（例如 "Email", "UUID", "DateTime", "IPv4", "URL"）
    public string? SemanticType { get; set; }

    // 以下为关于默认值的属性
    public string PrimaryType { get; set; } = "Unknown";
    public bool NullAppeared { get; set; }
    public string? DefaultValueRepresentation { get; set; }

    // 规则校验相关
    public FieldRule? AppliedRule { get; set; }

    // 字段值转换相关
    /// <summary>转换失败次数（保留原始值并记录）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TransformFailureCount { get; set; }

    /// <summary>应用的转换类型（如 jwt / aes / base64 / url）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransformType { get; set; }

    // Fix #8: EnumViolations 封装为只读接口，防止外部调用者意外修改分析结果
    private readonly List<string> _enumViolations = new();

    /// <summary>
    /// 不符合枚举约束的实际值列表。只读，由分析器内部填充。
    /// </summary>
    public IReadOnlyList<string> EnumViolations => _enumViolations;

    /// <summary>
    /// 供分析器内部使用，向枚举违规列表追加一个违规值。
    /// </summary>
    internal void AddEnumViolation(string value) => _enumViolations.Add(value);

    /// <summary>格式化分位数摘要（"P50 x · P90 y · P95 z · P99 w"）；无分位数数据时返回 null。</summary>
    public string? FormatQuantiles()
    {
        if (MedianNumber is null || P90Number is null || P95Number is null || P99Number is null)
            return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"P50 {MedianNumber:G4} · P90 {P90Number:G4} · P95 {P95Number:G4} · P99 {P99Number:G4}");
    }
}

/// <summary>数值直方图分桶（对数定宽，桶边界为 2 的幂；零值单独成桶 From=To=0）。</summary>
public sealed class NumericHistogramBucket
{
    /// <summary>桶下界（含）。正数桶与零桶为闭区间下界；负数桶表示开区间下界。</summary>
    public double From { get; init; }

    /// <summary>桶上界。正数桶为开区间上界，负数桶为闭区间上界。</summary>
    public double To { get; init; }

    /// <summary>落入该桶的数值出现次数。</summary>
    public long Count { get; set; }

    /// <summary>紧凑区间标签："0"、"[4,8)"（正数桶，左闭右开）、"(-8,-4]"（负数桶，左开右闭）。</summary>
    public override string ToString()
    {
        if (From == 0 && To == 0)
            return "0";
        return From > 0
            ? string.Create(CultureInfo.InvariantCulture, $"[{From:G3},{To:G3})")
            : string.Create(CultureInfo.InvariantCulture, $"({From:G3},{To:G3}]");
    }
}
