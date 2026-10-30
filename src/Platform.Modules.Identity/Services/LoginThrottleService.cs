using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Identity.Domain;
using Platform.Modules.Identity.Options;

namespace Platform.Modules.Identity.Services;

/// <summary>账号 + IP 双维度登录节流。</summary>
/// <remarks>
/// 两个维度互补：账号锁定拦不住"同一 IP 慢速撞多个账号"，IP 锁定拦不住"分布式换 IP 撞同一账号"。
/// 状态落库而不是放内存，多副本部署时仍然有效。
/// </remarks>
public interface ILoginThrottleService
{
    /// <summary>登录前检查；任一维度锁定中即抛 <see cref="ApiErrorCode.IdentityAccountThrottled"/>。</summary>
    Task EnsureNotLockedAsync(string account, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>记录一次失败；达到阈值即锁定。</summary>
    Task RegisterFailureAsync(string account, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>登录成功后清除账号维度的失败记录（IP 维度保留，防止换账号继续撞库）。</summary>
    Task RegisterSuccessAsync(string account, CancellationToken cancellationToken = default);
}

/// <summary>数据库实现的登录节流。</summary>
public sealed class LoginThrottleService : ILoginThrottleService
{
    private readonly PlatformDbContext _db;
    private readonly IClock _clock;
    private readonly LoginThrottleOptions _options;

    /// <summary>构造。</summary>
    public LoginThrottleService(
        PlatformDbContext db,
        IClock clock,
        IOptions<LoginThrottleOptions> options)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task EnsureNotLockedAsync(
        string account,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        foreach (var row in await LoadAsync(account, ipAddress, cancellationToken))
        {
            if (row.LockedUntil is { } until && until > now)
            {
                var minutes = (int)Math.Ceiling((until - now).TotalMinutes);
                throw new BusinessException(
                    ApiErrorCode.IdentityAccountThrottled,
                    $"登录失败次数过多，请 {minutes} 分钟后再试。");
            }
        }
    }

    /// <inheritdoc />
    public async Task RegisterFailureAsync(
        string account,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var rows = await LoadAsync(account, ipAddress, cancellationToken);

        ApplyFailure(rows, LoginThrottle.DimensionAccount, NormalizeAccount(account), now);
        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            ApplyFailure(rows, LoginThrottle.DimensionIp, ipAddress.Trim(), now);
        }

        foreach (var row in rows)
        {
            if (_db.Entry(row).State == EntityState.Detached)
            {
                _db.Set<LoginThrottle>().Add(row);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RegisterSuccessAsync(string account, CancellationToken cancellationToken = default)
    {
        var key = NormalizeAccount(account);
        var row = await _db.Set<LoginThrottle>()
            .FirstOrDefaultAsync(
                x => x.Dimension == LoginThrottle.DimensionAccount && x.Key == key,
                cancellationToken);

        if (row is null)
        {
            return;
        }

        _db.Set<LoginThrottle>().Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<LoginThrottle>> LoadAsync(
        string account,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var key = NormalizeAccount(account);
        if (string.IsNullOrEmpty(key))
        {
            return [];
        }

        var query = _db.Set<LoginThrottle>().Where(
            x => x.Dimension == LoginThrottle.DimensionAccount && x.Key == key);

        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            var ip = ipAddress.Trim();
            query = query.Union(_db.Set<LoginThrottle>().Where(
                x => x.Dimension == LoginThrottle.DimensionIp && x.Key == ip));
        }

        return await query.ToListAsync(cancellationToken);
    }

    private void ApplyFailure(
        List<LoginThrottle> rows,
        string dimension,
        string key,
        DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        var row = rows.FirstOrDefault(
            x => string.Equals(x.Dimension, dimension, StringComparison.Ordinal)
                 && string.Equals(x.Key, key, StringComparison.Ordinal));

        if (row is null)
        {
            row = new LoginThrottle
            {
                Dimension = dimension,
                Key = key,
                WindowStart = now,
            };
            rows.Add(row);
        }

        var window = TimeSpan.FromMinutes(Math.Max(1, _options.TrackingWindowMinutes));
        if ((row.LockedUntil is { } until && until <= now) || now - row.WindowStart > window)
        {
            row.FailureCount = 0;
            row.LockedUntil = null;
            row.WindowStart = now;
        }

        row.FailureCount++;
        if (row.FailureCount >= Math.Max(1, _options.MaxFailuresBeforeLockout))
        {
            row.LockedUntil = now.AddMinutes(Math.Max(1, _options.LockoutMinutes));
        }
    }

    private static string NormalizeAccount(string? account) =>
        string.IsNullOrWhiteSpace(account) ? string.Empty : account.Trim().ToLowerInvariant();
}
