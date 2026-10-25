namespace ObjectAnalyzer.Platform;

/// <summary>Exclusive ownership of a local analysis database for the lifetime of a host.</summary>
public sealed class AnalysisHostLease : IDisposable
{
    private readonly FileStream _lease;

    public AnalysisHostLease(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        try
        {
            // Keep the file after release: deleting it would allow another inode to be locked on Unix.
            _lease = new FileStream(fullPath + ".host.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("无法独占分析数据库；请确认没有其他分析宿主正在使用该数据库。", ex);
        }
    }

    public void Dispose() => _lease.Dispose();
}
