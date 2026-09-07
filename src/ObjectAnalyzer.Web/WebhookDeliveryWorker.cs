using System.Text;
using Microsoft.Extensions.Hosting;
using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Web;

/// <summary>后台消费 Webhook Outbox，负责投递、指数退避与失败终止。</summary>
public sealed class WebhookDeliveryWorker : BackgroundService
{
    private readonly IWebhookDeliveryStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookDeliveryWorker> _logger;

    private const int MaxAttempts = 6;

    public WebhookDeliveryWorker(
        IWebhookDeliveryStore store,
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookDeliveryWorker> logger)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delivery = await _store.GetNextPendingAsync(DateTime.UtcNow, stoppingToken);
                if (delivery == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                await DeliverAsync(delivery, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook delivery worker failed while polling outbox.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task DeliverAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        await _store.MarkProcessingAsync(delivery.Id, cancellationToken);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, delivery.WebhookUrl)
            {
                Content = new StringContent(delivery.PayloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-ObjectAnalyzer-Event", delivery.EventType);
            request.Headers.TryAddWithoutValidation("X-ObjectAnalyzer-Delivery", delivery.Id.ToString());

            using var response = await _httpClientFactory.CreateClient("WebhookDelivery")
                .SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                await _store.MarkSucceededAsync(delivery.Id, DateTime.UtcNow, cancellationToken);
                return;
            }

            var error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            await RescheduleOrFailAsync(delivery, error, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RescheduleOrFailAsync(delivery, ex.Message, cancellationToken);
        }
    }

    private async Task RescheduleOrFailAsync(WebhookDelivery delivery, string error, CancellationToken cancellationToken)
    {
        var nextAttempt = delivery.AttemptCount + 1;
        var permanent = nextAttempt >= MaxAttempts;
        var delaySeconds = Math.Min(300, Math.Pow(2, Math.Max(0, nextAttempt - 1)) * 5);
        var nextAttemptAt = DateTime.UtcNow.AddSeconds(delaySeconds);

        await _store.MarkFailedAsync(
            delivery.Id,
            error,
            nextAttemptAt,
            permanent,
            cancellationToken);

        if (permanent)
        {
            _logger.LogWarning(
                "Webhook delivery {DeliveryId} permanently failed after {Attempts} attempts: {Error}",
                delivery.Id,
                nextAttempt,
                error);
        }
        else
        {
            _logger.LogWarning(
                "Webhook delivery {DeliveryId} failed on attempt {Attempt}; retry scheduled at {NextAttemptAtUtc}: {Error}",
                delivery.Id,
                nextAttempt,
                nextAttemptAt,
                error);
        }
    }
}
