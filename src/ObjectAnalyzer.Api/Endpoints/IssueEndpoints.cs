using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>质量问题、责任人与整改记录 API。</summary>
public static class IssueEndpoints
{
    public static IEndpointRouteBuilder MapIssueApi(this IEndpointRouteBuilder routes)
    {
        var issues = routes.MapGroup("/issues").WithTags("质量问题");

        issues.MapGet("/{jobId:guid}", async Task<Ok<IReadOnlyList<QualityIssue>>> (
            Guid jobId,
            IQualityIssueQuery query,
            CancellationToken cancellationToken) =>
                TypedResults.Ok(await query.GetByJobAsync(jobId, cancellationToken)))
            .WithSummary("获取指定作业的质量问题");

        issues.MapGet("/{jobId:guid}/responsibilities", async Task<Ok<IReadOnlyList<QualityIssueResponsibility>>> (
            Guid jobId,
            IQualityIssueResponsibilityStore store,
            CancellationToken cancellationToken) =>
                TypedResults.Ok(await store.GetByJobAsync(jobId, cancellationToken)))
            .WithSummary("获取质量问题责任分配");

        issues.MapPut("/{jobId:guid}/responsibilities/{fieldName}/{check}",
            async Task<Results<Ok<QualityIssueResponsibility>, BadRequest<ErrorResponse>>> (
                Guid jobId,
                string fieldName,
                string check,
                IssueResponsibilityRequest request,
                IQualityIssueResponsibilityStore store,
                CancellationToken cancellationToken) =>
            {
                var responsibility = new QualityIssueResponsibility(
                    jobId,
                    fieldName,
                    check,
                    request.Owner,
                    request.Status,
                    request.Comment,
                    DateTime.UtcNow);
                try
                {
                    responsibility.Validate();
                    await store.SaveAsync(responsibility, cancellationToken);
                    return TypedResults.Ok(responsibility);
                }
                catch (ArgumentException ex)
                {
                    return TypedResults.BadRequest(new ErrorResponse(ex.Message));
                }
            })
            .WithSummary("保存质量问题责任分配");

        issues.MapGet("/{jobId:guid}/remediations/{fieldName}/{check}", async Task<Ok<IReadOnlyList<QualityIssueRemediation>>> (
            Guid jobId,
            string fieldName,
            string check,
            IQualityIssueRemediationStore store,
            CancellationToken cancellationToken) =>
                TypedResults.Ok(await store.GetByIssueAsync(jobId, fieldName, check, cancellationToken)))
            .WithSummary("获取指定质量问题的整改记录");

        issues.MapPost("/{jobId:guid}/remediations",
            async Task<Results<Ok<QualityIssueRemediation>, BadRequest<ErrorResponse>>> (
                Guid jobId,
                RemediationRequest request,
                IQualityIssueRemediationStore store,
                CancellationToken cancellationToken) =>
            {
                var remediation = new QualityIssueRemediation(
                    request.Id == Guid.Empty ? Guid.NewGuid() : request.Id,
                    jobId,
                    request.FieldName,
                    request.Check,
                    request.Action,
                    request.Status,
                    request.DueAtUtc,
                    request.CompletedAtUtc,
                    request.Comment,
                    request.CreatedAtUtc ?? DateTime.UtcNow);
                try
                {
                    remediation.Validate();
                    await store.SaveAsync(remediation, cancellationToken);
                    return TypedResults.Ok(remediation);
                }
                catch (ArgumentException ex)
                {
                    return TypedResults.BadRequest(new ErrorResponse(ex.Message));
                }
            })
            .WithSummary("新增质量问题整改记录");

        return routes;
    }

    public sealed record IssueResponsibilityRequest(
        string? Owner = null,
        string Status = "Open",
        string? Comment = null);

    public sealed record RemediationRequest(
        string FieldName,
        string Check,
        string Action,
        string Status = "Planned",
        DateTime? DueAtUtc = null,
        DateTime? CompletedAtUtc = null,
        string? Comment = null,
        Guid Id = default,
        DateTime? CreatedAtUtc = null);
}
