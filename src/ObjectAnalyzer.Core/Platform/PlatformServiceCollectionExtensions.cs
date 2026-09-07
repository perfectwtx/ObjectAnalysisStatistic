using Microsoft.Extensions.DependencyInjection;

namespace ObjectAnalyzer.Platform;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddAnalysisPlatform(this IServiceCollection services)
    {
        services.AddScoped<AnalysisJobService>();
        services.AddHttpClient<WebhookNotifier>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        return services;
    }
}