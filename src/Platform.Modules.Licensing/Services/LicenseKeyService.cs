using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Licensing;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Licensing.Domain;
using Platform.Modules.Licensing.Options;

namespace Platform.Modules.Licensing.Services;

/// <summary>签名密钥登记与轮换。</summary>
public interface ILicenseKeyService
{
    /// <summary>是否已配置可用的签发私钥。</summary>
    bool IsManaged { get; }

    /// <summary>
    /// 把部署配置里的私钥同步到密钥登记表：新密钥登记为 active，其它 active 全部退役。
    /// 未配置私钥返回 null。
    /// </summary>
    Task<LicenseSigningKey?> EnsureCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>全部受信任公钥（active + retired），供快照/离线码验签。</summary>
    Task<IReadOnlyDictionary<string, string>> GetTrustedKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>下发给客户端的可验签公钥集合。</summary>
    Task<IReadOnlyList<LicenseKeyDescriptorDto>> DescribeTrustedAsync(CancellationToken cancellationToken = default);

    /// <summary>管理端密钥总览。</summary>
    Task<LicenseSigningKeyStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>激活指定密钥（必须与当前配置私钥匹配）。</summary>
    Task<LicenseSigningKeyStatusDto> ActivateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>退役指定密钥（继续参与验签，不能再签发）。</summary>
    Task<LicenseSigningKeyStatusDto> RetireAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>签名密钥实现。私钥只在内存中从配置导入，绝不写入数据库。</summary>
public sealed class LicenseKeyService : ILicenseKeyService
{
    private static readonly Action<ILogger, Exception?> InvalidKeyLog = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1, "LicenseSigningKeyInvalid"),
        "Licensing:SigningKeyPkcs8 已配置但不是合法的 ECDSA PKCS#8 私钥，授权按 Unmanaged 处理。");

    private readonly PlatformDbContext _db;
    private readonly LicensingOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<LicenseKeyService> _logger;

    /// <summary>构造。</summary>
    public LicenseKeyService(
        PlatformDbContext db,
        IOptions<LicensingOptions> options,
        IClock clock,
        ILogger<LicenseKeyService> logger)
    {
        _db = db;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsManaged => TryGetConfiguredKey(out _);

    /// <inheritdoc />
    public async Task<LicenseSigningKey?> EnsureCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!TryGetConfiguredKey(out ConfiguredKey configured))
        {
            return null;
        }

        var now = _clock.UtcNow;
        var keys = await _db.Set<LicenseSigningKey>().ToListAsync(cancellationToken);
        LicenseSigningKey? current = keys.FirstOrDefault(k => k.KeyId == configured.KeyId);
        bool changed = false;

        if (current is null)
        {
            current = new LicenseSigningKey
            {
                KeyId = configured.KeyId,
                Algorithm = LicenseConventions.SignatureAlgorithm,
                PublicKey = configured.PublicKey,
                Fingerprint = configured.Fingerprint,
                Status = LicenseConventions.SigningKeyActive,
                ActivatedAt = now,
            };
            _db.Set<LicenseSigningKey>().Add(current);
            keys.Add(current);
            changed = true;
        }
        else if (current.Status != LicenseConventions.SigningKeyActive || current.RetiredAt is not null)
        {
            current.Status = LicenseConventions.SigningKeyActive;
            current.ActivatedAt = now;
            current.RetiredAt = null;
            changed = true;
        }

        foreach (LicenseSigningKey other in keys.Where(k =>
                     k != current && k.Status == LicenseConventions.SigningKeyActive))
        {
            other.Status = LicenseConventions.SigningKeyRetired;
            other.RetiredAt = now;
            changed = true;
        }

        if (!changed)
        {
            return current;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return current;
        }
        catch (DbUpdateException)
        {
            // 并发请求可能同时登记同一把密钥；回读以库里的行为准。
            _db.ChangeTracker.Clear();
            return await _db.Set<LicenseSigningKey>()
                .FirstOrDefaultAsync(k => k.KeyId == configured.KeyId, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetTrustedKeysAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentAsync(cancellationToken);

        var rows = await _db.Set<LicenseSigningKey>()
            .AsNoTracking()
            .Select(static k => new { k.KeyId, k.PublicKey })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(static x => x.KeyId, static x => x.PublicKey, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LicenseKeyDescriptorDto>> DescribeTrustedAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentAsync(cancellationToken);

        var rows = await _db.Set<LicenseSigningKey>()
            .AsNoTracking()
            .OrderByDescending(static k => k.Status == LicenseConventions.SigningKeyActive)
            .ThenByDescending(static k => k.ActivatedAt)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(static k => new LicenseKeyDescriptorDto(
            k.KeyId,
            k.Algorithm,
            k.PublicKey,
            k.Fingerprint,
            k.Status))];
    }

    /// <inheritdoc />
    public async Task<LicenseSigningKeyStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        LicenseSigningKey? current = await EnsureCurrentAsync(cancellationToken);
        var rows = await _db.Set<LicenseSigningKey>()
            .AsNoTracking()
            .OrderByDescending(static k => k.Status == LicenseConventions.SigningKeyActive)
            .ThenByDescending(static k => k.ActivatedAt)
            .ToListAsync(cancellationToken);

        return new LicenseSigningKeyStatusDto(
            IsManaged,
            current?.KeyId,
            current?.Fingerprint,
            [.. rows.Select(k => ToDto(k, current))]);
    }

    /// <inheritdoc />
    public async Task<LicenseSigningKeyStatusDto> ActivateAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetConfiguredKey(out ConfiguredKey configured))
        {
            throw new BusinessException(
                ApiErrorCode.LicensingSigningKeyUnavailable,
                "本部署未配置签发私钥（Licensing:SigningKeyPkcs8），无法激活签名密钥。");
        }

        var key = await _db.Set<LicenseSigningKey>()
                      .FirstOrDefaultAsync(k => k.Id == id, cancellationToken)
                  ?? throw new BusinessException(
                      ApiErrorCode.LicensingSigningKeyUnavailable,
                      "签名密钥不存在。");

        if (!string.Equals(key.PublicKey, configured.PublicKey, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.LicensingSigningKeyUnavailable,
                "该公钥与部署配置的私钥不匹配；请先把对应的私钥配置到 Licensing:SigningKeyPkcs8。");
        }

        var now = _clock.UtcNow;
        var active = await _db.Set<LicenseSigningKey>()
            .Where(k => k.Status == LicenseConventions.SigningKeyActive && k.Id != id)
            .ToListAsync(cancellationToken);
        foreach (LicenseSigningKey other in active)
        {
            other.Status = LicenseConventions.SigningKeyRetired;
            other.RetiredAt = now;
        }

        key.Status = LicenseConventions.SigningKeyActive;
        key.ActivatedAt ??= now;
        key.RetiredAt = null;
        await _db.SaveChangesAsync(cancellationToken);

        return await GetStatusAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<LicenseSigningKeyStatusDto> RetireAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var key = await _db.Set<LicenseSigningKey>()
                      .FirstOrDefaultAsync(k => k.Id == id, cancellationToken)
                  ?? throw new BusinessException(
                      ApiErrorCode.LicensingSigningKeyUnavailable,
                      "签名密钥不存在。");

        if (TryGetConfiguredKey(out ConfiguredKey configured)
            && string.Equals(key.KeyId, configured.KeyId, StringComparison.Ordinal))
        {
            throw new BusinessException(
                ApiErrorCode.LicensingSigningKeyUnavailable,
                "当前配置私钥对应的密钥不能退役；请先切换 Licensing:SigningKeyPkcs8 再退役旧密钥。");
        }

        if (key.Status != LicenseConventions.SigningKeyRetired)
        {
            key.Status = LicenseConventions.SigningKeyRetired;
            key.RetiredAt = _clock.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return await GetStatusAsync(cancellationToken);
    }

    private bool TryGetConfiguredKey(out ConfiguredKey configured)
    {
        configured = default;
        string? publicKey = LicenseCryptography.TryExportPublicKey(_options.SigningKeyPkcs8);
        if (publicKey is null)
        {
            if (!string.IsNullOrWhiteSpace(_options.SigningKeyPkcs8))
            {
                InvalidKeyLog(_logger, null);
            }

            return false;
        }

        string fingerprint = LicenseCryptography.ComputeFingerprint(publicKey);
        configured = new ConfiguredKey(
            LicenseCryptography.ComputeKeyId(publicKey),
            publicKey,
            fingerprint);
        return true;
    }

    private static LicenseSigningKeyDto ToDto(LicenseSigningKey key, LicenseSigningKey? current) =>
        new(
            key.Id,
            key.KeyId,
            key.Algorithm,
            key.PublicKey,
            key.Fingerprint,
            key.Status,
            key.KeyId == current?.KeyId,
            key.ActivatedAt,
            key.RetiredAt,
            key.CreatedAt);

    private readonly record struct ConfiguredKey(string KeyId, string PublicKey, string Fingerprint);
}
