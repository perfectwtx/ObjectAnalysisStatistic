using System.Text;
using System.Text.Json;
using ObjectAnalyzer.Analysis.Pipeline;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// W7 编排拆分——Report 与 Gates 阶段：分析完成后的控制台呈现、报告导出、
/// Schema / 分布快照，以及 CI 质量门禁评估（退出码 3/4/5/6）。
/// </summary>
public static partial class AnalysisRunner
{
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

    /// <summary>
    /// CI 质量门禁评估：按 数据质量(3) → 数据安全合规(4) → 异常值(5) → Schema 演进(6) 的顺序
    /// 逐条检查，首个命中的门禁决定退出码。结果（退出码）是数据，便于 W4 演进为门禁注册表。
    /// </summary>
    private static int EvaluateQualityGates(AnalysisResult result, AnalysisOptions options)
    {
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

        return exitCode;
    }

    /// <summary>门禁退出码对应的审计摘要（0 = 成功，无摘要）。</summary>
    private static string? GateFailureMessage(int exitCode) => exitCode switch
    {
        3 => "数据质量门禁未通过",
        4 => "数据安全合规门禁未通过",
        5 => "异常值门禁未通过",
        6 => "Schema 演进门禁未通过",
        _ => null
    };
}
