using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Contracts.Versions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Http;
using Platform.Kernel.Metrics;
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
        MapPackageEndpoints(admin);
        MapUpdaterEndpoints(admin);
        MapMediaEndpoints(admin);
        MapUploadEndpoints(admin);

        var client = api
            .MapGroup(PlatformRoutes.ClientModule(PlatformModuleKeys.Versions))
            .WithTags(PlatformModuleKeys.Versions);

        MapClientEndpoints(client);
    }

    /// <summary>
    /// 驱动媒体包管理端：登记（af-media/1 清单逐文件复核）、列表、详情、激活互斥、删除。
    /// 下载与浏览器票据在客户端组（<c>/media/{id}/download*</c>）。
    /// </summary>
    private static void MapMediaEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/media",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IDriverMediaService media,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? mediaKey = null,
                    bool? activeOnly = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize };
                    return ApiResults.Ok(
                        context,
                        await media.ListAsync(
                            RequireAppKey(principal), query, mediaKey, activeOnly, cancellationToken));
                })
            .WithName("versions.media.list")
            .WithSummary("驱动媒体包列表（可按 mediaKey 过滤；activeOnly=true 只看启用版本）")
            .RequirePermission(PermissionCatalog.VersionsReleaseRead);

        admin.MapGet(
                "/media/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IDriverMediaService media,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await media.GetAsync(RequireAppKey(principal), id, cancellationToken)))
            .WithName("versions.media.get")
            .WithSummary("驱动媒体包详情（含安装顺序）")
            .RequirePermission(PermissionCatalog.VersionsReleaseRead);

        admin.MapPost(
                "/media",
                async (
                    HttpContext context,
                    RegisterDriverMediaRequest request,
                    ICurrentPrincipal principal,
                    IDriverMediaService media,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "versions.media.register",
                        async ct =>
                        {
                            try
                            {
                                var registered = await media.RegisterAsync(
                                    appKey, request, principal.UserName, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "versions.media.register",
                                    targetType: "driver_media",
                                    targetId: registered.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: $"{registered.MediaKey}/{registered.Version} {registered.Sha256}");
                                return ApiResult.Ok(registered);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "versions.media.register",
                                    targetType: "driver_media",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("versions.media.register")
            .WithSummary("登记驱动媒体包（内容须先经上传票据落盘；清单与 zip 逐文件复核；支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.VersionsArtifactUpload)
            .RequireRateLimiting(PlatformRateLimitPolicies.VersionsPackageFinalize);

        admin.MapPost(
                "/media/{id:guid}/activate",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IDriverMediaService media,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var activated = await media.ActivateAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.media.activate",
                            targetType: "driver_media",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: $"{activated.MediaKey}/{activated.Version}");
                        return ApiResults.Ok(context, activated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.media.activate",
                            targetType: "driver_media",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("versions.media.activate")
            .WithSummary("启用该媒体版本（同 mediaKey 其余版本自动停用）")
            .RequirePermission(PermissionCatalog.VersionsArtifactUpload);

        admin.MapDelete(
                "/media/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IDriverMediaService media,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        await media.DeleteAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.media.delete",
                            targetType: "driver_media",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: null);
                        return ApiResults.Ok(context, true);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.media.delete",
                            targetType: "driver_media",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("versions.media.delete")
            .WithSummary("删除媒体包登记（blob 内容由孤儿扫描回收）")
            .RequirePermission(PermissionCatalog.VersionsReleaseArchive);
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
            .WithSummary("更新渠道名称/说明/启用状态/降级开关")
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

        admin.MapPatch(
                "/releases/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    UpdateReleaseRequest request,
                    ICurrentPrincipal principal,
                    IReleaseService releases,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var updated = await releases.UpdateAsync(appKey, id, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.release.update",
                            targetType: "release",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: updated.Version);
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.release.update",
                            targetType: "release",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("versions.releases.update")
            .WithSummary("编辑草稿版本的发布说明")
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

        admin.MapDelete(
                "/releases/{id:guid}",
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
                        var deleted = await releases.DeleteAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.release.delete",
                            targetType: "release",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: deleted.Version);
                        return ApiResults.Ok(context, deleted);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.release.delete",
                            targetType: "release",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("versions.releases.delete")
            .WithSummary("删除版本（草稿丢弃 / 归档彻底删除并回收无引用内容；已发布须先归档）")
            .RequirePermission(PermissionCatalog.VersionsReleaseDelete);
    }

    private static void MapPackageEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/packages",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IReleasePackageService packages,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? channelKey = null,
                    string? kind = null,
                    string? status = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize };
                    return ApiResults.Ok(
                        context,
                        await packages.ListAsync(
                            RequireAppKey(principal), query, channelKey, kind, status, cancellationToken));
                })
            .WithName("versions.packages.list")
            .WithSummary("发布包列表（按渠道/类型/摄取状态）")
            .RequirePermission(PermissionCatalog.VersionsReleaseRead);

        admin.MapGet(
                "/packages/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IReleasePackageService packages,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await packages.GetAsync(RequireAppKey(principal), id, cancellationToken)))
            .WithName("versions.packages.get")
            .WithSummary("发布包详情与摄取状态")
            .RequirePermission(PermissionCatalog.VersionsReleaseRead);

        admin.MapPost(
                "/packages",
                async (
                    HttpContext context,
                    RegisterReleasePackageRequest request,
                    ICurrentPrincipal principal,
                    IReleasePackageService packages,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "versions.package.register",
                        async ct =>
                        {
                            try
                            {
                                var registered = await packages.RegisterAsync(
                                    appKey, request, principal.UserName, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "versions.package.register",
                                    targetType: "release_package",
                                    targetId: registered.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: $"{registered.Kind}/{registered.Target}/{registered.Format} {registered.Status}");
                                return ApiResult.Ok(registered);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "versions.package.register",
                                    targetType: "release_package",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("versions.packages.register")
            .WithSummary("登记发布包（内容须先经上传票据落盘；支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.VersionsArtifactUpload)
            .RequireRateLimiting(PlatformRateLimitPolicies.VersionsPackageFinalize);

        admin.MapDelete(
                "/packages/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IReleasePackageService packages,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var deleted = await packages.DeleteAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.package.delete",
                            targetType: "release_package",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: deleted.FileName);
                        return ApiResults.Ok(context, deleted);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.package.delete",
                            targetType: "release_package",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("versions.packages.delete")
            .WithSummary("删除草稿版本下的发布包（已发布/已归档版本不可删）")
            .RequirePermission(PermissionCatalog.VersionsReleaseArchive);

        admin.MapPost(
                "/packages/{id:guid}/retry",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IReleasePackageService packages,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var retried = await packages.RetryAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.package.retry",
                            targetType: "release_package",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: retried.FileName);
                        return ApiResults.Ok(context, retried);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.package.retry",
                            targetType: "release_package",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("versions.packages.retry")
            .WithSummary("重试失败的增量包摄取（failed → pending）")
            .RequirePermission(PermissionCatalog.VersionsArtifactUpload);

        admin.MapPost(
                "/packages/{id:guid}/activate",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IReleasePackageService packages,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var activated = await packages.ActivateAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.package.activate",
                            targetType: "release_package",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: $"{activated.Kind}/{activated.Target} {activated.FileName}");
                        return ApiResults.Ok(context, activated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.package.activate",
                            targetType: "release_package",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("versions.packages.activate")
            .WithSummary("把就绪的包设为启用包（同版本同类型同目标只下发启用包）")
            .RequirePermission(PermissionCatalog.VersionsArtifactUpload);
    }

    private static void MapUpdaterEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/updaters",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IUpdaterReleaseService updaters,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? target = null,
                    string? status = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize };
                    return ApiResults.Ok(
                        context,
                        await updaters.ListAsync(
                            RequireAppKey(principal), query, target, status, cancellationToken));
                })
            .WithName("versions.updaters.list")
            .WithSummary("更新器版本列表（按目标/状态）")
            .RequirePermission(PermissionCatalog.VersionsReleaseRead);

        admin.MapPost(
                "/updaters",
                async (
                    HttpContext context,
                    RegisterUpdaterRequest request,
                    ICurrentPrincipal principal,
                    IUpdaterReleaseService updaters,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "versions.updater.register",
                        async ct =>
                        {
                            try
                            {
                                var registered = await updaters.RegisterAsync(
                                    appKey, request, principal.UserName, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "versions.updater.register",
                                    targetType: "app_updater_release",
                                    targetId: registered.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: $"{registered.Target}/{registered.Version}");
                                return ApiResult.Ok(registered);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "versions.updater.register",
                                    targetType: "app_updater_release",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("versions.updaters.register")
            .WithSummary("登记更新器分发包（内容须先经上传票据落盘；版本缺失时从清单/PE 读取）")
            .RequirePermission(PermissionCatalog.VersionsArtifactUpload)
            .RequireRateLimiting(PlatformRateLimitPolicies.VersionsPackageFinalize);

        admin.MapPost(
                "/updaters/{id:guid}/archive",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IUpdaterReleaseService updaters,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var archived = await updaters.ArchiveAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.updater.archive",
                            targetType: "app_updater_release",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: $"{archived.Target}/{archived.Version}");
                        return ApiResults.Ok(context, archived);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.updater.archive",
                            targetType: "app_updater_release",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("versions.updaters.archive")
            .WithSummary("归档更新器版本（幂等；归档后 check 回退到上一版更新器）")
            .RequirePermission(PermissionCatalog.VersionsReleaseArchive);

        admin.MapDelete(
                "/updaters/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IUpdaterReleaseService updaters,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var deleted = await updaters.DeleteAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.updater.delete",
                            targetType: "app_updater_release",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: $"{deleted.Target}/{deleted.Version}");
                        return ApiResults.Ok(context, deleted);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "versions.updater.delete",
                            targetType: "app_updater_release",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("versions.updaters.delete")
            .WithSummary("彻底删除已归档更新器版本（已发布拒绝；删除后回收无引用内容）")
            .RequirePermission(PermissionCatalog.VersionsReleaseDelete);
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
            .RequirePermission(PermissionCatalog.VersionsArtifactUpload)
            .RequireRateLimiting(PlatformRateLimitPolicies.VersionsUploadTicket);

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
                    PlatformMetrics metrics,
                    CancellationToken cancellationToken) =>
                {
                    var info = await content.ResolveAsync(
                        RequireAppKey(principal), sha256, cancellationToken);
                    RecordDownload(metrics, "file", info.SizeBytes);
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

        client.MapGet(
                "/packages/{id:guid}/download",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IReleasePackageService packages,
                    IBlobStorage blobs,
                    PlatformMetrics metrics,
                    CancellationToken cancellationToken) =>
                {
                    var download = await packages.ResolveDownloadAsync(
                        RequireAppKey(principal), id, requirePublished: true, cancellationToken);
                    RecordDownload(metrics, "package", download.SizeBytes);
                    var stream = await blobs.OpenReadAsync(download.BlobPath, cancellationToken);
                    context.Response.Headers["X-Content-Sha256"] = download.Sha256;
                    return Results.Stream(
                        stream,
                        contentType: "application/octet-stream",
                        fileDownloadName: download.FileName,
                        lastModified: null,
                        entityTag: new EntityTagHeaderValue($"\"{download.Sha256}\""),
                        enableRangeProcessing: true);
                })
            .WithName("versions.client.package.download")
            .WithSummary("下载全量/增量包（Range / ETag / 304，保留原文件名）")
            .RequirePermission(PermissionCatalog.VersionsArtifactDownload);

        client.MapGet(
                "/updater",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IUpdaterReleaseService updaters,
                    string? runtimeIdentifier = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var rid = runtimeIdentifier?.Trim().ToLowerInvariant();
                    // any 是兜底更新器自身的标识：具体 RID 会优先命中同 RID，
                    // 管理台也要能按 any 查「这一档兜底」的最新版本，所以不能只放行具体 RID。
                    var isAny = string.Equals(rid, VersionConventions.PackageTargetAny, StringComparison.Ordinal);
                    if (!isAny && !VersionConventions.IsSupportedRuntime(rid))
                    {
                        throw new BusinessException(
                            ApiErrorCode.VersionsRuntimeIdentifierUnsupported,
                            $"不支持的 runtimeIdentifier：{runtimeIdentifier}。");
                    }

                    var latest = await updaters.GetLatestAsync(
                                     RequireAppKey(principal), rid!, cancellationToken)
                                 ?? throw BusinessException.NotFound(
                                     ApiErrorCode.VersionsUpdaterNotFound,
                                     "没有可用的更新器版本。");
                    return ApiResults.Ok(context, latest);
                })
            .WithName("versions.client.updater.latest")
            .WithSummary("读取当前运行时下最新的已发布更新器（any 只看通用兜底档）")
            .RequirePermission(PermissionCatalog.VersionsReleaseRead);

        client.MapGet(
                "/updaters/{id:guid}/download",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IUpdaterReleaseService updaters,
                    IBlobStorage blobs,
                    PlatformMetrics metrics,
                    CancellationToken cancellationToken) =>
                {
                    var download = await updaters.ResolveDownloadAsync(
                        RequireAppKey(principal), id, cancellationToken);
                    RecordDownload(metrics, "updater", download.SizeBytes);
                    var stream = await blobs.OpenReadAsync(download.BlobPath, cancellationToken);
                    context.Response.Headers["X-Content-Sha256"] = download.Sha256;
                    return Results.Stream(
                        stream,
                        contentType: "application/octet-stream",
                        fileDownloadName: download.FileName,
                        lastModified: null,
                        entityTag: new EntityTagHeaderValue($"\"{download.Sha256}\""),
                        enableRangeProcessing: true);
                })
            .WithName("versions.client.updater.download")
            .WithSummary("下载更新器分发包（Range / ETag / 304，保留原文件名）")
            .RequirePermission(PermissionCatalog.VersionsArtifactDownload);

        client.MapGet(
                "/media/{id:guid}/download",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IDriverMediaService media,
                    IBlobStorage blobs,
                    PlatformMetrics metrics,
                    CancellationToken cancellationToken) =>
                {
                    var download = await media.GetDownloadAsync(RequireAppKey(principal), id, cancellationToken);
                    RecordDownload(metrics, "media", download.SizeBytes);
                    var stream = await blobs.OpenReadAsync(download.BlobPath, cancellationToken);
                    context.Response.Headers["X-Content-Sha256"] = download.Sha256;
                    return Results.Stream(
                        stream,
                        contentType: "application/zip",
                        fileDownloadName: download.FileName,
                        lastModified: null,
                        entityTag: new EntityTagHeaderValue($"\"{download.Sha256}\""),
                        enableRangeProcessing: true);
                })
            .WithName("versions.client.media.download")
            .WithSummary("下载单个驱动媒体包（Range / ETag / 304，保留原文件名）")
            .RequirePermission(PermissionCatalog.VersionsArtifactDownload);

        client.MapPost(
                "/media/{id:guid}/download-ticket",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IDriverMediaService media,
                    IDownloadArchiveService archives,
                    IDownloadTicketService tickets,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    var download = await media.GetDownloadAsync(appKey, id, cancellationToken);
                    var representation = await RepresentAsync(
                        appKey, download.Sha256, download.FileName, download.SizeBytes,
                        archives, cancellationToken);
                    var issued = tickets.Issue(appKey, DownloadTicketKinds.Media, id);
                    return ApiResults.Ok(
                        context,
                        new DownloadTicketDto(
                            $"{PlatformRoutes.ClientModule(PlatformModuleKeys.Versions)}/downloads/{issued.Ticket}",
                            representation.FileName,
                            representation.SizeBytes,
                            issued.ExpiresAt));
                })
            .WithName("versions.media.download.ticket")
            .WithSummary("签发驱动媒体包下载票据（媒体包本就是 zip，原样下发）")
            .RequirePermission(PermissionCatalog.VersionsArtifactDownload);

        // 管理台浏览器下载：Bearer 无法随 <a> 发出，改发限时票据；非 zip 源先打成 zip，
        // 因为浏览器对 exe 会弹安全拦截。SDK/客户端仍走上面的原始下载端点。
        client.MapPost(
                "/packages/{id:guid}/download-ticket",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IReleasePackageService packages,
                    IDownloadArchiveService archives,
                    IDownloadTicketService tickets,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    // 管理台核对安装包不要求版本已发布：草稿/归档也要能下载。
                    var download = await packages.ResolveDownloadAsync(
                        appKey, id, requirePublished: false, cancellationToken);
                    var representation = await RepresentAsync(
                        appKey, download.Sha256, download.FileName, download.SizeBytes,
                        archives, cancellationToken);
                    var issued = tickets.Issue(appKey, DownloadTicketKinds.Package, id);
                    return ApiResults.Ok(
                        context,
                        new DownloadTicketDto(
                            $"{PlatformRoutes.ClientModule(PlatformModuleKeys.Versions)}/downloads/{issued.Ticket}",
                            representation.FileName,
                            representation.SizeBytes,
                            issued.ExpiresAt));
                })
            .WithName("versions.packages.download.ticket")
            .WithSummary("签发安装包下载票据（非 zip 自动打包，浏览器原生流式下载）")
            .RequirePermission(PermissionCatalog.VersionsArtifactDownload);

        // 完整全量包：exe + 所选驱动媒体 + media/media-order.json 合成确定性 zip，按组合哈希缓存。
        // 校验（就绪全量包、同应用、声明覆盖、路径冲突）都在签发时做，兑现只重组缓存。
        client.MapPost(
                "/packages/{id:guid}/bundle-ticket",
                async (
                    HttpContext context,
                    Guid id,
                    CreatePackageBundleRequest request,
                    ICurrentPrincipal principal,
                    IPackageBundleService bundles,
                    IDownloadTicketService tickets,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    IReadOnlyList<Guid> mediaIds = request.MediaIds ?? [];
                    var prepared = await bundles.PrepareAsync(
                        appKey, id, mediaIds, request.AllowMissing, cancellationToken);
                    var issued = tickets.Issue(
                        appKey,
                        DownloadTicketKinds.Bundle,
                        id,
                        string.Join(',', mediaIds.Select(static x => x.ToString("N"))));
                    await WriteAuditAsync(
                        context,
                        audit,
                        principal,
                        "versions.package.bundle",
                        "release_package",
                        id.ToString("D"),
                        "success",
                        prepared.MissingComponents.Count > 0
                            ? $"组合 {prepared.CompositionHash}；allowMissing 放行缺项：{string.Join("、", prepared.MissingComponents)}"
                            : $"组合 {prepared.CompositionHash}；媒体 {mediaIds.Count} 份");
                    return ApiResults.Ok(
                        context,
                        new DownloadTicketDto(
                            $"{PlatformRoutes.ClientModule(PlatformModuleKeys.Versions)}/downloads/{issued.Ticket}",
                            prepared.FileName,
                            prepared.EstimatedSizeBytes,
                            issued.ExpiresAt));
                })
            .WithName("versions.packages.bundle.ticket")
            .WithSummary("签发完整全量包下载票据（包 + 所选媒体合成确定性 zip，按组合缓存）")
            .RequirePermission(PermissionCatalog.VersionsArtifactDownload);

        client.MapPost(
                "/updaters/{id:guid}/download-ticket",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IUpdaterReleaseService updaters,
                    IDownloadArchiveService archives,
                    IDownloadTicketService tickets,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    var download = await updaters.ResolveDownloadAsync(appKey, id, cancellationToken);
                    var representation = await RepresentAsync(
                        appKey, download.Sha256, download.FileName, download.SizeBytes,
                        archives, cancellationToken);
                    var issued = tickets.Issue(appKey, DownloadTicketKinds.Updater, id);
                    return ApiResults.Ok(
                        context,
                        new DownloadTicketDto(
                            $"{PlatformRoutes.ClientModule(PlatformModuleKeys.Versions)}/downloads/{issued.Ticket}",
                            representation.FileName,
                            representation.SizeBytes,
                            issued.ExpiresAt));
                })
            .WithName("versions.updaters.download.ticket")
            .WithSummary("签发更新器下载票据（非 zip 自动打包，浏览器原生流式下载）")
            .RequirePermission(PermissionCatalog.VersionsArtifactDownload);

        client.MapGet(
                "/downloads/{ticket}",
                async (
                    HttpContext context,
                    string ticket,
                    IDownloadTicketService tickets,
                    IReleasePackageService packages,
                    IUpdaterReleaseService updaters,
                    IDriverMediaService media,
                    IPackageBundleService bundles,
                    IDownloadArchiveService archives,
                    IBlobStorage blobs,
                    PlatformMetrics metrics,
                    CancellationToken cancellationToken) =>
                {
                    var parsed = tickets.Read(ticket);
                    var representation = await ResolveTicketDownloadAsync(
                        parsed, packages, updaters, media, bundles, archives, cancellationToken);
                    RecordDownload(metrics, parsed.Kind, representation.SizeBytes);
                    var stream = await blobs.OpenReadAsync(representation.BlobPath, cancellationToken);
                    context.Response.Headers["X-Content-Sha256"] = representation.Sha256;
                    return Results.Stream(
                        stream,
                        contentType: representation.ContentType,
                        fileDownloadName: representation.FileName,
                        lastModified: null,
                        entityTag: new EntityTagHeaderValue(
                            representation.IsArchive
                                ? $"\"{representation.Sha256}-zip\""
                                : $"\"{representation.Sha256}\""),
                        enableRangeProcessing: true);
                })
            .WithName("versions.download.ticket")
            .WithSummary("凭票据下载（浏览器原生流式 + Range 续传；非 zip 源下发 zip）")
            .AllowAnonymous();
    }

    private sealed record DownloadRepresentation(
        string BlobPath,
        string FileName,
        long SizeBytes,
        string Sha256,
        bool IsArchive)
    {
        /// <summary>浏览器下载类型：生成的 zip 与原始 zip 都按 <c>application/zip</c> 下发。</summary>
        public string ContentType =>
            string.Equals(Path.GetExtension(FileName), ".zip", StringComparison.OrdinalIgnoreCase)
                ? "application/zip"
                : "application/octet-stream";
    }

    private static async Task<DownloadRepresentation> ResolveTicketDownloadAsync(
        DownloadTicket ticket,
        IReleasePackageService packages,
        IUpdaterReleaseService updaters,
        IDriverMediaService media,
        IPackageBundleService bundles,
        IDownloadArchiveService archives,
        CancellationToken cancellationToken)
    {
        switch (ticket.Kind)
        {
            case DownloadTicketKinds.Package:
                {
                    // 票据签发时已按管理台权限放行，兑现不再复查版本状态（签发后归档不会作废已发票据）。
                    var download = await packages.ResolveDownloadAsync(
                        ticket.AppKey, ticket.Id, requirePublished: false, cancellationToken);
                    return await RepresentAsync(
                        ticket.AppKey, download.Sha256, download.FileName, download.SizeBytes,
                        archives, cancellationToken);
                }

            case DownloadTicketKinds.Updater:
                {
                    var download = await updaters.ResolveDownloadAsync(
                        ticket.AppKey, ticket.Id, cancellationToken);
                    return await RepresentAsync(
                        ticket.AppKey, download.Sha256, download.FileName, download.SizeBytes,
                        archives, cancellationToken);
                }

            case DownloadTicketKinds.Media:
                {
                    var download = await media.GetDownloadAsync(
                        ticket.AppKey, ticket.Id, cancellationToken);
                    return await RepresentAsync(
                        ticket.AppKey, download.Sha256, download.FileName, download.SizeBytes,
                        archives, cancellationToken);
                }

            case DownloadTicketKinds.Bundle:
                {
                    // 组合是缓存语义：命中直接下发，缺失按签发同款输入重建（内容一致）。
                    var download = await bundles.EnsureAsync(
                        ticket.AppKey, ticket.Id, ParseBundleMediaIds(ticket.Data), cancellationToken);
                    return new DownloadRepresentation(
                        download.BlobPath, download.FileName, download.SizeBytes,
                        download.CompositionHash, IsArchive: true);
                }

            default:
                throw new BusinessException(ApiErrorCode.Unauthorized, "下载票据无效或已过期，请回到管理台重新点击下载。");
        }
    }

    /// <summary>组合票据的媒体 id 载荷（GUID "N" 串逗号分隔）；畸形载荷按无效票据处理。</summary>
    private static Guid[] ParseBundleMediaIds(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return [];
        }

        var parts = data.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new Guid[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!Guid.TryParseExact(parts[index], "N", out ids[index]))
            {
                throw new BusinessException(
                    ApiErrorCode.Unauthorized,
                    "下载票据无效或已过期，请回到管理台重新点击下载。");
            }
        }

        return ids;
    }

    /// <summary>把源文件映射成实际下发文件：已是 zip 原样，否则取（必要时生成）zip 缓存。</summary>
    private static async Task<DownloadRepresentation> RepresentAsync(
        string appKey,
        string sha256,
        string fileName,
        long sizeBytes,
        IDownloadArchiveService archives,
        CancellationToken cancellationToken)
    {
        if (!archives.NeedsArchive(fileName))
        {
            return new DownloadRepresentation(
                VersionBlobPaths.Blob(appKey, sha256), fileName, sizeBytes, sha256, IsArchive: false);
        }

        var archive = await archives.EnsureAsync(appKey, sha256, fileName, cancellationToken);
        return new DownloadRepresentation(
            archive.BlobPath, archive.FileName, archive.SizeBytes, sha256, IsArchive: true);
    }

    private static void RecordDownload(PlatformMetrics metrics, string kind, long sizeBytes) =>
        metrics.Increment(
            PlatformMetricNames.VersionsPackageDownloadBytes,
            PlatformMetricNames.VersionsPackageDownloadBytesHelp,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = kind },
            sizeBytes);

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
