using ObjectAnalyzer.Analysis.Pipeline;

namespace ObjectAnalyzer.Analysis;

/// <summary>
/// W7 编排拆分——Resume 阶段：P6.2 增量分析的合并与落盘。
/// --resume 合并快照聚合结果、--baseline 合并历史结果 JSON、--checkpoint 落盘当前快照，
/// 合并发生后覆写 --output 以反映最终聚合。
/// </summary>
public static partial class AnalysisRunner
{
    /// <summary>
    /// P6.2：--resume / --baseline 合并 + --checkpoint 落盘 + 合并后报告覆写。
    /// resumeSnapshot / startFromPosition 来自 Preflight 阶段，runContext 携带本 run 已分析记录数。
    /// </summary>
    private static void MergeIncrementalResults(
        AnalysisOptions options,
        AnalysisResult result,
        AnalyzerSnapshot? resumeSnapshot,
        long startFromPosition,
        AnalysisRunContext runContext)
    {
        // --resume 合并快照中的已聚合结果（FieldStatistic / Welford / 直方图 / 枚举违规均可增量合并）
        if (resumeSnapshot != null)
        {
            AnalysisServices.Current.Console.MarkupLine(
                $"[green][[检查点]][/] 正在合并已处理的 {resumeSnapshot.Result.TotalObjects:N0} 条记录结果（含 {resumeSnapshot.Result.FieldStatistics.Count} 个字段）...");
            result.MergeFrom(resumeSnapshot.Result);
            AnalysisServices.Current.Console.MarkupLine(
                $"[green][[检查点]][/] 合并后共 {result.TotalObjects:N0} 条记录");
        }

        // --baseline 合并历史 AnalysisResult JSON（与 --resume 互补：--resume 是从源文件中间续跑；--baseline 是合并任意历史结果）
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

        // --checkpoint 落盘当前快照（用于下次 --resume）
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

        // --resume / --baseline 合并后，需要把合并后的最终结果重新导出到 OutputPath。
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
    }
}
