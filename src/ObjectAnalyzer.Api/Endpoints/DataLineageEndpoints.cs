using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api.Endpoints;

/// <summary>数据血缘查询接口。</summary>
public static class DataLineageEndpoints
{
    public static IEndpointRouteBuilder MapDataLineageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/analysis/jobs/{jobId:guid}/lineage", async (
            Guid jobId,
            IAnalysisHistoryStore historyStore,
            CancellationToken cancellationToken) =>
        {
            var job = await historyStore.GetByIdAsync(jobId, cancellationToken);
            if (job is null)
                return Results.NotFound();

            if (job.Status != AnalysisJobStatus.Succeeded || string.IsNullOrWhiteSpace(job.ResultJson))
                return Results.Problem("该分析作业没有可用的成功分析结果。", statusCode: StatusCodes.Status409Conflict);

            AnalysisResult? result;
            try
            {
                result = JsonSerializer.Deserialize<AnalysisResult>(job.ResultJson);
            }
            catch (JsonException)
            {
                return Results.Problem("分析结果无法解析。", statusCode: StatusCodes.Status500InternalServerError);
            }

            if (result is null)
                return Results.Problem("分析结果为空。", statusCode: StatusCodes.Status500InternalServerError);

            var lineage = DataLineageBuilder.Build(result.ObjectRelationships ?? Array.Empty<ObjectRelationship>());
            return Results.Ok(lineage);
        });

        return app;
    }
}
