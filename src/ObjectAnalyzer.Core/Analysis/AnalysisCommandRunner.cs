using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ObjectAnalyzer.Analysis.Rules;
using ObjectAnalyzer.Security;

namespace ObjectAnalyzer.Analysis;

public sealed partial class AnalysisCommandRunner
{
    private readonly AnalysisServices _services;
    public AnalysisCommandRunner(AnalysisServices? services = null) => _services = services ?? AnalysisServices.Silent;

    private static readonly JsonSerializerOptions s_diffJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Runs the analysis pipeline. Returns 0 on success, 1 on failure, 2 on cancellation.
    /// 当 <see cref="AnalysisOptions.AuditLogPath"/> 非空时，写入 NDJSON 审计日志。
    /// </summary>
    public int Run(AnalysisOptions options, CancellationToken cancellationToken = default)
    {
        options = AnalysisAccumulator.Clone(options);
        AuditLogger? audit = null;
        string runId = AuditLogger.NewRunId();
        var sw = new StopwatchLike();
        try
        {
            if (!string.IsNullOrWhiteSpace(options.Audit.AuditLogPath))
            {
                audit = new AuditLogger(options.Audit.AuditLogPath);
            }
        }
        catch (Exception ex)
        {
            // 审计日志路径不可写不应阻塞分析主流程；但要给操作者明确提示
            _services.Console.MarkupLine(
                $"[yellow]警告: 审计日志初始化失败 ({MarkupText.Escape(ex.Message)})；本次运行不写入审计记录。[/]");
            audit = null;
        }

        try { return RunCore(options, audit, runId, sw, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 2; }
        catch (Exception exception) { PrintError(exception); return 1; }
        finally { audit?.Dispose(); }
    }

    private int RunCore(
        AnalysisOptions options,
        AuditLogger? audit,
        string runId,
        StopwatchLike sw,
        CancellationToken cancellationToken)
    {
        // W7：编排按阶段拆分——Preflight（校验/规则/续跑快照）→ 统一管线执行 →
        // Resume 合并 → 报告与门禁。各阶段实现见 AnalysisRunner.{Preflight,Resume,ReportAndGates}.cs。
        var preflight = RunPreflight(options);
        if (!preflight.Ok)
            return preflight.ExitCode;

        AnalysisRules? rules = preflight.Rules;

        // 输入存在性与规则加载通过后，审计可正式记录 start 事件（不计入参数校验失败等）
        WriteAuditStart(audit, options, rules, runId);

        try
        {
            // P6.3：内存预算入口预检。在加载任何大对象前采样一次托管堆占用——
            // 目的：1) 硬模式下若启动时已超阈值，立即以退出码 1 失败，避免分析半途才发现；
            //       2) 软模式下若已超阈值且用户未指定采样，自动启用蓄水池兜底。
            // 必须在 try 块内：硬模式抛 MaxMemoryExceededException，由下方 catch 转为退出码 1。
            MemoryBudgetEntryCheck(preflight.MemoryBudget, options);

            // W1 统一分析管线：解析 → JSONPath 投影 → 字段选择/转换/过滤 → 采样 → 统计。
            // 运行期状态（内存预算 / 续跑位置）随 AnalysisRunContext 传入，回填 EffectiveFormat / LastRecordCount。
            var runContext = new AnalysisRunContext
            {
                MemoryBudget = preflight.MemoryBudget,
                EventSink = RenderPipelineEvent,
                StartFromPosition = preflight.StartFromPosition,
                InitialState = preflight.ResumeSnapshot?.State.Restore()
            };
            PipelineResult pipelineResult = new AnalysisExecutor(_services.Progress).Execute(options, rules, runContext, cancellationToken);
            AnalysisResult result = pipelineResult.Result;

            MergeIncrementalResults(options, pipelineResult, preflight.StartFromPosition, runContext);
            result = pipelineResult.Result;
            ProcessAndReport(pipelineResult, options, rules, runContext, cancellationToken);

            (int exitCode, string? gateMessage) = EvaluateQualityGates(result, options);
            WriteAuditEvent(audit, options, rules, runId, sw, "analysis_finish", exitCode, result, gateMessage);
            return exitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _services.Console.MarkupLine("[yellow]分析已取消。[/]");
            WriteAuditEvent(audit, options, rules, runId, sw, "analysis_cancel", exitCode: 2, result: null, message: "用户取消");
            return 2;
        }
        catch (MaxMemoryExceededException ex)
        {
            // P6.3：--max-memory hard 模式超限
            _services.Console.MarkupLine(
                $"[red][[内存]] 退出码 1: {MarkupText.Escape(ex.Message)}[/]");
            WriteAuditEvent(audit, options, rules, runId, sw, "analysis_error", exitCode: 1, result: null, message: ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            PrintError(ex);
            WriteAuditEvent(audit, options, rules, runId, sw, "analysis_error", exitCode: 1, result: null, message: ex.Message);
            return 1;
        }
    }

    private static void WriteAuditStart(AuditLogger? audit, AnalysisOptions options, AnalysisRules? rules, string runId)
    {
        if (audit == null) return;
        audit.Write(new AuditLogEntry
        {
            Timestamp = AuditLogger.FormatTimestamp(DateTime.UtcNow),
            Event = "analysis_start",
            RunId = runId,
            Operator = string.IsNullOrWhiteSpace(options.Audit.Operator) ? "anonymous" : options.Audit.Operator,
            ToolVersion = GetToolVersion(),
            InputSource = BuildInputSource(options),
            InputHash = AuditLogger.ComputeFileHash(options.Input.InputPath),
            InputBytes = AuditLogger.GetFileSize(options.Input.InputPath),
            Options = AuditLogger.Snapshot(options, rules)
        });
    }

    private static void WriteAuditEvent(
        AuditLogger? audit,
        AnalysisOptions options,
        AnalysisRules? rules,
        string runId,
        StopwatchLike sw,
        string eventName,
        int exitCode,
        AnalysisResult? result,
        string? message)
    {
        if (audit == null) return;
        audit.Write(new AuditLogEntry
        {
            Timestamp = AuditLogger.FormatTimestamp(DateTime.UtcNow),
            Event = eventName,
            RunId = runId,
            Operator = string.IsNullOrWhiteSpace(options.Audit.Operator) ? "anonymous" : options.Audit.Operator,
            ToolVersion = GetToolVersion(),
            InputSource = BuildInputSource(options),
            InputHash = AuditLogger.ComputeFileHash(options.Input.InputPath),
            InputBytes = AuditLogger.GetFileSize(options.Input.InputPath),
            Options = AuditLogger.Snapshot(options, rules),
            Result = result == null ? null : AuditLogger.Summarize(result, sw),
            Message = message,
            ExitCode = exitCode
        });
    }

    private static string BuildInputSource(AnalysisOptions options)
    {
        if (!string.IsNullOrEmpty(options.Input.ConnectionString))
        {
            string provider = string.IsNullOrEmpty(options.Input.DbProvider) ? "auto" : options.Input.DbProvider;
            string query = options.Input.Query ?? "";
            return $"db://{provider}?{query}";
        }
        return options.Input.InputPath;
    }

    private static string GetToolVersion()
    {
        try
        {
            var asm = typeof(AnalysisRunner).Assembly;
            var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            if (info.Length > 0)
            {
                return ((System.Reflection.AssemblyInformationalVersionAttribute)info[0]).InformationalVersion;
            }
            return asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private void RenderPipelineEvent(AnalysisEvent notification)
    {
        if (notification.Diagnostic is not { } diagnostic) return;
        object? Data(string key) => diagnostic.Data?.GetValueOrDefault(key);
        string message = diagnostic.Code switch
        {
            "input.format" => $"正在解析文件，格式: {Data("format")}",
            "input.compression" => $"自动解压: {Data("compression")}" + (Data("entry") is { } entry ? $"，选择条目: {entry}" : ""),
            "projection.root-ignored" => "警告: 当前格式没有文件根，已忽略 root-path。",
            "projection.root" => $"根节点投影产出 {Data("output")} 条记录。",
            "projection.summary" => $"记录投影产出 {Data("output")} 条记录，输入 {Data("input")} 条，无匹配 {Data("unmatched")} 条。",
            "sampling.enabled" => $"已启用采样，蓄水池容量: {Data("reservoir")}，等距步长: {Data("step")}",
            "memory.sampling" => "内存压力触发蓄水池采样。",
            _ => diagnostic.Message
        };
        _services.Console.MarkupLine(MarkupText.Escape(message));
    }

    private void PrintError(Exception ex)
    {
        _services.Console.MarkupLine($"[red]发生错误: {MarkupText.Escape(ex.Message)}[/]");
        if (ex.InnerException != null)
        {
            _services.Console.MarkupLine($"[red]内部错误: {MarkupText.Escape(ex.InnerException.Message)}[/]");
        }
    }

    private AnalysisRules LoadRules(string rulesPath, bool allowUnknownRules = false)
    {
        if (!File.Exists(rulesPath))
        {
            throw new FileNotFoundException($"规则文件不存在: {rulesPath}");
        }

        _services.Console.MarkupLine($"正在加载规则文件: [blue]{MarkupText.Escape(rulesPath)}[/] ...");
        var json = File.ReadAllText(rulesPath);

        // 未知键（拼错 / 已废弃）在反序列化时会被 STJ 静默忽略，这里先扫一遍原文拦下来：
        // 否则规则看着生效、实则完全没生效，比直接报错难查得多。
        if (!allowUnknownRules)
        {
            var unknowns = RulesLint.Scan(json);
            if (unknowns.Count > 0)
            {
                throw new InvalidOperationException(
                    $"规则文件 {MarkupText.Escape(rulesPath)} 校验失败：{RulesLint.Describe(unknowns)}");
            }
        }

        var rules = JsonSerializer.Deserialize<AnalysisRules>(json, AnalysisRules.RulesJsonOptions);
        if (rules == null)
        {
            throw new InvalidOperationException($"规则文件解析失败: {rulesPath}");
        }

        // KMS 集成：把 transform 中以 kms: 前缀引用的敏感密钥解密为明文（key/iv/privateKey/publicKey/signature）。
        // 明文字符串仅存在于本次进程的内存规则模型上，规则 JSON 不会被改写为明文。
        KmsRuleResolver.Resolve(rules, _services.Console);

        _services.Console.MarkupLine($"已加载 [green]{rules.Fields.Count}[/] 条字段规则。");
        return rules;
    }
}
