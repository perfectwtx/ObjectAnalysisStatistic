using System;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// <see cref="AnalysisOptions"/> 的扩展方法集合：把 <see cref="AnalysisRuntime"/> 节点配置
/// 按"CLI / API 显式参数 &gt; 配置文件 &gt; 内置默认"三段优先级合并到 <see cref="AnalysisOptions"/>。
/// </summary>
public static class AnalysisOptionsExtensions
{
    /// <summary>
    /// 把 <see cref="AnalysisRuntime"/> 节点配置合并到 <see cref="AnalysisOptions"/>。
    /// <para>
    /// 行为约定：
    /// </para>
    /// <list type="bullet">
    ///   <item>仅在 <see cref="AnalysisRuntime"/> 中显式设置（非 null / 非默认）时才会写入 <see cref="AnalysisOptions"/>。</item>
    ///   <item>写入条件：对应 <see cref="AnalysisOptions"/> 字段仍为"内置默认"才覆盖 —— 已由调用方显式设置的值不会被覆盖。</item>
    ///   <item>对于 nullable 字段（<c>string?</c> / <c>int?</c> / <c>double?</c> / <c>bool?</c>），null = 未设置，绕过合并。</item>
    ///   <item>对于非 nullable 字段（<c>bool</c> / <c>int</c> / <c>long</c> / <c>double</c> / 枚举），按 <see cref="AnalysisOptions"/> 的字段默认值比较。</item>
    /// </list>
    /// <para>
    /// 不进合并的字段：<c>InputPath</c>、<c>ConnectionString</c> / <c>Query</c> / <c>DbProvider</c>（输入源）、
    /// <c>RulesFile</c>（规则文件路径）、<c>MemoryFallbackSampleSize</c>（库内部）。
    /// </para>
    /// </summary>
    public static void ApplyRuntime(this AnalysisOptions options, AnalysisRuntime? runtime)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (runtime == null) return;

        // ── 解析行为 ──
        if (runtime.Flatten is bool rtFlatten && options.Flatten == true) // 默认 true
        {
            options.Flatten = rtFlatten;
        }
        if (runtime.CsvInferNumbers is bool rtCsv && options.CsvInferNumbers == true) // 默认 true
        {
            options.CsvInferNumbers = rtCsv;
        }
        if (runtime.MaxValuesToShow is int rtMvs && options.MaxValuesToShow == 5) // 默认 5
        {
            options.MaxValuesToShow = rtMvs;
        }
        if (runtime.MaxParallelism is int rtPar && options.MaxDegreeOfParallelism == -1) // 默认 -1
        {
            options.MaxDegreeOfParallelism = rtPar;
        }
        if (runtime.AllowUnknownRules is bool rtAur && options.AllowUnknownRules == false) // 默认 false
        {
            options.AllowUnknownRules = rtAur;
        }

        // ── 字段选择 / 过滤 ──
        if (runtime.SelectedFields is { Count: > 0 } && string.IsNullOrEmpty(options.Fields))
        {
            options.Fields = string.Join(",", runtime.SelectedFields);
        }
        if (!string.IsNullOrWhiteSpace(runtime.Filter) && string.IsNullOrWhiteSpace(options.Filter))
        {
            options.Filter = runtime.Filter;
        }

        // ── 采样 ──
        var sampling = runtime.Sampling;
        if (sampling != null)
        {
            if (sampling.Reservoir is int rtRes && options.SampleReservoir == 0) // 默认 0
            {
                options.SampleReservoir = rtRes;
            }
            if (sampling.Step is int rtStep && options.SampleStep == 0) // 默认 0
            {
                options.SampleStep = rtStep;
            }
        }

        // ── JSONPath ──
        var jsonPath = runtime.JsonPath;
        if (jsonPath != null)
        {
            if (!string.IsNullOrWhiteSpace(jsonPath.Root) && string.IsNullOrWhiteSpace(options.RootPath))
            {
                options.RootPath = jsonPath.Root;
            }
            if (!string.IsNullOrWhiteSpace(jsonPath.Record) && string.IsNullOrWhiteSpace(options.RecordPath))
            {
                options.RecordPath = jsonPath.Record;
            }
        }

