using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Tests.Platform;

public sealed class AnalysisHostLeaseTests
{
    [Fact]
    public void Ownership_IsPerDatabaseAndReleasedOnDispose()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oa-ownership-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = new AnalysisHostLease(path))
            {
                Assert.Throws<InvalidOperationException>(() => new AnalysisHostLease(path));
                using var different = new AnalysisHostLease(path + ".other");
            }
            using var next = new AnalysisHostLease(path);
        }
        finally
        {
            File.Delete(path + ".host.lock");
            File.Delete(path + ".other.host.lock");
        }
    }
}
