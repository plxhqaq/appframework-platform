using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Contracts.Hubs;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Identity.Domain;

namespace Platform.Modules.Identity.Services;

/// <summary>被吊销会话的设备侧归属（推送定向用）。</summary>
/// <param name="AppKey">会话绑定的应用键；平台上下文会话为空（无法定位设备侧，跳过定向推送）。</param>
/// <param name="MachineCode">会话绑定的机器码（可空）。</param>
/// <param name="InstallGuid">会话绑定的安装实例标识（可空）。</param>
/// <param name="SessionId">会话 id（刷新令牌行）；用户级全吊销推送不带。</param>
public sealed record RevokedSessionTarget(
    string? AppKey,
    string? MachineCode,
    Guid? InstallGuid,
    Guid? SessionId);

/// <summary>
/// 会话吊销推送（D7 体验层）：吊销落库后向目标安装实例定向发 <see cref="DeviceHubEvents.UserSessionRevoked"/>、
/// 向管理台身份域发 <see cref="AdminHubEvents.UserSessionsRevoked"/>。推送只是提示，强制兜底是 epoch 守卫。
/// 发布失败只记日志，不得影响已落库的吊销结果。
/// </summary>
public interface ISessionRevocationNotifier
{
    /// <summary>吊销前抓取用户活跃会话的设备归属（bump/吊销调用方先行采集）。</summary>
    Task<IReadOnlyList<RevokedSessionTarget>> CaptureUserTargetsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>吊销成功后推送。userWide=false 时按会话逐个定向（带 SessionId），true 时全部会话按用户级推送。</summary>
    Task NotifyAsync(
        Guid userId,
        string reason,
        IReadOnlyList<RevokedSessionTarget> targets,
        bool userWide,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SessionRevocationNotifier : ISessionRevocationNotifier
{
    private static readonly Action<ILogger, Guid, Exception?> PushFailedLog = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(1, "SessionRevocationPushFailed"),
        "会话吊销推送失败（epoch 守卫仍强制生效）：用户 {UserId}。");

    private readonly PlatformDbContext _db;
    private readonly IInstallationEventRouter? _router;
    private readonly IAdminEventPublisher _adminEvents;
    private readonly IClock _clock;
    private readonly ILogger<SessionRevocationNotifier> _logger;

    /// <summary>构造。</summary>
    public SessionRevocationNotifier(
        PlatformDbContext db,
        IEnumerable<IInstallationEventRouter> routers,
        IAdminEventPublisher adminEvents,
        IClock clock,
        ILogger<SessionRevocationNotifier> logger)
    {
        _db = db;
        _router = routers.FirstOrDefault();
        _adminEvents = adminEvents;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RevokedSessionTarget>> CaptureUserTargetsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var rows = await _db.Set<RefreshToken>()
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.RevokedAt == null && x.ExpiresAt > now)
            .Select(x => new RevokedSessionTarget(x.AppKey, x.MachineCode, x.InstallGuid, x.Id))
            .ToListAsync(cancellationToken);
        return rows;
    }

    /// <inheritdoc />
    public async Task NotifyAsync(
        Guid userId,
        string reason,
        IReadOnlyList<RevokedSessionTarget> targets,
        bool userWide,
        CancellationToken cancellationToken = default)
    {
        if (targets.Count == 0)
        {
            return;
        }

        try
        {
            var now = _clock.UtcNow;
            if (_router is not null)
            {
                foreach (var target in targets)
                {
                    if (string.IsNullOrWhiteSpace(target.AppKey))
                    {
                        continue;
                    }

                    var payload = new UserSessionRevokedNotification(
                        target.AppKey,
                        userId,
                        target.InstallGuid,
                        userWide ? null : target.SessionId,
                        reason,
                        now);

                    // installGuid 优先（桌面端登录尽量上报），退到机器码路由。
                    if (target.InstallGuid is { } installGuid)
                    {
                        await _router.ToInstallGuidAsync(
                            target.AppKey, installGuid, DeviceHubEvents.UserSessionRevoked, payload, cancellationToken);
                    }
                    else if (!string.IsNullOrWhiteSpace(target.MachineCode))
                    {
                        await _router.ToMachineAsync(
                            target.AppKey, target.MachineCode, DeviceHubEvents.UserSessionRevoked, payload, cancellationToken);
                    }
                }
            }

            // 管理台按应用键分组计数；平台上下文会话合并为一组（AppKey=null）。
            foreach (var group in targets.GroupBy(static x => x.AppKey, StringComparer.Ordinal))
            {
                await _adminEvents.PublishAsync(
                    AdminHubDomains.Identity,
                    AdminHubEvents.UserSessionsRevoked,
                    new AdminUserSessionsRevokedNotification(group.Key, userId, group.Count(), now),
                    cancellationToken);
            }
        }
        catch (Exception exception)
        {
            PushFailedLog(_logger, userId, exception);
        }
    }
}
