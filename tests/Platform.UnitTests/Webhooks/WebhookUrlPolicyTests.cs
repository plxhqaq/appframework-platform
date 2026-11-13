using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Kernel.Errors;
using Platform.Modules.Webhooks.Options;
using Platform.Modules.Webhooks.Services;

namespace Platform.UnitTests.Webhooks;

/// <summary>Webhook 出站地址策略（SSRF 防护）与投递前复检。</summary>
public sealed class WebhookUrlPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.9")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.10.10")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2002:0a00:0001::1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("::127.0.0.1")]
    public void 内网与保留地址判定为禁止(string value) =>
        Assert.True(WebhookUrlPolicy.IsBlockedAddress(IPAddress.Parse(value)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void 公网地址放行(string value) =>
        Assert.False(WebhookUrlPolicy.IsBlockedAddress(IPAddress.Parse(value)));

    [Theory]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://[fc00::1]/hook")]
    [InlineData("http://localhost/hook")]
    [InlineData("http://api.localhost/hook")]
    public async Task 默认拒绝内网目标(string url)
    {
        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => Create().ValidateAsync(url));
        Assert.Equal(ApiErrorCode.WebhooksUrlInvalid, exception.Code);
    }

    [Fact]
    public async Task 非HTTP协议在地址校验阶段被拒()
    {
        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => Create().ValidateAsync("ftp://example.test/hook"));
        Assert.Equal(ApiErrorCode.WebhooksUrlInvalid, exception.Code);
    }

    [Fact]
    public async Task 主机解析到私网时拒绝()
    {
        var resolver = new FakeResolver().Map("hooks.example.test", IPAddress.Parse("10.9.8.7"));

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => Create(resolver).ValidateAsync("https://hooks.example.test/hook"));
        Assert.Equal(ApiErrorCode.WebhooksUrlInvalid, exception.Code);
    }

    [Fact]
    public async Task 公网解析与解析失败都放行()
    {
        var resolver = new FakeResolver()
            .Map("public.example.test", IPAddress.Parse("93.184.216.34"));

        await Create(resolver).ValidateAsync("https://public.example.test/hook");
        await Create(resolver).ValidateAsync("https://unresolved.example.test/hook");
    }

    [Fact]
    public async Task 通配子域白名单可放行内网主机()
    {
        var resolver = new FakeResolver().Map("hooks.internal.example", IPAddress.Parse("10.1.1.5"));
        var policy = Create(resolver, "*.internal.example");

        await policy.ValidateAsync("https://hooks.internal.example/hook");
    }

    [Fact]
    public async Task CIDR白名单可放行指定网段且不支持其他私网段()
    {
        var resolver = new FakeResolver()
            .Map("hooks.example.test", IPAddress.Parse("10.2.3.4"))
            .Map("other.example.test", IPAddress.Parse("192.168.1.2"));

        var policy = Create(resolver, "10.0.0.0/8");
        await policy.ValidateAsync("https://hooks.example.test/hook");

        await Assert.ThrowsAsync<BusinessException>(
            () => policy.ValidateAsync("https://other.example.test/hook"));
    }

    [Fact]
    public async Task 单个配置项支持逗号分隔的白名单()
    {
        var resolver = new FakeResolver().Map("hooks.internal", IPAddress.Parse("10.5.5.5"));
        var policy = Create(resolver, "192.168.0.0/16, hooks.internal");

        await policy.ValidateAsync("https://hooks.internal/hook");
    }

    [Fact]
    public async Task 解析结果同时含公网与私网时拒绝()
    {
        var resolver = new FakeResolver().Map(
            "mixed.example.test",
            [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.1.1.1")]);

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => Create(resolver).ValidateAsync("https://mixed.example.test/hook"));
        Assert.Equal(ApiErrorCode.WebhooksUrlInvalid, exception.Code);
    }

    [Fact]
    public async Task 投递前复检阻止已写入的内网地址()
    {
        var sender = new HttpWebhookSender(
            new HttpClient(),
            Options.Create(new WebhooksOptions()),
            Create(),
            NullLogger<HttpWebhookSender>.Instance);

        var result = await sender.SendAsync(
            "http://127.0.0.1/hook",
            "whsec_test",
            Guid.CreateVersion7(),
            "test",
            1_760_000_000,
            "{}");

        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
        Assert.Contains("AllowedHosts", result.Error, StringComparison.Ordinal);
    }

    private static WebhookUrlPolicy Create(params string[] allowedHosts) =>
        Create(new FakeResolver(), allowedHosts);

    private static WebhookUrlPolicy Create(FakeResolver resolver, params string[] allowedHosts) =>
        new(
            Options.Create(new WebhooksOptions { AllowedHosts = string.Join(",", allowedHosts) }),
            resolver);

    private sealed class FakeResolver : IWebhookHostResolver
    {
        private readonly Dictionary<string, IPAddress[]> _map = new(StringComparer.OrdinalIgnoreCase);

        public FakeResolver Map(string host, params IPAddress[] addresses)
        {
            _map[host] = addresses;
            return this;
        }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            _map.TryGetValue(host, out var addresses)
                ? Task.FromResult(addresses)
                : Task.FromException<IPAddress[]>(new SocketException(11001));
    }
}
