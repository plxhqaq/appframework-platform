using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Contracts;
using Platform.Contracts.Audit;
using Platform.Contracts.Webhooks;
using Platform.IntegrationTests.Support;
using Platform.Modules.Webhooks.Options;
using Platform.Modules.Webhooks.Services;

namespace Platform.IntegrationTests.Api;

/// <summary>Webhook 订阅/投递/退避/重投，以及审计查询的只读语义。</summary>
public sealed class WebhooksAndAuditEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public WebhooksAndAuditEndpointsTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 订阅创建只返回一次密钥且测试事件可投递成功()
    {
        var sender = new FakeWebhookSender();
        using var factory = CreateFactory(sender);
        using var client = factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var created = await CreateSubscriptionAsync(
            client, token, "https://example.test/hooks", [WebhookConventions.EventTest]);
        Assert.StartsWith(WebhookConventions.SecretPrefix, created.Secret, StringComparison.Ordinal);

        var subscriptions = await IdentityTestHelpers.ReadDataAsync<List<WebhookSubscriptionDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/webhooks", token)));
        var listed = Assert.Single(subscriptions, x => x.Id == created.Subscription.Id);
        Assert.NotEqual(created.Secret, listed.SecretHint);
        Assert.DoesNotContain(created.Secret[14..], listed.SecretHint, StringComparison.Ordinal);

        var tested = await IdentityTestHelpers.ReadDataAsync<WebhookTestResultDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/webhooks/{created.Subscription.Id}/test", token)));
        Assert.Equal(1, tested.DeliveryCount);

        var deliveries = await ListDeliveriesAsync(client, token, created.Subscription.Id);
        var pending = Assert.Single(deliveries.Items);
        Assert.Equal(WebhookConventions.StatusPending, pending.Status);
        using (var document = JsonDocument.Parse(pending.PayloadJson))
        {
            Assert.Equal(pending.Id.ToString(), document.RootElement.GetProperty("id").GetString());
            Assert.Equal(WebhookConventions.EventTest, document.RootElement.GetProperty("type").GetString());
            Assert.Equal("demo", document.RootElement.GetProperty("appKey").GetString());
        }

        var processed = await DispatchAsync(factory);
        Assert.True(processed >= 1);

        var after = await ListDeliveriesAsync(client, token, created.Subscription.Id);
        var delivered = Assert.Single(after.Items);
        Assert.Equal(WebhookConventions.StatusSucceeded, delivered.Status);
        Assert.Equal(1, delivered.Attempts);
        Assert.NotNull(delivered.DeliveredAt);

        var sent = Assert.Single(sender.Sent);
        Assert.Equal("https://example.test/hooks", sent.Url);
        Assert.Equal(created.Secret, sent.Secret);
        Assert.Equal(delivered.Id, sent.DeliveryId);
        Assert.Contains(WebhookConventions.EventTest, sent.Payload, StringComparison.Ordinal);

        await IdentityTestHelpers.ReadDataAsync<object>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Delete, $"/api/v1/admin/webhooks/{created.Subscription.Id}", token)));
        Assert.Empty((await ListDeliveriesAsync(client, token, created.Subscription.Id)).Items);
    }

    [Fact]
    public async Task 非法地址与未知事件在写入前被拒绝()
    {
        var sender = new FakeWebhookSender();
        using var factory = CreateFactory(sender);
        using var client = factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var badUrl = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/webhooks",
            token,
            new CreateWebhookSubscriptionRequest("ftp://example.test/hook")));
        Assert.Equal(
            (int)ApiErrorCode.WebhooksUrlInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(badUrl, HttpStatusCode.BadRequest));

        var badEvent = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/webhooks",
            token,
            new CreateWebhookSubscriptionRequest("https://example.test/hook", ["license.unknown"])));
        Assert.Equal(
            (int)ApiErrorCode.WebhooksEventUnknown,
            await IdentityTestHelpers.ReadErrorCodeAsync(badEvent, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task 失败按指数退避重排并在上限后终止且可手动重投()
    {
        var sender = new FakeWebhookSender { ShouldFail = true };
        using var factory = CreateFactory(sender, options =>
        {
            options.MaxAttempts = 2;
            options.BaseDelaySeconds = 1;
        });
        using var client = factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var created = await CreateSubscriptionAsync(
            client, token, "https://example.test/retry", [WebhookConventions.EventTest]);
        await IdentityTestHelpers.ReadDataAsync<WebhookTestResultDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/webhooks/{created.Subscription.Id}/test", token)));

        await DispatchAsync(factory);
        var failed = Assert.Single((await ListDeliveriesAsync(client, token, created.Subscription.Id)).Items);
        Assert.Equal(WebhookConventions.StatusFailed, failed.Status);
        Assert.Equal(1, failed.Attempts);
        Assert.NotNull(failed.NextAttemptAt);
        Assert.Equal(500, failed.LastStatusCode);

        // 未到下次尝试时间时不会被再次拾取
        Assert.Equal(0, await DispatchAsync(factory));

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        await DispatchAsync(factory);
        var dead = Assert.Single((await ListDeliveriesAsync(client, token, created.Subscription.Id)).Items);
        Assert.Equal(WebhookConventions.StatusDead, dead.Status);
        Assert.Equal(2, dead.Attempts);

        var retryDenied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/webhooks/deliveries/{dead.Id}/redeliver",
            token));
        Assert.Equal(HttpStatusCode.OK, retryDenied.StatusCode);

        var pending = Assert.Single((await ListDeliveriesAsync(client, token, created.Subscription.Id)).Items);
        Assert.Equal(WebhookConventions.StatusPending, pending.Status);
        Assert.Equal(0, pending.Attempts);

        sender.ShouldFail = false;
        await DispatchAsync(factory);
        var succeeded = Assert.Single((await ListDeliveriesAsync(client, token, created.Subscription.Id)).Items);
        Assert.Equal(WebhookConventions.StatusSucceeded, succeeded.Status);
    }

    [Fact]
    public async Task 审计查询按动作与结果过滤且匿名被拒()
    {
        using var client = _factory.CreateClient();

        var anonymous = await client.GetAsync("/api/v1/admin/audit-logs");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        await client.PostAsJsonAsync(
            "/api/v1/identity/login",
            new Platform.Contracts.Identity.LoginRequest("admin", "wrong-password"));

        var success = await IdentityTestHelpers.ReadDataAsync<PageResult<AuditLogDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/audit-logs?action=identity.login&result=success&pageSize=20",
                token)));
        Assert.NotEmpty(success.Items);
        Assert.All(success.Items, item =>
        {
            Assert.Contains("identity.login", item.Action, StringComparison.Ordinal);
            Assert.Equal("success", item.Result);
        });

        var failures = await IdentityTestHelpers.ReadDataAsync<PageResult<AuditLogDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/audit-logs?action=identity.login&result=failure&pageSize=20",
                token)));
        Assert.NotEmpty(failures.Items);
        Assert.All(failures.Items, item => Assert.Equal("failure", item.Result));
    }

    private WebApplicationFactory<Program> CreateFactory(
        FakeWebhookSender sender,
        Action<WebhooksOptions>? configure = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IWebhookSender>();
                services.AddSingleton<IWebhookSender>(sender);
                if (configure is not null)
                {
                    services.Configure(configure);
                }
            });
        });

    private static async Task<CreatedWebhookSubscriptionDto> CreateSubscriptionAsync(
        HttpClient client,
        string token,
        string url,
        IReadOnlyList<string> eventTypes) =>
        await IdentityTestHelpers.ReadDataAsync<CreatedWebhookSubscriptionDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/webhooks",
                token,
                new CreateWebhookSubscriptionRequest(url, eventTypes))));

    private static async Task<PageResult<WebhookDeliveryDto>> ListDeliveriesAsync(
        HttpClient client,
        string token,
        Guid subscriptionId) =>
        await IdentityTestHelpers.ReadDataAsync<PageResult<WebhookDeliveryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/admin/webhooks/deliveries?subscriptionId={subscriptionId}&pageSize=50",
                token)));

    private static async Task<int> DispatchAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IWebhookDispatcher>();
        return await dispatcher.DispatchDueAsync(100);
    }

    private sealed class FakeWebhookSender : IWebhookSender
    {
        public List<SentDelivery> Sent { get; } = [];

        public bool ShouldFail { get; set; }

        public Task<WebhookSendResult> SendAsync(
            string url,
            string secret,
            Guid deliveryId,
            string eventType,
            long timestamp,
            string payload,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentDelivery(url, secret, deliveryId, eventType, timestamp, payload));
            return Task.FromResult(
                ShouldFail
                    ? new WebhookSendResult(false, 500, "upstream failed")
                    : new WebhookSendResult(true, 200, null));
        }
    }

    private sealed record SentDelivery(
        string Url,
        string Secret,
        Guid DeliveryId,
        string EventType,
        long Timestamp,
        string Payload);
}
