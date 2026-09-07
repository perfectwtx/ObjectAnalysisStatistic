using ObjectAnalyzer.Platform;

namespace ObjectAnalyzer.Web.Platform;

/// <summary>后台处理 Webhook Outbox，失败后按指数退避重试。</summary>
public sealed class WebhookDeliveryWorker : BackgroundService
{
    private const int MaxAttempts = 5;
    private readonly IWebhookDeliveryStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookDeliveryWorker> _logger;

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
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
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
                _logger.LogError(ex, "Webhook Outbox worker 执行异常。");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task DeliverAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        await _store.MarkProcessingAsync(delivery.Id, cancellationToken);

        try
        {
            var client = _httpClientFactory.CreateClient("WebhookDelivery");
            using var content = new StringContent(delivery.PayloadJson, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(delivery.WebhookUrl, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                await _store.MarkSucceededAsync(delivery.Id, DateTime.UtcNow, cancellationToken);
                _logger.LogInformation("Webhook 投递成功: {DeliveryId}, Job={JobId}", delivery.Id, delivery.JobId);
                return;
            }

            var error = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})";
            await MarkFailedAsync(delivery, error, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(delivery, ex.Message, cancellationToken);
        }
    }

    private async Task MarkFailedAsync(
        WebhookDelivery delivery,
        string error,
        CancellationToken cancellationToken)
    {
        var nextAttempt = delivery.AttemptCount + 1;
        var permanent = nextAttempt >= MaxAttempts;
        var delaySeconds = Math.Min(3600, 30 * Math.Pow(2, Math.Max(0, nextAttempt - 1)));
        var nextAttemptAt = permanent
            ? DateTime.UtcNow
            : DateTime.UtcNow.AddSeconds(delaySeconds);

        await _store.MarkFailedAsync(
            delivery.Id,
            error,
            nextAttemptAt,
            permanent,
            cancellationToken);

        if (permanent)
        {
            _logger.LogError(
                "Webhook 投递最终失败: {DeliveryId}, Job={JobId}, Attempts={Attempts}, Error={Error}",
                delivery.Id,
                delivery.JobId,
                nextAttempt,
                error);
        }
        else
        {
            _logger.LogWarning(
                "Webhook 投递失败，将重试: {DeliveryId}, Attempt={Attempt}, NextAttempt={NextAttempt}, Error={Error}",
                delivery.Id,
                nextAttempt,
                nextAttemptAt,
                error);
        }
    }
}
