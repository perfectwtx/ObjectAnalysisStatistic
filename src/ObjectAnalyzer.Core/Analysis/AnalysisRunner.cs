using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ObjectAnalyzer.Analysis.Rules;
using ObjectAnalyzer.Security;

namespace ObjectAnalyzer.Analysis;

public static class AnalysisRunner
{
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
    public static int Run(AnalysisOptions options, CancellationToken cancellationToken = default)
    {
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
            AnalysisServices.Current.Console.MarkupLine(
                $"[yellow]警告: 审计日志初始化失败 ({MarkupText.Escape(ex.Message)})；本次运行不写入审计记录。[/]");
            audit = null;
        }

        int result = RunCore(options, audit, runId, sw, cancellationToken);

        // 若 RunCore 因异常未写入 finish/error 事件，此处兜底记录
        if (audit != null)
        {
            // RunCore 已负责所有正常路径的写入；此处仅在 RunCore 自身抛出未捕获异常时使用
            audit.Dispose();
        }
        return result;
    }

    private static int RunCore(
        AnalysisOptions options,
        AuditLogger? audit,
        string runId,
        StopwatchLike sw,
        CancellationToken cancellationToken)
    {
        bool dbMode = !string.IsNullOrEmpty(options.Input.ConnectionString);
        if (!dbMode && !File.Exists(options.Input.InputPath))
        {
            AnalysisServices.Current.Console.MarkupLine($"[red]错误: 输入文件不存在: {MarkupText.Escape(options.Input.InputPath)}[/]");
            return 1;
        }

        AnalysisRules? rules = null;
        if (!string.IsNullOrEmpty(options.Rules.RulesFile))
        {
            try
            {
                rules = LoadRules(options.Rules.RulesFile, options.Rules.AllowUnknownRules);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 规则文件解析/预验证失败（含无效 pattern）快速失败，避免带病分析
                AnalysisServices.Current.Console.MarkupLine($"[red]错误: 规则文件加载失败: {MarkupText.Escape(ex.Message)}[/]");
                return 1;
            }
        }

        // 规则文件 (JsonObject runtime) → AnalysisOptions 一次性合并。
        // 优先级：CLI / API 显式参数 > rules.runtime > 子组内置默认。
        // Apply 内部只覆盖"默认"的子组字段，调用方已显式设的值不会被覆盖。
        if (rules != null)
        {
            RulesToOptionsConverter.Apply(options, rules);
        }

        // 采样参数提前校验，避免解析到一半才发现配置非法
        if (Sampling.IsEnabled(options.Sampling.SampleReservoir, options.Sampling.SampleStep))
            Sampling.Validate(options.Sampling.SampleReservoir, options.Sampling.SampleStep);

        // 数据库直连参数提前校验
        if (dbMode)
        {
            if (string.IsNullOrEmpty(options.Input.Query))
            {
                AnalysisServices.Current.Console.MarkupLine("[red]错误: 使用 --connection 直连数据库时必须指定 --query。[/]");
                return 1;
            }
            try
            {
                DatabaseReader.ResolveProvider(options.Input.DbProvider, options.Input.ConnectionString!);
            }
            catch (Exception ex)
            {
                AnalysisServices.Current.Console.MarkupLine($"[red]错误: {MarkupText.Escape(ex.Message)}[/]");
                return 1;
            }
        }

        // Schema diff 基线文件预检
        if (!string.IsNullOrEmpty(options.Schema.DiffSchemaBaseline) && !File.Exists(options.Schema.DiffSchemaBaseline))
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[red]错误: --diff-schema 基线文件不存在: {MarkupText.Escape(options.Schema.DiffSchemaBaseline)}[/]");
            return 1;
        }

        // P6.2：--resume 预检：快照文件存在性 + 源文件一致性 + 防止 --resume / --baseline / --checkpoint 自相矛盾
        if (!string.IsNullOrEmpty(options.Incremental.ResumeFrom) && !File.Exists(options.Incremental.ResumeFrom))
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[red]错误: --resume 快照文件不存在: {MarkupText.Escape(options.Incremental.ResumeFrom)}[/]");
            return 1;
        }
        if (!string.IsNullOrEmpty(options.Incremental.BaselinePath) && !File.Exists(options.Incremental.BaselinePath))
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[red]错误: --baseline 基线文件不存在: {MarkupText.Escape(options.Incremental.BaselinePath)}[/]");
            return 1;
        }
        if (!string.IsNullOrEmpty(options.Incremental.CheckpointPath) && !string.IsNullOrEmpty(options.Incremental.ResumeFrom) &&
            string.Equals(Path.GetFullPath(options.Incremental.CheckpointPath), Path.GetFullPath(options.Incremental.ResumeFrom), StringComparison.OrdinalIgnoreCase))
        {
            AnalysisServices.Current.Console.MarkupLine(
                "[red]错误: --checkpoint 与 --resume 指向同一文件；为避免相互覆盖，请使用不同路径。[/]");
            return 1;
        }
        if (!string.IsNullOrEmpty(options.Incremental.BaselinePath) && !string.IsNullOrEmpty(options.Incremental.ResumeFrom) &&
            string.Equals(Path.GetFullPath(options.Incremental.BaselinePath), Path.GetFullPath(options.Incremental.ResumeFrom), StringComparison.OrdinalIgnoreCase))
        {
            AnalysisServices.Current.Console.MarkupLine(
                "[red]错误: --baseline 与 --resume 指向同一文件；语义冲突，请使用不同路径。[/]");
            return 1;
        }

        // P6.2：--resume 加载快照
        AnalyzerSnapshot? resumeSnapshot = null;
        long startFromPosition = 0;
        if (!string.IsNullOrEmpty(options.Incremental.ResumeFrom))
        {
            try
            {
                resumeSnapshot = AnalyzerSnapshot.LoadFrom(options.Incremental.ResumeFrom!);
            }
            catch (Exception ex)
            {
                AnalysisServices.Current.Console.MarkupLine($"[red]错误: --resume 快照解析失败: {MarkupText.Escape(ex.Message)}[/]");
                return 1;
            }

            // 源文件一致性：防止用户错把 A 文件的快照套到 B 文件上（resume 的 SourcePosition 仅对 A 有意义）
            if (!string.IsNullOrEmpty(options.Input.InputPath))
            {
                string currentAbs = Path.GetFullPath(options.Input.InputPath);
                string snapAbs = Path.GetFullPath(resumeSnapshot.SourcePath);
                if (!string.Equals(currentAbs, snapAbs, StringComparison.OrdinalIgnoreCase))
                {
                    AnalysisServices.Current.Console.MarkupLine(
                        $"[red]错误: --resume 快照的源文件与当前输入不匹配（快照: {MarkupText.Escape(snapAbs)}，当前: {MarkupText.Escape(currentAbs)}）[/]");
                    return 1;
                }
            }

            startFromPosition = Math.Max(0, resumeSnapshot.SourcePosition);
            AnalysisServices.Current.Console.MarkupLine(
                $"[green][[检查点]][/] 从 {MarkupText.Escape(options.Incremental.ResumeFrom!)} 恢复分析，已处理 {startFromPosition:N0} 条记录，本次从该位置继续");
        }

        // P6.3：实例化内存预算（--max-memory）。0 表示不监控；其余值生效。
        MemoryBudget? memoryBudget = options.Memory.MaxMemoryMB > 0
            ? new MemoryBudget(options.Memory.MaxMemoryMB, options.Memory.MaxMemoryMode)
            : null;

        // JSONPath 表达式语法预检（fail-fast，避免解析到一半才发现）
        if (!string.IsNullOrWhiteSpace(options.Input.RootPath))
        {
            try
            {
                JsonPathProjector.TryValidate(options.Input.RootPath);
            }
            catch (Exception ex)
            {
                AnalysisServices.Current.Console.MarkupLine($"[red]错误: --root-path {MarkupText.Escape(ex.Message)}[/]");
                return 1;
            }
        }
        if (!string.IsNullOrWhiteSpace(options.Input.RecordPath))
        {
            try
            {
                JsonPathProjector.TryValidate(options.Input.RecordPath);
            }
            catch (Exception ex)
            {
                AnalysisServices.Current.Console.MarkupLine($"[red]错误: --record-path {MarkupText.Escape(ex.Message)}[/]");
                return 1;
            }
        }

        // 输入存在性与规则加载通过后，审计可正式记录 start 事件（不计入参数校验失败等）
        WriteAuditStart(audit, options, rules, runId);

        try
        {
            // P6.3：内存预算入口预检。在加载任何大对象前采样一次托管堆占用——
            // 目的：1) 硬模式下若启动时已超阈值，立即以退出码 1 失败，避免分析半途才发现；
            //       2) 软模式下若已超阈值且用户未指定采样，自动启用 50K 蓄水池兜底，
            //          让分析在有限内存下继续推进（避免后续 GC 抖动反复触发警告）。
            // 必须在 try 块内：硬模式抛 MaxMemoryExceededException，由下方 catch 转为退出码 1。
            // 注意：fresh 进程刚启动时 GC.GetTotalMemory 可能仅 0.1 MB 左右（无任何分配），
            // 因此仅靠入口预检不够；分析循环内每批 Check 后由本方法末尾的收尾检查统一抛错。
            if (memoryBudget is { IsEnabled: true } entryBudget)
            {
                entryBudget.Check(0, out bool autoEnable);
                if (autoEnable)
                {
                    bool userRequestedSampling = Sampling.IsEnabled(options.Sampling.SampleReservoir, options.Sampling.SampleStep);
                    if (!userRequestedSampling)
                    {
                        options.Sampling.SampleReservoir = options.Sampling.MemoryFallbackSampleSize;
                        AnalysisServices.Current.Console.MarkupLine(
                            $"[grey][[内存]][/] 检测到启动时已接近 [blue]--max-memory {options.Memory.MaxMemoryMB:N0} MB[/]；已自动切换为蓄水池采样 [blue]{options.Sampling.SampleReservoir:N0}[/] 条以保护进程。");
                    }
                }
                // 入口已超限时（启动内存即超阈值，如 xunit 测试进程）立即拒绝
                if (entryBudget.HardLimitExceeded)
                {
                    entryBudget.ThrowIfHardExceeded();
                }
            }

            // W1 统一分析管线：解析 → JSONPath 投影 → 字段选择/转换/过滤 → 采样 → 统计。
            // 运行期状态（内存预算 / 续跑位置）随 AnalysisRunContext 传入，回填 EffectiveFormat / LastRecordCount。
            var runContext = new AnalysisRunContext
            {
                MemoryBudget = memoryBudget,
                StartFromPosition = startFromPosition
            };
            PipelineResult pipelineResult = AnalysisPipeline.Execute(options, rules, runContext, cancellationToken);
            AnalysisResult result = pipelineResult.Result;

            // W1 收尾：呈现 / 导出 / Schema 与分布快照（宿主职责，留在 CLI 侧）
            ProcessAndReport(pipelineResult, options, rules, runContext, cancellationToken);

            // P6.2：--resume 合并快照中的已聚合结果（FieldStatistic / Welford / 直方图 / 枚举违规均可增量合并）
            if (resumeSnapshot != null)
            {
                AnalysisServices.Current.Console.MarkupLine(
                    $"[green][[检查点]][/] 正在合并已处理的 {resumeSnapshot.Result.TotalObjects:N0} 条记录结果（含 {resumeSnapshot.Result.FieldStatistics.Count} 个字段）...");
                result.MergeFrom(resumeSnapshot.Result);
                AnalysisServices.Current.Console.MarkupLine(
                    $"[green][[检查点]][/] 合并后共 {result.TotalObjects:N0} 条记录");
            }

            // P6.2：--baseline 合并历史 AnalysisResult JSON（与 --resume 互补：--resume 是从源文件中间续跑；--baseline 是合并任意历史结果）
            if (!string.IsNullOrEmpty(options.Incremental.BaselinePath))
            {
                var baseline = LoadAnalysisResultFromFile(options.Incremental.BaselinePath!);
                if (baseline != null)
                {
                    AnalysisServices.Current.Console.MarkupLine(
                        $"[green][[基线]][/] 正在合并基线 {MarkupText.Escape(options.Incremental.BaselinePath!)} 的 {baseline.TotalObjects:N0} 条记录结果...");
                    result.MergeFrom(baseline);
                    AnalysisServices.Current.Console.MarkupLine(
                        $"[green][[基线]][/] 合并后共 {result.TotalObjects:N0} 条记录");
                }
            }

            // P6.2：--checkpoint 落盘当前快照（用于下次 --resume）
            if (!string.IsNullOrEmpty(options.Incremental.CheckpointPath))
            {
                long totalProcessed = startFromPosition + runContext.LastRecordCount;
                string fmt = runContext.EffectiveFormat
                    ?? (string.IsNullOrEmpty(options.Input.InputPath) ? "db" : FileParsers.DetectFormat(options.Input.InputPath));
                var snapshot = new AnalyzerSnapshot
                {
                    SourcePath = options.Input.InputPath,
                    SourceFormat = fmt,
                    SourcePosition = totalProcessed,
                    PositionKind = "line",
                    Result = result
                };
                try
                {
                    snapshot.SaveTo(options.Incremental.CheckpointPath!);
                    AnalysisServices.Current.Console.MarkupLine(
                        $"[green][[检查点]][/] 已落盘分析快照至 [blue]{MarkupText.Escape(options.Incremental.CheckpointPath!)}[/]（共 {totalProcessed:N0} 条记录，{result.FieldStatistics.Count} 个字段）");
                }
                catch (Exception ex)
                {
                    AnalysisServices.Current.Console.MarkupLine(
                        $"[yellow]警告: 检查点写入失败 ({MarkupText.Escape(ex.Message)})；本次结果已输出但下次无法 --resume 续跑。[/]");
                }
            }

            // P6.2：--resume / --baseline 合并后，需要把合并后的最终结果重新导出到 OutputPath。
            // ProcessAndReport 已写过一遍（含"本 run 局部"结果），再次覆盖以反映 merge 后的最终聚合。
            if ((resumeSnapshot != null || !string.IsNullOrEmpty(options.Incremental.BaselinePath))
                && !string.IsNullOrEmpty(options.Report.OutputPath))
            {
                try
                {
                    ReportExporter.Save(result, options.Report.OutputPath!, redactPii: !options.Pii.PiiOff);
                }
                catch (Exception ex)
                {
                    AnalysisServices.Current.Console.MarkupLine(
                        $"[yellow]警告: 合并后报告覆写失败 ({MarkupText.Escape(ex.Message)})；{MarkupText.Escape(options.Report.OutputPath!)} 仍为合并前结果。[/]");
                }
            }

            int exitCode = 0;
            // CI 数据质量门禁：存在任一质量违规（枚举/范围/模式/必填/唯一/null 率/数据集期望）时以非零退出码结束
            if (options.Gates.FailOnViolations && result.QualityViolations.Count > 0)
            {
                long occurrenceCount = result.QualityViolations.Sum(v => v.Count);
                AnalysisServices.Current.Console.MarkupLine(
                    $"[red]数据质量未通过（{result.QualityViolations.Count} 条质量违规 / 涉及 {occurrenceCount} 行或值），退出码 3。[/]");
                foreach (var violation in result.QualityViolations.Take(5))
                {
                    string scope = string.IsNullOrEmpty(violation.Field)
                        ? QualityCheckTypes.Label(violation.Check)
                        : $"{violation.Field}: {QualityCheckTypes.Label(violation.Check)}";
                    AnalysisServices.Current.Console.MarkupLine($"  [red]·[/] {MarkupText.Escape(scope)} — {MarkupText.Escape(violation.Message)}");
                }

                if (result.QualityViolations.Count > 5)
                {
                    AnalysisServices.Current.Console.MarkupLine($"  [grey]... 还有 {result.QualityViolations.Count - 5} 条，详见 --violations 导出[/]");
                }

                exitCode = 3;
            }

            // CI 数据安全合规门禁：合规等级达到或劣于阈值时以非零退出码结束（与 --fail-on-violations 对称）
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(options.Gates.FailOnPiiLevel))
            {
                var piiReport = PiiReport.Build(result);
                if (piiReport.PiiFieldCount > 0 &&
                    PiiSeverityRank(piiReport.ComplianceLevel) >= PiiSeverityRank(options.Gates.FailOnPiiLevel))
                {
                    AnalysisServices.Current.Console.MarkupLine(
                        $"[red]数据安全合规未通过（合规等级 {piiReport.ComplianceLevel}，发现 {piiReport.PiiFieldCount} 个敏感字段 / {piiReport.PiiValueCount} 个敏感值），退出码 4。[/]");
                    exitCode = 4;
                }
            }

            // CI 异常值门禁：任一数值字段异常值比例超阈值时以退出码 5 结束
            if (exitCode == 0 && options.Gates.FailOnOutlierRatio.HasValue)
            {
                double threshold = options.Gates.FailOnOutlierRatio.Value;
                var offenders = result.FieldStatistics
                    .Where(s => s.OutlierRatio.HasValue && s.OutlierRatio.Value > threshold)
                    .ToList();
                if (offenders.Count > 0)
                {
                    AnalysisServices.Current.Console.MarkupLine(
                        $"[red]异常值未通过（阈值 {threshold:P1}，发现 {offenders.Count} 个字段超限），退出码 5。[/]");
                    foreach (var s in offenders.Take(5))
                    {
                        AnalysisServices.Current.Console.MarkupLine(
                            $"  [red]·[/] [cyan]{MarkupText.Escape(s.FieldName)}[/]: {s.OutlierRatio:P1} (区间 {MarkupText.Escape(s.OutlierBounds ?? "-")}, {s.OutlierCount} 样本, {s.OutlierMethod} 近似)");
                    }
                    if (offenders.Count > 5)
                    {
                        AnalysisServices.Current.Console.MarkupLine($"  [grey]... 还有 {offenders.Count - 5} 个字段，详见报告[/]");
                    }
                    exitCode = 5;
                }
            }

            // CI Schema 演进门禁：--allow-breaking-schema-changes=false 时，含破坏性变更以退出码 6 结束
            if (exitCode == 0 && !options.Gates.AllowBreakingSchemaChanges && result.SchemaDiff != null && result.SchemaDiff.HasBreakingChanges)
            {
                int removed = result.SchemaDiff.RemovedFields.Count;
                int breakingChanges = result.SchemaDiff.ChangedFields.Count(c => c.Breaking);
                AnalysisServices.Current.Console.MarkupLine(
                    $"[red]Schema 演进门禁未通过（--allow-breaking-schema-changes=false，发现 {removed} 个删除字段、{breakingChanges} 个破坏性变更字段），退出码 6。[/]");
                foreach (var f in result.SchemaDiff.RemovedFields.Take(5))
                {
                    AnalysisServices.Current.Console.MarkupLine(
                        $"  [red]-[/] [cyan]{MarkupText.Escape(f.Path)}[/]: {MarkupText.Escape(f.PrimaryType)} (coverage {f.Coverage:P1})");
                }
                foreach (var c in result.SchemaDiff.ChangedFields.Where(c => c.Breaking).Take(5))
                {
                    AnalysisServices.Current.Console.MarkupLine($"  [red]~[/] [cyan]{MarkupText.Escape(c.Path)}[/]");
                    foreach (var item in c.Changes)
                    {
                        if (item.Breaking)
                        {
                            AnalysisServices.Current.Console.MarkupLine($"      [red]·[/] {MarkupText.Escape(item.Property)}: {MarkupText.Escape(item.Description)}");
                        }
                    }
                }
                exitCode = 6;
            }

            // 成功 / 业务门禁失败统一记录 analysis_finish（含退出码与结果摘要）
            string finishEvent = exitCode == 0 ? "analysis_finish" : "analysis_finish";
            string? finishMsg = exitCode == 0
                ? null
                : exitCode switch
                {
                    3 => "数据质量门禁未通过",
                    4 => "数据安全合规门禁未通过",
                    5 => "异常值门禁未通过",
                    6 => "Schema 演进门禁未通过",
                    _ => null
                };
            WriteAuditEvent(audit, options, rules, runId, sw, "analysis_finish", exitCode, result, finishMsg);
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            AnalysisServices.Current.Console.MarkupLine("[yellow]分析已取消。[/]");
            WriteAuditEvent(audit, options, rules, runId, sw, "analysis_cancel", exitCode: 2, result: null, message: "用户取消");
            return 2;
        }
        catch (MaxMemoryExceededException ex)
        {
            // P6.3：--max-memory hard 模式超限
            AnalysisServices.Current.Console.MarkupLine(
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

    /// <summary>
    /// W1 收尾：呈现 / 导出（宿主职责）。计算路径由 <see cref="AnalysisPipeline.Execute"/> 统一完成；
    /// 本方法只负责分析完成后的控制台呈现、报告导出与 Schema / 分布快照。
    /// </summary>
    private static void ProcessAndReport(
        PipelineResult pipelineResult,
        AnalysisOptions options,
        AnalysisRules? rules,
        AnalysisRunContext runContext,
        CancellationToken cancellationToken)
    {
        AnalysisResult result = pipelineResult.Result;

        AnalysisServices.Current.Console.MarkupLine("[green]分析完成！[/]\n");

        // P6.3：内存预算总结（仅当启用了 --max-memory 时打印）
        if (runContext.MemoryBudget is { IsEnabled: true } finalBudget)
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[grey][[内存]][/] 本次分析峰值 [blue]{finalBudget.PeakMB:F1} MB[/]（--max-memory 阈值 {options.Memory.MaxMemoryMB:N0} MB，模式 {options.Memory.MaxMemoryMode}）");
            // hard 模式：分析完成后若曾触发 HardLimitExceeded（来自 progress 回调内的 Check），
            // 此处由主流程显式抛错；不能放在 progress 回调里直接 throw——会变成"未观察异常"逃出主流程。
            finalBudget.ThrowIfHardExceeded();
        }

        // Schema 快照导出 / diff 计算（必须在 ConsoleReporter.Print 与 ReportExporter.Save 之前，
        // 这样控制台与 HTML/MD/JSON 报告的「Schema 演进」小节才能带 diff 数据）
        ProcessSchemaSnapshot(result, options);

        // 分布快照导出 / diff 计算（与 SchemaSnapshot 互补：看分布变化）
        ProcessDistributionSnapshot(result, options, pipelineResult.QuantileSketches, pipelineResult.DistinctSketches);

        // 输出控制台统计结果（--pii-off 时保留原始值）。呈现由宿主注入的 IResultPresenter 完成
        // （CLI：Spectre 富渲染；API/Web：静默或结构化推送）。
        AnalysisServices.Current.Presenter.Present(result, options);

        // 输出数据安全合规摘要（发现敏感字段时提示打码与合规导出；--pii-off 时跳过）
        if (!options.Pii.PiiOff)
        {
            PiiReport.PrintSummary(result);
        }

        // 如果指定了输出路径，则保存详细报告
        if (!string.IsNullOrEmpty(options.Report.OutputPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportExporter.Save(result, options.Report.OutputPath, redactPii: !options.Pii.PiiOff);
        }

        // 如果指定了违规导出路径，则单独导出枚举违规清单
        if (!string.IsNullOrEmpty(options.Report.ViolationsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ViolationExporter.Save(result, options.Report.ViolationsPath, redactPii: !options.Pii.PiiOff);
        }

        // 如果指定了数据质量报告路径，导出结构化 Pass/Fail 质量报告
        if (!string.IsNullOrEmpty(options.Report.QualityPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            QualityExporter.Save(result, options.Report.QualityPath, redactPii: !options.Pii.PiiOff);
        }

        // 如果指定了数据安全合规报告路径，导出合规等级 + 敏感字段清单（--pii-off 时跳过）
        if (!string.IsNullOrEmpty(options.Pii.PiiPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PiiExporter.Save(result, options.Pii.PiiPath);
        }

        // 如果指定了 Schema 导出，自动生成 C# / TypeScript / JSON Schema 定义
        if (!string.IsNullOrEmpty(options.Report.ExportSchema))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string basePath = options.Report.OutputPath ?? options.Input.InputPath;
            if (string.IsNullOrEmpty(basePath)) basePath = "database"; // 数据库直连模式下没有输入文件
            string schemaExt = options.Report.ExportSchema.ToLowerInvariant() switch
            {
                "csharp" or "cs" => ".schema.cs",
                "typescript" or "ts" => ".schema.d.ts",
                "openapi" or "swagger" or "openapi-json" => ".openapi.json",
                "openapi-yaml" or "yaml-schema" => ".openapi.yaml",
                "protobuf" or "proto" or "proto3" => ".schema.proto",
                "sql" or "postgres" or "postgresql" or "pg" or "mysql" or "sqlite" or "sqlserver" or "mssql" => ".schema.sql",
                _ => ".schema.json"
            };
            string schemaPath = options.Report.SchemaOutputPath ?? Path.Combine(
                Path.GetDirectoryName(basePath) ?? "",
                Path.GetFileNameWithoutExtension(basePath) + schemaExt);

            AnalysisServices.Current.Console.MarkupLine($"正在导出 [bold green]{options.Report.ExportSchema}[/] 格式 Schema 定义至: [blue]{MarkupText.Escape(schemaPath)}[/] ...");
            SchemaExporter.Export(result, options.Report.ExportSchema, schemaPath);
            AnalysisServices.Current.Console.MarkupLine("[green]Schema 定义代码成功导出！[/]");
        }

        // Schema 快照导出 / diff 计算（独立于代码生成器 SchemaExporter）
        // 已在 ProcessAndReport 前置阶段完成（见 ProcessAndReport 顶部），此处保留注释便于阅读。
    }

    /// <summary>
    /// 处理 Schema 快照导出（--export-schema-snapshot）与 diff 计算（--diff-schema）。
    /// 失败抛 FileNotFoundException / InvalidDataException，由调用方转为退出码 1。
    /// </summary>
    private static void ProcessSchemaSnapshot(AnalysisResult result, AnalysisOptions options)
    {
        bool needBuildSnapshot =
            !string.IsNullOrEmpty(options.Schema.SchemaSnapshotPath) ||
            !string.IsNullOrEmpty(options.Schema.DiffSchemaBaseline) ||
            !string.IsNullOrEmpty(options.Schema.SchemaDiffReportPath);

        if (!needBuildSnapshot)
            return;

        // 来源标识：优先输入文件路径；其次数据库直连描述；最后空（snapshot 中省略）
        string? source = !string.IsNullOrEmpty(options.Input.InputPath)
            ? options.Input.InputPath
            : (!string.IsNullOrEmpty(options.Input.ConnectionString)
                ? $"db://{(string.IsNullOrEmpty(options.Input.DbProvider) ? "auto" : options.Input.DbProvider)}?{options.Input.Query ?? ""}"
                : null);

        SchemaSnapshot? currentSnapshot = null;
        if (!string.IsNullOrEmpty(options.Schema.SchemaSnapshotPath))
        {
            currentSnapshot = SchemaSnapshotExporter.Build(result, source);
            // SchemaSnapshotExporter.Save 内部已打印成功日志
            string? dir = Path.GetDirectoryName(options.Schema.SchemaSnapshotPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            SchemaSnapshotExporter.Save(result, options.Schema.SchemaSnapshotPath, source);
        }

        if (!string.IsNullOrEmpty(options.Schema.DiffSchemaBaseline))
        {
            var baseline = SchemaDiffer.LoadFromFile(options.Schema.DiffSchemaBaseline!);
            // 优先用刚导出的快照（确保与磁盘上的 baseline 比较时内容完全一致），否则现场构建
            currentSnapshot ??= SchemaSnapshotExporter.Build(result, source);
            var diff = SchemaDiffer.Compute(baseline, currentSnapshot);
            result.SchemaDiff = diff;

            AnalysisServices.Current.Console.WriteLine();
            AnalysisServices.Current.Console.MarkupLine("[bold]── Schema 演进（vs 基线） ──[/]");
            SchemaDiffer.PrintSummary(diff);

            if (!string.IsNullOrEmpty(options.Schema.SchemaDiffReportPath))
            {
                string? dir = Path.GetDirectoryName(options.Schema.SchemaDiffReportPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(options.Schema.SchemaDiffReportPath, JsonSerializer.Serialize(diff, s_diffJsonOptions), new UTF8Encoding(false));
                AnalysisServices.Current.Console.MarkupLine(
                    $"[green]Schema diff 已导出至[/] [blue]{MarkupText.Escape(options.Schema.SchemaDiffReportPath)}[/]");
            }
        }
    }

    /// <summary>
    /// 处理分布快照导出（--export-distribution-snapshot）与分布 diff 计算（--diff-distribution）。
    /// 与 <see cref="ProcessSchemaSnapshot"/> 互补：后者看结构变化，本方法看分布变化（覆盖率 / 均值 / 分位 / distinct）。
    /// 失败抛 FileNotFoundException / InvalidDataException，由调用方转为退出码 1。
    /// </summary>
    private static void ProcessDistributionSnapshot(
        AnalysisResult result,
        AnalysisOptions options,
        Dictionary<string, MergeableQuantileSketch>? quantileSketches,
        Dictionary<string, HyperLogLog>? distinctSketches)
    {
        bool needSnapshot =
            !string.IsNullOrEmpty(options.Distribution.DistributionSnapshotPath) ||
            !string.IsNullOrEmpty(options.Distribution.DiffDistributionBaseline) ||
            !string.IsNullOrEmpty(options.Distribution.DistributionDiffReportPath);

        if (!needSnapshot)
            return;

        // 来源标识：与 SchemaSnapshot 同一口径
        string? source = !string.IsNullOrEmpty(options.Input.InputPath)
            ? options.Input.InputPath
            : (!string.IsNullOrEmpty(options.Input.ConnectionString)
                ? $"db://{(string.IsNullOrEmpty(options.Input.DbProvider) ? "auto" : options.Input.DbProvider)}?{options.Input.Query ?? ""}"
                : null);

        DistributionSnapshot? currentSnapshot = null;
        if (!string.IsNullOrEmpty(options.Distribution.DistributionSnapshotPath))
        {
            currentSnapshot = DistributionSnapshotExporter.Build(result, quantileSketches, distinctSketches, source);
            DistributionSnapshotExporter.WriteToFile(currentSnapshot, options.Distribution.DistributionSnapshotPath);
            AnalysisServices.Current.Console.MarkupLine(
                $"[green]分布快照已导出至[/] [blue]{MarkupText.Escape(options.Distribution.DistributionSnapshotPath)}[/] [grey]({currentSnapshot.Fields.Count} 字段)[/]");
        }

        if (!string.IsNullOrEmpty(options.Distribution.DiffDistributionBaseline))
        {
            var baseline = DistributionSnapshotExporter.LoadFromFile(options.Distribution.DiffDistributionBaseline!);
            currentSnapshot ??= DistributionSnapshotExporter.Build(result, quantileSketches, distinctSketches, source);
            var diff = DistributionDiffer.Compute(baseline, currentSnapshot);
            result.DistributionDiff = diff;

            AnalysisServices.Current.Console.WriteLine();
            AnalysisServices.Current.Console.MarkupLine("[bold]── 分布漂移（vs 基线） ──[/]");
            DistributionDiffer.PrintSummary(diff);

            if (!string.IsNullOrEmpty(options.Distribution.DistributionDiffReportPath))
            {
                string? dir = Path.GetDirectoryName(options.Distribution.DistributionDiffReportPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(options.Distribution.DistributionDiffReportPath, JsonSerializer.Serialize(diff, s_diffJsonOptions), new UTF8Encoding(false));
                AnalysisServices.Current.Console.MarkupLine(
                    $"[green]分布 diff 已导出至[/] [blue]{MarkupText.Escape(options.Distribution.DistributionDiffReportPath)}[/]");
            }
        }
    }

    private static void PrintError(Exception ex)
    {
        AnalysisServices.Current.Console.MarkupLine($"[red]发生错误: {MarkupText.Escape(ex.Message)}[/]");
        if (ex.InnerException != null)
        {
            AnalysisServices.Current.Console.MarkupLine($"[red]内部错误: {MarkupText.Escape(ex.InnerException.Message)}[/]");
        }
    }

    /// <summary>
    /// P6.2：从 --baseline 指定的 JSON 文件反序列化为 <see cref="AnalysisResult"/>。
    /// 该文件可以是 <c>--output ... .json</c> 生成的标准报告（顶层即为 AnalysisResult）；
    /// 找不到字段时不抛异常，反序列化得到的对象只含存在的字段，由 <see cref="AnalysisResult.MergeFrom"/> 容错处理。
    /// </summary>
    private static AnalysisResult? LoadAnalysisResultFromFile(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            var result = JsonSerializer.Deserialize<AnalysisResult>(json, s_diffJsonOptions);
            return result;
        }
        catch (Exception ex)
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[red]错误: --baseline 文件解析失败: {MarkupText.Escape(ex.Message)}[/]");
            throw;
        }
    }

    /// <summary>合规等级严重度排序：A=0（干净）&lt; B=1（敏感已打码）&lt; C=2（需人工复核）。</summary>
    private static int PiiSeverityRank(string level) => level switch
    {
        "C" => 2,
        "B" => 1,
        _ => 0
    };

    private static AnalysisRules LoadRules(string rulesPath, bool allowUnknownRules = false)
    {
        if (!File.Exists(rulesPath))
        {
            throw new FileNotFoundException($"规则文件不存在: {rulesPath}");
        }

        AnalysisServices.Current.Console.MarkupLine($"正在加载规则文件: [blue]{MarkupText.Escape(rulesPath)}[/] ...");
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

        // 质量检查 v2：pattern 预编译验证，无效正则 fail-fast（而非分析完成后才报错）
        foreach (var field in rules.Fields)
        {
            if (string.IsNullOrEmpty(field.Value.Pattern))
                continue;
            try
            {
                QualityChecks.CompilePattern(field.Value.Pattern!);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"字段 [blue]{field.Key}[/] 的 pattern 正则无效: {ex.Message}");
            }
        }

        // KMS 集成：把 transform 中以 kms: 前缀引用的敏感密钥解密为明文（key/iv/privateKey/publicKey/signature）。
        // 明文字符串仅存在于本次进程的内存规则模型上，规则 JSON 不会被改写为明文。
        KmsRuleResolver.Resolve(rules);

        AnalysisServices.Current.Console.MarkupLine($"已加载 [green]{rules.Fields.Count}[/] 条字段规则。");
        return rules;
    }
}