        // ── Schema 演进 ──
        var schema = runtime.Schema;
        if (schema != null)
        {
            if (!string.IsNullOrWhiteSpace(schema.Snapshot) && string.IsNullOrWhiteSpace(options.SchemaSnapshotPath))
            {
                options.SchemaSnapshotPath = schema.Snapshot;
            }
            if (!string.IsNullOrWhiteSpace(schema.DiffBaseline) && string.IsNullOrWhiteSpace(options.DiffSchemaBaseline))
            {
                options.DiffSchemaBaseline = schema.DiffBaseline;
            }
            if (!string.IsNullOrWhiteSpace(schema.DiffReport) && string.IsNullOrWhiteSpace(options.SchemaDiffReportPath))
            {
                options.SchemaDiffReportPath = schema.DiffReport;
            }
            if (schema.AllowBreakingChanges is bool rtAbc && options.AllowBreakingSchemaChanges == true) // 默认 true
            {
                options.AllowBreakingSchemaChanges = rtAbc;
            }
        }

        // ── 分布漂移 ──
        var dist = runtime.Distribution;
        if (dist != null)
        {
            if (!string.IsNullOrWhiteSpace(dist.Snapshot) && string.IsNullOrWhiteSpace(options.DistributionSnapshotPath))
            {
                options.DistributionSnapshotPath = dist.Snapshot;
            }
            if (!string.IsNullOrWhiteSpace(dist.DiffBaseline) && string.IsNullOrWhiteSpace(options.DiffDistributionBaseline))
            {
                options.DiffDistributionBaseline = dist.DiffBaseline;
            }
            if (!string.IsNullOrWhiteSpace(dist.DiffReport) && string.IsNullOrWhiteSpace(options.DistributionDiffReportPath))
            {
                options.DistributionDiffReportPath = dist.DiffReport;
            }
            if (dist.LowConfidenceWidth is double rtLcw && options.LowConfidenceWidth == 0.2) // 默认 0.2
            {
                options.LowConfidenceWidth = rtLcw;
            }
        }

        // ── PII / 合规 ──
        var pii = runtime.Pii;
        if (pii != null)
        {
            if (pii.Off is bool rtPiiOff && options.PiiOff == false) // 默认 false
            {
                options.PiiOff = rtPiiOff;
            }
            if (!string.IsNullOrWhiteSpace(pii.Report) && string.IsNullOrWhiteSpace(options.PiiPath))
            {
                options.PiiPath = pii.Report;
            }
            if (!string.IsNullOrWhiteSpace(pii.FailOnLevel) && string.IsNullOrWhiteSpace(options.FailOnPiiLevel))
            {
                options.FailOnPiiLevel = pii.FailOnLevel;
            }
        }

        // ── CI 门禁 ──
        var gates = runtime.Gates;
        if (gates != null)
        {
            if (gates.FailOnViolations is bool rtFov && options.FailOnViolations == false) // 默认 false
            {
                options.FailOnViolations = rtFov;
            }
            if (gates.FailOnOutlierRatio is double rtFor && !options.FailOnOutlierRatio.HasValue) // 默认 null
            {
                options.FailOnOutlierRatio = rtFor;
            }
        }

        // ── P4 深度分析 ──
        var p4 = runtime.P4;
        if (p4 != null)
        {
            if (p4.Correlation is bool rtCorr && options.EnableCorrelation == false) // 默认 false
            {
                options.EnableCorrelation = rtCorr;
            }
            if (p4.StringPatterns is bool rtSp && options.EnableStringPatterns == false) // 默认 false
            {
                options.EnableStringPatterns = rtSp;
            }
            if (p4.UnicodeHygiene is bool rtUh && options.EnableUnicodeHygiene == false) // 默认 false
            {
                options.EnableUnicodeHygiene = rtUh;
            }
            if (p4.TimeSeries is bool rtTs && options.EnableTimeSeries == false) // 默认 false
            {
                options.EnableTimeSeries = rtTs;
            }
            if (!string.IsNullOrWhiteSpace(p4.FuzzyDedupField) && string.IsNullOrWhiteSpace(options.FuzzyDedupField))
            {
                options.FuzzyDedupField = p4.FuzzyDedupField;
            }
            // distributionSnapshot=true 且 distribution.snapshot 未设 → 推迟到 DistributionConfig 处理
            if (p4.DistributionSnapshot is bool rtDs && rtDs
                && string.IsNullOrWhiteSpace(options.DistributionSnapshotPath))
            {
                options.DistributionSnapshotPath = "auto"; // 占位，Run 阶段会回填默认路径
            }
        }

