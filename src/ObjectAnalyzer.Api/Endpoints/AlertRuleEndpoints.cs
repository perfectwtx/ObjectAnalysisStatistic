using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api;

/// <summary>质量告警规则管理 API。</summary>
public static class AlertRuleEndpoints
{
    public static IEndpointRouteBuilder MapAlertRuleApi(this IEndpointRouteBuilder routes)
    {
        MapGroup(routes.MapGroup("/alert-rules"));
        MapGroup(routes.MapGroup("/alerts/rules"));
        MapGroup(routes.MapGroup("/quality/alert-rules"));
        return routes;

        static void MapGroup(RouteGroupBuilder rules)
        {
            rules.WithTags("质量告警");

            rules.MapGet("", GetAll)
                .WithSummary("获取全部质量告警规则");

            rules.MapGet("/{id:guid}", Get)
                .WithSummary("获取指定质量告警规则");

            rules.MapPost("", Save)
                .WithSummary("新增质量告警规则")
                .WithDescription("支持完整字段，也兼容 condition + threshold 形式的前端请求。");

            rules.MapPut("/{id:guid}", Update)
                .WithSummary("更新质量告警规则");

            rules.MapDelete("/{id:guid}", Delete)
                .WithSummary("删除质量告警规则");
        }
    }

    private static async Task<Ok<IReadOnlyList<AlertRuleResponse>>> GetAll(
        IAlertRuleStore store,
        CancellationToken cancellationToken)
        => TypedResults.Ok<IReadOnlyList<AlertRuleResponse>>(
            (await store.GetAllAsync(cancellationToken)).Select(ToResponse).ToList());

    private static async Task<Results<Ok<AlertRuleResponse>, NotFound<ErrorResponse>>> Get(
        Guid id,
        IAlertRuleStore store,
        CancellationToken cancellationToken)
    {
        var rule = await store.GetAsync(id, cancellationToken);
        return rule is null
            ? TypedResults.NotFound(new ErrorResponse($"未找到告警规则：{id}"))
            : TypedResults.Ok(ToResponse(rule));
    }

