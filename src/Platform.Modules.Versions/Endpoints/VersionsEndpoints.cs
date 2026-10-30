using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Contracts.Versions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Http;
using Platform.Kernel.Security;
using Platform.Modules.Versions.Services;

namespace Platform.Modules.Versions.Endpoints;

/// <summary>版本模块端点：管理端渠道/版本/上传，客户端检查更新/清单/下载。</summary>
internal static class VersionsEndpoints
{
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

    public static void MapVersionsEndpoints(this IEndpointRouteBuilder api)
    {
        var admin = api
            .MapGroup(PlatformRoutes.AdminModule(PlatformModuleKeys.Versions))
            .WithTags(PlatformModuleKeys.Versions);

        MapChannelEndpoints(admin);
        MapReleaseEndpoints(admin);
        MapUploadEndpoints(admin);

        var client = api
            .MapGroup(PlatformRoutes.ClientModule(PlatformModuleKeys.Versions))
            .WithTags(PlatformModuleKeys.Versions);

        MapClientEndpoints(client);
    }

    private static void MapChannelEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/channels",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IReleaseChannelService channels,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await channels.ListAsync(RequireAppKey(principal), cancellationToken)))
            .WithName("versions.channels.list")
            .WithSummary("渠道列表")
            .RequirePermission(PermissionCatalog.VersionsChannelRead);

