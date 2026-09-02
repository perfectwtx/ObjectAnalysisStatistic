using System.ComponentModel;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Options;

namespace ObjectAnalyzer.Api;

/// <summary>分析请求选项（multipart 表单 / 查询参数 / JSON body 绑定，注释会进入 OpenAPI 文档）。</summary>
/// <remarks>
/// 全字段可空：null 表示"未指定"——由 <see cref="ToAnalysisOptions"/> 用 <see cref="AnalysisOptions"/> 的内置默认填充。
/// 规则文件 (rules.runtime) 通过 <see cref="RulesToOptionsConverter.Apply"/> 一次性合并到 <see cref="AnalysisOptions"/> 对应子组；
/// 合并只覆盖"未显式指定"的字段（保持请求的优先级最高）。
/// </remarks>
public class AnalyzeRequestOptions
{
    /// <summary>是否把嵌套 JSON 的属性展开为 a.b.c 路径后再统计（默认 true）。</summary>
    [Description("是否展开嵌套字段为 a.b.c 路径（默认 true）")]
    public bool? Flatten { get; init; }

    /// <summary>CSV 数字列是否推断为数值类型（默认 true）。关掉后一律按字符串统计。</summary>
    [Description("CSV 数字列推断为数值类型（默认 true，关掉则按字符串统计）")]
    public bool? CsvInferNumbers { get; init; }

    /// <summary>控制台报告每字段最多展示的唯一值数量。</summary>
    [Description("每字段最多展示的唯一值数量（默认 5）")]
    public int? MaxValuesToShow { get; init; }

    /// <summary>仅统计这些字段（逗号分隔），其余字段忽略。</summary>
    [Description("仅统计这些字段（逗号分隔，如 name,age）")]
    public string? Fields { get; init; }

    /// <summary>全局过滤表达式：只统计满足条件的记录。</summary>
    [Description("过滤表达式，如 age >= 18 AND status in ['active']")]
    public string? Filter { get; init; }

    /// <summary>文件根 JSONPath：从整体 JSON 提取子节点（仅 JSON 文件生效）。</summary>
    [Description("文件根 JSONPath，如 $.data[*]（仅整体 JSON 生效）")]
    public string? RootPath { get; init; }

    /// <summary>记录级 JSONPath：在每条记录上提取子对象作为新记录。</summary>
    [Description("记录级 JSONPath，如 $.payload（每条记录生效）")]
    public string? RecordPath { get; init; }

    /// <summary>蓄水池随机采样：最多分析的记录数（0 = 关闭）。</summary>
    [Description("蓄水池随机采样最大记录数（0 = 关闭）")]
    public int? SampleReservoir { get; init; }

    /// <summary>等距系统采样：每 N 条记录取 1 条（0 = 关闭）。</summary>
    [Description("等距采样步长，每 N 条取 1 条（0 = 关闭）")]
    public int? SampleStep { get; init; }

    /// <summary>最大并行线程数（-1 = 使用全部核心）。</summary>
    [Description("最大并行线程数（-1 = 全部核心）")]
    public int? MaxDegreeOfParallelism { get; init; }

    // ── 深度分析开关（默认全关，行为与 v1.x 一致，无额外开销）──

    /// <summary>输出数值字段 Pearson / Spearman 相关性矩阵。</summary>
    public bool? EnableCorrelation { get; init; }

    /// <summary>归纳字符串字段形态模板（如 ###-####）。</summary>
    public bool? EnableStringPatterns { get; init; }

    /// <summary>检查全/半角、不可见字符、控制字符等 Unicode 卫生问题。</summary>
    public bool? EnableUnicodeHygiene { get; init; }

    /// <summary>对 DateTime 字段做趋势 / 周期 / 间隙分析。</summary>
    public bool? EnableTimeSeries { get; init; }

    /// <summary>导出分布快照，用于漂移检测。</summary>
    public bool? EnableDistributionSnapshot { get; init; }

    /// <summary>
    /// 把请求选项（可空）展开为 <see cref="AnalysisOptions"/>（带默认），
    /// 并按需把规则文件 runtime 节点合并进来——返回的对象可直接喂给分析流水线。
    /// </summary>
    public AnalysisOptions ToAnalysisOptions(string dataPath, AnalysisRules? rules = null)
    {
        var options = new AnalysisOptions
        {
            Input =
            {
                InputPath = dataPath,
                RootPath = RootPath,
                RecordPath = RecordPath
            },
            Parse =
            {
                Flatten = Flatten ?? true,
                CsvInferNumbers = CsvInferNumbers ?? true,
                MaxValuesToShow = MaxValuesToShow ?? 5,
                MaxDegreeOfParallelism = MaxDegreeOfParallelism ?? -1
            },
            FieldSelection = { Fields = Fields, Filter = Filter },
            Sampling = { SampleReservoir = SampleReservoir ?? 0, SampleStep = SampleStep ?? 0 },
            Advanced =
            {
                EnableCorrelation = EnableCorrelation ?? false,
                EnableStringPatterns = EnableStringPatterns ?? false,
                EnableUnicodeHygiene = EnableUnicodeHygiene ?? false,
                EnableTimeSeries = EnableTimeSeries ?? false,
                EnableDistributionSnapshot = EnableDistributionSnapshot ?? false
            }
        };

        if (rules?.Runtime != null)
        {
            RulesToOptionsConverter.Apply(options, rules);
        }

        return options;
    }
}
