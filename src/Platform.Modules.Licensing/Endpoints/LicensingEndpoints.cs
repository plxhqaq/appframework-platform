using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Contracts;
using Platform.Contracts.Licensing;
using Platform.Contracts.Permissions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Http;
using Platform.Kernel.Security;
using Platform.Modules.Licensing.Services;

namespace Platform.Modules.Licensing.Endpoints;

/// <summary>授权模块端点：管理端产品/授权/离线码/密钥，客户端状态校验与公钥发现。</summary>
internal static class LicensingEndpoints
{
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

    public static void MapLicensingEndpoints(this IEndpointRouteBuilder api)
    {
        var admin = api
            .MapGroup(PlatformRoutes.AdminModule(PlatformModuleKeys.Licensing))
            .WithTags(PlatformModuleKeys.Licensing);

        MapProductEndpoints(admin);
        MapEntitlementEndpoints(admin);
        MapOfflineCodeEndpoints(admin);
        MapSigningKeyEndpoints(admin);

        var client = api
            .MapGroup(PlatformRoutes.ClientModule(PlatformModuleKeys.Licensing))
            .WithTags(PlatformModuleKeys.Licensing);

        MapClientEndpoints(client);
    }

    private static void MapProductEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/products",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    ILicenseProductService products,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await products.ListAsync(RequireAppKey(principal), cancellationToken)))
            .WithName("licensing.products.list")
            .WithSummary("授权产品列表")
            .RequirePermission(PermissionCatalog.LicenseProductRead);

