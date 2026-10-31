using Microsoft.Extensions.DependencyInjection;

namespace ObjectAnalyzer.Platform;

public static class PlatformServiceCollectionExtensions
{
    /// <summary>Starts the shared outbox delivery worker in long-running hosts.</summary>
    public static IServiceCollection AddAnalysisWebhookDelivery(this IServiceCollection services)
    {
        services.AddHttpClient("WebhookDelivery", client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddHostedService<WebhookDeliveryWorker>();
        return services;
    }

    public static IServiceCollection AddAnalysisPlatform(this IServiceCollection services, string databasePath)
    {
        services.AddSingleton<IPlatformDiagnostics, ConsolePlatformDiagnostics>();
        services.AddSingleton(_ => new AnalysisHostLease(databasePath));
        services.AddSingleton<IAnalysisHistoryStore>(_ => new SqliteAnalysisHistoryStore(databasePath));
        services.AddSingleton<IQualityHistoryQuery>(_ => new SqliteQualityHistoryQuery(databasePath));
        services.AddSingleton<IQualityIssueQuery>(_ => new SqliteQualityIssueQuery(databasePath));
        services.AddSingleton<IQualityIssueResponsibilityStore>(_ => new SqliteQualityIssueResponsibilityStore(databasePath));
        services.AddSingleton<IQualityIssueRemediationStore>(_ => new SqliteQualityIssueRemediationStore(databasePath));
        services.AddSingleton<IQualityBaselineStore>(_ => new SqliteQualityBaselineStore(databasePath));
        services.AddSingleton<IAlertRuleStore>(_ => new SqliteAlertRuleStore(databasePath));
        services.AddSingleton<IWebhookDeliveryStore>(_ => new SqliteWebhookDeliveryStore(databasePath));
        services.AddScoped<QualityIssueRemediationVerifier>();
        services.AddScoped<AnalysisJobService>();
        services.AddScoped<AnalysisExecutionCoordinator>();
        return services;
    }
}
