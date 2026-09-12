using Microsoft.Extensions.DependencyInjection;

namespace ObjectAnalyzer.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddAnalysisPlatform(this IServiceCollection services, string databasePath)
    {
        services.AddSingleton<IAnalysisHistoryStore>(_ => new SqliteAnalysisHistoryStore(databasePath));
        services.AddSingleton<IQualityHistoryQuery>(_ => new SqliteQualityHistoryQuery(databasePath));
        services.AddSingleton<IQualityIssueQuery>(_ => new SqliteQualityIssueQuery(databasePath));
        services.AddSingleton<IQualityBaselineStore>(_ => new SqliteQualityBaselineStore(databasePath));
        services.AddSingleton<IAlertRuleStore>(_ => new SqliteAlertRuleStore(databasePath));
        services.AddSingleton<IWebhookDeliveryStore>(_ => new SqliteWebhookDeliveryStore(databasePath));
        services.AddScoped<AnalysisJobService>();
        services.AddScoped<AnalysisExecutionCoordinator>();
        return services;
    }
}
