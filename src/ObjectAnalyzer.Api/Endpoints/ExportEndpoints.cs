using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Analysis;
using ObjectAnalyzer.Platform;
using ObjectAnalyzer.Reporting;

namespace ObjectAnalyzer.Api;

/// <summary>服务端分析结果导出 API。</summary>
public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportApi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/analysis/jobs/{jobId:guid}/export", Export)
            .WithTags("分析历史")
            .WithSummary("导出分析结果")
            .WithDescription("支持 json、csv、xlsx 三种格式。导出统一启用 PII 脱敏。");
        routes.MapGet("/jobs/{jobId:guid}/export", Export)
            .WithTags("分析历史")
            .WithSummary("导出分析结果");
        return routes;
    }

    private static async Task<IResult> Export(
        Guid jobId,
        string? format,
        IAnalysisHistoryStore history,
        CancellationToken cancellationToken)
    {
        var extension = NormalizeFormat(format);
        if (extension is null)
            return Results.BadRequest(new ErrorResponse("format 只支持 json、csv、xlsx。"));

        var job = await history.GetAsync(jobId, cancellationToken);
        if (job is null)
            return Results.NotFound(new ErrorResponse($"未找到分析作业：{jobId}"));

        if (string.IsNullOrWhiteSpace(job.ResultJson))
            return Results.BadRequest(new ErrorResponse("该分析作业没有可用的结果。"));

        AnalysisResult? result;
        try
        {
            result = global::ObjectAnalyzer.Reporting.ResultProjection.ReadStored(job.ResultJson);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ErrorResponse("该分析作业的历史结果 JSON 无法解析。"));
        }

        if (result is null)
            return Results.BadRequest(new ErrorResponse("该分析作业的历史结果为空。"));

        var tempPath = Path.Combine(Path.GetTempPath(), $"objectanalyzer-{jobId:N}{extension}");
        try
        {
            await Task.Run(
                () => ReportExporter.Save(result, tempPath, redactPii: true),
                cancellationToken);

            var bytes = await File.ReadAllBytesAsync(tempPath, cancellationToken);
            var contentType = extension switch
            {
                ".json" => "application/json",
                ".csv" => "text/csv; charset=utf-8",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                _ => "application/octet-stream"
            };
            var fileName = $"{SanitizeFileName(job.SourceName)}-{jobId:N}{extension}";
            return Results.File(bytes, contentType, fileName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Results.Problem(
                $"导出失败：{ex.Message}",
                statusCode: StatusCodes.Status500InternalServerError);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // 临时文件清理失败不应覆盖导出结果。
            }
        }
    }

    private static string? NormalizeFormat(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "json" => ".json",
            "csv" => ".csv",
            "xlsx" or "excel" => ".xlsx",
            _ when string.IsNullOrWhiteSpace(format) => ".json",
            _ => null
        };

    private static string SanitizeFileName(string sourceName)
    {
        var value = Path.GetFileNameWithoutExtension(sourceName);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "analysis" : value;
    }
}
