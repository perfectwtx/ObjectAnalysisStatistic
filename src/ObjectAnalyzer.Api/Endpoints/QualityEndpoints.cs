using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>
/// 面向 Web/React 的分析历史与质量查询 API。
/// API 只负责组合 Platform 已有的查询能力，不复制质量计算和持久化逻辑。
/// </summary>
public static class QualityEndpoints
{
    public static IEndpointRouteBuilder MapQualityApi(this IEndpointRouteBuilder routes)
    {
        var quality = routes.MapGroup("/quality").WithTags("数据质量");

        quality.MapGet("/dashboard", GetDashboard)
            .WithSummary("获取最近一次成功分析的质量总览");

        quality.MapGet("/trend", GetTrend)
            .WithSummary("获取质量趋势");

        quality.MapGet("/fields/{jobId:guid}", GetFields)
            .WithSummary("获取指定作业的字段质量");

        quality.MapGet("/relationships/{jobId:guid}", GetRelationships)
            .WithSummary("获取指定作业的字段关系");

        quality.MapGet("/object-relationships/{jobId:guid}", GetObjectRelationships)
            .WithSummary("获取指定作业的对象关系");

        quality.MapGet("/schema-impact/{jobId:guid}", GetSchemaImpact)
            .WithSummary("获取指定作业的 Schema 影响");

        quality.MapGet("/recommendations", GetRecommendations)
            .WithSummary("获取最近一次分析的质量修复建议");

        quality.MapGet("/regression", GetRegression)
            .WithSummary("比较最近两次成功分析并返回质量回归");

        return routes;
    }

    public static IEndpointRouteBuilder MapAnalysisHistoryApi(this IEndpointRouteBuilder routes)
    {
        var analysis = routes.MapGroup("/analysis").WithTags("分析历史");

        analysis.MapGet("/jobs", GetJobs)
            .WithSummary("获取分析作业历史");

        analysis.MapGet("/jobs/{jobId:guid}", GetJob)
            .WithSummary("获取分析作业详情");

        analysis.MapGet("/jobs/{jobId:guid}/result", GetJobResult)
            .WithSummary("获取分析作业结果");

        return routes;
    }

    private static async Task<Ok<IReadOnlyList<AnalysisJob>>> GetJobs(
        int? limit,
        IAnalysisHistoryStore store,
        CancellationToken cancellationToken)
    {
        var actualLimit = Math.Clamp(limit ?? 50, 1, 500);
        return TypedResults.Ok(await store.GetRecentAsync(actualLimit, cancellationToken));
    }

    private static async Task<Results<Ok<AnalysisJob>, NotFound<ErrorResponse>>> GetJob(
        Guid jobId,
        IAnalysisHistoryStore store,
        CancellationToken cancellationToken)
    {
        var job = await store.GetAsync(jobId, cancellationToken);
        return job is null
            ? TypedResults.NotFound(new ErrorResponse($"未找到分析作业：{jobId}"))
            : TypedResults.Ok(job);
    }

    private static async Task<Results<Ok<JsonElement>, NotFound<ErrorResponse>, BadRequest<ErrorResponse>>> GetJobResult(
        Guid jobId,
        IAnalysisHistoryStore store,
        CancellationToken cancellationToken)
    {
        var job = await store.GetAsync(jobId, cancellationToken);
        if (job is null)
            return TypedResults.NotFound(new ErrorResponse($"未找到分析作业：{jobId}"));
        if (string.IsNullOrWhiteSpace(job.ResultJson))
            return TypedResults.BadRequest(new ErrorResponse("该分析作业没有可用的结果。"));

        try
        {
            using var document = JsonDocument.Parse(job.ResultJson);
            return TypedResults.Ok(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return TypedResults.BadRequest(new ErrorResponse("该分析作业的历史结果 JSON 无法解析。"));
        }
    }

    private static async Task<Ok<QualityDashboardResponse>> GetDashboard(
        IAnalysisHistoryStore history,
        IQualityHistoryQuery qualityHistory,
        IQualityIssueQuery issues,
        CancellationToken cancellationToken)
    {
        var jobs = (await history.GetRecentAsync(100, cancellationToken))
            .Where(x => x.Status == AnalysisJobStatus.Succeeded && !string.IsNullOrWhiteSpace(x.ResultJson))
            .OrderByDescending(x => x.CompletedAtUtc ?? x.StartedAtUtc)
            .ToList();

        var job = jobs.FirstOrDefault();
        if (job is null)
            return TypedResults.Ok(new QualityDashboardResponse(null, null, Array.Empty<QualitySnapshot>(), Array.Empty<QualityIssue>(), Array.Empty<QualityGateResult>()));

        AnalysisResult? result = null;
        if (!string.IsNullOrWhiteSpace(job.ResultJson))
        {
            try { result = JsonSerializer.Deserialize<AnalysisResult>(job.ResultJson); }
            catch (JsonException) { }
        }

        var snapshots = await qualityHistory.GetSnapshotsAsync(limit: 8, cancellationToken: cancellationToken);
        var topIssues = (await issues.GetByJobAsync(job.Id, cancellationToken))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.FieldName)
            .Take(5)
            .ToList();

        var gates = DeserializeList<QualityGateResult>(job.GateResultsJson);
        var metrics = result is null ? null : QualityMetricsCalculator.Calculate(result);

        return TypedResults.Ok(new QualityDashboardResponse(
            job,
            metrics,
            snapshots,
            topIssues,
            gates));
    }