    private static async Task<Results<Ok<AlertRuleResponse>, BadRequest<ErrorResponse>>> Save(
        AlertRuleRequest request,
        IAlertRuleStore store,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return TypedResults.BadRequest(new ErrorResponse("告警规则名称不能为空。"));

        var rule = request.ToModel(Guid.NewGuid());
        try
        {
            Validate(rule);
            await store.SaveAsync(rule, cancellationToken);
            return TypedResults.Ok(ToResponse(rule));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<Results<Ok<AlertRuleResponse>, BadRequest<ErrorResponse>>> Update(
        Guid id,
        AlertRuleRequest request,
        IAlertRuleStore store,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return TypedResults.BadRequest(new ErrorResponse("告警规则名称不能为空。"));

        var rule = request.ToModel(id);
        try
        {
            Validate(rule);
            await store.SaveAsync(rule, cancellationToken);
            return TypedResults.Ok(ToResponse(rule));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<NoContent> Delete(
        Guid id,
        IAlertRuleStore store,
        CancellationToken cancellationToken)
    {
        await store.DeleteAsync(id, cancellationToken);
        return TypedResults.NoContent();
    }

    private static void Validate(AlertRule rule)
    {
        static void Check(double? value, string name)
        {
            if (value is double v && (double.IsNaN(v) || double.IsInfinity(v)))
                throw new ArgumentException($"{name} 必须是有效数字。");
        }

        Check(rule.QualityScoreBelow, nameof(rule.QualityScoreBelow));
        Check(rule.CompletenessBelow, nameof(rule.CompletenessBelow));
        Check(rule.DistributionDriftAbove, nameof(rule.DistributionDriftAbove));
        Check(rule.QualityRegressionBelow, nameof(rule.QualityRegressionBelow));

        if (rule.QualityScoreBelow is < 0 or > 100)
            throw new ArgumentException("QualityScoreBelow 必须位于 0 到 100 之间。");
        if (rule.CompletenessBelow is < 0 or > 100)
            throw new ArgumentException("CompletenessBelow 必须位于 0 到 100 之间。");
        if (rule.QualityRegressionBelow is < 0 or > 100)
            throw new ArgumentException("QualityRegressionBelow 必须位于 0 到 100 之间。");
        if (rule.DistributionDriftAbove is < 0)
            throw new ArgumentException("DistributionDriftAbove 不能小于 0。");
    }

    private static AlertRuleResponse ToResponse(AlertRule rule)
    {
        var conditions = new List<AlertConditionResponse>();
        Add(conditions, "overallScoreBelow", rule.QualityScoreBelow);
        Add(conditions, "completenessBelow", rule.CompletenessBelow);
        Add(conditions, "scoreDrop", rule.QualityRegressionBelow);
        if (rule.CriticalIssue) conditions.Add(new("criticalIssue", null));
        if (rule.BreakingSchemaChange) conditions.Add(new("breakingSchemaChange", null));
        Add(conditions, "distributionDrift", rule.DistributionDriftAbove);

        var primary = conditions.FirstOrDefault();
        return new AlertRuleResponse(
            rule.Id,
            rule.Name,
            rule.Enabled,
            rule.QualityScoreBelow,
            rule.CompletenessBelow,
            rule.CriticalIssue,
            rule.BreakingSchemaChange,
            rule.DistributionDriftAbove,
            rule.QualityRegressionBelow,
            rule.WebhookUrl,
            primary?.Condition,
            primary?.Threshold,
            conditions);
    }

    private static void Add(List<AlertConditionResponse> list, string condition, double? threshold)
    {
        if (threshold.HasValue)
            list.Add(new(condition, threshold));
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
        string? WebhookUrl = null,
        string? Condition = null,
        double? Threshold = null)
    {
        public AlertRule ToModel(Guid id)
        {
            var rule = new AlertRule
            {
                Id = id,
                Name = Name,
                Enabled = Enabled,
                QualityScoreBelow = QualityScoreBelow,
                CompletenessBelow = CompletenessBelow,
                CriticalIssue = CriticalIssue,
                BreakingSchemaChange = BreakingSchemaChange,
                DistributionDriftAbove = DistributionDriftAbove,
                QualityRegressionBelow = QualityRegressionBelow,
                WebhookUrl = WebhookUrl
            };

            if (!string.IsNullOrWhiteSpace(Condition))
            {
                switch (Condition.Trim(), Threshold)
                {
                    case ("overallScoreBelow", not null):
                    case ("qualityScoreBelow", not null):
                        rule.QualityScoreBelow = Threshold;
                        break;
                    case ("completenessBelow", not null):
                        rule.CompletenessBelow = Threshold;
                        break;
                    case ("scoreDrop", not null):
                    case ("qualityRegressionBelow", not null):
                        rule.QualityRegressionBelow = Threshold;
                        break;
                    case ("criticalIssue", _):
                        rule.CriticalIssue = true;
                        break;
                    case ("breakingSchemaChange", _):
                        rule.BreakingSchemaChange = true;
                        break;
                    case ("distributionDrift", not null):
                        rule.DistributionDriftAbove = Threshold;
                        break;
                    default:
                        throw new ArgumentException($"不支持的告警条件：{Condition}");
                }
            }

            return rule;
        }
    }

    public sealed record AlertConditionResponse(string Condition, double? Threshold);

    public sealed record AlertRuleResponse(
        Guid Id,
        string Name,
        bool Enabled,
        double? QualityScoreBelow,
        double? CompletenessBelow,
        bool CriticalIssue,
        bool BreakingSchemaChange,
        double? DistributionDriftAbove,
        double? QualityRegressionBelow,
        string? WebhookUrl,
        string? Condition,
        double? Threshold,
        IReadOnlyList<AlertConditionResponse> Conditions);
}
