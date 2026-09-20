using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api.Endpoints;

/// <summary>影响分析查询接口。</summary>
public static class ImpactAnalysisEndpoints
{
    public static RouteGroupBuilder MapImpactAnalysisEndpoints(this RouteGroupBuilder app)
    {
        Map("/analysis/jobs/{jobId:guid}/impact");
        Map("/jobs/{jobId:guid}/impact");
        return app;

        void Map(string pattern)
        {
            app.MapGet(pattern, async (
                Guid jobId,
                IAnalysisHistoryStore historyStore,
                CancellationToken cancellationToken) =>
            {
                var job = await historyStore.GetAsync(jobId, cancellationToken);
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

                var lineage = DataLineageBuilder.Build(result.ObjectRelationships ?? new List<ObjectRelationship>());
                var impact = ImpactAnalysisBuilder.Build(result.SchemaDiff, lineage);
                return Results.Ok(impact);
            }).WithTags("影响分析").WithSummary("查询 Schema 影响分析");
        }
    }
}
