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
