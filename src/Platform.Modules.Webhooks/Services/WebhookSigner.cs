using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Contracts.Webhooks;

namespace Platform.Modules.Webhooks.Services;

/// <summary>Webhook 密钥、信封与签名工具。</summary>
public static class WebhookSigner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>生成 <c>whsec_</c> 前缀的随机密钥（32 字节）。</summary>
    public static string GenerateSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return WebhookConventions.SecretPrefix
               + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>密钥提示：只暴露前缀与少量字符，便于管理台辨认。</summary>
    public static string Hint(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return "—";
        }

        var visible = Math.Min(secret.Length, 14);
        return secret[..visible] + "…";
    }

    /// <summary>签名串：<c>sha256=hex(HMAC-SHA256(secret, "{timestamp}.{payload}"))</c>。</summary>
    public static string Sign(string secret, long timestamp, string payload)
    {
        var signed = string.Create(CultureInfo.InvariantCulture, $"{timestamp}.{payload}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    /// <summary>构造投递信封 JSON（接收方验签后按 <c>data</c> 处理）。</summary>
    public static string BuildPayload(
        Guid deliveryId,
        string eventType,
        string? appKey,
        DateTimeOffset occurredAt,
        object? data)
    {
        var envelope = new
        {
            id = deliveryId,
            type = eventType,
            appKey,
            occurredAt,
            data,
        };
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }
}
