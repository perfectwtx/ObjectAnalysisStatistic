using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Analysis.Options;
using ObjectAnalyzer.Analysis.Pipeline;
using ObjectAnalyzer.Analysis.Rules;
using ObjectAnalyzer.Parsing;
using ObjectAnalyzer.Reporting;

namespace ObjectAnalyzer.Api;

// ── 分析执行核心：规则解析 → 预检 → W1 统一分析管线（解析 → 投影 → 转换/过滤 → 采样 → 统计）──
internal static class AnalysisCore
{
    /// <summary>
    /// 分析执行结果：结果本体 + 数据预览样本（W8 五页面视图的"数据预览"来源）。
    /// <para>
    /// <see cref="Result"/> 与 <see cref="Preview"/> 已经过出站打码关卡（见 <see cref="AnalyzeFile"/>）；
    /// <see cref="Pii"/> 是本次运行解析出的打码策略，<see cref="PiiRedacted"/> 供响应回显留痕。
    /// </para>
    /// </summary>
    internal sealed record AnalyzeOutcome(
        AnalysisResult Result,
        IReadOnlyList<Dictionary<string, object?>> Preview,
        string? EffectiveFormat,
        PiiPolicy Pii,
        bool PiiRedacted);

    /// <summary>预览捕获条数（与 AnalyzePreviewView.DefaultLimit 对齐）。</summary>
    internal const int PreviewLimit = AnalyzePreviewView.DefaultLimit;

    public static AnalyzeOutcome AnalyzeFile(
        string dataPath,
        AnalysisRules? rules,
        AnalysisOptions options,
        CancellationToken cancellationToken,
        IProgress<long>? progressBridge = null)
    {
        // W1 统一管线：解析分发（含 root-path / record-path 投影）、采样顺序、
        // 缺陷 1/2 修复全部收敛在 AnalysisPipeline，API 不再维护自有流程。
        // 格式由管线从 dataPath 探测（含 valueParsers 驱动的 excel-json 切换）。
        // W8：经 PreviewSink 捕获流经管线的前 N 条记录（转换/过滤后），供前端"数据预览"页；
        //     异步作业经 progressBridge 把已处理记录数桥接到 AnalysisJobRegistry（SSE / 轮询）。
        var preview = new List<Dictionary<string, object?>>(PreviewLimit);
        var runContext = new AnalysisRunContext
        {
            ProgressBridge = progressBridge,
            PreviewSink = captured => preview.AddRange(captured),
            PreviewLimit = PreviewLimit
        };
        var pipelineResult = AnalysisPipeline.Execute(options, rules, runContext, cancellationToken);

        // ── 出站打码关卡（delivery boundary）──
        // 分析内核始终处理原始值；任何结果离开服务前在这里统一按策略打码，
        // 覆盖全部同步 / 异步端点（新增端点天然继承，无需各自记得调用）。
        // piiOff=true 是显式退出：原始敏感值出站，PiiRedacted=false 留痕并写服务器日志。
        var policy = PiiPolicy.Resolve(pipelineResult.Result);
        if (options.Pii.PiiOff)
        {
            Console.Error.WriteLine($"[pii] 出站打码已关闭（piiOff=true），原始敏感值随响应出站：{dataPath}");
            return new AnalyzeOutcome(pipelineResult.Result, preview, runContext.EffectiveFormat,
                policy, PiiRedacted: false);
        }

        return new AnalyzeOutcome(
            PiiRedactor.Redact(pipelineResult.Result, policy),
            PreviewRedactor.Redact(preview, policy),
            runContext.EffectiveFormat,
            policy,
            PiiRedacted: true);
    }

    /// <summary>
    /// 响应回显用的实际解析格式：与管线内部 <see cref="AnalysisPipeline.ResolveEffectiveFormat"/> 同源。
    /// </summary>
    internal static string ResolveEffectiveFormat(string dataPath, AnalysisRules? rules, string? formatOverride)
        => AnalysisPipeline.ResolveEffectiveFormat(dataPath, rules, formatOverride);

    /// <summary>
    /// 规则 JSON 文本反序列化为 AnalysisRules，并在需要时执行未知键检查。
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

        // 与管线同一套解析参数口径（展平 / valueParsers / CSV 推断）
        var parseOptions = new ParseOptions
        {
            Flatten = !string.IsNullOrWhiteSpace(options.Input.RecordPath)
                ? false
                : options.Parse.Flatten,
            ValueParsers = rules.ValueParsers ?? new List<ValueParserConfig>(),
            CsvInferNumbers = options.Parse.CsvInferNumbers
        };
        var format = FileParsers.DetectFormat(dataPath);
        var effectiveFormat = AnalysisPipeline.ResolveEffectiveFormat(dataPath, rules, formatOverride: null);

        return PreflightChecker.Check(dataPath, rules, parseOptions, effectiveFormat);
    }
}
