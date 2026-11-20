using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Contracts.Retention;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Http;
using Platform.Kernel.Security;
using Platform.Modules.Backups.Options;
using Platform.Modules.Documents.Options;

namespace Platform.Api.Endpoints;

/// <summary>保留策略配置端点：两域清理宽限期（平台级，跨应用生效）。</summary>
public static class RetentionEndpoints
{
    /// <summary>挂载保留策略配置。</summary>
    public static IEndpointRouteBuilder MapRetentionEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var retention = api
            .MapGroup($"{PlatformRoutes.AdminPrefix}/retention")
            .WithTags("platform");

        retention.MapGet(
                "/settings",
                async (
                    HttpContext context,
                    IRetentionSettingsStore store,
                    IOptions<BackupsOptions> backupsOptions,
                    IOptions<DocumentsOptions> documentsOptions,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await ReadAsync(
                            store,
                            backupsOptions.Value,
                            documentsOptions.Value,
                            cancellationToken)))
            .WithName("retention.settings.get")
            .WithSummary("清理宽限期（库内覆盖出厂默认）")
            .RequirePermission(PermissionCatalog.RetentionCleanupManage);

        retention.MapPut(
                "/settings",
                async (
                    HttpContext context,
                    UpdateRetentionSettingsRequest request,
                    ICurrentPrincipal principal,
                    IRetentionSettingsStore store,
                    IOptions<BackupsOptions> backupsOptions,
                    IOptions<DocumentsOptions> documentsOptions,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        if (request.BackupsGraceMinutes < 1 || request.DocumentsGraceMinutes < 1)
                        {
                            throw BusinessException.Invalid("清理宽限期最小 1 分钟。");
                        }

                        await store.SetGraceMinutesAsync(
                            new Dictionary<string, int>(StringComparer.Ordinal)
                            {
                                [PlatformModuleKeys.Backups] = request.BackupsGraceMinutes,
                                [PlatformModuleKeys.Documents] = request.DocumentsGraceMinutes,
                            },
                            principal.UserName ?? principal.UserId ?? "unknown",
                            cancellationToken);

                        var settings = await ReadAsync(
                            store,
                            backupsOptions.Value,
                            documentsOptions.Value,
                            cancellationToken);
                        await WriteAuditAsync(
                            context,
                            audit,
                            principal,
                            action: "retention.settings.update",
                            result: AuditResults.Success,
                            message: $"backups={settings.Backups.GraceMinutes},documents={settings.Documents.GraceMinutes}");
                        return ApiResults.Ok(context, settings);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context,
                            audit,
                            principal,
                            action: "retention.settings.update",
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("retention.settings.update")
            .WithSummary("整体提交备份/文档清理宽限期（平台全部应用生效）")
            .RequirePermission(PermissionCatalog.RetentionCleanupManage);

        return api;
    }

    private static async Task<RetentionSettingsDto> ReadAsync(
        IRetentionSettingsStore store,
        BackupsOptions backups,
        DocumentsOptions documents,
        CancellationToken cancellationToken) =>
        new(
            await ResolveAsync(store, PlatformModuleKeys.Backups, backups.CleanupGraceMinutes, cancellationToken),
            await ResolveAsync(store, PlatformModuleKeys.Documents, documents.CleanupGraceMinutes, cancellationToken));

    private static async Task<RetentionDomainSettingsDto> ResolveAsync(
        IRetentionSettingsStore store,
        string moduleKey,
        int fallbackMinutes,
        CancellationToken cancellationToken)
    {
        var stored = await store.GetGraceMinutesAsync(moduleKey, cancellationToken);
        return new RetentionDomainSettingsDto(
            moduleKey,
            stored ?? Math.Max(1, fallbackMinutes),
            stored is not null);
    }

    private static Task WriteAuditAsync(
        HttpContext context,
        IAuditWriter audit,
        ICurrentPrincipal principal,
        string action,
        string result,
        string? message) =>
        audit.WriteAsync(new AuditEntry
        {
            AppKey = null,
            ActorId = principal.UserId,
            ActorName = principal.UserName,
            Action = action,
            TargetType = "retention_settings",
            TargetId = null,
            Result = result,
            Message = message,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            TraceId = context.TraceIdentifier,
        }, context.RequestAborted);
}
