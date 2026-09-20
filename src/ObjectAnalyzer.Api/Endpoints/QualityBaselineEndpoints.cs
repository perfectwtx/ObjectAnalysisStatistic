using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>数据质量基线管理端点。</summary>
public static class QualityBaselineEndpoints
{
    public static IEndpointRouteBuilder MapQualityBaselineApi(this IEndpointRouteBuilder routes)
    {
        MapRouteGroup(routes.MapGroup("/quality-baselines"));
        MapRouteGroup(routes.MapGroup("/quality/baselines"));
        return routes;

        static void MapRouteGroup(RouteGroupBuilder group)
        {
            group.WithTags("质量基线");

            group.MapGet("", GetAll)
                .WithSummary("获取全部质量基线");

            group.MapGet("/{sourceName}", Get)
                .WithSummary("获取指定数据源的质量基线");

            group.MapPost("", Save)
                .WithSummary("新增质量基线")
                .WithDescription("创建质量基线。所有阈值支持 0 到 100 的百分制，也兼容 minCompleteness=0.95 这样的 0 到 1 比例输入。");

            group.MapPut("/{sourceName}", Update)
                .WithSummary("创建或更新质量基线")
                .WithDescription("数据源名称来自 URL；同一数据源重复保存会覆盖原基线。");

            group.MapDelete("/{sourceName}", Delete)
                .WithSummary("删除质量基线");

            group.MapPost("/{sourceName}/evaluate", Evaluate)
                .WithSummary("评估质量基线")
                .WithDescription("按 jobId 指定一次成功分析进行评估；未指定 jobId 时使用该数据源最近一次成功分析。");
        }
    }

    private static async Task<Ok<IReadOnlyList<QualityBaselineResponse>>> GetAll(
        IQualityBaselineStore store,
        CancellationToken cancellationToken)
    {
        var items = await store.GetAllAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<QualityBaselineResponse>>(items.Select(ToResponse).ToList());
    }

    private static async Task<Results<Ok<QualityBaselineResponse>, NotFound<ErrorResponse>>> Get(
        string sourceName,
        IQualityBaselineStore store,
        CancellationToken cancellationToken)
    {
        var baseline = await store.GetAsync(sourceName, cancellationToken);
        return baseline is null
            ? TypedResults.NotFound(new ErrorResponse($"未找到数据源“{sourceName}”的质量基线。"))
            : TypedResults.Ok(ToResponse(baseline));
    }

    private static async Task<Results<Ok<QualityBaselineResponse>, BadRequest<ErrorResponse>>> Save(
        QualityBaselineRequest request,
        IQualityBaselineStore store,
        CancellationToken cancellationToken)
    {
        var sourceName = request.EffectiveSourceName;
        if (string.IsNullOrWhiteSpace(sourceName))
            return TypedResults.BadRequest(new ErrorResponse("数据源名称不能为空。"));

        return await SaveAsync(sourceName, request, store, cancellationToken);
    }

    private static async Task<Results<Ok<QualityBaselineResponse>, BadRequest<ErrorResponse>>> Update(
        string sourceName,
        QualityBaselineRequest request,
        IQualityBaselineStore store,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            return TypedResults.BadRequest(new ErrorResponse("数据源名称不能为空。"));

        return await SaveAsync(sourceName, request, store, cancellationToken);
    }

