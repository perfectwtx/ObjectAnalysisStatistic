using System.IO;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Desktop.Services;

/// <summary>
/// Desktop 本地平台服务，统一管理作业历史、质量快照和质量基线。
/// </summary>
public sealed class DesktopPlatformService : IDisposable
{
    private readonly object _leaseSync = new();
    private AnalysisHostLease? _hostLease;
    private bool _disposed;
    public string DatabasePath { get; }
    public IAnalysisHistoryStore HistoryStore { get; }
    public IQualityHistoryQuery QualityHistoryQuery { get; }
    public IQualityBaselineStore BaselineStore { get; }

    public DesktopPlatformService(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObjectAnalyzer",
            "object-analyzer.db");

        HistoryStore = new SqliteAnalysisHistoryStore(DatabasePath);
        QualityHistoryQuery = new SqliteQualityHistoryQuery(DatabasePath);
        BaselineStore = new SqliteQualityBaselineStore(DatabasePath);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_leaseSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _hostLease ??= new AnalysisHostLease(DatabasePath);
        }
        await ((SqliteAnalysisHistoryStore)HistoryStore).InitializeAsync(cancellationToken);
        await ((SqliteQualityBaselineStore)BaselineStore).InitializeAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (_leaseSync)
        {
            _disposed = true;
            _hostLease?.Dispose();
        }
    }
}
