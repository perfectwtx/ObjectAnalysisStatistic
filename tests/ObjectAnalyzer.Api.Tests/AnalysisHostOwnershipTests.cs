using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api.Tests;

public sealed class AnalysisHostOwnershipTests
{
    [Fact]
    public async Task SecondHost_CannotRecoverLiveTasksAndCanStartAfterOwnerStops()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oa-host-owner-{Guid.NewGuid():N}.db");
        Guid id;
        await using (var first = new IsolatedApiWebApplicationFactory().WithWebHostBuilder(b => b.UseSetting("Platform:DatabasePath", path)))
        {
            using var firstClient = first.CreateClient();
            var history = first.Services.GetRequiredService<IAnalysisHistoryStore>();
            var live = new AnalysisJob { SourceName = "live", SourceType = "json", Status = AnalysisJobStatus.Running };
            await history.SaveAsync(live);
            id = live.Id;
            await using var second = new IsolatedApiWebApplicationFactory().WithWebHostBuilder(b => b.UseSetting("Platform:DatabasePath", path));
            Assert.Throws<InvalidOperationException>(() => second.CreateClient());
            Assert.Equal(AnalysisJobStatus.Running, (await history.GetAsync(id))!.Status);
        }

        await using var restarted = new IsolatedApiWebApplicationFactory().WithWebHostBuilder(b => b.UseSetting("Platform:DatabasePath", path));
        using var restartedClient = restarted.CreateClient();
        Assert.Equal(AnalysisJobStatus.Failed, (await restarted.Services.GetRequiredService<IAnalysisHistoryStore>().GetAsync(id))!.Status);
    }
}
