using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Licensing;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Licensing.Domain;
using Platform.Modules.Licensing.Options;

namespace Platform.Modules.Licensing.Services;

/// <summary>客户端在线校验授权。</summary>
public interface ILicenseStatusService
{
    /// <summary>
    /// 返回授权状态与签名快照。产品开启试用且该机器尚无授权时自动签发试用；
    /// 未配置签名私钥时一律返回 <c>unmanaged</c>。
    /// </summary>
    Task<LicenseStatusResponse> GetStatusAsync(
        string appKey,
        LicenseStatusRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>客户端在线校验实现。</summary>
public sealed class LicenseStatusService : ILicenseStatusService
{
    private readonly PlatformDbContext _db;
    private readonly ILicenseKeyService _keys;
    private readonly IClock _clock;
    private readonly LicensingOptions _options;
    private readonly IDeviceNodeDirectory? _nodes;

    /// <summary>构造。</summary>
    public LicenseStatusService(
        PlatformDbContext db,
        ILicenseKeyService keys,
        IClock clock,
        IOptions<LicensingOptions> options,
        IEnumerable<IDeviceNodeDirectory> nodes)
    {
        _db = db;
        _keys = keys;
        _clock = clock;
        _options = options.Value;
        _nodes = nodes.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<LicenseStatusResponse> GetStatusAsync(
        string appKey,
        LicenseStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string productKey = request.ProductKey?.Trim() ?? string.Empty;
        if (productKey.Length == 0)
        {
            throw BusinessException.Invalid("产品键不能为空。");
        }

        if (!LicenseConventions.TryNormalizeMachineCode(request.MachineCode, out string machineCode))
        {
            throw new BusinessException(
                ApiErrorCode.LicensingMachineCodeMissing,
                $"机器码不合法（应为 {LicenseConventions.MachineCodeChars} 字符 Crockford Base32，请在客户端「软件授权」中复制）。");
        }

        var now = _clock.UtcNow;
        var product = await _db.Set<LicenseProduct>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AppKey == appKey && x.Key == productKey, cancellationToken)
            ?? throw BusinessException.NotFound(
                ApiErrorCode.LicensingProductNotFound,
                $"产品 {productKey} 不存在。");

        if (!product.IsEnabled)
        {
            throw BusinessException.Invalid($"产品 {productKey} 已停用。");
        }

        var entitlement = await _db.Set<LicenseEntitlement>()
            .FirstOrDefaultAsync(
                x => x.ProductId == product.Id && x.MachineCode == machineCode,
                cancellationToken);

        if (entitlement is null && product.TrialDays > 0)
        {
            entitlement = await TryIssueTrialAsync(appKey, product, machineCode, now, cancellationToken);
        }

        if (entitlement is not null)
        {
            entitlement.LastSeenAt = now;
            await _db.SaveChangesAsync(cancellationToken);
        }

        DeviceNodeDirectoryEntry? node = _nodes is null
            ? null
            : await _nodes.FindByMachineCodeAsync(machineCode, cancellationToken);

        if (!_keys.IsManaged)
        {
            return new LicenseStatusResponse(
                appKey,
                productKey,
                machineCode,
                LicenseConventions.StatusUnmanaged,
                Kind: null,
                NotBefore: null,
                NotAfter: null,
                GraceDays: 0,
                now,
                LicenseNo: null,
                KeyId: null,
                Snapshot: null,
                TrustedKeys: [],
                node?.NodeId);
        }

        LicenseSigningKey? current = await _keys.EnsureCurrentAsync(cancellationToken);
        IReadOnlyList<LicenseKeyDescriptorDto> trusted = await _keys.DescribeTrustedAsync(cancellationToken);

        if (entitlement is null || LicenseStatusResolver.IsOnlineOnlyRevoked(entitlement))
        {
            // 仅注销在线授权时按未授权下发：老客户端会清掉在线快照，已签发的离线码继续生效。
            return new LicenseStatusResponse(
                appKey,
                productKey,
                machineCode,
                LicenseConventions.StatusUnlicensed,
                Kind: null,
                NotBefore: null,
                NotAfter: null,
                GraceDays: 0,
                now,
                LicenseNo: null,
                KeyId: null,
                Snapshot: null,
                trusted,
                node?.NodeId);
        }

        string status = LicenseStatusResolver.Resolve(entitlement, now);
        string snapshot = string.Empty;
        if (current is not null && _options.SigningKeyPkcs8 is { Length: > 0 } privateKey)
        {
            snapshot = LicenseSnapshotCodec.Sign(
                new LicenseSnapshotPayload(
                    appKey,
                    productKey,
                    machineCode,
                    DeviceCode: null,
                    entitlement.Kind,
                    status,
                    entitlement.NotBefore,
                    entitlement.NotAfter,
                    entitlement.GraceDays,
                    now,
                    entitlement.LicenseNo,
                    current.KeyId),
                privateKey);
        }

        return new LicenseStatusResponse(
            appKey,
            productKey,
            machineCode,
            status,
            entitlement.Kind,
            entitlement.NotBefore,
            entitlement.NotAfter,
            entitlement.GraceDays,
            now,
            entitlement.LicenseNo,
            current?.KeyId,
            snapshot.Length == 0 ? null : snapshot,
            trusted,
            node?.NodeId);
    }

    private async Task<LicenseEntitlement?> TryIssueTrialAsync(
        string appKey,
        LicenseProduct product,
        string machineCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        int active = await _db.Set<LicenseEntitlement>()
            .CountAsync(
                x => x.ProductId == product.Id && x.RevokedAt == null && x.ExpiresAt >= now,
                cancellationToken);
        if (product.SeatLimit is { } limit && active >= limit)
        {
            return null;
        }

        DateTimeOffset notAfter = now.AddDays(product.TrialDays);
        var trial = new LicenseEntitlement
        {
            AppKey = appKey,
            ProductId = product.Id,
            MachineCode = machineCode,
            Kind = LicenseConventions.KindTrial,
            NotBefore = now,
            NotAfter = notAfter,
            GraceDays = product.DefaultGraceDays,
            ExpiresAt = notAfter.AddDays(product.DefaultGraceDays),
            LicenseNo = LicenseNumbers.Generate(now, machineCode),
            Notes = "首次联系自动发放的试用授权",
            IssuedBy = "system",
            LastSeenAt = now,
        };

        _db.Set<LicenseEntitlement>().Add(trial);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return trial;
        }
        catch (DbUpdateException)
        {
            // 同一机器的并发首次校验：另一个请求已发出试用，回读即可。
            _db.ChangeTracker.Clear();
            return await _db.Set<LicenseEntitlement>()
                .FirstOrDefaultAsync(
                    x => x.ProductId == product.Id && x.MachineCode == machineCode,
                    cancellationToken);
        }
    }
}
