using System.Text.Json;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

public static partial class AsyncAnalyzeEndpoints
{
    private static DateTimeOffset ToUtcOffset(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static AnalyzeJobSnapshotResponse ToResponse(AnalysisJobSnapshot snap)
    {
        string? errorMessage = snap.Error is { } err
            ? $"{err.GetType().Name}: {err.Message}"
            : null;

        return new AnalyzeJobSnapshotResponse(
            Id: snap.Id,
            Status: snap.Status,
            Meta: snap.Meta,
            CreatedAt: snap.CreatedAt,
            TerminalAt: snap.TerminalAt,
            ProgressText: snap.ProgressText,
            ProcessedObjects: snap.ProcessedObjects,
            Result: snap.Result,
            Preview: snap.Preview,
            Error: errorMessage,
            PiiRedacted: snap.PiiRedacted,
            Pii: AnalyzePiiView.From(snap.Pii));
    }

    private static AnalyzeJobSnapshotResponse ToResponse(AnalysisJob job, AnalysisJobSnapshot? runtime = null)
    {
        var status = job.Status switch
        {
            AnalysisJobStatus.Succeeded => AnalysisStatus.Completed,
            AnalysisJobStatus.Failed => AnalysisStatus.Failed,
            AnalysisJobStatus.Cancelled => AnalysisStatus.Cancelled,
            _ => AnalysisStatus.Running
        };

        AnalysisResult? result = null;
        if (status == AnalysisStatus.Completed && !string.IsNullOrWhiteSpace(job.ResultJson))
        {
            try
            {
                result = global::ObjectAnalyzer.Reporting.ResultProjection.ReadStored(job.ResultJson);
            }
            catch (JsonException)
            {
                // 历史结果损坏时仍返回作业状态和其它元数据，不让查询端点整体失败。
            }
        }

        return new AnalyzeJobSnapshotResponse(
            Id: job.Id.ToString("N"),
            Status: status,
            Meta: new AnalysisJobMeta(
                DataFileName: job.SourceName,
                Format: job.SourceType,
                OperatorId: null,
                SubmittedAt: ToUtcOffset(job.StartedAtUtc)),
            CreatedAt: ToUtcOffset(job.StartedAtUtc),
            TerminalAt: job.CompletedAtUtc.HasValue
                ? ToUtcOffset(job.CompletedAtUtc.Value)
                : null,
            ProgressText: null,
            ProcessedObjects: job.TotalObjects,
            Result: result,
            Preview: status == AnalysisStatus.Completed ? runtime?.Preview : null,
            Error: job.Status == AnalysisJobStatus.Failed ? job.ErrorMessage : null,
            PiiRedacted: status == AnalysisStatus.Completed ? runtime?.PiiRedacted ?? true : true,
            Pii: status == AnalysisStatus.Completed ? AnalyzePiiView.From(runtime?.Pii) : null);
    }
}
