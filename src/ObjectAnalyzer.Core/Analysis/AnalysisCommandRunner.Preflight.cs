using ObjectAnalyzer.Analysis.Pipeline;
using ObjectAnalyzer.Parsing;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// W7 编排拆分——Preflight 阶段：输入存在性、规则加载与 runtime 合并、采样/数据库/Schema 基线校验、
/// 增量三选项（--resume / --baseline / --checkpoint）一致性预检与快照加载、JSONPath 语法预检、
/// 内存预算实例化。任何校验失败直接以退出码 1 结束，不进入分析。
/// </summary>
public sealed partial class AnalysisCommandRunner
{
    /// <summary>Preflight 阶段产物：传递给管线与合并阶段的 per-run 状态。</summary>
    internal readonly record struct PreflightOutcome(
        bool Ok,
        int ExitCode,
        AnalysisRules? Rules,
        AnalyzerSnapshot? ResumeSnapshot,
        long StartFromPosition,
        MemoryBudget? MemoryBudget)
    {
        public static PreflightOutcome Fail(int exitCode) => new(Ok: false, ExitCode: exitCode, Rules: null, ResumeSnapshot: null, StartFromPosition: 0, MemoryBudget: null);
    }

    internal PreflightOutcome RunPreflight(AnalysisOptions options)
    {
        bool dbMode = !string.IsNullOrEmpty(options.Input.ConnectionString);
        if (!dbMode && !File.Exists(options.Input.InputPath))
        {
            _services.Console.MarkupLine($"[red]错误: 输入文件不存在: {MarkupText.Escape(options.Input.InputPath)}[/]");
            return PreflightOutcome.Fail(1);
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
                _services.Console.MarkupLine($"[red]错误: 规则文件加载失败: {MarkupText.Escape(ex.Message)}[/]");
                return PreflightOutcome.Fail(1);
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
                _services.Console.MarkupLine("[red]错误: 使用 --connection 直连数据库时必须指定 --query。[/]");
                return PreflightOutcome.Fail(1);
            }
            try
            {
                DatabaseReader.ResolveProvider(options.Input.DbProvider, options.Input.ConnectionString!);
            }
            catch (Exception ex)
            {
                _services.Console.MarkupLine($"[red]错误: {MarkupText.Escape(ex.Message)}[/]");
                return PreflightOutcome.Fail(1);
            }
        }

        // Schema diff 基线文件预检
        if (!string.IsNullOrEmpty(options.Schema.DiffSchemaBaseline) && !File.Exists(options.Schema.DiffSchemaBaseline))
        {
            _services.Console.MarkupLine(
                $"[red]错误: --diff-schema 基线文件不存在: {MarkupText.Escape(options.Schema.DiffSchemaBaseline)}[/]");
            return PreflightOutcome.Fail(1);
        }

        // P6.2：--resume 预检：快照文件存在性 + 源文件一致性 + 防止 --resume / --baseline / --checkpoint 自相矛盾
        if (!string.IsNullOrEmpty(options.Incremental.ResumeFrom) && !File.Exists(options.Incremental.ResumeFrom))
        {
            _services.Console.MarkupLine(
                $"[red]错误: --resume 快照文件不存在: {MarkupText.Escape(options.Incremental.ResumeFrom)}[/]");
            return PreflightOutcome.Fail(1);
        }
        if (!string.IsNullOrEmpty(options.Incremental.BaselinePath) && !File.Exists(options.Incremental.BaselinePath))
        {
            _services.Console.MarkupLine(
                $"[red]错误: --baseline 基线文件不存在: {MarkupText.Escape(options.Incremental.BaselinePath)}[/]");
            return PreflightOutcome.Fail(1);
        }
        if (!string.IsNullOrEmpty(options.Incremental.CheckpointPath) && !string.IsNullOrEmpty(options.Incremental.ResumeFrom) &&
            string.Equals(Path.GetFullPath(options.Incremental.CheckpointPath), Path.GetFullPath(options.Incremental.ResumeFrom), StringComparison.OrdinalIgnoreCase))
        {
            _services.Console.MarkupLine(
                "[red]错误: --checkpoint 与 --resume 指向同一文件；为避免相互覆盖，请使用不同路径。[/]");
            return PreflightOutcome.Fail(1);
        }
        if (!string.IsNullOrEmpty(options.Incremental.BaselinePath) && !string.IsNullOrEmpty(options.Incremental.ResumeFrom) &&
            string.Equals(Path.GetFullPath(options.Incremental.BaselinePath), Path.GetFullPath(options.Incremental.ResumeFrom), StringComparison.OrdinalIgnoreCase))
        {
            _services.Console.MarkupLine(
                "[red]错误: --baseline 与 --resume 指向同一文件；语义冲突，请使用不同路径。[/]");
            return PreflightOutcome.Fail(1);
        }

