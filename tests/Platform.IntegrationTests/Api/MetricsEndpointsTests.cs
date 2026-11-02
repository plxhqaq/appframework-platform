using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>抓取端点的可用性、内容与安全默认。</summary>
public sealed class MetricsEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public MetricsEndpointsTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 未配置令牌时可抓取且含进程与业务指标()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        // 先造一条待投递记录：业务仪表只有真实积压才写得出来，空库会让断言假绿
        var created = await IdentityTestHelpers.ReadDataAsync<Platform.Contracts.Webhooks.CreatedWebhookSubscriptionDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/webhooks",
                token,
                new Platform.Contracts.Webhooks.CreateWebhookSubscriptionRequest(
                    "https://example.test/metrics",
                    [Platform.Contracts.Webhooks.WebhookConventions.EventTest]))));
        await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/webhooks/{created.Subscription.Id}/test",
            token));

        var response = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE platform_process_uptime_seconds gauge", text, StringComparison.Ordinal);
        Assert.Contains("# TYPE platform_http_request_duration_seconds histogram", text, StringComparison.Ordinal);
        Assert.Contains("route=\"/api/v1/identity/login\"", text, StringComparison.Ordinal);
        Assert.Contains("route=\"/api/v1/admin/webhooks\"", text, StringComparison.Ordinal);
        Assert.Contains("platform_webhook_deliveries{app=\"demo\",status=\"pending\"} 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 路由标签只用模板不带路径参数()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/admin/apps/00000000-0000-0000-0000-000000000000", token));

        var text = await (await client.GetAsync("/metrics")).Content.ReadAsStringAsync();

        // 原始路径（含 guid）一旦成为标签值，时间序列会随数据无限增长
        Assert.DoesNotContain("/00000000-0000-0000-0000-000000000000\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 配置抓取令牌后无令牌被拒且携带令牌可用()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Metrics:ScrapeToken"] = "scrape-secret",
            })));
        using var client = factory.CreateClient();

        var anonymous = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var authorized = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        authorized.Headers.Add("X-Metrics-Token", "scrape-secret");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(authorized)).StatusCode);

        using var bearer = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "scrape-secret");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(bearer)).StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        wrong.Headers.Add("X-Metrics-Token", "scrape-secre");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);
    }

    [Fact]
    public async Task 关闭开关后不再暴露抓取端点()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Metrics:Enabled"] = "false",
            })));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/metrics")).StatusCode);
    }
}
