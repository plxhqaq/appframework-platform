using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Permissions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Http;
using Platform.Kernel.Security;
using Platform.Modules.Apps.Services;

namespace Platform.Modules.Apps.Endpoints;

/// <summary>应用接入模块端点：管理端 CRUD + API Key，匿名 M2M 换令牌。</summary>
internal static class AppsEndpoints
{
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

    public static void MapAppsEndpoints(this IEndpointRouteBuilder api)
    {
        var admin = api
            .MapGroup(PlatformRoutes.AdminModule(PlatformModuleKeys.Apps))
            .WithTags(PlatformModuleKeys.Apps);

        admin.MapGet(
                "",
                async (HttpContext context, IAppRegistryService apps, CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await apps.ListAsync(cancellationToken)))
            .WithName("apps.list")
            .WithSummary("应用列表")
            .RequirePermission(PermissionCatalog.AppsRead);

        admin.MapGet(
                "/{id:guid}",
                async (HttpContext context, Guid id, IAppRegistryService apps, CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await apps.GetAsync(id, cancellationToken)))
            .WithName("apps.get")
            .WithSummary("应用详情")
            .RequirePermission(PermissionCatalog.AppsRead);

        admin.MapPost(
                "",
                async (
                    HttpContext context,
                    CreateAppRequest request,
                    IAppRegistryService apps,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "apps.create",
                        async ct =>
                        {
                            try
                            {
                                var created = await apps.CreateAsync(request, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "apps.create",
                                    targetType: "app",
                                    targetId: created.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: null);
                                return ApiResult.Ok(created);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "apps.create",
                                    targetType: "app",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("apps.create")
            .WithSummary("创建应用（支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.AppsManage);

        admin.MapPatch(
                "/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    UpdateAppRequest request,
                    IAppRegistryService apps,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var updated = await apps.UpdateAsync(id, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "apps.update",
                            targetType: "app",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "apps.update",
                            targetType: "app",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("apps.update")
            .WithSummary("更新应用名称/说明/启用状态")
            .RequirePermission(PermissionCatalog.AppsManage);

        admin.MapGet(
                "/{id:guid}/keys",
                async (HttpContext context, Guid id, IApiKeyService keys, CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await keys.ListAsync(id, cancellationToken)))
            .WithName("apps.keys.list")
            .WithSummary("应用的 API Key 列表（不含明文）")
            .RequirePermission(PermissionCatalog.AppsApiKeyManage);

        var manifests = api
            .MapGroup(PlatformRoutes.Module(PlatformModuleKeys.Apps))
            .WithTags(PlatformModuleKeys.Apps);

        manifests.MapGet(
                "/{appKey}/permissions",
                async (
                    HttpContext context,
                    string appKey,
                    IAppPermissionService permissions,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await permissions.GetAsync(appKey, cancellationToken)))
            .WithName("apps.permissions.list")
            .WithSummary("应用自定义权限清单")
            .AllowAnonymous();

        manifests.MapPut(
                "/{appKey}/permissions",
                async (
                    HttpContext context,
                    string appKey,
                    ReplaceAppPermissionsRequest request,
                    IAppPermissionService permissions,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    // 应用可以自助上报自己的清单；管理端则凭 apps.manage 维护任意应用。
                    var isOwnService = principal.IsService
                        && string.Equals(principal.AppKey, appKey.Trim(), StringComparison.OrdinalIgnoreCase);
                    if (!isOwnService && !principal.HasPermission(PermissionCatalog.AppsManage))
                    {
                        throw new BusinessException(
                            ApiErrorCode.AppsApiKeyScopeDenied,
                            "只能维护本应用的权限清单。");
                    }

                    try
                    {
                        var replaced = await permissions.ReplaceAsync(appKey, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "apps.permissions.replace",
                            targetType: "app_permissions",
                            targetId: appKey,
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, replaced);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "apps.permissions.replace",
                            targetType: "app_permissions",
                            targetId: appKey,
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("apps.permissions.replace")
            .WithSummary("替换应用自定义权限清单（客户端可自助上报，成功后自动锁定）")
            .RequireAuthorization();

        admin.MapPost(
                "/{id:guid}/permissions/unlock",
                async (
                    HttpContext context,
                    Guid id,
                    IAppRegistryService apps,
                    IAppPermissionService permissions,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    var app = await apps.GetAsync(id, cancellationToken);
                    try
                    {
                        var manifest = await permissions.UnlockAsync(app.Key, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "apps.permissions.unlock",
                            targetType: "app_permissions",
                            targetId: app.Key,
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, manifest);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "apps.permissions.unlock",
                            targetType: "app_permissions",
                            targetId: app.Key,
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("apps.permissions.unlock")
            .WithSummary("人工解锁应用权限清单，允许再次上传")
            .RequirePermission(PermissionCatalog.AppsPermissionsManage);

        admin.MapPost(
                "/{id:guid}/permissions/lock",
                async (
                    HttpContext context,
                    Guid id,
                    IAppRegistryService apps,
                    IAppPermissionService permissions,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    var app = await apps.GetAsync(id, cancellationToken);
                    try
                    {
                        var manifest = await permissions.LockAsync(app.Key, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "apps.permissions.lock",
                            targetType: "app_permissions",
                            targetId: app.Key,
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, manifest);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "apps.permissions.lock",
                            targetType: "app_permissions",
                            targetId: app.Key,
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("apps.permissions.lock")
            .WithSummary("人工锁定应用权限清单，立即拒绝后续上传")
            .RequirePermission(PermissionCatalog.AppsPermissionsManage);

        admin.MapPost(
                "/{id:guid}/keys",
                async (
                    HttpContext context,
                    Guid id,
                    CreateApiKeyRequest request,
                    IApiKeyService keys,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "apps.apikey.create",
                        async ct =>
                        {
                            try
                            {
                                var created = await keys.CreateAsync(id, request, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "apps.apikey.create",
                                    targetType: "app_api_key",
                                    targetId: created.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: null);
                                return ApiResult.Ok(created);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "apps.apikey.create",
                                    targetType: "app_api_key",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("apps.keys.create")
            .WithSummary("签发 API Key（明文只返回一次，支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.AppsApiKeyManage);

        admin.MapPost(
                "/{id:guid}/keys/{keyId:guid}/revoke",
                async (
                    HttpContext context,
                    Guid id,
                    Guid keyId,
                    IApiKeyService keys,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var revoked = await keys.RevokeAsync(id, keyId, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "apps.apikey.revoke",
                            targetType: "app_api_key",
                            targetId: keyId.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, revoked);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "apps.apikey.revoke",
                            targetType: "app_api_key",
                            targetId: keyId.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("apps.keys.revoke")
            .WithSummary("吊销 API Key")
            .RequirePermission(PermissionCatalog.AppsApiKeyManage);

        admin.MapDelete(
                "/{id:guid}/keys/{keyId:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    Guid keyId,
                    IApiKeyService keys,
                    IAuditWriter audit,
                    ICurrentPrincipal principal,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        await keys.DeleteAsync(id, keyId, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "apps.apikey.delete",
                            targetType: "app_api_key",
                            targetId: keyId.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, true);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "apps.apikey.delete",
                            targetType: "app_api_key",
                            targetId: keyId.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("apps.keys.delete")
            .WithSummary("删除 API Key（仅限已吊销行）")
            .RequirePermission(PermissionCatalog.AppsApiKeyManage);

        api.MapPost(
                $"{PlatformRoutes.Module(PlatformModuleKeys.Apps)}/token",
                async (
                    HttpContext context,
                    M2MTokenRequest? request,
                    IM2MTokenService tokens,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = context.Request.Headers[PlatformHeaders.AppKey].ToString();
                    var apiKey = context.Request.Headers[PlatformHeaders.ApiKey].ToString();
                    if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(apiKey))
                    {
                        throw new BusinessException(
                            ApiErrorCode.AppsApiKeyInvalid,
                            $"缺少 {PlatformHeaders.AppKey} 或 {PlatformHeaders.ApiKey} 请求头。");
                    }

                    var issued = await tokens.IssueAsync(
                        appKey,
                        apiKey,
                        request?.Scopes,
                        cancellationToken);
                    return ApiResults.Ok(context, issued);
                })
            .WithName("apps.token")
            .WithSummary("API Key 换取 M2M 访问令牌")
            .WithTags(PlatformModuleKeys.Apps)
            .AllowAnonymous()
            .RequireRateLimiting(PlatformRateLimitPolicies.M2MToken);
    }

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
