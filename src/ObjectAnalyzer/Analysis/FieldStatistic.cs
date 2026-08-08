namespace ObjectAnalyzer;

using System.Collections.Generic;
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

    // 字符串长度统计
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }

    // 智能语义推断（例如 "Email", "UUID", "DateTime", "IPv4", "URL"）
    public string? SemanticType { get; set; }

    // 以下为关于默认值的属性
    public string PrimaryType { get; set; } = "Unknown";
    public bool NullAppeared { get; set; }
    public string? DefaultValueRepresentation { get; set; }

    // 规则校验相关
    public FieldRule? AppliedRule { get; set; }

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
}
