using System.ComponentModel;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Parsing;
using Analyzer = global::ObjectAnalyzer.Analysis.ObjectAnalyzer;

namespace ObjectAnalyzer.Api;

/// <summary>multipart 分析表单（字段注释会进入 OpenAPI / Scalar 文档）。</summary>
public sealed class AnalyzeForm
{
    /// <summary>数据文件：JSON / JSONL / CSV / Excel / XML / YAML（可直接上传 gzip/bzip2/zip 压缩包）。</summary>
    [Description("数据文件（JSON/JSONL/CSV/Excel/XML/YAML，支持压缩包）")]
    public IFormFile? File { get; init; }

    /// <summary>规则文件（.json，支持 // 注释；与 rulesJson 二选一）。</summary>
    [Description("规则文件（.json，支持 // 注释）")]
    public IFormFile? Rules { get; init; }

    /// <summary>内联规则 JSON 文本（优先于规则文件）。</summary>
    [Description("内联规则 JSON（优先于规则文件）")]
    public string? RulesJson { get; init; }

    /// <summary>规则里出现后端不认识的键时仍然继续分析（默认 false：直接报 400）。</summary>
    [Description("容忍规则里不认识的键（默认 false，拼错会报错）")]
    public bool? AllowUnknownRules { get; init; }

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

    // ── P4 深度分析开关（默认全关，行为与 v1.x 一致，无额外开销）──

    /// <summary>输出数值字段 Pearson / Spearman 相关性矩阵。</summary>
    [Description("启用字段相关性分析")]
    public bool? EnableCorrelation { get; init; }

    /// <summary>归纳字符串字段形态模板（如 ###-####）。</summary>
    [Description("启用字符串模式归纳")]
    public bool? EnableStringPatterns { get; init; }

    /// <summary>检查全/半角、不可见字符、控制字符等 Unicode 卫生问题。</summary>
    [Description("启用 Unicode 卫生检查")]
    public bool? EnableUnicodeHygiene { get; init; }

    /// <summary>对 DateTime 字段做趋势 / 周期 / 间隙分析。</summary>
    [Description("启用时间序列分析")]
    public bool? EnableTimeSeries { get; init; }

    /// <summary>导出分布快照，用于漂移检测。</summary>
    [Description("启用分布快照导出")]
    public bool? EnableDistributionSnapshot { get; init; }
}

// ── 分析执行核心：保存输入 → 解析 → 字段选择/转换/过滤 → 统计 ──
internal static class AnalysisCore
{
    public static AnalysisResult AnalyzeFile(
        string dataPath,
        string format,
        AnalysisRules? rules,
        AnalyzeRequestOptions options,
        CancellationToken cancellationToken)
    {
        var parseOptions = new ParseOptions
        {
            Flatten = options.Flatten ?? true,
            ValueParsers = rules?.ValueParsers ?? new List<ValueParserConfig>(),
            CsvInferNumbers = options.CsvInferNumbers ?? true,
            SampleReservoir = options.SampleReservoir ?? 0,
            SampleStep = options.SampleStep ?? 0
        };

        string effectiveFormat = ResolveEffectiveFormat(format, rules);

        IEnumerable<Dictionary<string, object?>> objects =
            FileParsers.ParseByFormat(dataPath, effectiveFormat, parseOptions);

        var selectedFields = options.Fields?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var processor = new FieldTransformProcessor(rules, selectedFields, options.Filter);
        if (processor.HasSelectionOrTransforms)
            objects = processor.Wrap(objects);

        if (Sampling.IsEnabled(options.SampleReservoir ?? 0, options.SampleStep ?? 0)
            && effectiveFormat is not ("jsonl" or "csv" or "excel" or "excel-json"))
        {
            objects = Sampling.Apply(objects, options.SampleReservoir ?? 0, options.SampleStep ?? 0);
        }

        return Analyzer.Analyze(objects, rules,
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism ?? -1 },
            BuildFeatures(options),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 解析格式兜底：规则里配置了 valueParsers（Excel / CSV 单元格内嵌 JSON）时，
    /// 必须切到 excel-json 解析器 —— 否则单元格只会被当成纯文本读出，
    /// 后续的 fields.transform 也永远匹配不上（字段还叫 "A"，不是 "A.body"）。
    /// 与 CLI 的 AnalysisRunner.ResolveEffectiveFormat 保持同一套规则，避免两边行为分叉。
    /// </summary>
    internal static string ResolveEffectiveFormat(string format, AnalysisRules? rules)
        => rules?.ValueParsers is { Count: > 0 } ? "excel-json" : format;

    /// <summary>把请求参数映射为 P4 特性开关；全未指定时返回 null（保持默认行为）。</summary>
    private static AnalysisFeatures? BuildFeatures(AnalyzeRequestOptions o)
    {
        if (o.EnableCorrelation is null
            && o.EnableStringPatterns is null
            && o.EnableUnicodeHygiene is null
            && o.EnableTimeSeries is null
            && o.EnableDistributionSnapshot is null)
        {
            return null;
        }

        return new AnalysisFeatures
        {
            EnableCorrelation = o.EnableCorrelation ?? false,
            EnableStringPatterns = o.EnableStringPatterns ?? false,
            EnableUnicodeHygiene = o.EnableUnicodeHygiene ?? false,
            EnableTimeSeries = o.EnableTimeSeries ?? false,
            EnableDistributionSnapshot = o.EnableDistributionSnapshot ?? false,
        };
    }
}

/// <summary>分析请求选项（multipart 表单 / 查询参数绑定，注释会进入 OpenAPI 文档）。</summary>
public sealed class AnalyzeRequestOptions
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

    // ── P4 深度分析开关（默认全关，行为与 v1.x 一致，无额外开销）──

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
}