        admin.MapPost(
                "/channels",
                async (
                    HttpContext context,
                    CreateReleaseChannelRequest request,
                    ICurrentPrincipal principal,
                    IReleaseChannelService channels,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "versions.channel.create",
                        async ct =>
                        {
                            try
                            {
                                var created = await channels.CreateAsync(appKey, request, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "versions.channel.create",
                                    targetType: "release_channel",
                                    targetId: created.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: null);
                                return ApiResult.Ok(created);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "versions.channel.create",
                                    targetType: "release_channel",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("versions.channels.create")
            .WithSummary("创建渠道（支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.VersionsChannelManage);

        admin.MapPatch(
                "/channels/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    UpdateReleaseChannelRequest request,
                    ICurrentPrincipal principal,
                    IReleaseChannelService channels,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var updated = await channels.UpdateAsync(appKey, id, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.channel.update",
                            targetType: "release_channel",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.channel.update",
                            targetType: "release_channel",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("versions.channels.update")
            .WithSummary("更新渠道名称/说明/启用状态")
            .RequirePermission(PermissionCatalog.VersionsChannelManage);
    }

    private static void MapReleaseEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/releases",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IReleaseService releases,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? channelKey = null,
                    string? status = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize };
                    return ApiResults.Ok(
                        context,
                        await releases.ListAsync(
                            RequireAppKey(principal), query, channelKey, status, cancellationToken));
                })
            .WithName("versions.releases.list")
            .WithSummary("版本列表")
            .RequirePermission(PermissionCatalog.VersionsReleaseRead);

        admin.MapGet(
                "/releases/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IReleaseService releases,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await releases.GetAsync(RequireAppKey(principal), id, cancellationToken)))
            .WithName("versions.releases.get")
            .WithSummary("版本详情（含产物与文件清单）")
            .RequirePermission(PermissionCatalog.VersionsReleaseRead);

        admin.MapPost(
                "/releases",
                async (
                    HttpContext context,
                    CreateReleaseRequest request,
                    ICurrentPrincipal principal,
                    IReleaseService releases,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "versions.release.create",
                        async ct =>
                        {
                            try
                            {
                                var created = await releases.CreateAsync(
                                    appKey, request, principal.UserName, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "versions.release.create",
                                    targetType: "release",
                                    targetId: created.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: created.Status == VersionConventions.StatusPublished ? "published" : "draft");
                                return ApiResult.Ok(created);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "versions.release.create",
                                    targetType: "release",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("versions.releases.create")
            .WithSummary("按清单创建版本（支持 Idempotency-Key；Publish=true 时创建即发布）")
            .RequirePermission(PermissionCatalog.VersionsReleasePublish);

        admin.MapPost(
                "/releases/{id:guid}/publish",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IReleaseService releases,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var published = await releases.PublishAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.release.publish",
                            targetType: "release",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: published.Version);
                        return ApiResults.Ok(context, published);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.release.publish",
                            targetType: "release",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("versions.releases.publish")
            .WithSummary("发布草稿版本")
            .RequirePermission(PermissionCatalog.VersionsReleasePublish);

        admin.MapPost(
                "/releases/{id:guid}/archive",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IReleaseService releases,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var archived = await releases.ArchiveAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.release.archive",
                            targetType: "release",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: archived.Version);
                        return ApiResults.Ok(context, archived);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.release.archive",
                            targetType: "release",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("versions.releases.archive")
            .WithSummary("归档版本（最新版本归档后 check 回退到上一版）")
            .RequirePermission(PermissionCatalog.VersionsReleaseArchive);
    }

    private static void MapUploadEndpoints(RouteGroupBuilder admin)
    {
        const string uploadsPath = "/uploads";

        admin.MapPost(
                uploadsPath,
                async (
                    HttpContext context,
                    CreateUploadTicketRequest request,
                    ICurrentPrincipal principal,
                    IUploadTicketService uploads,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    var prefix = PlatformRoutes.AdminModule(PlatformModuleKeys.Versions) + uploadsPath;
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "versions.upload.ticket.create",
                        async ct =>
                        {
                            try
                            {
                                var ticket = await uploads.CreateAsync(
                                    appKey, request, principal.UserName, prefix, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "versions.upload.ticket.create",
                                    targetType: "version_upload_ticket",
                                    targetId: ticket.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: null);
                                return ApiResult.Ok(ticket);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "versions.upload.ticket.create",
                                    targetType: "version_upload_ticket",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("versions.uploads.create")
            .WithSummary("申请上传票据（支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.VersionsArtifactUpload);

        admin.MapPut(
                $"{uploadsPath}/{{id:guid}}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IUploadTicketService uploads,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    var ticket = await uploads.GetPendingAsync(appKey, id, cancellationToken);

                    // 在读取请求体前收紧上限，避免超大请求把磁盘写满。
                    var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                    if (sizeFeature is { IsReadOnly: false })
                    {
                        sizeFeature.MaxRequestBodySize = ticket.SizeBytes;
                    }

                    try
                    {
                        var result = await uploads.UploadAsync(
                            appKey,
                            id,
                            context.Request.Body,
                            context.Request.ContentLength,
                            cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.upload.complete",
                            targetType: "version_upload_ticket",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: result.Reused ? "reused" : "stored");
                        return ApiResults.Ok(context, result);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.upload.complete",
                            targetType: "version_upload_ticket",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("versions.uploads.put")
            .WithSummary("凭票据上传内容（application/octet-stream，一次性）")
            .RequirePermission(PermissionCatalog.VersionsArtifactUpload);
    }

    private static void MapClientEndpoints(RouteGroupBuilder client)
    {
        client.MapPost(
                "/check",
                async (
                    HttpContext context,
                    CheckUpdateRequest request,
                    ICurrentPrincipal principal,
                    IReleaseService releases,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await releases.CheckAsync(RequireAppKey(principal), request, cancellationToken)))
            .WithName("versions.client.check")
            .WithSummary("检查更新（返回最新已发布版本的文件清单）")
            .RequirePermission(PermissionCatalog.VersionsReleaseRead);

        client.MapGet(
                "/{channelKey}/{version}/manifest",
                async (
                    HttpContext context,
                    string channelKey,
                    string version,
                    ICurrentPrincipal principal,
                    IReleaseService releases,
                    string? runtimeIdentifier = null,
                    CancellationToken cancellationToken = default) =>
                    ApiResults.Ok(
                        context,
                        await releases.GetManifestAsync(
                            RequireAppKey(principal),
                            channelKey,
                            version,
                            runtimeIdentifier ?? string.Empty,
                            cancellationToken)))
            .WithName("versions.client.manifest")
            .WithSummary("读取已发布版本的运行时清单")
            .RequirePermission(PermissionCatalog.VersionsReleaseRead);

        client.MapGet(
                "/download/{sha256}",
                async (
                    HttpContext context,
                    string sha256,
                    ICurrentPrincipal principal,
                    IReleaseContentService content,
                    IBlobStorage blobs,
                    CancellationToken cancellationToken) =>
                {
                    var info = await content.ResolveAsync(
                        RequireAppKey(principal), sha256, cancellationToken);
                    var stream = await blobs.OpenReadAsync(info.BlobPath, cancellationToken);
                    return Results.Stream(
                        stream,
                        contentType: "application/octet-stream",
                        fileDownloadName: null,
                        lastModified: null,
                        entityTag: new EntityTagHeaderValue($"\"{info.Sha256}\""),
                        enableRangeProcessing: true);
                })
            .WithName("versions.client.download")
            .WithSummary("按内容哈希下载文件（Range / ETag / 304）")
            .RequirePermission(PermissionCatalog.VersionsArtifactDownload);
    }

    private static string RequireAppKey(ICurrentPrincipal principal) =>
        string.IsNullOrWhiteSpace(principal.AppKey)
            ? throw new BusinessException(
                ApiErrorCode.Forbidden,
                "当前令牌没有应用上下文，无法访问版本资源。")
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
