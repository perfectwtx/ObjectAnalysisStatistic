namespace ObjectAnalyzer.Analysis.Options;

/// <summary>
/// 解析行为：嵌套属性展平、CSV 数值推断、每字段最多展示数、并行度。
/// </summary>
public sealed class ParseBehaviorOptions
{
    public MalformedRecordPolicy MalformedRecords { get; set; }
    public int InputDiagnosticLimit { get; set; } = 100;

    /// <summary>嵌套属性是否展平为 <c>a.b.c</c>。默认 true。与 <c>--no-flatten</c> 取反对应。</summary>
    public bool Flatten { get; set; } = true;

    /// <summary>CSV 是否把"看起来是数字"的字符串推断为 long / double。默认 true —— 不推断的话 CSV 数值列一律是 String，均值/标准差/分位数/相关性全部失效。</summary>
    public bool CsvInferNumbers { get; set; } = true;

    /// <summary>每字段最多展示的 distinct 数（控制台 / 报告）。与 <c>--max-values</c> 对应。</summary>
    public int MaxValuesToShow { get; set; } = 5;

    /// <summary>
    /// 每字段唯一值跟踪上限（超出裁剪进 [Other values] 桶）。与 MaxValuesToShow 正交：
    /// 后者只影响控制台展示数量，本项决定分析引擎实际跟踪的唯一值规模（影响内存与精度）。
    /// </summary>
    public int MaxTrackedValues { get; set; } = 1000;

    /// <summary>最大并行线程数，-1 = 使用所有核。与 <c>--parallel</c> 对应。</summary>
    public int MaxDegreeOfParallelism { get; set; } = -1;
}
