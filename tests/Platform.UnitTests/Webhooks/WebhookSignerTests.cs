using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Contracts.Webhooks;
using Platform.Modules.Webhooks.Options;
using Platform.Modules.Webhooks.Services;

namespace Platform.UnitTests.Webhooks;

/// <summary>Webhook 签名、信封与退避策略。</summary>
public sealed class WebhookSignerTests
{
    [Fact]
    public void 密钥带前缀且每次生成不同()
    {
        var first = WebhookSigner.GenerateSecret();
        var second = WebhookSigner.GenerateSecret();

        Assert.StartsWith(WebhookConventions.SecretPrefix, first, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
        Assert.True(first.Length > WebhookConventions.SecretPrefix.Length + 20);
    }

    [Fact]
    public void 提示只暴露少量字符()
    {
        var secret = "whsec_abcdefghijklmnopqrstuvwxyz";

        var hint = WebhookSigner.Hint(secret);

        Assert.StartsWith("whsec_", hint, StringComparison.Ordinal);
        Assert.EndsWith("…", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("mnopqrstuvwxyz", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void 签名与独立计算的HMAC一致且时间戳参与签名()
    {
        const string secret = "whsec_test-secret";
        const string payload = """{"id":"x","type":"version.released"}""";
        const long timestamp = 1_760_000_000;

        var signature = WebhookSigner.Sign(secret, timestamp, payload);

        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret),
                Encoding.UTF8.GetBytes($"{timestamp}.{payload}")));
        Assert.Equal("sha256=" + expected, signature);
        Assert.NotEqual(signature, WebhookSigner.Sign(secret, timestamp + 1, payload));
    }

    [Fact]
    public void 信封包含投递id类型应用与负载()
    {
        var deliveryId = Guid.CreateVersion7();
        var occurredAt = DateTimeOffset.Parse("2026-10-30T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        var payload = WebhookSigner.BuildPayload(
            deliveryId,
            WebhookConventions.EventVersionReleased,
            "demo",
            occurredAt,
            new { version = "1.2.3" });

        using var document = JsonDocument.Parse(payload);
        Assert.Equal(deliveryId.ToString(), document.RootElement.GetProperty("id").GetString());
        Assert.Equal("version.released", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("demo", document.RootElement.GetProperty("appKey").GetString());
        Assert.Equal("1.2.3", document.RootElement.GetProperty("data").GetProperty("version").GetString());
    }

    [Fact]
    public void 退避按二的幂递增并封顶()
    {
        var options = new WebhooksOptions { BaseDelaySeconds = 30, MaxDelaySeconds = 120 };

        Assert.Equal(30, options.DelaySeconds(1));
        Assert.Equal(60, options.DelaySeconds(2));
        Assert.Equal(120, options.DelaySeconds(3));
        Assert.Equal(120, options.DelaySeconds(8));
    }

    [Fact]
    public void 事件白名单拒绝未知类型()
    {
        Assert.True(WebhookConventions.IsKnownEvent(WebhookConventions.EventVersionReleased));
        Assert.True(WebhookConventions.IsKnownEvent(WebhookConventions.EventTest));
        Assert.False(WebhookConventions.IsKnownEvent("license.unknown"));
        Assert.False(WebhookConventions.IsKnownEvent(null));
    }
}
