using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Options;
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
        AnalysisOptions options,
        CancellationToken cancellationToken)
    {
        // 解析前按"record-path 强制不展平"的口径决定 Flatten：
        // 提前展平会让 $.foo.bar 这种路径找不到目标
        // （与 AnalysisRunner.ResolveFlatten 行为一致，CLI / API 同源）。
        bool flatten = !string.IsNullOrWhiteSpace(options.Input.RecordPath)
            ? false
            : options.Parse.Flatten;

        var parseOptions = new ParseOptions
        {
            Flatten = flatten,
            ValueParsers = rules?.ValueParsers ?? new List<ValueParserConfig>(),
            CsvInferNumbers = options.Parse.CsvInferNumbers,
            SampleReservoir = options.Sampling.SampleReservoir,
            SampleStep = options.Sampling.SampleStep
        };

        string effectiveFormat = ResolveEffectiveFormat(format, rules);

        IEnumerable<Dictionary<string, object?>> objects =
            FileParsers.ParseByFormat(dataPath, effectiveFormat, parseOptions);

        var selectedFields = string.IsNullOrWhiteSpace(options.FieldSelection.Fields)
            ? null
            : options.FieldSelection.Fields
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var processor = new FieldTransformProcessor(rules, selectedFields, options.FieldSelection.Filter);
        if (processor.HasSelectionOrTransforms)
            objects = processor.Wrap(objects);

        if (Sampling.IsEnabled(options.Sampling.SampleReservoir, options.Sampling.SampleStep)
            && effectiveFormat is not ("jsonl" or "csv" or "excel" or "excel-json"))
        {
            objects = Sampling.Apply(objects, options.Sampling.SampleReservoir, options.Sampling.SampleStep);
        }

        return Analyzer.Analyze(objects, rules,
            new ParallelOptions { MaxDegreeOfParallelism = options.Parse.MaxDegreeOfParallelism },
            options.Advanced,
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
}
