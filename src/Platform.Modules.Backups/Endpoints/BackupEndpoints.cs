using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Platform.Contracts;
using Platform.Contracts.Backups;
using Platform.Contracts.Permissions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Http;
using Platform.Kernel.Security;
using Platform.Modules.Backups.Options;
using Platform.Modules.Backups.Services;

namespace Platform.Modules.Backups.Endpoints;

/// <summary>备份模块端点：命名空间、备份创建/下载/软删除、下发与回执。</summary>
internal static class BackupEndpoints
{
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

    public static void MapBackupEndpoints(this IEndpointRouteBuilder api)
    {
        // 创建备份是 JSON 请求体：上限 = 配置负载上限 + JSON 开销，避免 Kestrel 默认 30MB 先拒。
        var createRequestLimit = api.ServiceProvider
                                     .GetRequiredService<IOptions<BackupsOptions>>()
                                     .Value
                                     .MaxPayloadBytes
                                 + RequestLimits.JsonOverheadBytes;

        var admin = api
            .MapGroup(PlatformRoutes.AdminModule(PlatformModuleKeys.Backups))
            .WithTags(PlatformModuleKeys.Backups);

        MapNamespaceEndpoints(admin);
        MapBackupEndpointsInternal(admin, createRequestLimit);
        MapRecycleEndpoints(admin);

        var client = api
            .MapGroup(PlatformRoutes.ClientModule(PlatformModuleKeys.Backups))
            .WithTags(PlatformModuleKeys.Backups);

        MapClientEndpoints(client, createRequestLimit);
    }

    private static void MapNamespaceEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/namespaces",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IBackupNamespaceService namespaces,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await namespaces.ListAsync(RequireAppKey(principal), cancellationToken)))
            .WithName("backups.namespaces.list")
            .WithSummary("命名空间列表")
            .RequirePermission(PermissionCatalog.BackupNamespaceRead);

