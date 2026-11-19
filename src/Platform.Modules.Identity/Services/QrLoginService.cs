using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Contracts.Hubs;
using Platform.Contracts.Identity;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Identity.Domain;
using Platform.Modules.Identity.Options;

namespace Platform.Modules.Identity.Services;

/// <summary>扫码登录：创建待审批会话、客户端轮询状态、管理端批准/拒绝。</summary>
public interface IQrLoginService
{
    /// <summary>创建待审批会话（匿名入口）。</summary>
    /// <param name="request">客户端自述信息。</param>
    /// <param name="ipAddress">发起方 IP。</param>
    /// <param name="requestOrigin">当前请求的 scheme://host[:port]，仅在未配置 <c>QrLogin:ApprovalUrlBase</c> 时兜底。</param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<QrSessionCreatedDto> CreateAsync(
        CreateQrSessionRequest request,
        string? ipAddress,
        string? requestOrigin,
        CancellationToken cancellationToken = default);

    /// <summary>客户端轮询状态；批准后用同一请求 id 领取临时令牌。</summary>
    Task<QrSessionStatusDto> GetStatusAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>管理端按 id 取单个会话概要（手机扫码直达审批页时用）。</summary>
    Task<QrSessionSummaryDto> GetAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>管理端分页查询会话。</summary>
    Task<PageResult<QrSessionSummaryDto>> ListAsync(
        PageQuery query,
        string? status,
        CancellationToken cancellationToken = default);

    /// <summary>批准并签发临时令牌。重复批准返回 111。</summary>
    Task<QrSessionSummaryDto> ApproveAsync(
        Guid requestId,
        ApproveQrSessionRequest request,
        Guid approvedBy,
        string? approvedByName,
        CancellationToken cancellationToken = default);

    /// <summary>拒绝。重复拒绝返回 111。</summary>
    Task<QrSessionSummaryDto> RejectAsync(
        Guid requestId,
        Guid approvedBy,
        string? approvedByName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 客户端放弃本次扫码（离开二维码页、刷新码、关闭登录框）：待审批会话转 cancelled。
    /// 幂等：已取消、已批准、已拒绝、已过期都原样返回当前状态，不签发也不吊销令牌。
    /// </summary>
    Task<QrSessionStatusDto> CancelAsync(Guid requestId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 扫码登录实现。审批的竞态由带 <c>status = 'pending'</c> 条件的状态迁移语句决定，
/// 两个审批人同时点击时只有一个生效。SignalR 只推送"变了"的提示，客户端仍以轮询结果为准。
/// </summary>
public sealed class QrLoginService : IQrLoginService
{
    private static readonly SemaphoreSlim InMemoryGate = new(1, 1);

    private static readonly Action<ILogger, Guid, Exception?> NotifyFailedLog = LoggerMessage.Define<Guid>(
        LogLevel.Warning,
        new EventId(1, "QrLoginNotifyFailed"),
        "扫码会话 {RequestId} 的审批通知推送失败。");

    private readonly PlatformDbContext _db;
    private readonly ITemporarySessionFactory _temporary;
    private readonly IInstallationEventRouter? _router;
    private readonly IAdminEventPublisher _adminEvents;
    private readonly IAppDirectory _apps;
    private readonly IClock _clock;
    private readonly QrLoginOptions _options;
    private readonly ILogger<QrLoginService> _logger;

    /// <summary>构造。事件路由可选：Devices 模块缺席时客户端只能靠轮询领取审批结果。</summary>
    public QrLoginService(
        PlatformDbContext db,
        ITemporarySessionFactory temporary,
        IEnumerable<IInstallationEventRouter> routers,
        IAdminEventPublisher adminEvents,
        IAppDirectory apps,
        IClock clock,
        IOptions<QrLoginOptions> options,
        ILogger<QrLoginService> logger)
    {
        _db = db;
        _temporary = temporary;
        _router = routers.FirstOrDefault();
        _adminEvents = adminEvents;
        _apps = apps;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<QrSessionCreatedDto> CreateAsync(
        CreateQrSessionRequest request,
        string? ipAddress,
        string? requestOrigin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string appKey = Normalize(request.AppKey)
            ?? throw BusinessException.Invalid("必须指定应用（AppKey）：扫码登录需要明确目标应用。");
        if (!await _apps.IsEnabledAsync(appKey, cancellationToken))
        {
            throw BusinessException.Invalid($"应用 {appKey} 不存在或已停用，不能发起扫码登录。");
        }

        string? machineCode;
        try
        {
            machineCode = DeviceIdentityHasher.NormalizeMachineCode(request.MachineCode);
        }
        catch (ArgumentException exception)
        {
            throw BusinessException.Invalid(exception.Message);
        }

        var now = _clock.UtcNow;
        var session = new QrLoginSession
        {
            Status = QrLoginStatuses.Pending,
            AppKey = appKey,
            ClientName = Normalize(request.ClientName),
            DeviceFingerprint = Normalize(request.DeviceFingerprint),
            MachineCode = machineCode,
            InstallGuid = request.InstallGuid is { } install && install != Guid.Empty ? install : null,
            CreatedByIp = Normalize(ipAddress),
            ExpiresAt = now.AddMinutes(Math.Max(1, _options.ExpireMinutes)),
        };

        _db.Set<QrLoginSession>().Add(session);
        await _db.SaveChangesAsync(cancellationToken);
        await NotifyAdminAsync(appKey, session.Id, QrLoginStatuses.Pending);

        return new QrSessionCreatedDto(
            session.Id,
            session.ExpiresAt,
            BuildApprovalUrl(session.Id, requestOrigin));
    }

    /// <inheritdoc />
    public async Task<QrSessionSummaryDto> GetAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var session = await _db.Set<QrLoginSession>()
                          .AsNoTracking()
                          .FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken)
                      ?? throw SessionNotFound(requestId);

        return ToSummary(session, _clock.UtcNow);
    }

    /// <inheritdoc />
    public async Task<QrSessionStatusDto> GetStatusAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var session = await _db.Set<QrLoginSession>()
                          .AsNoTracking()
                          .FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken)
                      ?? throw SessionNotFound(requestId);

        var now = _clock.UtcNow;
        var status = ResolveStatus(session, now);

        TemporarySessionResponse? temporary = null;
        if (string.Equals(status, QrLoginStatuses.Approved, StringComparison.Ordinal)
            && session.AccessToken is not null
            && session.TokenExpiresAt > now
            && session.TemporaryUserId is not null)
        {
            var user = await _temporary.BuildUserAsync(session.TemporaryUserId.Value, session.AppKey, cancellationToken);
            temporary = new TemporarySessionResponse(session.AccessToken, session.TokenExpiresAt.Value, user);
        }

        return new QrSessionStatusDto(
            session.Id,
            status,
            session.CreatedAt,
            session.ExpiresAt,
            session.ApprovedAt,
            temporary);
    }

    /// <inheritdoc />
    public async Task<PageResult<QrSessionSummaryDto>> ListAsync(
        PageQuery query,
        string? status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filter = NormalizeStatus(status);
        var now = _clock.UtcNow;

        var source = _db.Set<QrLoginSession>().AsNoTracking();
        source = filter switch
        {
            null => source,
            QrLoginStatuses.Pending => source.Where(x => x.Status == QrLoginStatuses.Pending && x.ExpiresAt > now),
            QrLoginStatuses.Expired => source.Where(x => x.Status == QrLoginStatuses.Pending && x.ExpiresAt <= now),
            _ => source.Where(x => x.Status == filter),
        };

        var page = await source
            .OrderByDescending(static x => x.CreatedAt)
            .ToPageResultAsync(query, cancellationToken);

        return PageResult<QrSessionSummaryDto>.Create(
            [.. page.Items.Select(item => ToSummary(item, now))],
            page.Total,
            query);
    }

    /// <inheritdoc />
    public async Task<QrSessionSummaryDto> ApproveAsync(
        Guid requestId,
        ApproveQrSessionRequest request,
        Guid approvedBy,
        string? approvedByName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        var session = await LoadPendingAsync(requestId, now, cancellationToken);

        var lifetime = TimeSpan.FromMinutes(ClampDuration(request.DurationMinutes));
        // 临时会话绑定审批人与会话记录中的应用/机器/安装：客户端拿到的是该用户在该应用下的角色与权限。
        // BuildAsync 会校验审批人可访问该应用，无权即 403。
        var temporary = await _temporary.IssueAsync(
            approvedBy,
            session.AppKey,
            lifetime,
            session.MachineCode,
            session.InstallGuid,
            cancellationToken);

        var approvedName = Normalize(approvedByName);
        if (_db.Database.IsRelational())
        {
            var affected = await _db.Set<QrLoginSession>()
                .Where(x => x.Id == requestId
                            && x.Status == QrLoginStatuses.Pending
                            && x.ExpiresAt > now)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.Status, QrLoginStatuses.Approved)
                        .SetProperty(static x => x.ApprovedAt, now)
                        .SetProperty(static x => x.ApprovedBy, approvedBy)
                        .SetProperty(static x => x.ApprovedByName, approvedName)
                        .SetProperty(static x => x.AccessToken, temporary.AccessToken)
                        .SetProperty(static x => x.TokenExpiresAt, temporary.ExpiresAt)
                        .SetProperty(static x => x.TemporaryUserId, temporary.User.UserId)
                        .SetProperty(static x => x.UpdatedAt, now),
                    cancellationToken);

            if (affected != 1)
            {
                await ThrowStateConflictAsync(requestId, now, cancellationToken);
            }
        }
        else
        {
            await ApproveInMemoryAsync(requestId, now, approvedBy, approvedName, temporary, cancellationToken);
        }

        await NotifyAsync(session, approved: true);

        return new QrSessionSummaryDto(
            session.Id,
            QrLoginStatuses.Approved,
            session.AppKey,
            session.ClientName,
            session.DeviceFingerprint,
            session.InstallGuid,
            session.CreatedByIp,
            session.CreatedAt,
            session.ExpiresAt,
            now,
            approvedName);
    }

    /// <inheritdoc />
    public async Task<QrSessionSummaryDto> RejectAsync(
        Guid requestId,
        Guid approvedBy,
        string? approvedByName,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var session = await LoadPendingAsync(requestId, now, cancellationToken);
        var approvedName = Normalize(approvedByName);

        if (_db.Database.IsRelational())
        {
            var affected = await _db.Set<QrLoginSession>()
                .Where(x => x.Id == requestId
                            && x.Status == QrLoginStatuses.Pending
                            && x.ExpiresAt > now)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.Status, QrLoginStatuses.Rejected)
                        .SetProperty(static x => x.ApprovedAt, now)
                        .SetProperty(static x => x.ApprovedBy, approvedBy)
                        .SetProperty(static x => x.ApprovedByName, approvedName)
                        .SetProperty(static x => x.UpdatedAt, now),
                    cancellationToken);

            if (affected != 1)
            {
                await ThrowStateConflictAsync(requestId, now, cancellationToken);
            }
        }
        else
        {
            await RejectInMemoryAsync(requestId, now, approvedBy, approvedName, cancellationToken);
        }

        await NotifyAsync(session, approved: false);

        return new QrSessionSummaryDto(
            session.Id,
            QrLoginStatuses.Rejected,
            session.AppKey,
            session.ClientName,
            session.DeviceFingerprint,
            session.InstallGuid,
            session.CreatedByIp,
            session.CreatedAt,
            session.ExpiresAt,
            now,
            approvedName);
    }

    /// <inheritdoc />
    public async Task<QrSessionStatusDto> CancelAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        // 条件迁移同一套口径：只有仍待审批且未过期的会话会被取消。
        // 客户端重试、批准与取消并发时以先落库的状态为准，后到的一方原样读回。
        if (_db.Database.IsRelational())
        {
            await _db.Set<QrLoginSession>()
                .Where(x => x.Id == requestId
                            && x.Status == QrLoginStatuses.Pending
                            && x.ExpiresAt > now)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.Status, QrLoginStatuses.Cancelled)
                        .SetProperty(static x => x.UpdatedAt, now),
                    cancellationToken);
        }
        else
        {
            await CancelInMemoryAsync(requestId, now, cancellationToken);
        }

        var session = await _db.Set<QrLoginSession>()
                          .AsNoTracking()
                          .FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken)
                      ?? throw SessionNotFound(requestId);

        // 取消并发时以已落库状态为准；提示事件按读回状态发，管理台回拉自然收敛。
        await NotifyAdminAsync(session.AppKey, session.Id, ResolveStatus(session, now));

        return new QrSessionStatusDto(
            session.Id,
            ResolveStatus(session, now),
            session.CreatedAt,
            session.ExpiresAt,
            session.ApprovedAt,
            Session: null);
    }

    private async Task CancelInMemoryAsync(
        Guid requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await InMemoryGate.WaitAsync(cancellationToken);
        try
        {
            var session = await _db.Set<QrLoginSession>()
                .FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken);
            if (session is null)
            {
                return;
            }

            if (string.Equals(session.Status, QrLoginStatuses.Pending, StringComparison.Ordinal)
                && session.ExpiresAt > now)
            {
                session.Status = QrLoginStatuses.Cancelled;
                session.UpdatedAt = now;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }
        finally
        {
            InMemoryGate.Release();
        }
    }

    private async Task<QrLoginSession> LoadPendingAsync(
        Guid requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var session = await _db.Set<QrLoginSession>()
                          .AsNoTracking()
                          .FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken)
                      ?? throw SessionNotFound(requestId);

        ThrowIfNotPending(session, now);
        return session;
    }

    private async Task ApproveInMemoryAsync(
        Guid requestId,
        DateTimeOffset now,
        Guid approvedBy,
        string? approvedName,
        TemporarySession temporary,
        CancellationToken cancellationToken)
    {
        await InMemoryGate.WaitAsync(cancellationToken);
        try
        {
            var session = await LoadTrackedPendingAsync(requestId, now, cancellationToken);
            session.Status = QrLoginStatuses.Approved;
            session.ApprovedAt = now;
            session.ApprovedBy = approvedBy;
            session.ApprovedByName = approvedName;
            session.AccessToken = temporary.AccessToken;
            session.TokenExpiresAt = temporary.ExpiresAt;
            session.TemporaryUserId = temporary.User.UserId;
            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            InMemoryGate.Release();
        }
    }

    private async Task RejectInMemoryAsync(
        Guid requestId,
        DateTimeOffset now,
        Guid approvedBy,
        string? approvedName,
        CancellationToken cancellationToken)
    {
        await InMemoryGate.WaitAsync(cancellationToken);
        try
        {
            var session = await LoadTrackedPendingAsync(requestId, now, cancellationToken);
            session.Status = QrLoginStatuses.Rejected;
            session.ApprovedAt = now;
            session.ApprovedBy = approvedBy;
            session.ApprovedByName = approvedName;
            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            InMemoryGate.Release();
        }
    }

    private async Task<QrLoginSession> LoadTrackedPendingAsync(
        Guid requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var session = await _db.Set<QrLoginSession>()
                          .FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken)
                      ?? throw SessionNotFound(requestId);

        ThrowIfNotPending(session, now);
        return session;
    }

    private async Task ThrowStateConflictAsync(
        Guid requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var current = await _db.Set<QrLoginSession>()
                          .AsNoTracking()
                          .FirstOrDefaultAsync(x => x.Id == requestId, cancellationToken)
                      ?? throw SessionNotFound(requestId);

        ThrowIfNotPending(current, now);
        throw new BusinessException(
            ApiErrorCode.IdentityQrSessionAlreadyUsed,
            "扫码会话状态已变化，请刷新后重试。");
    }

    private static void ThrowIfNotPending(QrLoginSession session, DateTimeOffset now)
    {
        if (!string.Equals(session.Status, QrLoginStatuses.Pending, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.IdentityQrSessionAlreadyUsed,
                "扫码会话已被处理，请重新扫码。");
        }

        if (session.ExpiresAt <= now)
        {
            throw new BusinessException(
                ApiErrorCode.IdentityQrSessionExpired,
                "扫码会话已过期，请重新扫码。");
        }
    }

    /// <summary>审批结果通知：定向到发起会话的安装实例（优先 installGuid，退而机器码），并向管理台广播会话状态。</summary>
    private async Task NotifyAsync(QrLoginSession session, bool approved)
    {
        try
        {
            if (_router is not null)
            {
                var payload = new QrLoginApprovedNotification(session.Id, approved);
                if (session.InstallGuid is { } installGuid)
                {
                    await _router.ToInstallGuidAsync(
                        session.AppKey, installGuid, DeviceHubEvents.QrLoginApproved, payload, CancellationToken.None);
                }
                else if (!string.IsNullOrWhiteSpace(session.MachineCode))
                {
                    await _router.ToMachineAsync(
                        session.AppKey, session.MachineCode, DeviceHubEvents.QrLoginApproved, payload, CancellationToken.None);
                }
            }

            await NotifyAdminAsync(
                session.AppKey,
                session.Id,
                approved ? QrLoginStatuses.Approved : QrLoginStatuses.Rejected);
        }
        catch (Exception exception)
        {
            // 推送只是提示；失败不能影响已经落库的审批结果。
            NotifyFailedLog(_logger, session.Id, exception);
        }
    }

    /// <summary>管理台会话状态提示：identity 域 QrSessionChanged（创建/审批/拒绝/取消都走这里）。</summary>
    private async Task NotifyAdminAsync(string appKey, Guid sessionId, string status)
    {
        try
        {
            await _adminEvents.PublishAsync(
                AdminHubDomains.Identity,
                AdminHubEvents.QrSessionChanged,
                new AdminQrSessionChangedNotification(appKey, sessionId, status, _clock.UtcNow),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            // 推送只是提示；失败不能影响已经落库的会话状态。
            NotifyFailedLog(_logger, sessionId, exception);
        }
    }

    private int ClampDuration(int? requestedMinutes)
    {
        var fallback = Math.Max(1, _options.DefaultDurationMinutes);
        var max = Math.Max(fallback, _options.MaxDurationMinutes);
        return Math.Clamp(requestedMinutes ?? fallback, 1, max);
    }

    /// <summary>
    /// 二维码里放的是手机相机能直接打开的 https 链接。基址优先取配置：
    /// 反代把 Host 改成不带端口的主机名时，从请求推出来的地址扫开是连不上的。
    /// </summary>
    private string BuildApprovalUrl(Guid requestId, string? requestOrigin)
    {
        var baseUrl = Normalize(_options.ApprovalUrlBase) ?? Normalize(requestOrigin);
        var path = QrLoginConventions.ApprovalPath + requestId.ToString("N");
        return baseUrl is null ? path : baseUrl.TrimEnd('/') + path;
    }

    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var value = status.Trim().ToLowerInvariant();
        return QrLoginStatuses.IsValid(value)
            ? value
            : throw BusinessException.Invalid($"未知的扫码会话状态：{status}。");
    }

    private static string ResolveStatus(QrLoginSession session, DateTimeOffset now) =>
        string.Equals(session.Status, QrLoginStatuses.Pending, StringComparison.Ordinal)
        && session.ExpiresAt <= now
            ? QrLoginStatuses.Expired
            : session.Status;

    private static QrSessionSummaryDto ToSummary(QrLoginSession session, DateTimeOffset now) =>
        new(
            session.Id,
            ResolveStatus(session, now),
            session.AppKey,
            session.ClientName,
            session.DeviceFingerprint,
            session.InstallGuid,
            session.CreatedByIp,
            session.CreatedAt,
            session.ExpiresAt,
            session.ApprovedAt,
            session.ApprovedByName);

    private static BusinessException SessionNotFound(Guid requestId) =>
        BusinessException.NotFound(ApiErrorCode.IdentityQrSessionNotFound, $"扫码会话 {requestId} 不存在。");

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
