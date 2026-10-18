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
    /// <see cref="Result"/> 与 <see cref="Preview"/> 已经过出站打码关卡（见 <see cref="AnalyzeFileAsync"/>）；
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

    public static async Task<AnalyzeOutcome> AnalyzeFileAsync(
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
        var pipelineResult = await AnalysisRuntime.CreateExecutor().ExecuteAsync(options, rules, runContext, cancellationToken).ConfigureAwait(false);

        // ── 出站打码关卡（delivery boundary）──
        // 分析内核始终处理原始值；任何结果离开服务前在这里统一按策略打码，
        // 覆盖全部同步 / 异步端点（新增端点天然继承，无需各自记得调用）。
        // piiOff=true 是显式退出：原始敏感值出站，PiiRedacted=false 留痕并写审计 / 服务器日志。
        var delivery = ResultProjection.Create(pipelineResult.Result, preview, redact: !options.Pii.PiiOff);
        var policy = delivery.Policy;
        if (options.Pii.PiiOff)
        {
            Console.Error.WriteLine($"[pii] 出站打码已关闭（piiOff=true），原始敏感值随响应出站：{dataPath}");
            WritePiiOffAudit(options, dataPath);
            return new AnalyzeOutcome(delivery.Result, delivery.Preview, runContext.EffectiveFormat,
                policy, PiiRedacted: false);
        }

        return new AnalyzeOutcome(
            delivery.Result,
            delivery.Preview,
            runContext.EffectiveFormat,
            policy,
            PiiRedacted: true);
    }

    /// <summary>
    /// piiOff=true 的结构化审计留痕：仅当请求方配置了审计日志路径
    /// （CLI --audit-log / 规则 runtime.audit.log，经 RulesToOptionsConverter 合并进 options）时落盘，
    /// 事件类型 pii_off，只记输入哈希 / 字节数与说明，绝不记字段值（与审计 schema 同口径）。
    /// 审计写入失败不阻断分析主流程。
    /// </summary>
    private static void WritePiiOffAudit(AnalysisOptions options, string dataPath)
    {
        string? auditPath = options.Audit.AuditLogPath;
        if (string.IsNullOrWhiteSpace(auditPath))
            return;

        try
        {
            using var audit = new AuditLogger(auditPath);
            audit.Write(new AuditLogEntry
            {
                Timestamp = AuditLogger.FormatTimestamp(DateTime.UtcNow),
                Event = "pii_off",
                RunId = AuditLogger.NewRunId(),
                InputSource = dataPath,
                InputHash = AuditLogger.ComputeFileHash(dataPath),
                InputBytes = AuditLogger.GetFileSize(dataPath),
                Message = "出站打码已关闭（piiOff=true）：原始敏感值随响应出站，请确认分发渠道可信。"
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pii] piiOff 审计写入失败（不阻断分析）：{ex.Message}");
        }
    }

    /// <summary>
    /// 读取 /analyze/config 与 /analyze/async/config 的纯 JSON 请求体。
    /// 与 multipart / raw 端点同源的未知键防护：强类型绑定会静默忽略不认识的键
    /// （错层级的 pii.maskChar、拼错的规则名「看似生效实则无效」），
    /// 因此先取原始体里的 rules 子树跑 RulesLint（默认 400 + unknowns），再反序列化为强类型请求。
    /// </summary>
    public static async Task<(AnalyzeConfigRequest? config, ErrorResponse? error)> ReadConfigRequestAsync(
        HttpRequest request, CancellationToken cancellationToken)
    {
        string body;
        using (var reader = new StreamReader(request.Body, System.Text.Encoding.UTF8))
            body = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(body))
            return (null, new ErrorResponse("请求体为空：请提供 JSON 对象（dataText 必填）。"));

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            return (null, new ErrorResponse($"请求体不是有效 JSON：{ex.Message}"));
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, new ErrorResponse("请求体必须是一个 JSON 对象。"));

            if (doc.RootElement.TryGetProperty("rules", out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Object)
            {
                // GetRawText 保留未知键原文，lint 才能发现强类型绑定已丢失的键
                var unknownRaw = RulesLint.Scan(rulesEl.GetRawText());
                if (unknownRaw.Count > 0)
                    return (null, ApiErrors.UnknownRules(unknownRaw));
            }

            try
            {
                // 与 [FromBody] 完全同源的序列化选项（宿主 JsonOptions）
                var serializerOptions = request.HttpContext.RequestServices
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                    .Value.SerializerOptions;
                var config = JsonSerializer.Deserialize<AnalyzeConfigRequest>(body, serializerOptions);
                return (config, null);
            }
            catch (JsonException ex)
            {
                return (null, new ErrorResponse($"请求体反序列化失败：{ex.Message}"));
            }
        }
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
