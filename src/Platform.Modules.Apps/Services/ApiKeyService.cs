using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Apps.Domain;

namespace Platform.Modules.Apps.Services;

/// <summary>API Key 生命周期：签发、列出、吊销、校验。</summary>
public interface IApiKeyService
{
    /// <summary>列出应用下的 Key（永不含明文或哈希）。</summary>
    Task<IReadOnlyList<ApiKeySummaryDto>> ListAsync(Guid appId, CancellationToken cancellationToken = default);

    /// <summary>签发 Key；明文只在返回值中出现一次。</summary>
    /// <remarks>scope 必须是调用方已持有的权限，防止低权限管理员借签发密钥给自己提权。</remarks>
    Task<CreatedApiKeyDto> CreateAsync(
        Guid appId,
        CreateApiKeyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>吊销 Key；已吊销视为成功。</summary>
    Task<ApiKeySummaryDto> RevokeAsync(Guid appId, Guid keyId, CancellationToken cancellationToken = default);

    /// <summary>校验明文并返回所属应用与 scopes；失败抛对应业务错误码。</summary>
    Task<AuthenticatedApiKey> AuthenticateAsync(
        string appKey,
        string plaintext,
        CancellationToken cancellationToken = default);
}

/// <summary>校验通过后的 API Key 上下文。</summary>
/// <param name="App">所属应用（已确认启用）。</param>
/// <param name="Key">密钥记录。</param>
/// <param name="Scopes">授予的权限码。</param>
public sealed record AuthenticatedApiKey(App App, AppApiKey Key, IReadOnlyList<string> Scopes);

/// <summary>API Key 服务实现。</summary>
public sealed class ApiKeyService : IApiKeyService
{
    private const string InvalidMessage = "API Key 无效。";

    private readonly PlatformDbContext _db;
    private readonly IClock _clock;
    private readonly ICurrentPrincipal _principal;
    private readonly IAppPermissionDirectory _appPermissions;

    /// <summary>构造。</summary>
    public ApiKeyService(
        PlatformDbContext db,
        IClock clock,
        ICurrentPrincipal principal,
        IAppPermissionDirectory appPermissions)
    {
        _db = db;
        _clock = clock;
        _principal = principal;
        _appPermissions = appPermissions;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiKeySummaryDto>> ListAsync(
        Guid appId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAppExistsAsync(appId, cancellationToken);
        var now = _clock.UtcNow;

        var keys = await _db.Set<AppApiKey>()
            .AsNoTracking()
            .Where(x => x.AppId == appId)
            .OrderByDescending(static x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return [.. keys.Select(key => ToSummary(key, now))];
    }

    /// <inheritdoc />
    public async Task<CreatedApiKeyDto> CreateAsync(
        Guid appId,
        CreateApiKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var app = await RequireAppAsync(appId, cancellationToken);

        var appCodes = await _appPermissions.ListCodesAsync(app.Key, cancellationToken);
        var scopes = NormalizeScopes(request.Scopes, appCodes);
        EnsureScopesGrantable(scopes);
        var now = _clock.UtcNow;
        if (request.ExpiresAt is { } expires && expires <= now)
        {
            throw BusinessException.Invalid("过期时间必须晚于当前时间。");
        }

        var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(AppConventions.ApiKeyPrefixLength / 2))
            .ToLowerInvariant();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var plaintext = $"{AppConventions.ApiKeyPrefix}{prefix}_{secret}";

        var key = new AppApiKey
        {
            AppId = appId,
            Prefix = prefix,
            SecretHash = HashSecret(secret),
            Name = string.IsNullOrWhiteSpace(request.Name) ? "未命名密钥" : request.Name.Trim(),
            Scopes = scopes,
            ExpiresAt = request.ExpiresAt,
        };

        _db.Set<AppApiKey>().Add(key);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _db.ChangeTracker.Clear();
            throw new BusinessException(ApiErrorCode.Conflict, "密钥前缀冲突，请重试。", exception);
        }

        return new CreatedApiKeyDto(
            key.Id,
            key.Prefix,
            plaintext,
            key.Name,
            key.Scopes,
            key.ExpiresAt,
            key.CreatedAt);
    }

    /// <inheritdoc />
    public async Task<ApiKeySummaryDto> RevokeAsync(
        Guid appId,
        Guid keyId,
        CancellationToken cancellationToken = default)
    {
        var key = await _db.Set<AppApiKey>()
                      .FirstOrDefaultAsync(x => x.Id == keyId && x.AppId == appId, cancellationToken)
                  ?? throw BusinessException.NotFound(ApiErrorCode.AppsApiKeyNotFound, $"API Key {keyId} 不存在。");

        if (key.RevokedAt is null)
        {
            key.RevokedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return ToSummary(key, _clock.UtcNow);
    }

    /// <inheritdoc />
    public async Task<AuthenticatedApiKey> AuthenticateAsync(
        string appKey,
        string plaintext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appKey)
            || string.IsNullOrWhiteSpace(plaintext)
            || !TryParse(plaintext, out var prefix, out var secret))
        {
            throw new BusinessException(ApiErrorCode.AppsApiKeyInvalid, InvalidMessage);
        }