        // ── P6 增量 / 内存 / 进度 / 审计 ──
        var checkpoint = runtime.Checkpoint;
        if (checkpoint != null)
        {
            if (!string.IsNullOrWhiteSpace(checkpoint.Path) && string.IsNullOrWhiteSpace(options.CheckpointPath))
            {
                options.CheckpointPath = checkpoint.Path;
            }
            if (!string.IsNullOrWhiteSpace(checkpoint.ResumeFrom) && string.IsNullOrWhiteSpace(options.ResumeFrom))
            {
                options.ResumeFrom = checkpoint.ResumeFrom;
            }
            if (!string.IsNullOrWhiteSpace(checkpoint.Baseline) && string.IsNullOrWhiteSpace(options.BaselinePath))
            {
                options.BaselinePath = checkpoint.Baseline;
            }
        }

        var memory = runtime.Memory;
        if (memory != null)
        {
            if (memory.MaxMB is long rtMb && options.MaxMemoryMB == 0) // 默认 0
            {
                options.MaxMemoryMB = rtMb;
            }
            if (!string.IsNullOrWhiteSpace(memory.Mode))
            {
                if (Enum.TryParse<MaxMemoryMode>(memory.Mode, ignoreCase: true, out var mode))
                {
                    if (options.MaxMemoryMode == MaxMemoryMode.Soft) // 默认 Soft
                    {
                        options.MaxMemoryMode = mode;
                    }
                }
            }
        }

        var progress = runtime.Progress;
        if (progress != null)
        {
            if (progress.Enabled is bool rtPe && options.ShowProgress == true) // 默认 true
            {
                options.ShowProgress = rtPe;
            }
        }

        var audit = runtime.Audit;
        if (audit != null)
        {
            if (!string.IsNullOrWhiteSpace(audit.Log) && string.IsNullOrWhiteSpace(options.AuditLogPath))
            {
                options.AuditLogPath = audit.Log;
            }
            if (!string.IsNullOrWhiteSpace(audit.Operator) && options.Operator == "anonymous") // 默认 anonymous
            {
                options.Operator = audit.Operator;
            }
        }

        // ── 输出（多格式 / 报告 / 违规 / 质量 / Schema 代码）──
        var outputs = runtime.Outputs;
        if (outputs != null)
        {
            if (!string.IsNullOrWhiteSpace(outputs.Primary) && string.IsNullOrWhiteSpace(options.OutputPath))
            {
                options.OutputPath = outputs.Primary;
            }
            if (!string.IsNullOrWhiteSpace(outputs.Violations) && string.IsNullOrWhiteSpace(options.ViolationsPath))
            {
                options.ViolationsPath = outputs.Violations;
            }
            if (!string.IsNullOrWhiteSpace(outputs.Quality) && string.IsNullOrWhiteSpace(options.QualityPath))
            {
                options.QualityPath = outputs.Quality;
            }
            if (!string.IsNullOrWhiteSpace(outputs.SchemaCode) && string.IsNullOrWhiteSpace(options.ExportSchema))
            {
                // 复用 "format[:path]" 拆分逻辑
                string code = outputs.SchemaCode;
                int colonIdx = code.IndexOf(':');
                if (colonIdx > 0 && colonIdx < code.Length - 1)
                {
                    options.ExportSchema = code.Substring(0, colonIdx);
                    options.SchemaOutputPath = code.Substring(colonIdx + 1);
                }
                else
                {
                    options.ExportSchema = code;
                }
            }
        }
    }
}
