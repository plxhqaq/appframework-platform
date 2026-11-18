using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Identity.Domain;
using Platform.Modules.Identity.Options;

namespace Platform.Modules.Identity.Services;

/// <summary>单次密钥签发与兑换。</summary>
public interface ISingleLoginKeyService
{
    /// <summary>签发 6 位一次性密钥；明文只在返回值中出现一次。</summary>
    Task<SingleLoginKeyDto> IssueAsync(
        Guid createdBy,
        string? principalAppKey,
        IssueSingleLoginKeyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>兑换密钥并签发临时访问令牌。并发兑换只有一个请求成功。</summary>
    Task<TemporarySessionResponse> RedeemAsync(
        RedeemSingleLoginKeyRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 单次密钥实现。兑换的竞态由一条带条件的 UPDATE 决定胜负：
/// <c>WHERE key = ... AND used_at IS NULL AND expires_at &gt; now AND attempt_count &lt; max</c>，
/// 受影响行数为 1 的请求才是赢家。InMemory（测试用）不支持该语句，退回进程内互斥 + 重查。
/// </summary>
public sealed class SingleLoginKeyService : ISingleLoginKeyService
{
    private const string InvalidKeyMessage = "单次密钥无效。";
    private const string ExpiredKeyMessage = "单次密钥已过期。";
    private const string ExhaustedKeyMessage = "单次密钥已使用或尝试次数过多。";
    private const string DeviceMismatchMessage = "单次密钥与当前设备不匹配。";

    private static readonly SemaphoreSlim InMemoryGate = new(1, 1);

    private readonly PlatformDbContext _db;
    private readonly ITemporarySessionFactory _temporary;
    private readonly IAppDirectory _apps;
    private readonly IClock _clock;
    private readonly SingleLoginOptions _options;

    /// <summary>构造。</summary>
    public SingleLoginKeyService(
        PlatformDbContext db,
        ITemporarySessionFactory temporary,
        IAppDirectory apps,
        IClock clock,
        IOptions<SingleLoginOptions> options)
    {
        _db = db;
        _temporary = temporary;
        _apps = apps;
        _clock = clock;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<SingleLoginKeyDto> IssueAsync(
        Guid createdBy,
        string? principalAppKey,
        IssueSingleLoginKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string appKey = await ResolveAppKeyAsync(principalAppKey, request.AppKey, cancellationToken);

        var now = _clock.UtcNow;
        var duration = ClampDuration(request.DurationMinutes);
        var fingerprint = Normalize(request.DeviceFingerprint);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var keyText = RandomNumberGenerator.GetInt32(0, 1_000_000)
                .ToString("D6", CultureInfo.InvariantCulture);

            if (await _db.Set<SingleLoginKey>().AnyAsync(x => x.Key == keyText, cancellationToken))
            {
                continue;
            }

            var entity = new SingleLoginKey
            {
                Key = keyText,
                AppKey = appKey,
                DeviceFingerprint = fingerprint,
                CreatedBy = createdBy,
                ExpiresAt = now.AddMinutes(duration),
            };

            _db.Set<SingleLoginKey>().Add(entity);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return new SingleLoginKeyDto(keyText, entity.ExpiresAt, duration);
            }
            catch (DbUpdateException) when (attempt < 4)
            {
                // 6 位空间极小概率碰撞；从跟踪器摘掉后换一个密钥重试。
                _db.Entry(entity).State = EntityState.Detached;
            }
        }

        throw new BusinessException(ApiErrorCode.Conflict, "生成单次密钥失败，请重试。");
    }

    /// <inheritdoc />
    public async Task<TemporarySessionResponse> RedeemAsync(
        RedeemSingleLoginKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var keyText = NormalizeKey(request.Key);
        var fingerprint = Normalize(request.DeviceFingerprint);
        var now = _clock.UtcNow;
        var maxAttempts = Math.Max(1, _options.MaxAttempts);

        var record = await _db.Set<SingleLoginKey>()
                         .AsNoTracking()
                         .FirstOrDefaultAsync(x => x.Key == keyText, cancellationToken)
                     ?? throw new BusinessException(ApiErrorCode.IdentitySingleKeyInvalid, InvalidKeyMessage);

        ThrowIfUnusable(record, now, maxAttempts);

        if (record.DeviceFingerprint is not null
            && !string.Equals(record.DeviceFingerprint, fingerprint, StringComparison.Ordinal))
        {
            await IncrementAttemptsAsync(keyText, now, cancellationToken);
            throw new BusinessException(ApiErrorCode.IdentitySingleKeyDeviceMismatch, DeviceMismatchMessage);
        }

        if (_db.Database.IsRelational())
        {
            var affected = await _db.Set<SingleLoginKey>()
                .Where(x => x.Key == keyText
                            && x.UsedAt == null
                            && x.ExpiresAt > now
                            && x.AttemptCount < maxAttempts
                            && (x.DeviceFingerprint == null || x.DeviceFingerprint == fingerprint))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.UsedAt, now)
                        .SetProperty(static x => x.UpdatedAt, now)
                        .SetProperty(static x => x.AttemptCount, static x => x.AttemptCount + 1),
                    cancellationToken);

            if (affected != 1)
            {
                var current = await _db.Set<SingleLoginKey>()
                                  .AsNoTracking()
                                  .FirstOrDefaultAsync(x => x.Key == keyText, cancellationToken)
                              ?? throw new BusinessException(ApiErrorCode.IdentitySingleKeyInvalid, InvalidKeyMessage);
                ThrowIfUnusable(current, now, maxAttempts);
                throw new BusinessException(ApiErrorCode.IdentitySingleKeyDeviceMismatch, DeviceMismatchMessage);
            }
        }
        else
        {
            await RedeemWithGateAsync(keyText, fingerprint, now, maxAttempts, cancellationToken);
        }

        var lifetime = record.ExpiresAt - now;
        if (lifetime <= TimeSpan.Zero)
        {
            throw new BusinessException(ApiErrorCode.IdentitySingleKeyExpired, ExpiredKeyMessage);
        }

        // 临时会话绑定密钥签发人与密钥的目标应用：客户端拿到的是该用户在该应用下的角色与权限。
        var session = await _temporary.IssueAsync(
            record.CreatedBy,
            record.AppKey,
            lifetime,
            Normalize(request.MachineCode),
            request.InstallGuid,
            cancellationToken);

        return new TemporarySessionResponse(session.AccessToken, session.ExpiresAt, session.User);
    }

