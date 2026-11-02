using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Contracts.Webhooks;
using Platform.Modules.Webhooks.Options;

namespace Platform.Modules.Webhooks.Services;

/// <summary>单次投递结果。</summary>
/// <param name="Success">是否 2xx。</param>
/// <param name="StatusCode">HTTP 状态码（连接失败时为 <c>null</c>）。</param>
/// <param name="Error">失败摘要（响应体截断或异常消息）。</param>
public sealed record WebhookSendResult(bool Success, int? StatusCode, string? Error);

/// <summary>投递通道。测试可用假实现替换，避免真实外呼。</summary>
public interface IWebhookSender
{
    /// <summary>发送一次事件。</summary>
    Task<WebhookSendResult> SendAsync(
        string url,
        string secret,
        Guid deliveryId,
        string eventType,
        long timestamp,
        string payload,
        CancellationToken cancellationToken = default);
}

/// <summary>基于 <see cref="HttpClient"/> 的投递实现。</summary>
public sealed class HttpWebhookSender : IWebhookSender
{
    private const int MaxErrorLength = 512;

    private static readonly Action<ILogger, string, string, Exception?> SendFailed = LoggerMessage.Define<string, string>(
        LogLevel.Warning,
        new EventId(1, "WebhookSendFailed"),
        "Webhook 投递失败：{Url} {Error}");

    private readonly HttpClient _client;
    private readonly WebhooksOptions _options;
    private readonly ILogger<HttpWebhookSender> _logger;

    /// <summary>构造。</summary>
    public HttpWebhookSender(
        HttpClient client,
        IOptions<WebhooksOptions> options,
        ILogger<HttpWebhookSender> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WebhookSendResult> SendAsync(
        string url,
        string secret,
        Guid deliveryId,
        string eventType,
        long timestamp,
        string payload,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation(WebhookConventions.EventHeader, eventType);
            request.Headers.TryAddWithoutValidation(WebhookConventions.DeliveryHeader, deliveryId.ToString());
            request.Headers.TryAddWithoutValidation(
                WebhookConventions.TimestampHeader,
                timestamp.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation(
                WebhookConventions.SignatureHeader,
                WebhookSigner.Sign(secret, timestamp, payload));

            using var response = await _client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new WebhookSendResult(true, (int)response.StatusCode, null);
            }

            var error = string.IsNullOrWhiteSpace(body)
                ? $"HTTP {(int)response.StatusCode}"
                : Truncate(body);
            SendFailed(_logger, url, error, null);
            return new WebhookSendResult(false, (int)response.StatusCode, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SendFailed(_logger, url, "请求超时", null);
            return new WebhookSendResult(false, null, "请求超时");
        }
        catch (HttpRequestException exception)
        {
            SendFailed(_logger, url, exception.Message, exception);
            return new WebhookSendResult(false, null, Truncate(exception.Message));
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