        var app = await _db.Set<App>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == appKey, cancellationToken)
            ?? throw new BusinessException(ApiErrorCode.AppsApiKeyInvalid, InvalidMessage);

        var key = await _db.Set<AppApiKey>()
            .FirstOrDefaultAsync(x => x.AppId == app.Id && x.Prefix == prefix, cancellationToken)
            ?? throw new BusinessException(ApiErrorCode.AppsApiKeyInvalid, InvalidMessage);

        if (!VerifySecret(secret, key.SecretHash))
        {
            throw new BusinessException(ApiErrorCode.AppsApiKeyInvalid, InvalidMessage);
        }

        if (!app.IsEnabled)
        {
            throw new BusinessException(ApiErrorCode.AppsDisabled, $"应用 {app.Key} 已禁用。");
        }

        var now = _clock.UtcNow;
        if (key.RevokedAt is not null)
        {
            throw new BusinessException(ApiErrorCode.AppsApiKeyRevoked, "API Key 已吊销。");
        }

        if (key.ExpiresAt is { } expires && expires <= now)
        {
            throw new BusinessException(ApiErrorCode.AppsApiKeyExpired, "API Key 已过期。");
        }

        key.LastUsedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        return new AuthenticatedApiKey(app, key, key.Scopes);
    }

    private async Task EnsureAppExistsAsync(Guid appId, CancellationToken cancellationToken)
    {
        if (!await _db.Set<App>().AnyAsync(x => x.Id == appId, cancellationToken))
        {
            throw BusinessException.NotFound(ApiErrorCode.AppsNotFound, $"应用 {appId} 不存在。");
        }
    }

    private async Task<App> RequireAppAsync(Guid appId, CancellationToken cancellationToken) =>
        await _db.Set<App>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == appId, cancellationToken)
        ?? throw BusinessException.NotFound(ApiErrorCode.AppsNotFound, $"应用 {appId} 不存在。");

    /// <summary>只减不增的同源约束：调用方不能签发自己都没持有的权限。</summary>
    private void EnsureScopesGrantable(List<string> scopes)
    {
        if (scopes.Count == 0 || _principal.Permissions.Contains(PermissionCatalog.Wildcard))
        {
            return;
        }

        var denied = scopes.Where(scope => !_principal.Permissions.Contains(scope)).ToList();
        if (denied.Count > 0)
        {
            throw new BusinessException(
                ApiErrorCode.AppsApiKeyScopeDenied,
                $"不能授予自己未持有的权限：{string.Join(", ", denied)}。");
        }
    }

    private static List<string> NormalizeScopes(
        IReadOnlyList<string>? scopes,
        IReadOnlySet<string> appCodes)
    {
        if (scopes is null || scopes.Count == 0)
        {
            return [];
        }

        var normalized = new List<string>();
        foreach (var raw in scopes)
        {
            var scope = raw?.Trim();
            if (string.IsNullOrEmpty(scope))
            {
                continue;
            }

            if (string.Equals(scope, PermissionCatalog.Wildcard, StringComparison.Ordinal))
            {
                throw new BusinessException(
                    ApiErrorCode.AppsApiKeyScopeDenied,
                    "API Key 不得授予通配符权限。");
            }

            if (!PermissionCatalog.IsKnown(scope) && !appCodes.Contains(scope))
            {
                throw new BusinessException(
                    ApiErrorCode.AppsApiKeyScopeUnknown,
                    $"未知权限码：{scope}。");
            }

            if (!normalized.Contains(scope, StringComparer.Ordinal))
            {
                normalized.Add(scope);
            }
        }

        return normalized;
    }

    private static bool TryParse(string plaintext, out string prefix, out string secret)
    {
        prefix = string.Empty;
        secret = string.Empty;

        var parts = plaintext.Split('_');
        if (parts.Length != 3
            || !string.Equals(parts[0], "afk", StringComparison.Ordinal)
            || parts[1].Length != AppConventions.ApiKeyPrefixLength
            || parts[2].Length == 0)
        {
            return false;
        }

        prefix = parts[1];
        secret = parts[2];
        return true;
    }

    private static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static bool VerifySecret(string secret, string storedHash)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        if (expected.Length != 32)
        {
            return false;
        }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static ApiKeySummaryDto ToSummary(AppApiKey key, DateTimeOffset now) =>
        new(
            key.Id,
            key.Prefix,
            key.Name,
            key.Scopes,
            key.ExpiresAt,
            key.RevokedAt,
            key.LastUsedAt,
            key.CreatedAt,
            key.RevokedAt is null && (key.ExpiresAt is null || key.ExpiresAt > now));
}
