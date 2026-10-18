using System.Text;
using System.Text.Json;
using ObjectAnalyzer.Analysis.Pipeline;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// W7 编排拆分——Report 与 Gates 阶段：分析完成后的控制台呈现、报告导出、
/// Schema / 分布快照，以及 CI 质量门禁评估（退出码 3/4/5/6）。
/// </summary>
public sealed partial class AnalysisCommandRunner
{
    /// W1 收尾：呈现 / 导出（宿主职责）。计算路径由 <see cref="AnalysisPipeline.Execute"/> 统一完成；
    /// 本方法只负责分析完成后的控制台呈现、报告导出与 Schema / 分布快照。
    /// </summary>
    private void ProcessAndReport(
        PipelineResult pipelineResult,
        AnalysisOptions options,
        AnalysisRules? rules,
        AnalysisRunContext runContext,
        CancellationToken cancellationToken)
    {
        AnalysisResult result = pipelineResult.Result;

        _services.Console.MarkupLine("[green]分析完成！[/]\n");

        // P6.3：内存预算总结（仅当启用了 --max-memory 时打印）
        if (runContext.MemoryBudget is { IsEnabled: true } finalBudget)
        {
            _services.Console.MarkupLine(
                $"[grey][[内存]][/] 观测到的进程托管堆峰值 [blue]{finalBudget.PeakMB:F1} MB[/]（--max-memory 阈值 {options.Memory.MaxMemoryMB:N0} MB，模式 {options.Memory.MaxMemoryMode}）");
            // hard 模式：分析完成后若曾触发 HardLimitExceeded（来自 progress 回调内的 Check），
            // 此处由主流程显式抛错；不能放在 progress 回调里直接 throw——会变成"未观察异常"逃出主流程。
            finalBudget.ThrowIfHardExceeded();
        }

        if (result.Reliability is { Limitations.Count: > 0 } reliability)
            foreach (var item in reliability.Limitations)
                _services.Console.WriteLine($"{item.Metric} ({item.Accuracy}): {item.Reason}");

        // Schema 快照导出 / diff 计算（必须在 ConsoleReporter.Print 与 ReportExporter.Save 之前，
        // 这样控制台与 HTML/MD/JSON 报告的「Schema 演进」小节才能带 diff 数据）
        ProcessSchemaSnapshot(result, options);

        // 分布快照导出 / diff 计算（与 SchemaSnapshot 互补：看分布变化）
        ProcessDistributionSnapshot(result, options, pipelineResult.QuantileSketches, pipelineResult.DistinctSketches);

        // 输出控制台统计结果（--pii-off 时保留原始值）。呈现由宿主注入的 IResultPresenter 完成
        // （CLI：Spectre 富渲染；API/Web：静默或结构化推送）。
        _services.Presenter.Present(result, options);

        // 输出数据安全合规摘要（发现敏感字段时提示打码与合规导出；--pii-off 时跳过）
        if (!options.Pii.PiiOff)
        {
            PiiReport.PrintSummary(result, _services.Console);
        }

        // 如果指定了输出路径，则保存详细报告
        if (!string.IsNullOrEmpty(options.Report.OutputPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportExporter.Save(result, options.Report.OutputPath, redactPii: !options.Pii.PiiOff);
            _services.Console.MarkupLine($"报告已保存至: {MarkupText.Escape(options.Report.OutputPath)}");
        }

        // 如果指定了违规导出路径，则单独导出枚举违规清单
        if (!string.IsNullOrEmpty(options.Report.ViolationsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ViolationExporter.Save(result, options.Report.ViolationsPath, redactPii: !options.Pii.PiiOff, console: _services.Console);
        }

        // 如果指定了数据质量报告路径，导出结构化 Pass/Fail 质量报告
        if (!string.IsNullOrEmpty(options.Report.QualityPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            QualityExporter.Save(result, options.Report.QualityPath, redactPii: !options.Pii.PiiOff, console: _services.Console);
        }

        // 如果指定了数据安全合规报告路径，导出合规等级 + 敏感字段清单（--pii-off 时跳过）
        if (!string.IsNullOrEmpty(options.Pii.PiiPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PiiExporter.Save(result, options.Pii.PiiPath, _services.Console);
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

            _services.Console.MarkupLine($"正在导出 [bold green]{options.Report.ExportSchema}[/] 格式 Schema 定义至: [blue]{MarkupText.Escape(schemaPath)}[/] ...");
            SchemaExporter.Export(result, options.Report.ExportSchema, schemaPath);
            _services.Console.MarkupLine("[green]Schema 定义代码成功导出！[/]");
        }

        // Schema 快照导出 / diff 计算（独立于代码生成器 SchemaExporter）
        // 已在 ProcessAndReport 前置阶段完成（见 ProcessAndReport 顶部），此处保留注释便于阅读。
    }

    /// <summary>
    /// 处理 Schema 快照导出（--export-schema-snapshot）与 diff 计算（--diff-schema）。
    /// 失败抛 FileNotFoundException / InvalidDataException，由调用方转为退出码 1。
    /// </summary>
    private void ProcessSchemaSnapshot(AnalysisResult result, AnalysisOptions options)
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
            SchemaSnapshotExporter.Save(result, options.Schema.SchemaSnapshotPath, source, _services.Console);
        }

        if (!string.IsNullOrEmpty(options.Schema.DiffSchemaBaseline))
        {
            var baseline = SchemaDiffer.LoadFromFile(options.Schema.DiffSchemaBaseline!);
            // 优先用刚导出的快照（确保与磁盘上的 baseline 比较时内容完全一致），否则现场构建
            currentSnapshot ??= SchemaSnapshotExporter.Build(result, source);
            var diff = SchemaDiffer.Compute(baseline, currentSnapshot);
            result.SchemaDiff = diff;

            _services.Console.WriteLine();
            _services.Console.MarkupLine("[bold]── Schema 演进（vs 基线） ──[/]");
            SchemaDiffer.PrintSummary(diff, console: _services.Console);

            if (!string.IsNullOrEmpty(options.Schema.SchemaDiffReportPath))
            {
                string? dir = Path.GetDirectoryName(options.Schema.SchemaDiffReportPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(options.Schema.SchemaDiffReportPath, JsonSerializer.Serialize(diff, s_diffJsonOptions), new UTF8Encoding(false));
                _services.Console.MarkupLine(
                    $"[green]Schema diff 已导出至[/] [blue]{MarkupText.Escape(options.Schema.SchemaDiffReportPath)}[/]");
            }
        }
    }

    /// <summary>
    /// 处理分布快照导出（--export-distribution-snapshot）与分布 diff 计算（--diff-distribution）。
    /// 与 <see cref="ProcessSchemaSnapshot"/> 互补：后者看结构变化，本方法看分布变化（覆盖率 / 均值 / 分位 / distinct）。
    /// 失败抛 FileNotFoundException / InvalidDataException，由调用方转为退出码 1。
    /// </summary>
    private void ProcessDistributionSnapshot(
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
            _services.Console.MarkupLine(
                $"[green]分布快照已导出至[/] [blue]{MarkupText.Escape(options.Distribution.DistributionSnapshotPath)}[/] [grey]({currentSnapshot.Fields.Count} 字段)[/]");
        }

        if (!string.IsNullOrEmpty(options.Distribution.DiffDistributionBaseline))
        {
            var baseline = DistributionSnapshotExporter.LoadFromFile(options.Distribution.DiffDistributionBaseline!);
            currentSnapshot ??= DistributionSnapshotExporter.Build(result, quantileSketches, distinctSketches, source);
            var diff = DistributionDiffer.Compute(baseline, currentSnapshot);
            result.DistributionDiff = diff;

            _services.Console.WriteLine();
            _services.Console.MarkupLine("[bold]── 分布漂移（vs 基线） ──[/]");
            DistributionDiffer.PrintSummary(diff, console: _services.Console);

            if (!string.IsNullOrEmpty(options.Distribution.DistributionDiffReportPath))
            {
                string? dir = Path.GetDirectoryName(options.Distribution.DistributionDiffReportPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(options.Distribution.DistributionDiffReportPath, JsonSerializer.Serialize(diff, s_diffJsonOptions), new UTF8Encoding(false));
                _services.Console.MarkupLine(
                    $"[green]分布 diff 已导出至[/] [blue]{MarkupText.Escape(options.Distribution.DistributionDiffReportPath)}[/]");
            }
        }
    }

    /// <summary>
    /// CI 质量门禁评估：W4 门禁注册表逐条检查（数据质量 3 → 合规 4 → 异常值 5 → Schema 演进 6），
    /// 首个触发的门禁决定退出码；退出码与审计摘要均为门禁的数据属性。
    /// </summary>
    private (int ExitCode, string? FailureMessage) EvaluateQualityGates(AnalysisResult result, AnalysisOptions options)
    {
        return QualityGateRegistry.Evaluate(result, options, _services.Console);
    }
}
