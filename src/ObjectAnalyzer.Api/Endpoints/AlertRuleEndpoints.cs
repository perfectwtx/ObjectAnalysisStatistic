using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>质量告警规则管理 API。</summary>
public static class AlertRuleEndpoints
{
    public static IEndpointRouteBuilder MapAlertRuleApi(this IEndpointRouteBuilder routes)
    {
        var rules = routes.MapGroup("/alert-rules").WithTags("质量告警");

        rules.MapGet("", async Task<Ok<IReadOnlyList<AlertRule>>> (
            IAlertRuleStore store,
            CancellationToken cancellationToken) =>
                TypedResults.Ok(await store.GetAllAsync(cancellationToken)))
            .WithSummary("获取全部质量告警规则");

        rules.MapGet("/{id:guid}", async Task<Results<Ok<AlertRule>, NotFound<ErrorResponse>>> (
            Guid id,
            IAlertRuleStore store,
            CancellationToken cancellationToken) =>
        {
            var rule = await store.GetAsync(id, cancellationToken);
            return rule is null
                ? TypedResults.NotFound(new ErrorResponse($"未找到告警规则：{id}"))
                : TypedResults.Ok(rule);
        })
        .WithSummary("获取指定质量告警规则");

        rules.MapPut("/{id:guid}", async Task<Results<Ok<AlertRule>, BadRequest<ErrorResponse>>> (
            Guid id,
            AlertRuleRequest request,
            IAlertRuleStore store,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return TypedResults.BadRequest(new ErrorResponse("告警规则名称不能为空。"));

            var rule = new AlertRule
            {
                Id = id,
                Name = request.Name,
                Enabled = request.Enabled,
                QualityScoreBelow = request.QualityScoreBelow,
                CompletenessBelow = request.CompletenessBelow,
                CriticalIssue = request.CriticalIssue,
                BreakingSchemaChange = request.BreakingSchemaChange,
                DistributionDriftAbove = request.DistributionDriftAbove,
                QualityRegressionBelow = request.QualityRegressionBelow,
                WebhookUrl = request.WebhookUrl
            };
            await store.SaveAsync(rule, cancellationToken);
            return TypedResults.Ok(rule);
        })
        .WithSummary("创建或更新质量告警规则");

        rules.MapPost("", async Task<Results<Ok<AlertRule>, BadRequest<ErrorResponse>>> (
            AlertRuleRequest request,
            IAlertRuleStore store,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return TypedResults.BadRequest(new ErrorResponse("告警规则名称不能为空。"));

            var rule = new AlertRule
            {
                Id = Guid.NewGuid(),
                Name = request.Name,
                Enabled = request.Enabled,
                QualityScoreBelow = request.QualityScoreBelow,
                CompletenessBelow = request.CompletenessBelow,
                CriticalIssue = request.CriticalIssue,
                BreakingSchemaChange = request.BreakingSchemaChange,
                DistributionDriftAbove = request.DistributionDriftAbove,
                QualityRegressionBelow = request.QualityRegressionBelow,
                WebhookUrl = request.WebhookUrl
            };
            await store.SaveAsync(rule, cancellationToken);
            return TypedResults.Ok(rule);
        })
        .WithSummary("新增质量告警规则");

        rules.MapDelete("/{id:guid}", async Task<NoContent> (
            Guid id,
            IAlertRuleStore store,
            CancellationToken cancellationToken) =>
        {
            await store.DeleteAsync(id, cancellationToken);
            return TypedResults.NoContent();
        })
        .WithSummary("删除质量告警规则");

        return routes;
    }

    public sealed record AlertRuleRequest(
        string Name,
        bool Enabled = true,
        double? QualityScoreBelow = null,
        double? CompletenessBelow = null,
        bool CriticalIssue = false,
        bool BreakingSchemaChange = false,
        double? DistributionDriftAbove = null,
        double? QualityRegressionBelow = null,
        string? WebhookUrl = null);
}
