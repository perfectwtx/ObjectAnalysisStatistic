using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Tests.Platform;

public sealed class WebhookLeaseTests
{
    [Fact]
    public async Task ConcurrentWorkers_OnlyOneClaimsEachDelivery()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oa-lease-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteWebhookDeliveryStore(path);
            await store.InitializeAsync();
            await store.EnqueueAsync(new WebhookDelivery { PayloadJson = "{}" });
            var now = DateTime.UtcNow;
            var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                new SqliteWebhookDeliveryStore(path).TryClaimNextAsync(now, TimeSpan.FromMinutes(1)))));
            var winner = Assert.Single(claims, x => x is not null);
            Assert.Equal(WebhookDeliveryStatus.Processing, winner!.Status);
            Assert.NotNull(winner.LeaseToken);
            Assert.Null(await store.TryClaimNextAsync(now.AddSeconds(30), TimeSpan.FromMinutes(1)));
        }
        finally { SqliteTestDatabase.Delete(path); }
    }

    [Fact]
    public async Task ExpiredLease_CanBeReclaimedAndOldWorkerCannotWrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oa-lease-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteWebhookDeliveryStore(path);
            await store.InitializeAsync();
            var delivery = new WebhookDelivery { PayloadJson = "{}" };
            await store.EnqueueAsync(delivery);
            var now = DateTime.UtcNow;
            var old = (await store.TryClaimNextAsync(now, TimeSpan.FromMinutes(1)))!;
            var current = (await store.TryClaimNextAsync(now.AddMinutes(2), TimeSpan.FromMinutes(1)))!;
            Assert.Equal(old.Id, current.Id);
            Assert.NotEqual(old.LeaseToken, current.LeaseToken);
            Assert.False(await store.MarkSucceededAsync(old.Id, old.LeaseToken!, now));
            Assert.False(await store.MarkFailedAsync(old.Id, old.LeaseToken!, "stale", now, true));
            Assert.True(await store.MarkSucceededAsync(current.Id, current.LeaseToken!, now));
            Assert.False(await store.MarkFailedAsync(current.Id, current.LeaseToken!, "late", now, true));
            Assert.Equal(WebhookDeliveryStatus.Succeeded, (await store.GetAsync(delivery.Id))!.Status);
            Assert.Null(await store.TryClaimNextAsync(now.AddHours(1), TimeSpan.FromMinutes(1)));
        }
        finally { SqliteTestDatabase.Delete(path); }
    }

    [Fact]
    public async Task FailedAttempt_RespectsRetryTimeAndPermanentFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oa-lease-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteWebhookDeliveryStore(path);
            await store.InitializeAsync();
            await store.EnqueueAsync(new WebhookDelivery { PayloadJson = "{}" });
            var now = DateTime.UtcNow;
            var first = (await store.TryClaimNextAsync(now, TimeSpan.FromMinutes(1)))!;
            Assert.True(await store.MarkFailedAsync(first.Id, first.LeaseToken!, "retry", now.AddMinutes(2), false));
            Assert.Null(await store.TryClaimNextAsync(now.AddMinutes(1), TimeSpan.FromMinutes(1)));
            var retry = (await store.TryClaimNextAsync(now.AddMinutes(3), TimeSpan.FromMinutes(1)))!;
            Assert.Equal(1, retry.AttemptCount);
            Assert.True(await store.MarkFailedAsync(retry.Id, retry.LeaseToken!, "permanent", now, true));
            Assert.Null(await store.TryClaimNextAsync(now.AddDays(1), TimeSpan.FromMinutes(1)));
            Assert.True(await store.RetryAsync(retry.Id));
            var manual = (await store.TryClaimNextAsync(DateTime.UtcNow.AddSeconds(1), TimeSpan.FromMinutes(1)))!;
            Assert.Equal(0, manual.AttemptCount);
        }
        finally { SqliteTestDatabase.Delete(path); }
    }
}