        if (!string.IsNullOrEmpty(options.Incremental.CheckpointPath) || !string.IsNullOrEmpty(options.Incremental.ResumeFrom))
        {
            try { ValidateCheckpointOptions(options); }
            catch (Exception ex)
            {
                _services.Console.MarkupLine($"[red]{MarkupText.Escape(ex.Message)}[/]");
                return PreflightOutcome.Fail(1);
            }
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
                _services.Console.MarkupLine($"[red]错误: --resume 快照解析失败: {MarkupText.Escape(ex.Message)}[/]");
                return PreflightOutcome.Fail(1);
            }

            // 源文件一致性：防止用户错把 A 文件的快照套到 B 文件上（resume 的 SourcePosition 仅对 A 有意义）
            if (!string.IsNullOrEmpty(options.Input.InputPath))
            {
                string currentAbs = Path.GetFullPath(options.Input.InputPath);
                string snapAbs = Path.GetFullPath(resumeSnapshot.SourcePath);
                if (!string.Equals(currentAbs, snapAbs, StringComparison.OrdinalIgnoreCase))
                {
                    _services.Console.MarkupLine(
                        $"[red]错误: --resume 快照的源文件与当前输入不匹配（快照: {MarkupText.Escape(snapAbs)}，当前: {MarkupText.Escape(currentAbs)}）[/]");
                    return PreflightOutcome.Fail(1);
                }
            }

            try
            {
                if (resumeSnapshot.PositionKind != "line" || resumeSnapshot.ExecutionSignature != BuildExecutionSignature(options))
                    throw new InvalidDataException("Checkpoint pipeline configuration differs from the current run.");
                if (resumeSnapshot.SourceFingerprint != ComputeSourceFingerprint(options.Input.InputPath, resumeSnapshot.SourcePosition))
                    throw new InvalidDataException("Previously processed input has changed; rebuild the checkpoint.");
            }
            catch (Exception ex)
            {
                _services.Console.MarkupLine($"[red]{MarkupText.Escape(ex.Message)}[/]");
                return PreflightOutcome.Fail(1);
            }

            startFromPosition = Math.Max(0, resumeSnapshot.SourcePosition);
            _services.Console.MarkupLine(
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
                _services.Console.MarkupLine($"[red]错误: --root-path {MarkupText.Escape(ex.Message)}[/]");
                return PreflightOutcome.Fail(1);
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
                _services.Console.MarkupLine($"[red]错误: --record-path {MarkupText.Escape(ex.Message)}[/]");
                return PreflightOutcome.Fail(1);
            }
        }

        return new PreflightOutcome(
            Ok: true,
            ExitCode: 0,
            Rules: rules,
            ResumeSnapshot: resumeSnapshot,
            StartFromPosition: startFromPosition,
            MemoryBudget: memoryBudget);
    }

    /// <summary>Observe heap pressure at entry; the execution session chooses sampling before reading input.</summary>
    private static void MemoryBudgetEntryCheck(MemoryBudget? memoryBudget)
    {
        if (memoryBudget is not { IsEnabled: true } entryBudget)
            return;

        entryBudget.Check(0, out _);
        // 入口已超限时（启动内存即超阈值，如 xunit 测试进程）立即拒绝
        if (entryBudget.HardLimitExceeded)
        {
            entryBudget.ThrowIfHardExceeded();
        }
    }
}
