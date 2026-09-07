using System.Net.Http.Json;

namespace ObjectAnalyzer.Platform;

/// <summary>质量平台 Webhook 通知器。第一阶段采用直接 HTTP POST，后续可替换为 Outbox。</summary>
public sealed class WebhookNotifier
{
    private readonly HttpClient _httpClient;

    public WebhookNotifier(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<bool> NotifyAsync(
        AlertRule rule,
        WebhookEvent webhookEvent,
        CancellationToken cancellationToken = default)
    {
        if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.WebhookUrl))
            return false;

        using var response = await _httpClient.PostAsJsonAsync(
            rule.WebhookUrl,
            webhookEvent,
            cancellationToken);

        return response.IsSuccessStatusCode;
    }
}