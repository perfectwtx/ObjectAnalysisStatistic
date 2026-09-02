using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Options;
using ObjectAnalyzer.Analysis.Rules;
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

    /// <summary>
    /// 把规则 JSON 文本反序列化为 AnalysisRules，并在需要时执行未知键检查。
    /// 与 CLI 的 RunPreflight 同源：unknown-keys 是后端静默忽略 STJ 默认行为，
    /// 一旦让它们通过，规则名拼错时用户得不到任何反馈 —— 所以默认直接拦截。
    /// </summary>
    public static (AnalysisRules? rules, ErrorResponse? error) ParseRules(
        string rulesText,
        bool allowUnknownRules)
    {
        if (!allowUnknownRules)
        {
            var unknownRaw = RulesLint.Scan(rulesText);
            if (unknownRaw.Count > 0)
                return (null, ApiErrors.UnknownRules(unknownRaw));
        }

        try
        {
            var rules = JsonSerializer.Deserialize<AnalysisRules>(rulesText, AnalysisRules.RulesJsonOptions);
            return rules is null
                ? (null, new ErrorResponse("规则 JSON 反序列化结果为空。"))
                : ((AnalysisRules? rules, ErrorResponse? error))(rules, null);
        }
        catch (Exception ex)
        {
            return (null, ApiErrors.FromException(ex));
        }
    }

    /// <summary>
    /// 触发 Core 预检（与 Web / CLI 同一份实现），把规则字段与样本字段对比结果原样返回。
    /// 数据库 / 非文件型输入场景在调用方跳过此步骤。
    /// </summary>
    public static PreflightResult Preflight(
        string dataPath,
        AnalysisRules? rules,
        AnalysisOptions options)
    {
        if (rules is null) return PreflightResult.NoWarning();

        // 与 AnalysisFile 保持同一套解析参数（展平 / valueParsers / CSV 推断）
        var parseOptions = new ParseOptions
        {
            Flatten = !string.IsNullOrWhiteSpace(options.Input.RecordPath)
                ? false
                : options.Parse.Flatten,
            ValueParsers = rules.ValueParsers ?? new List<ValueParserConfig>(),
            CsvInferNumbers = options.Parse.CsvInferNumbers
        };
        var format = FileParsers.DetectFormat(dataPath);
        var effectiveFormat = ResolveEffectiveFormat(format, rules);

        return PreflightChecker.Check(dataPath, rules, parseOptions, effectiveFormat);
    }
}
