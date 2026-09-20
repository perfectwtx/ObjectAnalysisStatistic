using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>质量问题、责任人与整改记录 API。</summary>
public static class IssueEndpoints
{
    private static readonly HashSet<string> s_validStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Open", "InProgress", "Resolved", "Ignored"
    };

    public static IEndpointRouteBuilder MapIssueApi(this IEndpointRouteBuilder routes)
    {
        var issues = routes.MapGroup("/issues").WithTags("质量问题");

        issues.MapGet("", GetIssueCenter)
            .WithSummary("获取质量问题中心数据")
            .WithDescription("支持 jobId、severity、check、status、limit、offset 筛选与分页；未保存责任状态的问题默认视为 Open。");

        issues.MapGet("/summary", GetIssueSummary)
            .WithSummary("获取质量问题聚合摘要")
            .WithDescription("按 severity、check、field、status 返回问题项与违规次数聚合。");

        issues.MapGet("/{jobId:guid}", async Task<Ok<IReadOnlyList<QualityIssue>>> (
            Guid jobId,
            IQualityIssueQuery query,
            CancellationToken cancellationToken) =>
                TypedResults.Ok(await query.GetByJobAsync(jobId, cancellationToken)))
            .WithSummary("获取指定作业的质量问题");

        routes.MapGet("/jobs/{jobId:guid}/issues", GetJobIssues)
            .WithTags("质量问题")
            .WithSummary("获取指定作业的可运营质量问题");

        issues.MapMethods("/{jobId:guid}/{fieldName}/{check}/status", ["PATCH", "PUT"], UpdateIssueStatus)
            .WithSummary("更新质量问题处理状态")
            .WithDescription("状态只允许 Open、InProgress、Resolved、Ignored。责任人和备注沿用已有责任分配记录。");

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
                    if (!s_validStatuses.Contains(responsibility.Status))
                        return TypedResults.BadRequest(new ErrorResponse("处理状态只允许 Open、InProgress、Resolved、Ignored。"));
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
                    request.Id ?? Guid.NewGuid(),
                    jobId,
                    request.FieldName,
                    request.Check,
                    request.Action,
                    request.Status,
                    request.DueAtUtc,
                    request.CompletedAtUtc,
                    request.Comment)
                {
                    CreatedAtUtc = request.CreatedAtUtc ?? DateTime.UtcNow
                };
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

    private static async Task<Ok<IssueQueryResponse>> GetIssueCenter(
        Guid? jobId,
        string? severity,
        string? check,
        string? status,
        int? limit,
        int? offset,
        IAnalysisHistoryStore history,
        IQualityIssueQuery query,
        IQualityIssueResponsibilityStore responsibilities,
        CancellationToken cancellationToken)
    {
        var jobs = (await history.GetRecentAsync(100, cancellationToken))
            .Where(x => x.Status == AnalysisJobStatus.Succeeded)
            .OrderByDescending(x => x.StartedAtUtc)
            .ToList();

        var selectedJob = jobId.HasValue
            ? jobs.FirstOrDefault(x => x.Id == jobId.Value)
            : jobs.FirstOrDefault();

        var targetJobs = jobId.HasValue
            ? jobs.Where(x => x.Id == jobId.Value).ToList()
            : jobs;

        var all = await LoadIssueItemsAsync(targetJobs, query, responsibilities, cancellationToken);
        var filtered = Filter(all, severity, check, status);
        var actualOffset = Math.Max(0, offset ?? 0);
        var actualLimit = Math.Clamp(limit ?? 200, 1, 500);
        var items = filtered.Skip(actualOffset).Take(actualLimit).ToList();

        return TypedResults.Ok(new IssueQueryResponse(
            Items: items,
            Total: filtered.Count,
            Offset: actualOffset,
            Limit: actualLimit,
            Jobs: jobs,
            SelectedJob: selectedJob,
            Issues: items));
    }

    private static async Task<Ok<IssueSummaryResponse>> GetIssueSummary(
        Guid? jobId,
        string? severity,
        string? check,
        string? status,
        IAnalysisHistoryStore history,
        IQualityIssueQuery query,
        IQualityIssueResponsibilityStore responsibilities,
        CancellationToken cancellationToken)
    {
        var jobs = (await history.GetRecentAsync(100, cancellationToken))
            .Where(x => x.Status == AnalysisJobStatus.Succeeded)
            .OrderByDescending(x => x.StartedAtUtc)
            .ToList();
        if (jobId.HasValue)
            jobs = jobs.Where(x => x.Id == jobId.Value).ToList();

        var all = await LoadIssueItemsAsync(jobs, query, responsibilities, cancellationToken);
        var filtered = Filter(all, severity, check, status);

        return TypedResults.Ok(new IssueSummaryResponse(
            TotalIssues: filtered.Count,
            TotalViolations: filtered.Sum(x => x.Count),
            BySeverity: GroupCounts(filtered.Select(x => x.Severity)),
            ByCheck: GroupCounts(filtered.Select(x => x.Check)),
            ByField: GroupCounts(filtered.Select(x => x.Field)),
            ByStatus: GroupCounts(filtered.Select(x => x.Status))));
    }

    private static async Task<Results<Ok<IReadOnlyList<IssueItemResponse>>, NotFound<ErrorResponse>>> GetJobIssues(
        Guid jobId,
        string? severity,
        string? check,
        string? status,
        int? limit,
        int? offset,
        IAnalysisHistoryStore history,
        IQualityIssueQuery query,
        IQualityIssueResponsibilityStore responsibilities,
        CancellationToken cancellationToken)
    {
        var job = await history.GetAsync(jobId, cancellationToken);
        if (job is null)
            return TypedResults.NotFound(new ErrorResponse($"未找到分析作业：{jobId}"));

        var items = await LoadIssueItemsAsync([job], query, responsibilities, cancellationToken);
        var filtered = Filter(items, severity, check, status);
        var actualOffset = Math.Max(0, offset ?? 0);
        var actualLimit = Math.Clamp(limit ?? 200, 1, 500);
        return TypedResults.Ok<IReadOnlyList<IssueItemResponse>>(filtered.Skip(actualOffset).Take(actualLimit).ToList());
    }

    private static async Task<Results<Ok<IssueItemResponse>, BadRequest<ErrorResponse>, NotFound<ErrorResponse>>> UpdateIssueStatus(
        Guid jobId,
        string fieldName,
        string check,
        IssueStatusRequest request,
        IQualityIssueQuery query,
        IQualityIssueResponsibilityStore responsibilities,
        CancellationToken cancellationToken)
    {
        if (request is null || !s_validStatuses.Contains(request.Status))
            return TypedResults.BadRequest(new ErrorResponse("状态只允许 Open、InProgress、Resolved、Ignored。"));

        var issue = (await query.GetByJobAsync(jobId, cancellationToken))
            .FirstOrDefault(x => string.Equals(x.FieldName, fieldName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Check, check, StringComparison.OrdinalIgnoreCase));
        if (issue is null)
            return TypedResults.NotFound(new ErrorResponse($"未找到质量问题：{jobId}/{fieldName}/{check}"));

        var current = await responsibilities.GetAsync(jobId, fieldName, check, cancellationToken);
        var saved = new QualityIssueResponsibility(
            jobId,
            issue.FieldName,
            issue.Check,
            current?.Owner,
            request.Status,
            current?.Comment,
            DateTime.UtcNow);
        await responsibilities.SaveAsync(saved, cancellationToken);

        return TypedResults.Ok(ToItem(issue, null, saved));
    }

    private static async Task<List<IssueItemResponse>> LoadIssueItemsAsync(
        IReadOnlyList<AnalysisJob> jobs,
        IQualityIssueQuery query,
        IQualityIssueResponsibilityStore responsibilities,
        CancellationToken cancellationToken)
    {
        var result = new List<IssueItemResponse>();
        foreach (var job in jobs)
        {
            var issues = await query.GetByJobAsync(job.Id, cancellationToken);
            var owners = (await responsibilities.GetByJobAsync(job.Id, cancellationToken))
                .ToDictionary(x => new IssueKey(x.FieldName, x.Check), StringComparerKey.Instance);

            foreach (var issue in issues)
            {
                owners.TryGetValue(new IssueKey(issue.FieldName, issue.Check), out var responsibility);
                result.Add(ToItem(issue, job, responsibility));
            }
        }

        return result;
    }

    private static IssueItemResponse ToItem(QualityIssue issue, AnalysisJob? currentJob, QualityIssueResponsibility? responsibility)
    {
        var severity = GetSeverity(issue.Check);
        return new IssueItemResponse(
            Id: $"{issue.JobId:N}:{issue.FieldName}:{issue.Check}",
            JobId: issue.JobId,
            Field: issue.FieldName,
            FieldName: issue.FieldName,
            Check: issue.Check,
            Title: QualityCheckTypes.Label(issue.Check),
            Dimension: GetDimension(issue.Check),
            Severity: severity,
            Count: issue.Count,
            Message: issue.Message,
            Detail: issue.Message,
            Status: responsibility?.Status ?? "Open",
            Owner: responsibility?.Owner,
            Comment: responsibility?.Comment,
            CreatedAt: currentJob?.StartedAtUtc);
    }

    private static List<IssueItemResponse> Filter(
        IEnumerable<IssueItemResponse> source,
        string? severity,
        string? check,
        string? status)
        => source
            .Where(x => string.IsNullOrWhiteSpace(severity)
                || string.Equals(x.Severity, severity, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(check)
                || string.Equals(x.Check, check, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(status)
                || string.Equals(x.Status, status, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Field, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Check, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static Dictionary<string, int> GroupCounts(IEnumerable<string> values)
        => values.GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

    private static string GetSeverity(string check) => check switch
    {
        QualityCheckTypes.Unique or QualityCheckTypes.DuplicateGroups => "critical",
        QualityCheckTypes.Required or QualityCheckTypes.Range or QualityCheckTypes.Pattern or QualityCheckTypes.EnumValues => "high",
        QualityCheckTypes.NullRate or QualityCheckTypes.RowCount or QualityCheckTypes.DuplicateRate or QualityCheckTypes.OutlierRatio => "medium",
        _ => "low"
    };

    private static string GetDimension(string check) => check switch
    {
        QualityCheckTypes.Required or QualityCheckTypes.NullRate or QualityCheckTypes.RowCount => "completeness",
        QualityCheckTypes.Unique or QualityCheckTypes.DuplicateRate or QualityCheckTypes.DuplicateGroups => "uniqueness",
        QualityCheckTypes.Pattern or QualityCheckTypes.EnumValues => "consistency",
        QualityCheckTypes.OutlierRatio => "anomaly",
        _ => "validity"
    };

    private readonly record struct IssueKey(string Field, string Check);

    private sealed class StringComparerKey : IEqualityComparer<IssueKey>
    {
        public static readonly StringComparerKey Instance = new();

        public bool Equals(IssueKey x, IssueKey y) =>
            string.Equals(x.Field, y.Field, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Check, y.Check, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(IssueKey obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Field),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Check));
    }

    public sealed record IssueQueryResponse(
        IReadOnlyList<IssueItemResponse> Items,
        int Total,
        int Offset,
        int Limit,
        IReadOnlyList<AnalysisJob> Jobs,
        AnalysisJob? SelectedJob,
        IReadOnlyList<IssueItemResponse> Issues);

    public sealed record IssueSummaryResponse(
        int TotalIssues,
        long TotalViolations,
        IReadOnlyDictionary<string, int> BySeverity,
        IReadOnlyDictionary<string, int> ByCheck,
        IReadOnlyDictionary<string, int> ByField,
        IReadOnlyDictionary<string, int> ByStatus);

    public sealed record IssueItemResponse(
        string Id,
        Guid JobId,
        string Field,
        string FieldName,
        string Check,
        string Title,
        string Dimension,
        string Severity,
        long Count,
        string Message,
        string Detail,
        string Status,
        string? Owner,
        string? Comment,
        DateTime? CreatedAt);

    public sealed record IssueStatusRequest(string Status);

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
        Guid? Id = null,
        DateTime? CreatedAtUtc = null);
}