    private static async Task<Ok<IReadOnlyList<QualitySnapshot>>> GetTrend(
        string? sourceName,
        int? limit,
        IQualityHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var actualLimit = Math.Clamp(limit ?? 100, 1, 500);
        return TypedResults.Ok(await query.GetSnapshotsAsync(sourceName, actualLimit, cancellationToken));
    }

    private static async Task<Results<Ok<FieldQualityResponse>, NotFound<ErrorResponse>>> GetFields(
        Guid jobId,
        IAnalysisHistoryStore history,
        IQualityHistoryQuery qualityHistory,
        IQualityIssueQuery issues,
        CancellationToken cancellationToken)
    {
        var job = await history.GetAsync(jobId, cancellationToken);
        if (job is null)
            return TypedResults.NotFound(new ErrorResponse($"未找到分析作业：{jobId}"));

        var fields = await qualityHistory.GetFieldsByJobAsync(jobId, cancellationToken);
        var qualityIssues = await issues.GetByJobAsync(jobId, cancellationToken);
        return TypedResults.Ok(new FieldQualityResponse(job, fields, qualityIssues));
    }

    private static async Task<Results<Ok<JsonElement>, NotFound<ErrorResponse>, BadRequest<ErrorResponse>>> GetRelationships(
        Guid jobId,
        IAnalysisHistoryStore history,
        CancellationToken cancellationToken)
        => await GetResultSection(jobId, "correlationMatrix", history, cancellationToken);

    private static async Task<Results<Ok<JsonElement>, NotFound<ErrorResponse>, BadRequest<ErrorResponse>>> GetObjectRelationships(
        Guid jobId,
        IAnalysisHistoryStore history,
        CancellationToken cancellationToken)
        => await GetResultSection(jobId, "objectRelationships", history, cancellationToken);

    private static async Task<Results<Ok<JsonElement>, NotFound<ErrorResponse>, BadRequest<ErrorResponse>>> GetSchemaImpact(
        Guid jobId,
        IAnalysisHistoryStore history,
        CancellationToken cancellationToken)
        => await GetResultSection(jobId, "schemaDiff", history, cancellationToken);

