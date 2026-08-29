namespace ObjectAnalyzer.Analysis;

/// <summary>质量检查类型常量（FieldViolation.Check 取值）。</summary>
public static class QualityCheckTypes
{
    public const string EnumValues = "enum";
    public const string Range = "range";
    public const string Pattern = "pattern";
    public const string Required = "required";
    public const string Unique = "unique";
    public const string NullRate = "nullRate";
    public const string RowCount = "rowCount";
    public const string DuplicateRate = "duplicateRate";
    public const string OutlierRatio = "outlierRatio";

    /// <summary>检查类型的中文标签（控制台/报告展示用）。</summary>
    public static string Label(string check) => check switch
    {
        EnumValues => "枚举违规",
        Range => "数值越界",
        Pattern => "模式不匹配",
        Required => "必填缺失",
        Unique => "唯一性冲突",
        NullRate => "null 率超限",
        RowCount => "行数不足",
        DuplicateRate => "重复率超限",
        OutlierRatio => "异常值超限",
        _ => check
    };
}
