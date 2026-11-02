using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Contracts.Webhooks;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Http;
using Platform.Kernel.Security;
using Platform.Modules.Webhooks.Services;

namespace Platform.Modules.Webhooks.Endpoints;

/// <summary>Webhook 管理端端点：订阅、密钥轮换、投递记录与重投。</summary>
internal static class WebhookEndpoints
{
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

    public static void MapWebhookEndpoints(this IEndpointRouteBuilder api)
    {
        var admin = api
            .MapGroup(PlatformRoutes.AdminModule(PlatformModuleKeys.Webhooks))
            .WithTags(PlatformModuleKeys.Webhooks);

        admin.MapGet(
                "",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IWebhookSubscriptionService webhooks,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await webhooks.ListAsync(RequireAppKey(principal), cancellationToken)))
            .WithName("webhooks.list")
            .WithSummary("Webhook 订阅列表")
            .RequirePermission(PermissionCatalog.WebhookRead);

        admin.MapPost(
                "",
                async (
                    HttpContext context,
                    CreateWebhookSubscriptionRequest request,
                    ICurrentPrincipal principal,
                    IWebhookSubscriptionService webhooks,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "webhooks.subscription.create",
                        async ct =>
                        {
                            try
                            {
                                var created = await webhooks.CreateAsync(
                                    appKey, request, principal.UserName, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "webhooks.subscription.create",
                                    targetType: "webhook_subscription",
                                    targetId: created.Subscription.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: created.Subscription.Url);
                                return ApiResult.Ok(created);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "webhooks.subscription.create",
                                    targetType: "webhook_subscription",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("webhooks.create")
            .WithSummary("创建 Webhook 订阅（密钥只返回一次；支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.WebhookManage);

        admin.MapPatch(
                "/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    UpdateWebhookSubscriptionRequest request,
                    ICurrentPrincipal principal,
                    IWebhookSubscriptionService webhooks,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var updated = await webhooks.UpdateAsync(
                            RequireAppKey(principal), id, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "webhooks.subscription.update",
                            targetType: "webhook_subscription",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: updated.Url);
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "webhooks.subscription.update",
                            targetType: "webhook_subscription",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("webhooks.update")
            .WithSummary("更新 Webhook 订阅")
            .RequirePermission(PermissionCatalog.WebhookManage);

        admin.MapDelete(
                "/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IWebhookSubscriptionService webhooks,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        await webhooks.DeleteAsync(RequireAppKey(principal), id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "webhooks.subscription.delete",
                            targetType: "webhook_subscription",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, new { id });
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "webhooks.subscription.delete",
                            targetType: "webhook_subscription",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("webhooks.delete")
            .WithSummary("删除 Webhook 订阅及其投递记录")
            .RequirePermission(PermissionCatalog.WebhookManage);

        admin.MapPost(
                "/{id:guid}/secret/rotate",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IWebhookSubscriptionService webhooks,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var rotated = await webhooks.RotateSecretAsync(
                            RequireAppKey(principal), id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "webhooks.subscription.rotate-secret",
                            targetType: "webhook_subscription",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, rotated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "webhooks.subscription.rotate-secret",
                            targetType: "webhook_subscription",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("webhooks.rotate-secret")
            .WithSummary("轮换签名密钥（新密钥只返回一次）")
            .RequirePermission(PermissionCatalog.WebhookManage);

        admin.MapPost(
                "/{id:guid}/test",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IWebhookSubscriptionService webhooks,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await webhooks.TestAsync(RequireAppKey(principal), id, cancellationToken)))
            .WithName("webhooks.test")
            .WithSummary("发送一条测试事件")
            .RequirePermission(PermissionCatalog.WebhookManage);

        admin.MapGet(
                "/deliveries",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IWebhookSubscriptionService webhooks,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    Guid? subscriptionId = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize };
                    return ApiResults.Ok(
                        context,
                        await webhooks.ListDeliveriesAsync(
                            RequireAppKey(principal), subscriptionId, query, cancellationToken));
                })
            .WithName("webhooks.deliveries.list")
            .WithSummary("投递记录（可按订阅过滤）")
            .RequirePermission(PermissionCatalog.WebhookRead);

        admin.MapPost(
                "/deliveries/{deliveryId:guid}/redeliver",
                async (
                    HttpContext context,
                    Guid deliveryId,
                    ICurrentPrincipal principal,
                    IWebhookSubscriptionService webhooks,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var delivery = await webhooks.RedeliverAsync(appKey, deliveryId, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "webhooks.delivery.redeliver",
                            targetType: "webhook_delivery",
                            targetId: deliveryId.ToString(),
                            result: AuditResults.Success,
                            message: delivery.EventType);
                        return ApiResults.Ok(context, delivery);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "webhooks.delivery.redeliver",
                            targetType: "webhook_delivery",
                            targetId: deliveryId.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("webhooks.deliveries.redeliver")
            .WithSummary("手动重投失败/终止的投递")
            .RequirePermission(PermissionCatalog.WebhookManage);
    }

    private static string RequireAppKey(ICurrentPrincipal principal) =>
        string.IsNullOrWhiteSpace(principal.AppKey)
            ? throw new BusinessException(
                ApiErrorCode.Forbidden,
                "当前令牌没有应用上下文，无法访问 Webhook 资源。")
            : principal.AppKey;

    private static Task WriteAuditAsync(
        HttpContext context,
        IAuditWriter audit,
        ICurrentPrincipal principal,
        string action,
        string? targetType,
        string? targetId,
        string result,
        string? message)
    {
        return audit.WriteAsync(new AuditEntry
        {
            AppKey = principal.AppKey,
            ActorId = principal.UserId,
            ActorName = principal.UserName,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Result = result,
            Message = message,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            TraceId = context.TraceIdentifier,
        }, context.RequestAborted);
    }
}