        admin.MapPost(
                "/namespaces",
                async (
                    HttpContext context,
                    CreateBackupNamespaceRequest request,
                    ICurrentPrincipal principal,
                    IBackupNamespaceService namespaces,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "backups.namespace.create",
                        async ct =>
                        {
                            try
                            {
                                var created = await namespaces.CreateAsync(appKey, request, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "backups.namespace.create",
                                    targetType: "backup_namespace",
                                    targetId: created.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: created.Key);
                                return ApiResult.Ok(created);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "backups.namespace.create",
                                    targetType: "backup_namespace",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("backups.namespaces.create")
            .WithSummary("创建命名空间（支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.BackupNamespaceManage);

        admin.MapPatch(
                "/namespaces/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    UpdateBackupNamespaceRequest request,
                    ICurrentPrincipal principal,
                    IBackupNamespaceService namespaces,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var updated = await namespaces.UpdateAsync(appKey, id, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.namespace.update",
                            targetType: "backup_namespace",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: updated.Key);
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.namespace.update",
                            targetType: "backup_namespace",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("backups.namespaces.update")
            .WithSummary("更新命名空间渠道/配额/启用状态")
            .RequirePermission(PermissionCatalog.BackupNamespaceManage);

        admin.MapDelete(
                "/namespaces/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IBackupNamespaceService namespaces,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        await namespaces.DeleteAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.namespace.delete",
                            targetType: "backup_namespace",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, true);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.namespace.delete",
                            targetType: "backup_namespace",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("backups.namespaces.delete")
            .WithSummary("删除备份命名空间（仍有备份时拒绝）")
            .RequirePermission(PermissionCatalog.BackupNamespaceManage);
    }

    private static void MapBackupEndpointsInternal(RouteGroupBuilder admin, long createRequestLimit)
    {
        admin.MapGet(
                "",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IBackupService backups,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? namespaceKey = null,
                    string? kind = null,
                    string? keyword = null,
                    Guid? installationId = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize, Keyword = keyword };
                    return ApiResults.Ok(
                        context,
                        await backups.ListAsync(
                            RequireAppKey(principal),
                            query,
                            namespaceKey,
                            kind,
                            keyword,
                            installationId,
                            cancellationToken));
                })
            .WithName("backups.list")
            .WithSummary("备份列表（可按安装实例过滤）")
            .RequirePermission(PermissionCatalog.BackupRead);

        admin.MapPost(
                "",
                async (
                    HttpContext context,
                    CreateBackupRequest request,
                    ICurrentPrincipal principal,
                    IBackupService backups,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "backups.create",
                        async ct =>
                        {
                            try
                            {
                                var created = await backups.CreateAsync(appKey, request, principal.UserName, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "backups.create",
                                    targetType: "backup",
                                    targetId: created.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: $"{created.NamespaceKey}/{created.Name}/{created.SizeBytes}B");
                                return ApiResult.Ok(created);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "backups.create",
                                    targetType: "backup",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("backups.create")
            .WithSummary("创建备份（≤64KB 内联，超过转存；支持 Idempotency-Key）")
            .WithMetadata(new RequestSizeLimitAttribute(createRequestLimit))
            .RequirePermission(PermissionCatalog.BackupCreate);

        admin.MapGet(
                "/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IBackupService backups,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await backups.GetAsync(RequireAppKey(principal), id, cancellationToken)))
            .WithName("backups.get")
            .WithSummary("备份详情")
            .RequirePermission(PermissionCatalog.BackupRead);

        admin.MapGet(
                "/{id:guid}/download",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IBackupService backups,
                    IBlobStorage blobs,
                    CancellationToken cancellationToken) =>
                    await StreamAsync(
                        context,
                        await backups.ResolveDownloadAsync(RequireAppKey(principal), id, cancellationToken),
                        blobs,
                        cancellationToken))
            .WithName("backups.download")
            .WithSummary("下载备份（内联 JSON 或流式文件；ETag / Range）")
            .RequirePermission(PermissionCatalog.BackupRead);

        admin.MapPost(
                "/{id:guid}/push",
                async (
                    HttpContext context,
                    Guid id,
                    PushBackupRequest request,
                    ICurrentPrincipal principal,
                    IBackupPushService push,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var result = await push.PushAsync(
                            appKey, id, request, principal.UserName, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.push",
                            targetType: "backup",
                            targetId: id.ToString(),
                            result: result.Success ? AuditResults.Success : AuditResults.Failure,
                            message: result.Message);
                        return ApiResults.Ok(context, result);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.push",
                            targetType: "backup",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("backups.push")
            .WithSummary("下发备份到指定在线安装实例（多选）并逐个等待回执；应用组广播需显式声明")
            .RequirePermission(PermissionCatalog.BackupPush);

        admin.MapDelete(
                "/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IBackupService backups,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var deleted = await backups.DeleteAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.delete",
                            targetType: "backup",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: deleted.Name);
                        return ApiResults.Ok(context, deleted);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.delete",
                            targetType: "backup",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("backups.delete")
            .WithSummary("软删除备份（宽限期后由后台清理物理文件）")
            .RequirePermission(PermissionCatalog.BackupDelete);
    }

    private static void MapRecycleEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/deleted",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IBackupRecycleService recycle,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? keyword = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize, Keyword = keyword };
                    return ApiResults.Ok(
                        context,
                        await recycle.ListDeletedAsync(
                            RequireAppKey(principal), query, keyword, cancellationToken));
                })
            .WithName("backups.deleted.list")
            .WithSummary("回收站列表（软删除、宽限期内可恢复）")
            .RequirePermission(PermissionCatalog.BackupRestore);

        admin.MapPost(
                "/{id:guid}/restore",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IBackupRecycleService recycle,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var restored = await recycle.RestoreAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.restore",
                            targetType: "backup",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: restored.Name);
                        return ApiResults.Ok(context, restored);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.restore",
                            targetType: "backup",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("backups.restore")
            .WithSummary("恢复回收站中的备份（物理文件缺失时拒绝）")
            .RequirePermission(PermissionCatalog.BackupRestore);

        admin.MapPost(
                "/{id:guid}/purge",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IBackupRecycleService recycle,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var result = await recycle.PurgeAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.purge",
                            targetType: "backup",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: $"行 {result.RowsDeleted} / 文件 {result.FilesDeleted}");
                        return ApiResults.Ok(context, result);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.purge",
                            targetType: "backup",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("backups.purge")
            .WithSummary("立即物理清理单条已删除备份")
            .RequirePermission(PermissionCatalog.BackupPurge);

        admin.MapPost(
                "/purge-all",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IBackupRecycleService recycle,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var result = await recycle.PurgeAllAsync(appKey, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.purge",
                            targetType: "backup",
                            targetId: null,
                            result: AuditResults.Success,
                            message: $"全部清理：行 {result.RowsDeleted} / 文件 {result.FilesDeleted}");
                        return ApiResults.Ok(context, result);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.purge",
                            targetType: "backup",
                            targetId: null,
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("backups.purge-all")
            .WithSummary("立即物理清理当前应用全部已删除备份")
            .RequirePermission(PermissionCatalog.BackupPurge);
    }

    private static void MapClientEndpoints(RouteGroupBuilder client, long createRequestLimit)
    {
        client.MapGet(
                "",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IBackupService backups,
                    IAuditWriter audit,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    Guid? installationId = null,
                    string? kind = null,
                    string? namespaceKey = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var appKey = RequireAppKey(principal);
                    var query = new PageQuery { Page = page, PageSize = pageSize };
                    var result = await backups.ListAsync(
                        appKey,
                        query,
                        namespaceKey ?? BackupConventions.DeviceBackupsNamespaceKey,
                        kind,
                        keyword: null,
                        installationId,
                        cancellationToken);
                    await WriteAuditAsync(
                        context, audit, principal,
                        action: "backups.client.list",
                        targetType: "backup",
                        targetId: null,
                        result: AuditResults.Success,
                        message: $"installationId={installationId};page={page};pageSize={pageSize}");
                    return ApiResults.Ok(context, result);
                })
            .WithName("backups.client.list")
            .WithSummary("客户端按安装实例列出备份（默认 device-backups 命名空间）")
            .RequirePermission(PermissionCatalog.BackupRead);

        client.MapPost(
                "",
                async (
                    HttpContext context,
                    CreateBackupRequest request,
                    ICurrentPrincipal principal,
                    IBackupService backups,
                    IBackupNamespaceService namespaces,
                    IEnumerable<IInstallationDirectory> directories,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "backups.client.upload",
                        async ct =>
                        {
                            try
                            {
                                var installation = await ResolveInstallationAsync(
                                    appKey, request.InstallationId, directories, ct);
                                var effective = request with
                                {
                                    NamespaceKey = string.IsNullOrWhiteSpace(request.NamespaceKey)
                                        ? BackupConventions.DeviceBackupsNamespaceKey
                                        : request.NamespaceKey,
                                };

                                if (string.Equals(
                                        effective.NamespaceKey,
                                        BackupConventions.DeviceBackupsNamespaceKey,
                                        StringComparison.Ordinal))
                                {
                                    await namespaces.EnsureAsync(
                                        appKey,
                                        BackupConventions.DeviceBackupsNamespaceKey,
                                        BackupConventions.DeviceBackupsNamespaceName,
                                        ct);
                                }

                                var created = await backups.CreateAsync(
                                    appKey, effective, principal.UserName, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "backups.client.upload",
                                    targetType: "backup",
                                    targetId: created.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: $"{created.NamespaceKey}/{created.Name}/{created.SizeBytes}B/installation={installation.InstallationId}");
                                return ApiResult.Ok(created);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "backups.client.upload",
                                    targetType: "backup",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("backups.client.upload")
            .WithSummary("客户端按安装实例上传备份（≤64KB 内联，超过转存；支持 Idempotency-Key）")
            .WithMetadata(new RequestSizeLimitAttribute(createRequestLimit))
            .RequirePermission(PermissionCatalog.BackupCreate);

        client.MapGet(
                "/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IBackupService backups,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await backups.GetAsync(RequireAppKey(principal), id, cancellationToken)))
            .WithName("backups.client.get")
            .WithSummary("客户端读取备份元数据（下发后回拉）")
            .RequirePermission(PermissionCatalog.BackupRead);

        client.MapGet(
                "/{id:guid}/download",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IBackupService backups,
                    IBlobStorage blobs,
                    CancellationToken cancellationToken) =>
                    await StreamAsync(
                        context,
                        await backups.ResolveDownloadAsync(RequireAppKey(principal), id, cancellationToken),
                        blobs,
                        cancellationToken))
            .WithName("backups.client.download")
            .WithSummary("客户端下载备份（内联 JSON 或流式文件）")
            .RequirePermission(PermissionCatalog.BackupRead);
    }

    private static async Task<InstallationDirectoryEntry> ResolveInstallationAsync(
        string appKey,
        Guid? installationId,
        IEnumerable<IInstallationDirectory> directories,
        CancellationToken cancellationToken)
    {
        if (installationId is not { } id)
        {
            throw BusinessException.Invalid("客户端上传备份必须提供 installationId。");
        }

        var directory = directories.FirstOrDefault()
                        ?? throw new BusinessException(
                            ApiErrorCode.ServiceUnavailable,
                            "设备域未启用，无法按安装实例上传备份。");

        return await directory.FindAsync(appKey, id, cancellationToken)
               ?? throw BusinessException.NotFound(
                   ApiErrorCode.DevicesInstallationNotFound,
                   $"安装实例 {id} 不存在或不属于当前应用，请先连接后再上传备份。");
    }

    private static async Task<IResult> StreamAsync(
        HttpContext context,
        BackupDownload download,
        IBlobStorage blobs,
        CancellationToken cancellationToken)
    {
        context.Response.Headers["X-Content-Sha256"] = download.Sha256 ?? string.Empty;

        if (download.InlineJson is not null)
        {
            return Results.Text(download.InlineJson, "application/json; charset=utf-8");
        }

        var stream = await blobs.OpenReadAsync(download.BlobPath!, cancellationToken);
        return Results.Stream(
            stream,
            contentType: "application/json",
            fileDownloadName: download.FileName,
            lastModified: null,
            entityTag: string.IsNullOrEmpty(download.Sha256)
                ? null
                : new EntityTagHeaderValue($"\"{download.Sha256}\""),
            enableRangeProcessing: true);
    }

    private static string RequireAppKey(ICurrentPrincipal principal) =>
        string.IsNullOrWhiteSpace(principal.AppKey)
            ? throw new BusinessException(
                ApiErrorCode.Forbidden,
                "当前令牌没有应用上下文，无法访问备份资源。")
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