    private static async Task<Results<Ok<JsonElement>, NotFound<ErrorResponse>, BadRequest<ErrorResponse>>> GetResultSection(
        Guid jobId,
        string propertyName,
        IAnalysisHistoryStore history,
        CancellationToken cancellationToken)
    {
        var job = await history.GetAsync(jobId, cancellationToken);
        if (job is null)
            return TypedResults.NotFound(new ErrorResponse($"未找到分析作业：{jobId}"));
        if (string.IsNullOrWhiteSpace(job.ResultJson))
            return TypedResults.BadRequest(new ErrorResponse("该分析作业没有可用的结果。"));

        try
        {
            using var document = JsonDocument.Parse(job.ResultJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var value))
                return TypedResults.Ok((JsonElement)default);
            return TypedResults.Ok(value.Clone());
        }
        catch (JsonException)
        {
            return TypedResults.BadRequest(new ErrorResponse("历史结果 JSON 无法解析。"));
        }
    }

    private static async Task<Ok<QualityRecommendationsResponse>> GetRecommendations(
        IAnalysisHistoryStore history,
        CancellationToken cancellationToken)
    {
        var job = (await history.GetRecentAsync(100, cancellationToken))
            .Where(x => x.Status == AnalysisJobStatus.Succeeded && !string.IsNullOrWhiteSpace(x.ResultJson))
            .OrderByDescending(x => x.StartedAtUtc)
            .FirstOrDefault();

        if (job is null)
            return TypedResults.Ok(new QualityRecommendationsResponse(null, Array.Empty<QualityRecommendation>()));

        var result = new List<QualityRecommendation>();
        try
        {
            using var document = JsonDocument.Parse(job.ResultJson!);
            if (document.RootElement.TryGetProperty("qualityViolations", out var violations) && violations.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in violations.EnumerateArray())
                {
                    var field = GetString(item, "field");
                    if (string.IsNullOrWhiteSpace(field) || field == "-") continue;
                    var check = GetString(item, "check");
                    var count = GetLong(item, "count");
                    var message = GetString(item, "message");
                    result.Add(BuildRecommendation(field, check, count, message));
                }
            }
        }
        catch (JsonException) { }

        return TypedResults.Ok(new QualityRecommendationsResponse(job, result));
    }

    private static async Task<Ok<QualityRegressionReport?>> GetRegression(
        IAnalysisHistoryStore history,
        CancellationToken cancellationToken)
    {
        var jobs = (await history.GetRecentAsync(100, cancellationToken))
            .Where(x => x.Status == AnalysisJobStatus.Succeeded && !string.IsNullOrWhiteSpace(x.ResultJson))
            .OrderByDescending(x => x.StartedAtUtc)
            .Take(2)
            .ToList();

        if (jobs.Count < 2)
            return TypedResults.Ok<QualityRegressionReport?>(null);

        try
        {
            var current = JsonSerializer.Deserialize<AnalysisResult>(jobs[0].ResultJson!);
            var previous = JsonSerializer.Deserialize<AnalysisResult>(jobs[1].ResultJson!);
            return TypedResults.Ok(current is null || previous is null
                ? null
                : QualityRegressionAnalyzer.Analyze(previous, current));
        }
        catch (JsonException)
        {
            return TypedResults.Ok<QualityRegressionReport?>(null);
        }
    }

    private static QualityRecommendation BuildRecommendation(string field, string check, long count, string message)
    {
        var (action, rule, priority) = check switch
        {
            QualityCheckTypes.Required => ("补齐缺失值，或明确允许为空", "required: true", 3),
            QualityCheckTypes.NullRate => ("排查空值来源并降低字段空值率", "nullRate: < 当前阈值", 3),
            QualityCheckTypes.Range => ("检查异常数值来源，并设置合理上下界", "minValue / maxValue", 3),
            QualityCheckTypes.Pattern => ("统一格式并增加模式校验", "pattern: <正则表达式>", 2),
            QualityCheckTypes.EnumValues => ("统一枚举编码，清理非法枚举值", "enumValues: [允许值]", 2),
            QualityCheckTypes.Unique => ("去除重复数据并确认唯一键设计", "unique: true", 3),
            QualityCheckTypes.OutlierRatio => ("检查异常值来源，必要时增加异常值处理策略", "outlierRatio: < 当前阈值", 2),
            _ => ("根据问题说明检查数据来源并增加针对性质量规则", "配置对应质量规则", 1)
        };
        return new QualityRecommendation(field, check, count, message, action, rule, priority);
    }

    private static string GetString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : "-";

    private static long GetLong(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static List<T> DeserializeList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<T>();
        try { return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>(); }
        catch (JsonException) { return new List<T>(); }
    }

    public sealed record QualityDashboardResponse(
        AnalysisJob? Job,
        QualityMetrics? Metrics,
        IReadOnlyList<QualitySnapshot> RecentSnapshots,
        IReadOnlyList<QualityIssue> TopIssues,
        IReadOnlyList<QualityGateResult> GateResults);

    public sealed record FieldQualityResponse(
        AnalysisJob Job,
        IReadOnlyList<FieldQualitySnapshot> Fields,
        IReadOnlyList<QualityIssue> Issues);

    public sealed record QualityRecommendationsResponse(
        AnalysisJob? Job,
        IReadOnlyList<QualityRecommendation> Recommendations);

    public sealed record QualityRecommendation(
        string Field,
        string Check,
        long Count,
        string Message,
        string Action,
        string Rule,
        int Priority)
    {
        public string PriorityText => Priority >= 3 ? "高" : Priority == 2 ? "中" : "低";
    }
}
