using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>Webhook Outbox 投递记录查询与人工重试 API。</summary>
public static class WebhookDeliveryEndpoints
{
    public static IEndpointRouteBuilder MapWebhookDeliveryApi(this IEndpointRouteBuilder routes)
    {
        var deliveries = routes.MapGroup("/webhooks/deliveries").WithTags("Webhook");

        deliveries.MapGet("", GetRecent)
            .WithSummary("获取 Webhook 投递记录")
            .WithDescription("支持 limit、status、jobId、ruleId 筛选。");

        deliveries.MapGet("/{id:guid}", Get)
            .WithSummary("获取 Webhook 投递详情");

        deliveries.MapPost("/{id:guid}/retry", Retry)
            .WithSummary("手动重试失败的 Webhook 投递")
            .WithDescription("仅 Failed 状态允许人工重试；重试会重置尝试次数并立即重新进入 Outbox。");

        return routes;
    }

    private static async Task<Results<Ok<IReadOnlyList<WebhookDelivery>>, BadRequest<ErrorResponse>>> GetRecent(
        int? limit,
        string? status,
        Guid? jobId,
        Guid? ruleId,
        IWebhookDeliveryStore store,
        CancellationToken cancellationToken)
    {
        WebhookDeliveryStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<WebhookDeliveryStatus>(status, ignoreCase: true, out var value))
                return TypedResults.BadRequest(new ErrorResponse($"未知 Webhook 状态：{status}"));
            parsedStatus = value;
        }

        var actualLimit = Math.Clamp(limit ?? 100, 1, 500);
        var deliveries = await store.GetRecentAsync(500, cancellationToken);
        var filtered = deliveries
            .Where(x => !parsedStatus.HasValue || x.Status == parsedStatus.Value)
            .Where(x => !jobId.HasValue || x.JobId == jobId.Value)
            .Where(x => !ruleId.HasValue || x.RuleId == ruleId.Value)
            .Take(actualLimit)
            .ToList();

        return TypedResults.Ok<IReadOnlyList<WebhookDelivery>>(filtered);
    }

    private static async Task<Results<Ok<WebhookDelivery>, NotFound<ErrorResponse>>> Get(
        Guid id,
        IWebhookDeliveryStore store,
        CancellationToken cancellationToken)
    {
        var delivery = await store.GetAsync(id, cancellationToken);
        return delivery is null
            ? TypedResults.NotFound(new ErrorResponse($"未找到 Webhook 投递记录：{id}"))
            : TypedResults.Ok(delivery);
    }

    private static async Task<Results<Ok<WebhookDelivery>, Conflict<ErrorResponse>, NotFound<ErrorResponse>>> Retry(
        Guid id,
        IWebhookDeliveryStore store,
        CancellationToken cancellationToken)
    {
        var delivery = await store.GetAsync(id, cancellationToken);
        if (delivery is null)
            return TypedResults.NotFound(new ErrorResponse($"未找到 Webhook 投递记录：{id}"));

        if (delivery.Status != WebhookDeliveryStatus.Failed)
            return TypedResults.Conflict(new ErrorResponse("只有 Failed 状态的 Webhook 投递允许手动重试。"));

        if (!await store.RetryAsync(id, cancellationToken))
            return TypedResults.NotFound(new ErrorResponse($"Webhook 投递记录已不存在：{id}"));

        var retried = await store.GetAsync(id, cancellationToken);
        return retried is null
            ? TypedResults.NotFound(new ErrorResponse($"Webhook 投递记录已不存在：{id}"))
            : TypedResults.Ok(retried);
    }
}
