using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Identity.Domain;

namespace Platform.Modules.Identity.Services;

/// <summary>令牌代号目录实现：直读 <c>users.token_epoch</c>（守卫已有 30s 缓存，这里每用户至多 30s 一次）。</summary>
public sealed class UserTokenEpochDirectory : IUserTokenEpochDirectory
{
    private readonly PlatformDbContext _db;

    /// <summary>构造。</summary>
    public UserTokenEpochDirectory(PlatformDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<int?> GetEpochAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await _db.Set<User>()
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => (int?)x.TokenEpoch)
            .FirstOrDefaultAsync(cancellationToken);
}
