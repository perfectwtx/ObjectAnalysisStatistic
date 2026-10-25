using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

public static class RemediationVerificationEndpoints
{
    public static IEndpointRouteBuilder MapRemediationVerificationApi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/analysis/jobs/{jobId:guid}/remediation-verification", GetStatus).WithTags("质量问题");
        routes.MapPost("/analysis/jobs/{jobId:guid}/remediation-verification/retry", Retry).WithTags("质量问题");
        return routes;
    }

    private static async Task<Results<Ok<VerificationStatusResponse>, NotFound<ErrorResponse>>> GetStatus(
        Guid jobId, IAnalysisHistoryStore history, CancellationToken cancellationToken)
    {
        if (await history.GetAsync(jobId, cancellationToken) is null)
            return TypedResults.NotFound(new ErrorResponse("未找到分析作业。"));
        return TypedResults.Ok(ToResponse(jobId, await history.GetRemediationVerificationAsync(jobId, cancellationToken)));
    }

    private static async Task<Results<Ok<VerificationStatusResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>>> Retry(
        Guid jobId, AnalysisJobService service, CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(ToResponse(jobId, await service.RetryRemediationVerificationAsync(jobId, cancellationToken)));
        }
        catch (KeyNotFoundException) { return TypedResults.NotFound(new ErrorResponse("未找到分析作业。")); }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return TypedResults.Conflict(new ErrorResponse("无法重试：需要成功任务的完整结果及原先用于验证的历史任务。"));
        }
    }

    private static VerificationStatusResponse ToResponse(Guid id, RemediationVerificationOutcome? outcome)
        => new(id, outcome is null ? "NotStarted" : outcome.Succeeded ? "Completed" : "Failed",
            outcome?.Error, outcome?.AttemptedAtUtc);

    public sealed record VerificationStatusResponse(Guid JobId, string Status, string? Error, DateTime? AttemptedAtUtc);
}
