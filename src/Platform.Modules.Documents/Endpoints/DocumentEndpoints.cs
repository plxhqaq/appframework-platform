using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Platform.Contracts;
using Platform.Contracts.Documents;
using Platform.Contracts.Permissions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Http;
using Platform.Kernel.Security;
using Platform.Modules.Documents.Services;

namespace Platform.Modules.Documents.Endpoints;

/// <summary>文档模块端点：集合、文档上传/修订/软删除/下载、客户端清单。</summary>
internal static class DocumentEndpoints
{
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

    /// <summary>上传请求体上限（文件上限 + multipart 开销），实际单文件上限由配置校验。</summary>
    private const long UploadRequestLimitBytes = DocumentConventions.MaxFileSizeBytes + (8L * 1024 * 1024);

    public static void MapDocumentEndpoints(this IEndpointRouteBuilder api)
    {
        var admin = api
            .MapGroup(PlatformRoutes.AdminModule(PlatformModuleKeys.Documents))
            .WithTags(PlatformModuleKeys.Documents);

        MapCollectionEndpoints(admin);
        MapDocumentAdminEndpoints(admin);

        var client = api
            .MapGroup(PlatformRoutes.ClientModule(PlatformModuleKeys.Documents))
            .WithTags(PlatformModuleKeys.Documents);

        MapClientEndpoints(client);
    }

    private static void MapCollectionEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/collections",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IDocumentCollectionService collections,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await collections.ListAsync(RequireAppKey(principal), cancellationToken)))
            .WithName("documents.collections.list")
            .WithSummary("集合列表")
            .RequirePermission(PermissionCatalog.DocumentCollectionRead);

        admin.MapPost(
                "/collections",
                async (
                    HttpContext context,
                    CreateDocumentCollectionRequest request,
                    ICurrentPrincipal principal,
                    IDocumentCollectionService collections,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "documents.collection.create",
                        async ct =>
                        {
                            try
                            {
                                var created = await collections.CreateAsync(appKey, request, ct);
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "documents.collection.create",
                                    targetType: "document_collection",
                                    targetId: created.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: created.Key);
                                return ApiResult.Ok(created);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "documents.collection.create",
                                    targetType: "document_collection",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("documents.collections.create")
            .WithSummary("创建集合（支持 Idempotency-Key）")
            .RequirePermission(PermissionCatalog.DocumentCollectionManage);

        admin.MapPatch(
                "/collections/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    UpdateDocumentCollectionRequest request,
                    ICurrentPrincipal principal,
                    IDocumentCollectionService collections,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var updated = await collections.UpdateAsync(appKey, id, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.collection.update",
                            targetType: "document_collection",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: updated.Key);
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.collection.update",
                            targetType: "document_collection",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("documents.collections.update")
            .WithSummary("更新集合名称/说明/启用状态")
            .RequirePermission(PermissionCatalog.DocumentCollectionManage);
    }

    private static void MapDocumentAdminEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IDocumentService documents,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? collectionKey = null,
                    string? keyword = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize, Keyword = keyword };
                    return ApiResults.Ok(
                        context,
                        await documents.ListAsync(
                            RequireAppKey(principal), query, collectionKey, keyword, cancellationToken));
                })
            .WithName("documents.list")
            .WithSummary("文档列表")
            .RequirePermission(PermissionCatalog.DocumentRead);

