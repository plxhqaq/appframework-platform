using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Hubs;
using Platform.Contracts.Identity;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Hubs;
using Platform.Modules.Identity.Domain;
using Platform.Modules.Identity.Options;

namespace Platform.Modules.Identity.Services;

/// <summary>扫码登录：创建待审批会话、客户端轮询状态、管理端批准/拒绝。</summary>
public interface IQrLoginService
{
    /// <summary>创建待审批会话（匿名入口）。</summary>
    Task<QrSessionCreatedDto> CreateAsync(
        CreateQrSessionRequest request,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>客户端轮询状态；批准后用同一请求 id 领取临时令牌。</summary>
    Task<QrSessionStatusDto> GetStatusAsync(Guid requestId, CancellationToken cancellationToken = default);

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
    private readonly IHubContext<PlatformHub> _hub;
    private readonly IClock _clock;
    private readonly QrLoginOptions _options;
    private readonly ILogger<QrLoginService> _logger;

    /// <summary>构造。</summary>
    public QrLoginService(
        PlatformDbContext db,
        ITemporarySessionFactory temporary,
        IHubContext<PlatformHub> hub,
        IClock clock,
        IOptions<QrLoginOptions> options,
        ILogger<QrLoginService> logger)
    {
        _db = db;
        _temporary = temporary;
        _hub = hub;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<QrSessionCreatedDto> CreateAsync(
        CreateQrSessionRequest request,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        var session = new QrLoginSession
        {
            Status = QrLoginStatuses.Pending,
            ClientName = Normalize(request.ClientName),
            DeviceFingerprint = Normalize(request.DeviceFingerprint),
            CreatedByIp = Normalize(ipAddress),
            ExpiresAt = now.AddMinutes(Math.Max(1, _options.ExpireMinutes)),
        };

        _db.Set<QrLoginSession>().Add(session);
        await _db.SaveChangesAsync(cancellationToken);

        return new QrSessionCreatedDto(
            session.Id,
            session.ExpiresAt,
            QrLoginConventions.PayloadPrefix + session.Id.ToString("N"));
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
            var user = await _temporary.BuildUserAsync(session.TemporaryUserId.Value, cancellationToken);
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
        var temporary = await _temporary.IssueAsync(
            Guid.CreateVersion7(),
            lifetime,
            machineCode: null,
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

        await NotifyAsync(requestId, approved: true);

        return new QrSessionSummaryDto(
            session.Id,
            QrLoginStatuses.Approved,
            session.ClientName,
            session.DeviceFingerprint,
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

        await NotifyAsync(requestId, approved: false);

        return new QrSessionSummaryDto(
            session.Id,
            QrLoginStatuses.Rejected,
            session.ClientName,
            session.DeviceFingerprint,
            session.CreatedByIp,
            session.CreatedAt,
            session.ExpiresAt,
            now,
            approvedName);
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

    private async Task NotifyAsync(Guid requestId, bool approved)
    {
        try
        {
            await _hub.Clients.All.SendAsync(
                PlatformHubEvents.QrLoginApproved,
                new QrLoginApprovedNotification(requestId, approved),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            // 推送只是提示；失败不能影响已经落库的审批结果。
            NotifyFailedLog(_logger, requestId, exception);
        }
    }

    private int ClampDuration(int? requestedMinutes)
    {
        var fallback = Math.Max(1, _options.DefaultDurationMinutes);
        var max = Math.Max(fallback, _options.MaxDurationMinutes);
        return Math.Clamp(requestedMinutes ?? fallback, 1, max);
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
            session.ClientName,
            session.DeviceFingerprint,
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
