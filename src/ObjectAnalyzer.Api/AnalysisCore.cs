using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Parsing;
using Analyzer = global::ObjectAnalyzer.Analysis.ObjectAnalyzer;

namespace ObjectAnalyzer.Api;

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

/// <summary>分析请求选项（表单/查询参数绑定）。</summary>
public sealed class AnalyzeRequestOptions
{
    public bool? Flatten { get; init; }
    public int? MaxValuesToShow { get; init; }
    public string? Fields { get; init; }
    public string? Filter { get; init; }
    public string? RootPath { get; init; }
    public string? RecordPath { get; init; }
    public int? SampleReservoir { get; init; }
    public int? SampleStep { get; init; }
    public int? MaxDegreeOfParallelism { get; init; }
}
