using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>
/// 异步分析端点：
/// - POST /api/analyze/async                   提交分析作业，返回 jobId
/// - GET  /api/analyze/async/{jobId}           查询作业状态 / 拉取结果
/// - GET  /api/analyze/async/{jobId}/events   订阅进度事件
/// - POST /api/analyze/async/{jobId}/cancel    取消正在进行的分析
///
/// 持久化 AnalysisJob 是平台作业的唯一标识；AnalysisJobRegistry 只负责进程内的
/// CancellationToken、实时进度、SSE 和短期结果缓存。后台任务通过独立 DI Scope
/// 复用已经创建的 AnalysisJob，避免 HTTP 请求结束后继续使用已释放的请求 Scope。
///
/// 数据库 / 非文件型输入场景不支持取消；这些请求一律走同步端点 /api/analyze。
/// </summary>
public static partial class AsyncAnalyzeEndpoints
{
    private static readonly JsonSerializerOptions s_sseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Action<ILogger, string, Exception?> s_jobFailed = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(1, "JobFailed"), "异步分析作业 {JobId} 失败");

    public static IEndpointRouteBuilder MapAsyncAnalyzeApi(this IEndpointRouteBuilder routes)
    {
        var jobLogger = routes.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ObjectAnalyzer.AsyncAnalyze");

        MapSubmissionEndpoints(routes, jobLogger);
        MapQueryEndpoints(routes);
        MapCancelEndpoint(routes);

        return routes;
    }
}
