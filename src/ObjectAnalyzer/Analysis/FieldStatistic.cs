namespace ObjectAnalyzer;

using System.Text.Json.Serialization;

public class FieldStatistic
{
    public string FieldName { get; set; } = "";
    public int Count { get; set; }
    public double Coverage { get; set; } // 字段在所有对象中的覆盖率 (0.0 - 1.0)
    public Dictionary<string, int> TypeCounts { get; set; } = new();
    public Dictionary<string, int> ValueCounts { get; set; } = new();
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
    public List<string> EnumViolations { get; set; } = new(); // 不符合枚举约束的实际值
}
