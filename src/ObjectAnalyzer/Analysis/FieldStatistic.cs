namespace ObjectAnalyzer;

public class FieldStatistic
{
    public string FieldName { get; set; } = "";
    public int Count { get; set; }
    public double Coverage { get; set; } // 字段在所有对象中的覆盖率 (0.0 - 1.0)
    public Dictionary<string, int> TypeCounts { get; set; } = new();
    public Dictionary<string, int> ValueCounts { get; set; } = new();

    // 以下为关于默认值的属性
    public string PrimaryType { get; set; } = "Unknown";
    public bool NullAppeared { get; set; }
    public string? DefaultValueRepresentation { get; set; }

    // 规则校验相关
    public FieldRule? AppliedRule { get; set; }
    public List<string> EnumViolations { get; set; } = new(); // 不符合枚举约束的实际值
}
