using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Web.Platform;

public static class PlatformServiceExtensions
{
    public static IServiceCollection AddAnalysisPlatform(this IServiceCollection services, string databasePath)
    {
        services.AddSingleton<IAnalysisHistoryStore>(_ => new SqliteAnalysisHistoryStore(databasePath));
        return services;
    }
}
