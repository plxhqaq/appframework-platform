using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Platform.Contracts;
using Platform.Contracts.Hubs;
using Platform.Contracts.Licensing;
using Platform.Contracts.Webhooks;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Hubs;
using Platform.Modules.Licensing.Domain;
using Platform.Modules.Licensing.Options;
using Microsoft.Extensions.Options;

namespace Platform.Modules.Licensing.Services;

/// <summary>在线授权的签发、续期、吊销与查询。</summary>
public interface ILicenseEntitlementService
{
    /// <summary>分页查询授权。</summary>
    Task<PageResult<LicenseEntitlementDto>> ListAsync(
        string appKey,
        PageQuery query,
        string? productKey,
        string? status,
        CancellationToken cancellationToken = default);

    /// <summary>读取单个授权。</summary>
    Task<LicenseEntitlementDto> GetAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>签发正式授权；席位用尽抛 <see cref="ApiErrorCode.LicensingSeatExhausted"/>。</summary>
    Task<LicenseEntitlementDto> IssueAsync(
        string appKey,
        IssueLicenseRequest request,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>续期；已吊销的授权不可续期。</summary>
    Task<LicenseEntitlementDto> RenewAsync(
        string appKey,
        Guid id,
        RenewLicenseRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>吊销；重复吊销幂等返回。吊销后推送提示，客户端回拉校验。</summary>
    Task<LicenseEntitlementDto> RevokeAsync(
        string appKey,
        Guid id,
        RevokeLicenseRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>在线授权实现。</summary>
public sealed class LicenseEntitlementService : ILicenseEntitlementService
{
    private static readonly Action<ILogger, string, Exception?> NotifyFailedLog = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1, "LicenseChangedNotifyFailed"),
        "授权变化推送失败（应用 {AppKey}）。");

    private readonly PlatformDbContext _db;
    private readonly IClock _clock;
    private readonly IHubContext<PlatformHub> _hub;
    private readonly LicensingOptions _options;
    private readonly ILogger<LicenseEntitlementService> _logger;
    private readonly IPlatformEventPublisher _events;
    private readonly IDeviceNodeDirectory? _nodes;

    /// <summary>构造。</summary>
    public LicenseEntitlementService(
        PlatformDbContext db,
        IClock clock,
        IHubContext<PlatformHub> hub,
        IOptions<LicensingOptions> options,
        ILogger<LicenseEntitlementService> logger,
        IPlatformEventPublisher events,
        IEnumerable<IDeviceNodeDirectory> nodes)
    {
        _db = db;
        _clock = clock;
        _hub = hub;
        _options = options.Value;
        _logger = logger;
        _events = events;
        _nodes = nodes.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<PageResult<LicenseEntitlementDto>> ListAsync(
        string appKey,
        PageQuery query,
        string? productKey,
        string? status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var now = _clock.UtcNow;

        IQueryable<LicenseEntitlement> source = _db.Set<LicenseEntitlement>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey);

        if (!string.IsNullOrWhiteSpace(productKey))
        {
            string key = productKey.Trim();
            source = source.Where(x => x.Product.Key == key);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            source = status.Trim() switch
            {
                LicenseConventions.StatusActive => source.Where(x => x.RevokedAt == null && x.ExpiresAt >= now),
                LicenseConventions.StatusExpired => source.Where(x => x.RevokedAt == null && x.ExpiresAt < now),
                LicenseConventions.StatusRevoked => source.Where(x => x.RevokedAt != null),
                _ => throw BusinessException.Invalid("status 只能是 active / expired / revoked。"),
            };
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            string keyword = query.Keyword.Trim();
            source = source.Where(x => x.MachineCode.Contains(keyword)
                || (x.LicenseNo != null && x.LicenseNo.Contains(keyword)));
        }

        long total = await source.LongCountAsync(cancellationToken);
        var rows = await source
            .OrderByDescending(static x => x.CreatedAt)
            .Skip(query.Skip)
            .Take(query.ClampedPageSize)
            .Include(static x => x.Product)
            .ToListAsync(cancellationToken);

        var nodes = await LoadNodesAsync(rows.Select(static x => x.MachineCode), cancellationToken);
        return PageResult<LicenseEntitlementDto>.Create(
            [.. rows.Select(row => ToDto(row, now, nodes.GetValueOrDefault(row.MachineCode)))],
            total,
            query);
    }

    /// <inheritdoc />
    public async Task<LicenseEntitlementDto> GetAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var entitlement = await _db.Set<LicenseEntitlement>()
                              .AsNoTracking()
                              .Include(static x => x.Product)
                              .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                          ?? throw BusinessException.NotFound(
                              ApiErrorCode.LicensingEntitlementNotFound,
                              $"授权 {id} 不存在。");

        return ToDto(entitlement, _clock.UtcNow, await FindNodeAsync(entitlement.MachineCode, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<LicenseEntitlementDto> IssueAsync(
        string appKey,
        IssueLicenseRequest request,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        LicenseProduct product = await GetProductAsync(appKey, request.ProductKey, cancellationToken);
        string machineCode = await ResolveMachineCodeAsync(request.NodeId, request.MachineCode, cancellationToken);

        DateTimeOffset notAfter = ResolveNotAfter(now, request.NotAfter, request.AddDays);
        int graceDays = NormalizeGraceDays(request.GraceDays ?? product.DefaultGraceDays);

        var existing = await _db.Set<LicenseEntitlement>()
            .FirstOrDefaultAsync(
                x => x.ProductId == product.Id && x.MachineCode == machineCode,
                cancellationToken);

        // 自动试用可被同机器同产品的正式授权就地升级：沿用原行，不新增席位，也不必先吊销。
        if (existing is not null
            && existing.RevokedAt is null
            && !string.Equals(existing.Kind, LicenseConventions.KindTrial, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.Conflict,
                "该机器码已有有效授权，请使用续期。");
        }

        if (existing is null)
        {
            int active = await _db.Set<LicenseEntitlement>()
                .CountAsync(
                    x => x.ProductId == product.Id && x.RevokedAt == null && x.ExpiresAt >= now,
                    cancellationToken);
            if (product.SeatLimit is { } limit && active >= limit)
            {
                throw new BusinessException(
                    ApiErrorCode.LicensingSeatExhausted,
                    $"产品 {product.Key} 的席位已用尽（{active}/{limit}）。");
            }
        }

        var entitlement = existing ?? new LicenseEntitlement
        {
            AppKey = appKey,
            ProductId = product.Id,
            MachineCode = machineCode,
        };

        entitlement.Kind = LicenseConventions.KindOfficial;
        entitlement.NotBefore = now;
        entitlement.NotAfter = notAfter;
        entitlement.GraceDays = graceDays;
        entitlement.ExpiresAt = notAfter.AddDays(graceDays);
        entitlement.LicenseNo = NormalizeLicenseNo(request.LicenseNo, now, machineCode);
        entitlement.Notes = NormalizeText(request.Notes, 1024);
        entitlement.IssuedBy = actor;
        entitlement.RevokedAt = null;
        entitlement.RevokedReason = null;
        entitlement.RevokeScope = null;

        if (existing is null)
        {
            _db.Set<LicenseEntitlement>().Add(entitlement);
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _db.ChangeTracker.Clear();
            throw new BusinessException(
                ApiErrorCode.Conflict,
                "该机器码的授权已被并发签发，请刷新后重试。",
                exception);
        }

        await NotifyAsync(appKey, machineCode, LicenseChangedNotification.ReasonIssued);
        entitlement.Product = product;
        return ToDto(entitlement, now, await FindNodeAsync(machineCode, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<LicenseEntitlementDto> RenewAsync(
        string appKey,
        Guid id,
        RenewLicenseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        var entitlement = await _db.Set<LicenseEntitlement>()
                              .Include(static x => x.Product)
                              .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                          ?? throw BusinessException.NotFound(
                              ApiErrorCode.LicensingEntitlementNotFound,
                              $"授权 {id} 不存在。");

        if (entitlement.RevokedAt is not null)
        {
            throw new BusinessException(
                ApiErrorCode.LicensingEntitlementRevoked,
                "授权已吊销，不能续期。");
        }

        if (request.NotAfter is null && request.AddDays is null)
        {
            throw BusinessException.Invalid("续期必须提供 NotAfter 或 AddDays。");
        }

        DateTimeOffset notAfter;
        if (request.NotAfter is { } explicitDate)
        {
            notAfter = explicitDate.ToUniversalTime();
        }
        else
        {
            int days = request.AddDays!.Value;
            if (days is < 1 or > LicenseConventions.MaxLicenseDays)
            {
                throw BusinessException.Invalid($"续期天数必须在 1–{LicenseConventions.MaxLicenseDays} 之间。");
            }

            DateTimeOffset baseline = entitlement.NotAfter > now ? entitlement.NotAfter : now;
            notAfter = baseline.AddDays(days);
        }

        if (notAfter <= now)
        {
            throw BusinessException.Invalid("新的到期时间必须晚于当前时间。");
        }

        int graceDays = NormalizeGraceDays(request.GraceDays ?? entitlement.GraceDays);
        entitlement.NotAfter = notAfter;
        entitlement.GraceDays = graceDays;
        entitlement.ExpiresAt = notAfter.AddDays(graceDays);
        if (request.Notes is not null)
        {
            entitlement.Notes = NormalizeText(request.Notes, 1024);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await NotifyAsync(appKey, entitlement.MachineCode, LicenseChangedNotification.ReasonRenewed);
        return ToDto(entitlement, now, await FindNodeAsync(entitlement.MachineCode, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<LicenseEntitlementDto> RevokeAsync(
        string appKey,
        Guid id,
        RevokeLicenseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        var entitlement = await _db.Set<LicenseEntitlement>()
                              .Include(static x => x.Product)
                              .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                          ?? throw BusinessException.NotFound(
                              ApiErrorCode.LicensingEntitlementNotFound,
                              $"授权 {id} 不存在。");

        string requestedScope = NormalizeRevokeScope(request.Scope);
        bool changed = false;

        if (entitlement.RevokedAt is null)
        {
            entitlement.RevokedAt = now;
            entitlement.RevokeScope = requestedScope;
            entitlement.RevokedReason = NormalizeText(request.Reason, 512);
            changed = true;
        }
        else
        {
            // 历史行没有 scope，按完全吊销处理；「仅在线」可升级为完全吊销，反向不降级。
            string currentScope = entitlement.RevokeScope ?? LicenseConventions.RevokeScopeAll;
            if (!string.Equals(currentScope, requestedScope, StringComparison.Ordinal)
                && requestedScope == LicenseConventions.RevokeScopeAll)
            {
                entitlement.RevokeScope = LicenseConventions.RevokeScopeAll;
                if (!string.IsNullOrWhiteSpace(request.Reason))
                {
                    entitlement.RevokedReason = NormalizeText(request.Reason, 512);
                }

                changed = true;
            }
        }

        if (changed)
        {
            await _db.SaveChangesAsync(cancellationToken);
            await NotifyAsync(appKey, entitlement.MachineCode, LicenseChangedNotification.ReasonRevoked);
            await _events.PublishAsync(
                new PlatformEvent(
                    WebhookConventions.EventLicenseRevoked,
                    appKey,
                    new
                    {
                        entitlementId = entitlement.Id,
                        productKey = entitlement.Product.Key,
                        machineCode = entitlement.MachineCode,
                        scope = entitlement.RevokeScope ?? LicenseConventions.RevokeScopeAll,
                        revokedAt = entitlement.RevokedAt,
                        reason = entitlement.RevokedReason,
                    },
                    now),
                cancellationToken);
        }

        return ToDto(entitlement, now, await FindNodeAsync(entitlement.MachineCode, cancellationToken));
    }

    private async Task<string> ResolveMachineCodeAsync(
        Guid? nodeId,
        string? machineCode,
        CancellationToken cancellationToken)
    {
        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(machineCode))
        {
            normalized = NormalizeMachineCode(machineCode);
        }

        if (nodeId is not { } id || id == Guid.Empty)
        {
            return normalized ?? throw new BusinessException(
                ApiErrorCode.LicensingMachineCodeMissing,
                "必须提供 NodeId 或 MachineCode。");
        }

        DeviceNodeDirectoryEntry node = await FindNodeByIdAsync(id, cancellationToken);
        if (normalized is not null && !string.Equals(normalized, node.MachineCode, StringComparison.Ordinal))
        {
            throw BusinessException.Invalid("NodeId 与 MachineCode 指向不同的机器。");
        }

        return node.MachineCode;
    }

    private async Task<DeviceNodeDirectoryEntry> FindNodeByIdAsync(
        Guid nodeId,
        CancellationToken cancellationToken)
    {
        if (_nodes is null)
        {
            throw new BusinessException(
                ApiErrorCode.DependencyFailure,
                "设备域未启用，无法按 NodeId 签发授权；请直接提供 MachineCode。");
        }

        return await _nodes.FindAsync(nodeId, cancellationToken)
               ?? throw BusinessException.NotFound(ApiErrorCode.DevicesNodeNotFound, $"节点 {nodeId} 不存在。");
    }

    private async Task<DeviceNodeDirectoryEntry?> FindNodeAsync(
        string machineCode,
        CancellationToken cancellationToken) =>
        _nodes is null ? null : await _nodes.FindByMachineCodeAsync(machineCode, cancellationToken);

    private async Task<IReadOnlyDictionary<string, DeviceNodeDirectoryEntry>> LoadNodesAsync(
        IEnumerable<string> machineCodes,
        CancellationToken cancellationToken)
    {
        if (_nodes is null)
        {
            return EmptyNodes;
        }

        string[] codes = machineCodes
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return codes.Length == 0
            ? EmptyNodes
            : await _nodes.FindByMachineCodesAsync(codes, cancellationToken);
    }

    private static readonly IReadOnlyDictionary<string, DeviceNodeDirectoryEntry> EmptyNodes =
        new Dictionary<string, DeviceNodeDirectoryEntry>(StringComparer.Ordinal);

    private async Task<LicenseProduct> GetProductAsync(
        string appKey,
        string? productKey,
        CancellationToken cancellationToken)
    {
        string key = productKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            throw BusinessException.Invalid("产品键不能为空。");
        }

        var product = await _db.Set<LicenseProduct>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AppKey == appKey && x.Key == key, cancellationToken)
            ?? throw BusinessException.NotFound(
                ApiErrorCode.LicensingProductNotFound,
                $"产品 {key} 不存在。");

        if (!product.IsEnabled)
        {
            throw BusinessException.Invalid($"产品 {key} 已停用，不能签发授权。");
        }

        return product;
    }

    private DateTimeOffset ResolveNotAfter(DateTimeOffset now, DateTimeOffset? explicitDate, int? addDays)
    {
        DateTimeOffset notAfter;
        if (explicitDate is { } value)
        {
            notAfter = value.ToUniversalTime();
        }
        else
        {
            int days = addDays ?? LicenseConventions.DefaultLicenseDays;
            int max = Math.Clamp(_options.MaxLicenseDays, 1, LicenseConventions.MaxLicenseDays);
            if (days is < 1 || days > max)
            {
                throw BusinessException.Invalid($"授权天数必须在 1–{max} 之间。");
            }

            notAfter = now.AddDays(days);
        }

        if (notAfter <= now)
        {
            throw BusinessException.Invalid("到期时间必须晚于当前时间。");
        }

        return notAfter;
    }

    private static string NormalizeRevokeScope(string? scope)
    {
        string value = scope?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.Length == 0)
        {
            return LicenseConventions.RevokeScopeAll;
        }

        if (!LicenseConventions.IsValidRevokeScope(value))
        {
            throw BusinessException.Invalid("scope 只能是 all / online。");
        }

        return value;
    }

    private static int NormalizeGraceDays(int value)
    {
        if (value is < 0 or > LicenseConventions.MaxGraceDays)
        {
            throw BusinessException.Invalid($"宽限天数必须在 0–{LicenseConventions.MaxGraceDays} 之间。");
        }

        return value;
    }

    private static string NormalizeMachineCode(string? machineCode)
    {
        if (!LicenseConventions.TryNormalizeMachineCode(machineCode, out string normalized))
        {
            throw new BusinessException(
                ApiErrorCode.LicensingMachineCodeMissing,
                $"机器码不合法（应为 {LicenseConventions.MachineCodeChars} 字符 Crockford Base32，请在客户端「软件授权」中复制）。");
        }

        return normalized;
    }

    private static string NormalizeLicenseNo(string? licenseNo, DateTimeOffset now, string machineCode)
    {
        if (string.IsNullOrWhiteSpace(licenseNo))
        {
            return LicenseNumbers.Generate(now, machineCode);
        }

        string trimmed = licenseNo.Trim();
        if (trimmed.Length > LicenseConventions.MaxLicenseNoChars)
        {
            throw BusinessException.Invalid(
                $"授权编号不能超过 {LicenseConventions.MaxLicenseNoChars} 个字符。");
        }

        if (trimmed.Any(static c => c is < ' ' or > '~'))
        {
            throw BusinessException.Invalid("授权编号只能包含可打印 ASCII 字符。");
        }

        return trimmed;
    }

    private static string? NormalizeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw BusinessException.Invalid($"文本长度不能超过 {maxLength} 个字符。");
        }

        return trimmed;
    }

    private async Task NotifyAsync(string appKey, string machineCode, string reason)
    {
        try
        {
            await _hub.Clients.All.SendAsync(
                PlatformHubEvents.LicenseChanged,
                new LicenseChangedNotification(appKey, machineCode, reason),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            // 推送只是提示；失败不能影响已经落库的授权结果。
            NotifyFailedLog(_logger, appKey, exception);
        }
    }

    private static LicenseEntitlementDto ToDto(
        LicenseEntitlement entitlement,
        DateTimeOffset now,
        DeviceNodeDirectoryEntry? node) =>
        new(
            entitlement.Id,
            entitlement.Product?.Key ?? string.Empty,
            entitlement.MachineCode,
            entitlement.Kind,
            LicenseStatusResolver.Resolve(entitlement, now),
            entitlement.NotBefore,
            entitlement.NotAfter,
            entitlement.ExpiresAt,
            entitlement.GraceDays,
            entitlement.LicenseNo,
            entitlement.Notes,
            entitlement.IssuedBy,
            entitlement.RevokedAt,
            entitlement.RevokedReason,
            entitlement.LastSeenAt,
            entitlement.CreatedAt,
            entitlement.UpdatedAt,
            entitlement.RevokedAt is null
                ? null
                : entitlement.RevokeScope ?? LicenseConventions.RevokeScopeAll,
            node?.NodeId,
            node?.DisplayName);
}
