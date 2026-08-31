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
        AnalyzeRequestOptions request,
        CancellationToken cancellationToken)
    {
        // 方案 A：把 rules.runtime 节点配置合并到 options（与 CLI 同源、同优先级链）。
        // 流程：先把 AnalyzeRequestOptions（可空）展开为 AnalysisOptions（带默认）→
        //      调用 ApplyRuntime 仅覆盖"默认"字段 →
        //      把合并后的值用回 AnalyzeRequestOptions，让下游代码无需感知。
        if (rules?.Runtime != null)
        {
            var merged = BuildAnalysisOptionsFromRequest(dataPath, request);
            merged.ApplyRuntime(rules.Runtime);
            request = MergeBack(merged, request);
        }

        var parseOptions = new ParseOptions
        {
            Flatten = request.Flatten ?? true,
            ValueParsers = rules?.ValueParsers ?? new List<ValueParserConfig>(),
            CsvInferNumbers = request.CsvInferNumbers ?? true,
            SampleReservoir = request.SampleReservoir ?? 0,
            SampleStep = request.SampleStep ?? 0
        };

        string effectiveFormat = ResolveEffectiveFormat(format, rules);

        IEnumerable<Dictionary<string, object?>> objects =
            FileParsers.ParseByFormat(dataPath, effectiveFormat, parseOptions);

        var selectedFields = request.Fields?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var processor = new FieldTransformProcessor(rules, selectedFields, request.Filter);
        if (processor.HasSelectionOrTransforms)
            objects = processor.Wrap(objects);

        if (Sampling.IsEnabled(request.SampleReservoir ?? 0, request.SampleStep ?? 0)
            && effectiveFormat is not ("jsonl" or "csv" or "excel" or "excel-json"))
        {
            objects = Sampling.Apply(objects, request.SampleReservoir ?? 0, request.SampleStep ?? 0);
        }

        return Analyzer.Analyze(objects, rules,
            new ParallelOptions { MaxDegreeOfParallelism = request.MaxDegreeOfParallelism ?? -1 },
            BuildFeatures(request),
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

    /// <summary>
    /// 把 <see cref="AnalyzeRequestOptions"/>（全可空）展开为 <see cref="AnalysisOptions"/>（带默认）。
    /// 调用方已显式提供的值映射到对应字段；未提供的字段保持 <see cref="AnalysisOptions"/> 的内置默认，
    /// 这样 <see cref="AnalysisOptionsExtensions.ApplyRuntime"/> 才能正确判断"哪些字段还是默认"。
    /// </summary>
    private static AnalysisOptions BuildAnalysisOptionsFromRequest(string dataPath, AnalyzeRequestOptions req)
    {
        return new AnalysisOptions
        {
            InputPath = dataPath,
            Flatten = req.Flatten ?? true,
            CsvInferNumbers = req.CsvInferNumbers ?? true,
            MaxValuesToShow = req.MaxValuesToShow ?? 5,
            Fields = req.Fields,
            Filter = req.Filter,
            RootPath = req.RootPath,
            RecordPath = req.RecordPath,
            SampleReservoir = req.SampleReservoir ?? 0,
            SampleStep = req.SampleStep ?? 0,
            MaxDegreeOfParallelism = req.MaxDegreeOfParallelism ?? -1,
            EnableCorrelation = req.EnableCorrelation ?? false,
            EnableStringPatterns = req.EnableStringPatterns ?? false,
            EnableUnicodeHygiene = req.EnableUnicodeHygiene ?? false,
            EnableTimeSeries = req.EnableTimeSeries ?? false,
        };
    }

    /// <summary>
    /// 把 ApplyRuntime 合并后的 <see cref="AnalysisOptions"/> 关键字段回写到 <see cref="AnalyzeRequestOptions"/>。
    /// 仅回写 ApplyRuntime 实际覆盖的字段集合（P4 / Sampling / JsonPath），其他字段保持原样。
    /// </summary>
    private static AnalyzeRequestOptions MergeBack(AnalysisOptions merged, AnalyzeRequestOptions original)
    {
        return new AnalyzeRequestOptions
        {
            Flatten = original.Flatten, // 由后续 parseOptions 处理
            CsvInferNumbers = original.CsvInferNumbers,
            MaxValuesToShow = original.MaxValuesToShow ?? merged.MaxValuesToShow,
            Fields = string.IsNullOrEmpty(original.Fields) ? (string.IsNullOrEmpty(merged.Fields) ? null : merged.Fields) : original.Fields,
            Filter = string.IsNullOrWhiteSpace(original.Filter) ? merged.Filter : original.Filter,
            RootPath = string.IsNullOrWhiteSpace(original.RootPath) ? merged.RootPath : original.RootPath,
            RecordPath = string.IsNullOrWhiteSpace(original.RecordPath) ? merged.RecordPath : original.RecordPath,
            SampleReservoir = original.SampleReservoir ?? (merged.SampleReservoir > 0 ? merged.SampleReservoir : null),
            SampleStep = original.SampleStep ?? (merged.SampleStep > 0 ? merged.SampleStep : null),
            MaxDegreeOfParallelism = original.MaxDegreeOfParallelism ?? (merged.MaxDegreeOfParallelism >= 1 ? merged.MaxDegreeOfParallelism : null),
            EnableCorrelation = original.EnableCorrelation ?? merged.EnableCorrelation,
            EnableStringPatterns = original.EnableStringPatterns ?? merged.EnableStringPatterns,
            EnableUnicodeHygiene = original.EnableUnicodeHygiene ?? merged.EnableUnicodeHygiene,
            EnableTimeSeries = original.EnableTimeSeries ?? merged.EnableTimeSeries,
            EnableDistributionSnapshot = original.EnableDistributionSnapshot
                ?? (!string.IsNullOrEmpty(merged.DistributionSnapshotPath)),
        };
    }
}
