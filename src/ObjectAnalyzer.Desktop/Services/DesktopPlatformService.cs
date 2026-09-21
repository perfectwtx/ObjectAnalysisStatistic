using System.IO;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Desktop.Services;

/// <summary>
/// Desktop 本地平台服务，统一管理作业历史、质量快照和质量基线。
/// </summary>
public sealed class DesktopPlatformService
{
    public string DatabasePath { get; }
    public IAnalysisHistoryStore HistoryStore { get; }
    public IQualityHistoryQuery QualityHistoryQuery { get; }
    public IQualityBaselineStore BaselineStore { get; }

    public DesktopPlatformService()
    {
        DatabasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObjectAnalyzer",
            "object-analyzer.db");

        HistoryStore = new SqliteAnalysisHistoryStore(DatabasePath);
        QualityHistoryQuery = new SqliteQualityHistoryQuery(DatabasePath);
        BaselineStore = new SqliteQualityBaselineStore(DatabasePath);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ((SqliteAnalysisHistoryStore)HistoryStore).InitializeAsync(cancellationToken);
        await ((SqliteQualityBaselineStore)BaselineStore).InitializeAsync(cancellationToken);
    }
}