    private async Task RedeemWithGateAsync(
        string keyText,
        string? fingerprint,
        DateTimeOffset now,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        await InMemoryGate.WaitAsync(cancellationToken);
        try
        {
            var record = await _db.Set<SingleLoginKey>()
                             .FirstOrDefaultAsync(x => x.Key == keyText, cancellationToken)
                         ?? throw new BusinessException(ApiErrorCode.IdentitySingleKeyInvalid, InvalidKeyMessage);

            ThrowIfUnusable(record, now, maxAttempts);

            if (record.DeviceFingerprint is not null
                && !string.Equals(record.DeviceFingerprint, fingerprint, StringComparison.Ordinal))
            {
                record.AttemptCount++;
                await _db.SaveChangesAsync(cancellationToken);
                throw new BusinessException(ApiErrorCode.IdentitySingleKeyDeviceMismatch, DeviceMismatchMessage);
            }

            record.UsedAt = now;
            record.AttemptCount++;
            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            InMemoryGate.Release();
        }
    }

    private async Task IncrementAttemptsAsync(
        string keyText,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_db.Database.IsRelational())
        {
            await _db.Set<SingleLoginKey>()
                .Where(x => x.Key == keyText)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.UpdatedAt, now)
                        .SetProperty(static x => x.AttemptCount, static x => x.AttemptCount + 1),
                    cancellationToken);
            return;
        }

        var record = await _db.Set<SingleLoginKey>()
            .FirstOrDefaultAsync(x => x.Key == keyText, cancellationToken);
        if (record is null)
        {
            return;
        }

        record.AttemptCount++;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static void ThrowIfUnusable(SingleLoginKey record, DateTimeOffset now, int maxAttempts)
    {
        if (record.UsedAt is not null || record.AttemptCount >= maxAttempts)
        {
            throw new BusinessException(ApiErrorCode.IdentitySingleKeyExhausted, ExhaustedKeyMessage);
        }

        if (record.ExpiresAt <= now)
        {
            throw new BusinessException(ApiErrorCode.IdentitySingleKeyExpired, ExpiredKeyMessage);
        }
    }

    private async Task<string> ResolveAppKeyAsync(
        string? principalAppKey,
        string? requestedAppKey,
        CancellationToken cancellationToken)
    {
        string? principal = Normalize(principalAppKey);
        string? requested = Normalize(requestedAppKey);

        if (requested is null && principal is null)
        {
            throw BusinessException.Invalid("必须指定应用（AppKey）：平台上下文签发单次密钥时需要显式给出目标应用。");
        }

        if (requested is not null && principal is not null
            && !string.Equals(requested, principal, StringComparison.Ordinal))
        {
            throw BusinessException.Invalid("只能在当前应用下签发单次密钥。");
        }

        string appKey = requested ?? principal!;
        if (!await _apps.IsEnabledAsync(appKey, cancellationToken))
        {
            throw BusinessException.Invalid($"应用 {appKey} 不存在或已停用，不能签发单次密钥。");
        }

        return appKey;
    }

    private int ClampDuration(int? requestedMinutes)
    {
        var fallback = Math.Max(1, _options.DefaultDurationMinutes);
        var max = Math.Max(fallback, _options.MaxDurationMinutes);
        return Math.Clamp(requestedMinutes ?? fallback, 1, max);
    }

    private static string NormalizeKey(string? key)
    {
        var value = key?.Trim() ?? string.Empty;
        if (value.Length != SingleLoginConventions.KeyLength || !value.All(char.IsAsciiDigit))
        {
            throw new BusinessException(ApiErrorCode.IdentitySingleKeyInvalid, InvalidKeyMessage);
        }

        return value;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