    private static async Task<Results<Ok<QualityBaselineResponse>, BadRequest<ErrorResponse>>> SaveAsync(
        string sourceName,
        QualityBaselineRequest request,
        IQualityBaselineStore store,
        CancellationToken cancellationToken)
    {
        var baseline = request.ToModel(sourceName);
        try
        {
            baseline.Validate();
            await store.SaveAsync(baseline, cancellationToken);
            return TypedResults.Ok(ToResponse(baseline, request.Name));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<NoContent> Delete(
        string sourceName,
        IQualityBaselineStore store,
        CancellationToken cancellationToken)
    {
        await store.DeleteAsync(sourceName, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<QualityBaselineEvaluation>, BadRequest<ErrorResponse>, NotFound<ErrorResponse>>> Evaluate(
        string sourceName,
        Guid? jobId,
        IQualityBaselineStore baselineStore,
        IAnalysisHistoryStore history,
        CancellationToken cancellationToken)
    {
        var baseline = await baselineStore.GetAsync(sourceName, cancellationToken);
        if (baseline is null)
            return TypedResults.NotFound(new ErrorResponse($"未找到数据源“{sourceName}”的质量基线。"));

        var jobs = await history.GetRecentAsync(500, cancellationToken);
        var job = jobId.HasValue
            ? jobs.FirstOrDefault(x => x.Id == jobId.Value)
            : jobs
                .Where(x => x.Status == AnalysisJobStatus.Succeeded
                    && string.Equals(x.SourceName, sourceName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CompletedAtUtc ?? x.StartedAtUtc)
                .FirstOrDefault();

        if (job is null)
            return TypedResults.NotFound(new ErrorResponse("没有找到可用于基线评估的成功分析作业。"));

        if (string.IsNullOrWhiteSpace(job.ResultJson))
            return TypedResults.BadRequest(new ErrorResponse("该分析作业没有可用的结果。"));

        try
        {
            var result = System.Text.Json.JsonSerializer.Deserialize<ObjectAnalyzer.Analysis.AnalysisResult>(job.ResultJson);
            if (result is null)
                return TypedResults.BadRequest(new ErrorResponse("该分析作业的结果为空。"));

            var metrics = QualityMetricsCalculator.Calculate(result);
            return TypedResults.Ok(QualityBaselineEvaluator.Evaluate(baseline, metrics));
        }
        catch (System.Text.Json.JsonException)
        {
            return TypedResults.BadRequest(new ErrorResponse("该分析作业的历史结果 JSON 无法解析。"));
        }
    }

    private static QualityBaselineResponse ToResponse(QualityBaseline baseline, string? explicitName = null)
        => new(
            Id: baseline.SourceName,
            Name: explicitName ?? baseline.SourceName,
            SourceName: baseline.SourceName,
            MinimumOverallScore: baseline.MinimumOverallScore,
            MinimumCompleteness: baseline.MinimumCompleteness,
            MinimumValidity: baseline.MinimumValidity,
            MinimumUniqueness: baseline.MinimumUniqueness,
            MinimumConsistency: baseline.MinimumConsistency,
            MinimumAnomalyControl: baseline.MinimumAnomalyControl,
            MinOverall: baseline.MinimumOverallScore,
            MinCompleteness: baseline.MinimumCompleteness,
            MinValidity: baseline.MinimumValidity,
            MinUniqueness: baseline.MinimumUniqueness,
            MinConsistency: baseline.MinimumConsistency,
            MinAnomalyControl: baseline.MinimumAnomalyControl);

    public sealed record QualityBaselineRequest(
        string? SourceName = null,
        double? MinimumOverallScore = 90,
        double? MinimumCompleteness = 90,
        double? MinimumValidity = 90,
        double? MinimumUniqueness = 90,
        double? MinimumConsistency = 90,
        double? MinimumAnomalyControl = 90,
        string? Name = null,
        double? MinOverallScore = null,
        double? MinCompleteness = null,
        double? MinValidity = null,
        double? MinUniqueness = null,
        double? MinConsistency = null,
        double? MinAnomalyControl = null)
    {
        public string EffectiveSourceName => !string.IsNullOrWhiteSpace(SourceName)
            ? SourceName!
            : Name ?? string.Empty;

        public QualityBaseline ToModel(string sourceName)
            => new(
                sourceName,
                Normalize(MinOverallScore ?? MinimumOverallScore),
                Normalize(MinCompleteness ?? MinimumCompleteness),
                Normalize(MinValidity ?? MinimumValidity),
                Normalize(MinUniqueness ?? MinimumUniqueness),
                Normalize(MinConsistency ?? MinimumConsistency),
                Normalize(MinAnomalyControl ?? MinimumAnomalyControl));

        private static double? Normalize(double? value)
            => value is >= 0 and <= 1 ? value * 100 : value;
    }

    public sealed record QualityBaselineResponse(
        string Id,
        string Name,
        string SourceName,
        double? MinimumOverallScore,
        double? MinimumCompleteness,
        double? MinimumValidity,
        double? MinimumUniqueness,
        double? MinimumConsistency,
        double? MinimumAnomalyControl,
        double? MinOverall,
        double? MinCompleteness,
        double? MinValidity,
        double? MinUniqueness,
        double? MinConsistency,
        double? MinAnomalyControl);
}