        admin.MapPost(
                "/products",
                async (
                    HttpContext context,
                    CreateLicenseProductRequest request,
                    ICurrentPrincipal principal,
                    ILicenseProductService products,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "licensing.product.create",
                        async ct =>
                        {
                            try
                            {
                                var created = await products.CreateAsync(appKey, request, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "licensing.product.create",
                                    targetType: "license_product",
                                    targetId: created.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: created.Key);
                                return ApiResult.Ok(created);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "licensing.product.create",
                                    targetType: "license_product",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("licensing.products.create")
            .WithSummary("创建授权产品（支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.LicenseProductManage);

        admin.MapPatch(
                "/products/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    UpdateLicenseProductRequest request,
                    ICurrentPrincipal principal,
                    ILicenseProductService products,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var updated = await products.UpdateAsync(appKey, id, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "licensing.product.update",
                            targetType: "license_product",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: updated.Key);
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "licensing.product.update",
                            targetType: "license_product",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("licensing.products.update")
            .WithSummary("更新产品名称/说明/席位/宽限/试用")
            .RequirePermission(PermissionCatalog.LicenseProductManage);
    }

    private static void MapEntitlementEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/entitlements",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    ILicenseEntitlementService entitlements,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? productKey = null,
                    string? status = null,
                    string? keyword = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize, Keyword = keyword };
                    return ApiResults.Ok(
                        context,
                        await entitlements.ListAsync(
                            RequireAppKey(principal), query, productKey, status, cancellationToken));
                })
            .WithName("licensing.entitlements.list")
            .WithSummary("授权列表")
            .RequirePermission(PermissionCatalog.LicenseRead);

        admin.MapGet(
                "/entitlements/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    ILicenseEntitlementService entitlements,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await entitlements.GetAsync(RequireAppKey(principal), id, cancellationToken)))
            .WithName("licensing.entitlements.get")
            .WithSummary("授权详情")
            .RequirePermission(PermissionCatalog.LicenseRead);

        admin.MapPost(
                "/entitlements",
                async (
                    HttpContext context,
                    IssueLicenseRequest request,
                    ICurrentPrincipal principal,
                    ILicenseEntitlementService entitlements,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "licensing.entitlement.issue",
                        async ct =>
                        {
                            try
                            {
                                var issued = await entitlements.IssueAsync(
                                    appKey, request, principal.UserName, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "licensing.entitlement.issue",
                                    targetType: "license_entitlement",
                                    targetId: issued.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: $"{issued.ProductKey}/{issued.MachineCode}");
                                return ApiResult.Ok(issued);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "licensing.entitlement.issue",
                                    targetType: "license_entitlement",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("licensing.entitlements.issue")
            .WithSummary("签发正式授权（支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.LicenseIssue);

        admin.MapPost(
                "/entitlements/{id:guid}/renew",
                async (
                    HttpContext context,
                    Guid id,
                    RenewLicenseRequest request,
                    ICurrentPrincipal principal,
                    ILicenseEntitlementService entitlements,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var renewed = await entitlements.RenewAsync(appKey, id, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "licensing.entitlement.renew",
                            targetType: "license_entitlement",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: renewed.NotAfter.ToString("O"));
                        return ApiResults.Ok(context, renewed);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "licensing.entitlement.renew",
                            targetType: "license_entitlement",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("licensing.entitlements.renew")
            .WithSummary("续期授权")
            .RequirePermission(PermissionCatalog.LicenseIssue);

        admin.MapPost(
                "/entitlements/{id:guid}/revoke",
                async (
                    HttpContext context,
                    Guid id,
                    RevokeLicenseRequest request,
                    ICurrentPrincipal principal,
                    ILicenseEntitlementService entitlements,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var revoked = await entitlements.RevokeAsync(appKey, id, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "licensing.entitlement.revoke",
                            targetType: "license_entitlement",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: revoked.RevokedReason);
                        return ApiResults.Ok(context, revoked);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "licensing.entitlement.revoke",
                            targetType: "license_entitlement",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("licensing.entitlements.revoke")
            .WithSummary("吊销授权（推送提示，客户端回拉后锁定）")
            .RequirePermission(PermissionCatalog.LicenseRevoke);
    }

    private static void MapOfflineCodeEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/offline-codes",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IOfflineLicenseCodeService codes,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? keyword = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize, Keyword = keyword };
                    return ApiResults.Ok(
                        context,
                        await codes.ListAsync(RequireAppKey(principal), query, cancellationToken));
                })
            .WithName("licensing.offlinecodes.list")
            .WithSummary("离线授权码台账")
            .RequirePermission(PermissionCatalog.LicenseRead);

        admin.MapPost(
                "/offline-codes",
                async (
                    HttpContext context,
                    IssueOfflineLicenseCodeRequest request,
                    ICurrentPrincipal principal,
                    IOfflineLicenseCodeService codes,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "licensing.offlinecode.issue",
                        async ct =>
                        {
                            try
                            {
                                var issued = await codes.IssueAsync(appKey, request, principal.UserName, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "licensing.offlinecode.issue",
                                    targetType: "offline_license_code",
                                    targetId: issued.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: $"{issued.ProductKey}/{issued.MachineCode}/{issued.LicenseNo}");
                                return ApiResult.Ok(issued);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "licensing.offlinecode.issue",
                                    targetType: "offline_license_code",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("licensing.offlinecodes.create")
            .WithSummary("签发离线授权码（支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.LicenseOfflineCodeIssue);

        admin.MapDelete(
                "/offline-codes/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IOfflineLicenseCodeService codes,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        await codes.DeleteAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "licensing.offlinecode.delete",
                            targetType: "offline_license_code",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: "ledger-removed");
                        return ApiResults.Success(context);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "licensing.offlinecode.delete",
                            targetType: "offline_license_code",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("licensing.offlinecodes.delete")
            .WithSummary("删除离线码台账行（已发出的码到期前仍可用）")
            .RequirePermission(PermissionCatalog.LicenseOfflineCodeIssue);
    }

    private static void MapSigningKeyEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/signing-keys",
                async (
                    HttpContext context,
                    ILicenseKeyService keys,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await keys.GetStatusAsync(cancellationToken)))
            .WithName("licensing.signingkeys.list")
            .WithSummary("签名密钥总览（含当前配置私钥对应的密钥）")
            .RequirePermission(PermissionCatalog.LicenseSigningKeyManage);

        admin.MapPost(
                "/signing-keys/sync",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    ILicenseKeyService keys,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var status = await keys.GetStatusAsync(cancellationToken);
                    await WriteAuditAsync(
                        context, audit, principal,
                        action: "licensing.signingkey.sync",
                        targetType: "license_signing_key",
                        targetId: status.CurrentKeyId,
                        result: AuditResults.Success,
                        message: status.IsManaged ? "managed" : "unmanaged");
                    return ApiResults.Ok(context, status);
                })
            .WithName("licensing.signingkeys.sync")
            .WithSummary("把部署配置的私钥同步到密钥登记表（轮换后调用）")
            .RequirePermission(PermissionCatalog.LicenseSigningKeyManage);

        admin.MapPost(
                "/signing-keys/{id:guid}/activate",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    ILicenseKeyService keys,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var status = await keys.ActivateAsync(id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "licensing.signingkey.activate",
                            targetType: "license_signing_key",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: status.CurrentKeyId);
                        return ApiResults.Ok(context, status);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "licensing.signingkey.activate",
                            targetType: "license_signing_key",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("licensing.signingkeys.activate")
            .WithSummary("激活签名密钥（必须与配置私钥匹配）")
            .RequirePermission(PermissionCatalog.LicenseSigningKeyManage);

        admin.MapPost(
                "/signing-keys/{id:guid}/retire",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    ILicenseKeyService keys,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var status = await keys.RetireAsync(id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "licensing.signingkey.retire",
                            targetType: "license_signing_key",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, status);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "licensing.signingkey.retire",
                            targetType: "license_signing_key",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("licensing.signingkeys.retire")
            .WithSummary("退役签名密钥（仍参与验签，不能再签发）")
            .RequirePermission(PermissionCatalog.LicenseSigningKeyManage);
    }

    private static void MapClientEndpoints(RouteGroupBuilder client)
    {
        client.MapPost(
                "/status",
                async (
                    HttpContext context,
                    LicenseStatusRequest request,
                    ICurrentPrincipal principal,
                    ILicenseStatusService status,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await status.GetStatusAsync(RequireAppKey(principal), request, cancellationToken)))
            .WithName("licensing.client.status")
            .WithSummary("校验在线授权并返回签名快照")
            .RequirePermission(PermissionCatalog.LicenseRead);

        client.MapPost(
                "/offline/verify",
                async (
                    HttpContext context,
                    VerifyOfflineLicenseCodeRequest request,
                    ICurrentPrincipal principal,
                    IOfflineLicenseCodeService codes,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await codes.VerifyAsync(RequireAppKey(principal), request, cancellationToken)))
            .WithName("licensing.client.offline.verify")
            .WithSummary("校验离线授权码（签名 + 产品绑定 + 机器码）")
            .RequirePermission(PermissionCatalog.LicenseRead);

        client.MapGet(
                "/keys",
                async (
                    HttpContext context,
                    ILicenseKeyService keys,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(context, await keys.DescribeTrustedAsync(cancellationToken)))
            .WithName("licensing.client.keys")
            .WithSummary("签名公钥发现（公钥不是秘密；用于轮换过渡与离线验签）")
            .AllowAnonymous();
    }

    private static string RequireAppKey(ICurrentPrincipal principal) =>
        string.IsNullOrWhiteSpace(principal.AppKey)
            ? throw new BusinessException(
                ApiErrorCode.Forbidden,
                "当前令牌没有应用上下文，无法访问授权资源。")
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