        admin.MapGet(
                "/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IDocumentService documents,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await documents.GetAsync(RequireAppKey(principal), id, cancellationToken)))
            .WithName("documents.get")
            .WithSummary("文档详情（含修订历史）")
            .RequirePermission(PermissionCatalog.DocumentRead);

        admin.MapPost(
                "",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IDocumentService documents,
                    IIdempotencyStore idempotency,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    return await IdempotentExecution.ExecuteAsync(
                        context,
                        idempotency,
                        "documents.create",
                        async ct =>
                        {
                            try
                            {
                                var form = await context.Request.ReadFormAsync(ct);
                                var file = form.Files.GetFile("file")
                                           ?? throw BusinessException.Invalid("缺少上传文件字段 file。");
                                await using var stream = file.OpenReadStream();
                                var created = await documents.UploadAsync(
                                    appKey,
                                    new DocumentUpload(
                                        form["collectionKey"],
                                        form["title"],
                                        form["visibility"],
                                        file.FileName,
                                        file.ContentType,
                                        file.Length,
                                        stream),
                                    principal.UserName,
                                    ct);

                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "documents.create",
                                    targetType: "document",
                                    targetId: created.Id.ToString(),
                                    result: AuditResults.Success,
                                    message: $"{created.CollectionKey}/{created.Title}");
                                return ApiResult.Ok(created);
                            }
                            catch (BusinessException exception)
                            {
                                await WriteAuditAsync(
                                    context, audit, principal,
                                    action: "documents.create",
                                    targetType: "document",
                                    targetId: null,
                                    result: AuditResults.Failure,
                                    message: exception.Message);
                                throw;
                            }
                        },
                        IdempotencyTtl);
                })
            .WithName("documents.create")
            .WithSummary("上传新文档（multipart/form-data；支持 Idempotency-Key）")
            .WithMetadata(new RequestSizeLimitAttribute(UploadRequestLimitBytes))
            .RequirePermission(PermissionCatalog.DocumentManage);

        admin.MapPost(
                "/{id:guid}/revisions",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IDocumentService documents,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var form = await context.Request.ReadFormAsync(cancellationToken);
                        var file = form.Files.GetFile("file")
                                   ?? throw BusinessException.Invalid("缺少上传文件字段 file。");
                        await using var stream = file.OpenReadStream();
                        var updated = await documents.AddRevisionAsync(
                            appKey,
                            id,
                            new DocumentFileUpload(
                                file.FileName,
                                file.ContentType,
                                file.Length,
                                stream),
                            principal.UserName,
                            cancellationToken);

                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.revision.add",
                            targetType: "document",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: $"r{updated.Revision}");
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.revision.add",
                            targetType: "document",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("documents.revisions.add")
            .WithSummary("追加修订（multipart/form-data）")
            .WithMetadata(new RequestSizeLimitAttribute(UploadRequestLimitBytes))
            .RequirePermission(PermissionCatalog.DocumentManage);

        admin.MapPatch(
                "/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    UpdateDocumentRequest request,
                    ICurrentPrincipal principal,
                    IDocumentService documents,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var updated = await documents.UpdateAsync(appKey, id, request, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.update",
                            targetType: "document",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: $"{updated.Title}/{updated.Visibility}");
                        return ApiResults.Ok(context, updated);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.update",
                            targetType: "document",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("documents.update")
            .WithSummary("更新标题/可见性")
            .RequirePermission(PermissionCatalog.DocumentManage);

        admin.MapDelete(
                "/{id:guid}",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IDocumentService documents,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var deleted = await documents.DeleteAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.delete",
                            targetType: "document",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: deleted.Title);
                        return ApiResults.Ok(context, deleted);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.delete",
                            targetType: "document",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("documents.delete")
            .WithSummary("软删除文档（宽限期后由后台清理文件与修订）")
            .RequirePermission(PermissionCatalog.DocumentDelete);

        admin.MapGet(
                "/{id:guid}/download",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IDocumentService documents,
                    IBlobStorage blobs,
                    CancellationToken cancellationToken) =>
                    await StreamAsync(
                        await documents.ResolveDownloadAsync(
                            RequireAppKey(principal), id, revision: null, clientOnly: false, cancellationToken),
                        blobs,
                        cancellationToken))
            .WithName("documents.download")
            .WithSummary("下载最新版文档（ETag / Range）")
            .RequirePermission(PermissionCatalog.DocumentRead);

        admin.MapGet(
                "/{id:guid}/revisions/{revision:int}/download",
                async (
                    HttpContext context,
                    Guid id,
                    int revision,
                    ICurrentPrincipal principal,
                    IDocumentService documents,
                    IBlobStorage blobs,
                    CancellationToken cancellationToken) =>
                    await StreamAsync(
                        await documents.ResolveDownloadAsync(
                            RequireAppKey(principal), id, revision, clientOnly: false, cancellationToken),
                        blobs,
                        cancellationToken))
            .WithName("documents.revisions.download")
            .WithSummary("下载指定修订（ETag / Range）")
            .RequirePermission(PermissionCatalog.DocumentRead);
    }

    private static void MapClientEndpoints(RouteGroupBuilder client)
    {
        client.MapGet(
                "/manifest",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IDocumentService documents,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await documents.GetClientManifestAsync(RequireAppKey(principal), cancellationToken)))
            .WithName("documents.client.manifest")
            .WithSummary("客户端文档清单（服务端强制 visibility=client 过滤）")
            .RequirePermission(PermissionCatalog.DocumentRead);

        client.MapGet(
                "/{id:guid}/download",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IDocumentService documents,
                    IBlobStorage blobs,
                    CancellationToken cancellationToken) =>
                    await StreamAsync(
                        await documents.ResolveDownloadAsync(
                            RequireAppKey(principal), id, revision: null, clientOnly: true, cancellationToken),
                        blobs,
                        cancellationToken))
            .WithName("documents.client.download")
            .WithSummary("客户端下载文档（可见性二次校验）")
            .RequirePermission(PermissionCatalog.DocumentRead);
    }

    private static async Task<IResult> StreamAsync(
        DocumentDownload download,
        IBlobStorage blobs,
        CancellationToken cancellationToken)
    {
        var stream = await blobs.OpenReadAsync(download.BlobPath, cancellationToken);
        return Results.Stream(
            stream,
            contentType: download.ContentType ?? "application/octet-stream",
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
                "当前令牌没有应用上下文，无法访问文档资源。")
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
