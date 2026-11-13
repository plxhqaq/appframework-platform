using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Kernel.Errors;
using Platform.Modules.Webhooks.Options;

namespace Platform.Modules.Webhooks.Services;

/// <summary>解析 Webhook 目标主机；单元测试可替换为固定结果。</summary>
public interface IWebhookHostResolver
{
    /// <summary>解析主机名；解析失败由调用方决定如何处理。</summary>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default);
}

/// <summary>基于系统 DNS 的解析器。</summary>
public sealed class DnsWebhookHostResolver : IWebhookHostResolver
{
    /// <inheritdoc />
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// Webhook 目标地址策略：只允许 http/https，默认拒绝环回、私网、链路本地与保留地址。
/// </summary>
/// <remarks>
/// 内网回调必须显式列入 <see cref="WebhooksOptions.AllowedHosts"/>（主机名、通配子域、IP 或 CIDR）。
/// 创建/修改订阅时校验一次，每次投递前再校验一次 —— DNS 可能在两次之间被改成内网地址。
/// 创建时无法解析的主机先放行（此刻不可达），投递阶段会重新解析并拒绝。
/// </remarks>
public sealed class WebhookUrlPolicy
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(2);

    private readonly WebhooksOptions _options;
    private readonly IWebhookHostResolver _resolver;

    /// <summary>构造。</summary>
    public WebhookUrlPolicy(IOptions<WebhooksOptions> options, IWebhookHostResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolver);
        _options = options.Value;
        _resolver = resolver;
    }

    /// <summary>校验地址并返回规范化后的 URL；不通过抛 <see cref="ApiErrorCode.WebhooksUrlInvalid"/>。</summary>
    public async Task<string> ValidateAsync(string? value, CancellationToken cancellationToken = default)
    {
        var url = value?.Trim();
        if (string.IsNullOrEmpty(url) || url.Length > 2048)
        {
            throw new BusinessException(ApiErrorCode.WebhooksUrlInvalid, "接收地址不能为空且不超过 2048 字符。");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new BusinessException(ApiErrorCode.WebhooksUrlInvalid, "接收地址必须是 http/https 绝对地址。");
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (host.Length > 1 && host[0] == '[' && host[^1] == ']')
        {
            // IPv6 字面量在不同 Uri 属性上带不带方括号不一致，统一去掉再判定。
            host = host[1..^1];
        }

        if (host.Length == 0)
        {
            throw new BusinessException(ApiErrorCode.WebhooksUrlInvalid, "接收地址缺少主机名。");
        }

        if (IsHostAllowlisted(host))
        {
            return url;
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            if (IsBlockedAddress(literal))
            {
                throw BlockedTarget();
            }

            return url;
        }

        if (IsLocalhostName(host))
        {
            throw BlockedTarget();
        }

        var addresses = await ResolveSafeAsync(host, cancellationToken);
        if (addresses.Length == 0)
        {
            // 此刻解析不了就不可达；投递时还会重新校验。
            return url;
        }

        var blocked = addresses.Where(IsBlockedAddress).ToArray();
        if (blocked.Length > 0 && !blocked.All(IsAddressAllowlisted))
        {
            throw BlockedTarget();
        }

        return url;
    }

    /// <summary>地址是否属于禁止的环回/私网/链路本地/保留范围。</summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Loopback)
                || address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast)
            {
                return true;
            }

            var v6 = address.GetAddressBytes();
            // ::/96 是已废弃的 IPv4 兼容格式，可用来夹带 127/8、10/8 等地址。
            var ipv4Compatible = true;
            for (var i = 0; i < 12; i++)
            {
                if (v6[i] != 0)
                {
                    ipv4Compatible = false;
                    break;
                }
            }

            // fc00::/7 唯一本地、2002::/16 6to4、2001::/32 Teredo、64:ff9b::/96 NAT64
            // 都可能把流量带进内网，统一按保留地址拒绝。
            return ipv4Compatible
                   || (v6[0] & 0xFE) == 0xFC
                   || (v6[0] == 0x20 && v6[1] == 0x02)
                   || (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x00 && v6[3] == 0x00)
                   || (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xFF && v6[3] == 0x9B);
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        var first = bytes[0];
        var second = bytes[1];
        if (first is 0 or 10 or 127 or >= 224)
        {
            return true;
        }

        if (first == 100 && second is >= 64 and <= 127)
        {
            return true;
        }

        if (first == 169 && second == 254)
        {
            return true;
        }

        if (first == 172 && second is >= 16 and <= 31)
        {
            return true;
        }

        if (first == 192 && second == 168)
        {
            return true;
        }

        if (first == 192 && second == 0 && bytes[2] == 0)
        {
            return true;
        }

        return first == 198 && second is 18 or 19;
    }

    private static bool IsLocalhostName(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private async Task<IPAddress[]> ResolveSafeAsync(string host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ResolveTimeout);
        try
        {
            return await _resolver.ResolveAsync(host, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (SocketException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    private bool IsHostAllowlisted(string host)
    {
        foreach (var entry in Entries())
        {
            if (entry.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = entry[1..];
                if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(entry, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsAddressAllowlisted(IPAddress address)
    {
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        foreach (var entry in Entries())
        {
            if (IPAddress.TryParse(entry, out var ip)
                && (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).Equals(candidate))
            {
                return true;
            }

            if (TryParseCidr(entry, out var network, out var prefix)
                && network.AddressFamily == candidate.AddressFamily
                && Contains(network, prefix, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private string[] Entries() =>
        _options.AllowedHosts.Split(
            [',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParseCidr(string entry, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == entry.Length - 1)
        {
            return false;
        }

        if (!IPAddress.TryParse(entry[..slash], out var parsed)
            || !int.TryParse(entry[(slash + 1)..], out var bits))
        {
            return false;
        }

        var maxBits = parsed.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (bits < 0 || bits > maxBits)
        {
            return false;
        }

        network = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        prefix = bits;
        return true;
    }

    private static bool Contains(IPAddress network, int prefix, IPAddress candidate)
    {
        var networkBytes = network.GetAddressBytes();
        var candidateBytes = candidate.GetAddressBytes();
        if (networkBytes.Length != candidateBytes.Length)
        {
            return false;
        }

        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != candidateBytes[i])
            {
                return false;
            }
        }

        var remainingBits = prefix % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (networkBytes[fullBytes] & mask) == (candidateBytes[fullBytes] & mask);
    }

    private static BusinessException BlockedTarget() =>
        new(
            ApiErrorCode.WebhooksUrlInvalid,
            "接收地址不能指向环回、私网、链路本地或保留地址；如确需内网回调，请把主机或网段加入 Webhooks:AllowedHosts。");
}
