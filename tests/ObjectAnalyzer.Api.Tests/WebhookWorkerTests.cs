using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Api.Tests;

public sealed class WebhookWorkerTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, WebhookDeliveryStatus.Succeeded)]
    [InlineData(HttpStatusCode.ServiceUnavailable, WebhookDeliveryStatus.Pending)]
    public async Task ApiHost_DeliversOutboxAndSchedulesRetries(HttpStatusCode status, WebhookDeliveryStatus expected)
    {
        using var handler = new RecordingHandler(status);
        await using var factory = new IsolatedApiWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("WorkerTests");
            builder.ConfigureServices(services => services.AddHttpClient("WebhookDelivery")
                .ConfigurePrimaryHttpMessageHandler(() => handler));
        });
        using var client = factory.CreateClient();
        Assert.Contains(factory.Services.GetServices<IHostedService>(), x => x is WebhookDeliveryWorker);
        var store = factory.Services.GetRequiredService<IWebhookDeliveryStore>();
        var delivery = new WebhookDelivery { JobId = Guid.NewGuid(), WebhookUrl = "https://example.invalid", PayloadJson = "{\"event\":\"test\"}" };
        await store.EnqueueAsync(delivery);

        Assert.Equal(delivery.PayloadJson, await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        WebhookDelivery saved;
        do
        {
            await Task.Delay(20, timeout.Token);
            saved = (await store.GetAsync(delivery.Id, timeout.Token))!;
        } while (saved.Status != expected || (expected == WebhookDeliveryStatus.Pending && saved.AttemptCount == 0));

        if (expected == WebhookDeliveryStatus.Succeeded)
            Assert.NotNull(saved.SentAtUtc);
        else
        {
            Assert.Equal(1, saved.AttemptCount);
            Assert.True(saved.NextAttemptAtUtc > DateTime.UtcNow);
        }
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public TaskCompletionSource<string> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Received.TrySetResult(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status);
        }
    }
}
