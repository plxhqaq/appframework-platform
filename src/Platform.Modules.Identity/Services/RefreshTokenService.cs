using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Paging;
using Platform.Kernel.Security;
using Platform.Modules.Identity.Domain;
using Platform.Modules.Identity.Options;

namespace Platform.Modules.Identity.Services;

/// <summary>新签发的刷新令牌。明文只返回给调用方一次，库里只有哈希。</summary>
public sealed record IssuedRefreshToken(string Plaintext, DateTimeOffset ExpiresAt);

/// <summary>轮换结果。应用上下文随家族延续，避免刷新后跨应用越权。</summary>
public sealed record RotatedRefreshToken(
    Guid UserId,
    string Plaintext,
    DateTimeOffset ExpiresAt,
    string? AppKey,
    string? MachineCode,
    Guid? InstallGuid);

/// <summary>会话的应用上下文快照（切换应用时同步刷新令牌记录）。</summary>
public sealed record RefreshTokenContext(string? AppKey, string? MachineCode, Guid? InstallGuid);

/// <summary>刷新令牌生命周期与会话查询。</summary>
public interface IRefreshTokenService
{
    /// <summary>为新登录创建令牌家族。</summary>
    Task<IssuedRefreshToken> IssueAsync(
        Guid userId,
        string? appKey,
        string? machineCode,
        Guid? installGuid,
        string? deviceFingerprint,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>轮换令牌；已轮换令牌被再次使用会吊销整个家族并抛 114。</summary>
    Task<RotatedRefreshToken> RotateAsync(
        string plaintext,
        string? ipAddress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新当前刷新令牌记录的应用上下文。令牌不属于该用户或已失效时返回 <c>null</c>（不阻断换发访问令牌）。
    /// </summary>
    Task<RefreshTokenContext?> SetAppKeyAsync(
        string plaintext,
        Guid userId,
        string? appKey,
        CancellationToken cancellationToken = default);

    /// <summary>吊销指定令牌及其家族；令牌不存在时返回 <c>false</c>（登出保持幂等）。</summary>
    Task<bool> RevokeAsync(string? plaintext, string reason, CancellationToken cancellationToken = default);

    /// <summary>吊销用户全部活跃令牌。</summary>
    Task<int> RevokeAllAsync(
        Guid userId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>用户会话列表。</summary>
    Task<IReadOnlyList<SessionSummaryDto>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>分页查询会话（运维视角）。<paramref name="isActive"/> 为 null 时不过滤状态。</summary>
    Task<PageResult<SessionSummaryDto>> ListAsync(
        PageQuery query,
        Guid? userId,
        bool? isActive,
        CancellationToken cancellationToken = default);

    /// <summary>按会话 id 吊销整个家族。</summary>
    Task<SessionSummaryDto> RevokeSessionAsync(
        Guid sessionId,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>刷新令牌实现：随机 32 字节、SHA-256 存储、同家族轮换与复用检测。</summary>
public sealed class RefreshTokenService : IRefreshTokenService
{
    private readonly PlatformDbContext _db;
    private readonly IClock _clock;
    private readonly IdentityTokenOptions _options;
    private readonly IDeviceNodeDirectory? _nodes;
    private readonly UserEpochCache _epochs;

    /// <summary>非关系型提供程序（单元测试内存库）没有原子 UPDATE，用进程内闸门串行化轮换。</summary>
    private static readonly SemaphoreSlim InMemoryRotationGate = new(1, 1);

    /// <summary>构造。</summary>
    public RefreshTokenService(
        PlatformDbContext db,
        IClock clock,
        IOptions<IdentityTokenOptions> options,
        IEnumerable<IDeviceNodeDirectory> nodes,
        UserEpochCache epochs)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _nodes = nodes.FirstOrDefault();
        _epochs = epochs;
    }

    /// <inheritdoc />
    public async Task<IssuedRefreshToken> IssueAsync(
        Guid userId,
        string? appKey,
        string? machineCode,
        Guid? installGuid,
        string? deviceFingerprint,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var plaintext = CreatePlaintext();
        var token = new RefreshToken
        {
            UserId = userId,
            FamilyId = Guid.CreateVersion7(),
            TokenHash = Hash(plaintext),
            AppKey = Normalize(appKey),
            MachineCode = Normalize(machineCode),
            InstallGuid = Normalize(installGuid),
            DeviceFingerprint = Normalize(deviceFingerprint),
            CreatedByIp = Normalize(ipAddress),
            ExpiresAt = now.AddDays(Math.Max(1, _options.RefreshTokenDays)),
        };

        _db.Set<RefreshToken>().Add(token);
        await _db.SaveChangesAsync(cancellationToken);
        return new IssuedRefreshToken(plaintext, token.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<RotatedRefreshToken> RotateAsync(
        string plaintext,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var token = await FindForRotationAsync(plaintext, cancellationToken)
                    ?? throw InvalidRefreshToken();
        var now = _clock.UtcNow;

        await ThrowIfUnrotatableAsync(token, now, cancellationToken);

        var plaintextNew = CreatePlaintext();
        var replacement = new RefreshToken
        {
            UserId = token.UserId,
            FamilyId = token.FamilyId,
            TokenHash = Hash(plaintextNew),
            AppKey = token.AppKey,
            MachineCode = token.MachineCode,
            InstallGuid = token.InstallGuid,
            DeviceFingerprint = token.DeviceFingerprint,
            CreatedByIp = token.CreatedByIp,
            ExpiresAt = now.AddDays(Math.Max(1, _options.RefreshTokenDays)),
        };

        // 读到的令牌状态可能已经过时：抢占式的原子 UPDATE 保证同一令牌只有一个请求轮换成功。
        var rotated = _db.Database.IsRelational()
            ? await TryRotateRelationalAsync(token, replacement, now, cancellationToken)
            : await TryRotateInMemoryAsync(token.Id, replacement, now, cancellationToken);

        if (!rotated)
        {
            var current = await FindForRotationAsync(plaintext, cancellationToken)
                          ?? throw InvalidRefreshToken();
            await ThrowIfUnrotatableAsync(current, now, cancellationToken);
            throw InvalidRefreshToken();
        }

        return new RotatedRefreshToken(
            replacement.UserId,
            plaintextNew,
            replacement.ExpiresAt,
            replacement.AppKey,
            replacement.MachineCode,
            replacement.InstallGuid);
    }

    private async Task<bool> TryRotateRelationalAsync(
        RefreshToken token,
        RefreshToken replacement,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await _db.Set<RefreshToken>()
            .Where(x => x.Id == token.Id && x.RevokedAt == null && x.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static x => x.LastUsedAt, now)
                    .SetProperty(static x => x.RevokedAt, now)
                    .SetProperty(static x => x.RevokedReason, RefreshTokenRevokeReasons.Rotated)
                    .SetProperty(static x => x.ReplacedByTokenId, replacement.Id)
                    .SetProperty(static x => x.UpdatedAt, now),
                cancellationToken);

        if (claimed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        _db.Set<RefreshToken>().Add(replacement);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> TryRotateInMemoryAsync(
        Guid tokenId,
        RefreshToken replacement,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await InMemoryRotationGate.WaitAsync(cancellationToken);
        try
        {
            var current = await _db.Set<RefreshToken>()
                .FirstOrDefaultAsync(x => x.Id == tokenId, cancellationToken);
            if (current is null || current.RevokedAt is not null || current.ExpiresAt <= now)
            {
                return false;
            }

            current.LastUsedAt = now;
            current.RevokedAt = now;
            current.RevokedReason = RefreshTokenRevokeReasons.Rotated;
            current.ReplacedByTokenId = replacement.Id;
            _db.Set<RefreshToken>().Add(replacement);
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            InMemoryRotationGate.Release();
        }
    }

    private async Task ThrowIfUnrotatableAsync(
        RefreshToken token,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (token.RevokedAt is not null)
        {
            if (string.Equals(token.RevokedReason, RefreshTokenRevokeReasons.Rotated, StringComparison.Ordinal)
                || string.Equals(token.RevokedReason, RefreshTokenRevokeReasons.ReuseDetected, StringComparison.Ordinal))
            {
                // 已轮换的令牌再次出现 = 泄露：同一家族全部吊销，合法持有人在下次刷新时被迫重新登录。
                // epoch 为用户级（D7）：疑似泄露直接把该用户全部访问令牌作废（≤30s 缓存窗口）。
                await RevokeFamilyForReuseAsync(token.FamilyId, token.UserId, now, cancellationToken);
            }

            throw new BusinessException(
                ApiErrorCode.IdentityRefreshTokenRevoked,
                "刷新令牌已失效，请重新登录。");
        }

        if (token.ExpiresAt <= now)
        {
            throw new BusinessException(
                ApiErrorCode.IdentityRefreshTokenInvalid,
                "刷新令牌已过期，请重新登录。");
        }

        if (token.User is null || !token.User.IsEnabled)
        {
            throw new BusinessException(
                ApiErrorCode.IdentityUserDisabled,
                "账号已被禁用，请重新登录。");
        }
    }

    private async Task RevokeFamilyForReuseAsync(
        Guid familyId,
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_db.Database.IsRelational())
        {
            await _db.Set<RefreshToken>()
                .Where(x => x.FamilyId == familyId && x.RevokedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.RevokedAt, now)
                        .SetProperty(static x => x.RevokedReason, RefreshTokenRevokeReasons.ReuseDetected)
                        .SetProperty(static x => x.UpdatedAt, now),
                    cancellationToken);
            await BumpEpochAsync(userId, now, cancellationToken);
            return;
        }

        await RevokeFamilyAsync(familyId, RefreshTokenRevokeReasons.ReuseDetected, now, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await BumpEpochAsync(userId, now, cancellationToken);
    }

    private static BusinessException InvalidRefreshToken() =>
        new(ApiErrorCode.IdentityRefreshTokenInvalid, "刷新令牌无效，请重新登录。");

    /// <inheritdoc />
    public async Task<RefreshTokenContext?> SetAppKeyAsync(
        string plaintext,
        Guid userId,
        string? appKey,
        CancellationToken cancellationToken = default)
    {
        var token = await FindAsync(plaintext, cancellationToken);
        var now = _clock.UtcNow;
        if (token is null
            || token.UserId != userId
            || token.RevokedAt is not null
            || token.ExpiresAt <= now)
        {
            return null;
        }

        token.AppKey = Normalize(appKey);
        await _db.SaveChangesAsync(cancellationToken);
        return new RefreshTokenContext(token.AppKey, token.MachineCode, token.InstallGuid);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(
        string? plaintext,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var token = await FindAsync(plaintext, cancellationToken);
        if (token is null)
        {
            return false;
        }

        if (token.RevokedAt is null)
        {
            var now = _clock.UtcNow;
            token.RevokedAt = now;
            token.RevokedReason = reason;
            await RevokeFamilyAsync(token.FamilyId, reason, now, cancellationToken, token.Id);
            await _db.SaveChangesAsync(cancellationToken);
            await BumpEpochAsync(token.UserId, now, cancellationToken);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<int> RevokeAllAsync(
        Guid userId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var tokens = await _db.Set<RefreshToken>()
            .Where(x => x.UserId == userId && x.RevokedAt == null && x.ExpiresAt > now)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.RevokedAt = now;
            token.RevokedReason = reason;
        }

        if (tokens.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            await BumpEpochAsync(userId, now, cancellationToken);
        }

        return tokens.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionSummaryDto>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var tokens = await _db.Set<RefreshToken>()
            .AsNoTracking()
            .Include(static x => x.User)
            .Where(x => x.UserId == userId)
            .OrderByDescending(static x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        var nodes = await LoadNodesAsync(tokens, cancellationToken);
        return [.. tokens.Select(token => ToSummary(token, now, nodes))];
    }

    /// <inheritdoc />
    public async Task<PageResult<SessionSummaryDto>> ListAsync(
        PageQuery query,
        Guid? userId,
        bool? isActive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var now = _clock.UtcNow;
        var source = _db.Set<RefreshToken>()
            .AsNoTracking()
            .Include(static x => x.User)
            .Where(x => userId == null || x.UserId == userId);

        if (isActive is { } active)
        {
            source = active
                ? source.Where(x => x.RevokedAt == null && x.ExpiresAt > now)
                : source.Where(x => x.RevokedAt != null || x.ExpiresAt <= now);
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim().ToLowerInvariant();
            source = source.Where(x => x.User!.UserName.Contains(keyword));
        }

        var ordered = source.OrderByDescending(static x => x.CreatedAt);

        var page = await ordered.ToPageResultAsync(query, cancellationToken);
        var nodes = await LoadNodesAsync(page.Items, cancellationToken);
        return PageResult<SessionSummaryDto>.Create(
            [.. page.Items.Select(token => ToSummary(token, now, nodes))],
            page.Total,
            query);
    }

    /// <inheritdoc />
    public async Task<SessionSummaryDto> RevokeSessionAsync(
        Guid sessionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var token = await _db.Set<RefreshToken>()
                        .Include(static x => x.User)
                        .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken)
                    ?? throw BusinessException.NotFound(ApiErrorCode.NotFound, $"会话 {sessionId} 不存在。");

        var now = _clock.UtcNow;
        if (token.RevokedAt is null)
        {
            await RevokeFamilyAsync(token.FamilyId, reason, now, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await BumpEpochAsync(token.UserId, now, cancellationToken);
        }

        var node = await FindNodeAsync(token.MachineCode, cancellationToken);
        return ToSummary(token, now, node);
    }

    private async Task<RefreshToken?> FindAsync(string? plaintext, CancellationToken cancellationToken)
    {
        if (!TryHash(plaintext, out var hash))
        {
            return null;
        }

        return await _db.Set<RefreshToken>()
            .Include(static x => x.User)
            .FirstOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
    }

    private async Task<RefreshToken?> FindForRotationAsync(string? plaintext, CancellationToken cancellationToken)
    {
        if (!TryHash(plaintext, out var hash))
        {
            return null;
        }

        return await _db.Set<RefreshToken>()
            .AsNoTracking()
            .Include(static x => x.User)
            .FirstOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
    }

    private async Task RevokeFamilyAsync(
        Guid familyId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Guid? exceptTokenId = null)
    {
        var tokens = await _db.Set<RefreshToken>()
            .Where(x => x.FamilyId == familyId && x.RevokedAt == null && x.Id != exceptTokenId)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.RevokedAt = now;
            token.RevokedReason = reason;
        }
    }

    /// <summary>
    /// 吊销收口 bump（D7）：用户 <c>token_epoch</c> 递增并失效守卫缓存，本副本即时生效，
    /// 该用户全部残余访问令牌在守卫下一个缓存窗口内全路径 401（含 /context 换发、QR 审批、单次密钥签发）。
    /// </summary>
    private async Task BumpEpochAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_db.Database.IsRelational())
        {
            await _db.Set<User>()
                .Where(x => x.Id == userId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(static x => x.TokenEpoch, static x => x.TokenEpoch + 1)
                        .SetProperty(static x => x.UpdatedAt, now),
                    cancellationToken);
        }
        else
        {
            var user = await _db.Set<User>()
                .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
            if (user is not null)
            {
                user.TokenEpoch++;
                user.UpdatedAt = now;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        _epochs.Invalidate(userId);
    }

    private async Task<IReadOnlyDictionary<string, DeviceNodeDirectoryEntry>> LoadNodesAsync(
        IEnumerable<RefreshToken> tokens,
        CancellationToken cancellationToken)
    {
        if (_nodes is null)
        {
            return EmptyNodes;
        }

        string[] codes = tokens
            .Select(static x => x.MachineCode)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return codes.Length == 0
            ? EmptyNodes
            : await _nodes.FindByMachineCodesAsync(codes, cancellationToken);
    }

    private async Task<DeviceNodeDirectoryEntry?> FindNodeAsync(
        string? machineCode,
        CancellationToken cancellationToken) =>
        _nodes is null || string.IsNullOrWhiteSpace(machineCode)
            ? null
            : await _nodes.FindByMachineCodeAsync(machineCode, cancellationToken);

    private static readonly IReadOnlyDictionary<string, DeviceNodeDirectoryEntry> EmptyNodes =
        new Dictionary<string, DeviceNodeDirectoryEntry>(StringComparer.Ordinal);

    private static SessionSummaryDto ToSummary(
        RefreshToken token,
        DateTimeOffset now,
        IReadOnlyDictionary<string, DeviceNodeDirectoryEntry> nodes)
    {
        DeviceNodeDirectoryEntry? node = null;
        if (token.MachineCode is { Length: > 0 } code)
        {
            nodes.TryGetValue(code, out node);
        }

        return ToSummary(token, now, node);
    }

    private static SessionSummaryDto ToSummary(
        RefreshToken token,
        DateTimeOffset now,
        DeviceNodeDirectoryEntry? node) =>
        new(
            token.Id,
            token.UserId,
            token.User?.UserName ?? string.Empty,
            token.FamilyId,
            token.AppKey,
            token.DeviceFingerprint,
            token.MachineCode,
            token.InstallGuid,
            node?.NodeId,
            node?.DisplayName,
            token.CreatedByIp,
            token.CreatedAt,
            token.ExpiresAt,
            token.LastUsedAt,
            token.RevokedAt is null && token.ExpiresAt > now);

    private static string CreatePlaintext() =>
        IdentityConventions.RefreshTokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(IdentityConventions.RefreshTokenBytes));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

    private static bool TryHash(string? plaintext, out string hash)
    {
        hash = string.Empty;
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return false;
        }

        var value = plaintext.Trim();
        if (!value.StartsWith(IdentityConventions.RefreshTokenPrefix, StringComparison.Ordinal)
            || value.Length <= IdentityConventions.RefreshTokenPrefix.Length)
        {
            return false;
        }

        hash = Hash(value);
        return true;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Guid? Normalize(Guid? value) =>
        value is { } guid && guid != Guid.Empty ? guid : null;
}
