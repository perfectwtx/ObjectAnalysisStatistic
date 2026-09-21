using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Desktop.Services;

/// <summary>
/// Desktop 本地平台服务，统一管理作业历史、质量快照和质量基线。
/// </summary>
public sealed class DesktopPlatformService : IAsyncDisposable
{
    private readonly string _databasePath;

    public IAnalysisHistoryStore HistoryStore { get; }
    public IQualityHistoryQuery QualityHistoryQuery { get; }
    public IQualityBaselineStore BaselineStore { get; }

    public DesktopPlatformService()
    {
        _databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObjectAnalyzer",
            "object-analyzer.db");

        HistoryStore = new SqliteAnalysisHistoryStore(_databasePath);
        QualityHistoryQuery = new SqliteQualityHistoryQuery(_databasePath);
        BaselineStore = new SqliteQualityBaselineStore(_databasePath);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ((SqliteAnalysisHistoryStore)HistoryStore).InitializeAsync(cancellationToken);
        await ((SqliteQualityBaselineStore)BaselineStore).InitializeAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
