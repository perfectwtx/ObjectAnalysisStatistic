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

    /// <summary>是否把嵌套 JSON 的属性展开为 a.b.c 路径后再统计（默认 true）。</summary>
    [Description("是否展开嵌套字段为 a.b.c 路径（默认 true）")]
    public bool? Flatten { get; init; }

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
            SampleReservoir = options.SampleReservoir ?? 0,
            SampleStep = options.SampleStep ?? 0
        };

        IEnumerable<Dictionary<string, object?>> objects =
            FileParsers.ParseByFormat(dataPath, format, parseOptions);

        var selectedFields = options.Fields?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var processor = new FieldTransformProcessor(rules, selectedFields, options.Filter);
        if (processor.HasSelectionOrTransforms)
            objects = processor.Wrap(objects);

        if (Sampling.IsEnabled(options.SampleReservoir ?? 0, options.SampleStep ?? 0)
            && format is not ("jsonl" or "csv" or "excel" or "excel-json"))
        {
            objects = Sampling.Apply(objects, options.SampleReservoir ?? 0, options.SampleStep ?? 0);
        }

        return Analyzer.Analyze(objects, rules,
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism ?? -1 },
            cancellationToken: cancellationToken);
    }
}

/// <summary>分析请求选项（multipart 表单 / 查询参数绑定，注释会进入 OpenAPI 文档）。</summary>
public sealed class AnalyzeRequestOptions
{
    /// <summary>是否把嵌套 JSON 的属性展开为 a.b.c 路径后再统计（默认 true）。</summary>
    [Description("是否展开嵌套字段为 a.b.c 路径（默认 true）")]
    public bool? Flatten { get; init; }

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
}
