using Microsoft.Extensions.DependencyInjection;

namespace ObjectAnalyzer.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddAnalysisPlatform(this IServiceCollection services, string databasePath)
    {
        services.AddSingleton<IAnalysisHistoryStore>(_ => new SqliteAnalysisHistoryStore(databasePath));
        services.AddSingleton<IQualityHistoryQuery>(_ => new SqliteQualityHistoryQuery(databasePath));
        services.AddSingleton<IAlertRuleStore>(_ => new SqliteAlertRuleStore(databasePath));
        services.AddSingleton<IWebhookDeliveryStore>(_ => new SqliteWebhookDeliveryStore(databasePath));
        services.AddScoped<AnalysisJobService>();
        services.AddScoped<AnalysisExecutionCoordinator>();
        services.AddHttpClient<WebhookNotifier>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        return services;
    }
}
