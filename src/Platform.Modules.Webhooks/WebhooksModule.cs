using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Modules;
using Platform.Modules.Webhooks.Endpoints;
using Platform.Modules.Webhooks.Options;
using Platform.Modules.Webhooks.Persistence;
using Platform.Modules.Webhooks.Services;

namespace Platform.Modules.Webhooks;

/// <summary>
/// Webhook 模块：订阅、HMAC 签名投递、指数退避重投与手动重放。
/// </summary>
/// <remarks>
/// 事件由各业务模块通过 <see cref="IPlatformEventPublisher"/> 发布；本模块装配后替换默认空实现，
/// 摘除后事件自动丢弃，业务模块无需改动。投递失败按 2 的幂退避，达到上限转为 <c>dead</c>。
/// </remarks>
public sealed class WebhooksModule : IPlatformModule
{
    /// <inheritdoc />
    public string Key => PlatformModuleKeys.Webhooks;

    /// <inheritdoc />
    public string DisplayNameKey => "module.webhooks";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredPermissions { get; } =
    [
        PermissionCatalog.WebhookRead,
        PermissionCatalog.WebhookManage,
    ];

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<WebhooksOptions>(configuration.GetSection(WebhooksOptions.SectionName));

        services.Replace(ServiceDescriptor.Scoped<IPlatformEventPublisher, WebhookEventPublisher>());
        services.AddScoped<IWebhookSubscriptionService, WebhookSubscriptionService>();
        services.AddScoped<IWebhookDispatcher, WebhookDispatcher>();
        services.AddHttpClient<IWebhookSender, HttpWebhookSender>(
            static client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHostedService<WebhookDispatchWorker>();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IPlatformModelContributor, WebhooksModelContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IPlatformMetricSource, WebhookMetricSource>());
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapModuleInfo(this);
        api.MapWebhookEndpoints();
    }
}
