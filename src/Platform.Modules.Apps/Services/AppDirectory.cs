using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Apps.Domain;

namespace Platform.Modules.Apps.Services;

/// <summary>
/// 应用键目录实现。身份模块只依赖 <see cref="IAppDirectory"/>，不引用本模块实体，
/// 因而模块仍可独立摘除（摘除后身份模块的应用级角色会在运行期明确失败）。
/// </summary>
public sealed class AppDirectory : IAppDirectory
{
    private readonly PlatformDbContext _db;

    /// <summary>构造。</summary>
    public AppDirectory(PlatformDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListEnabledKeysAsync(CancellationToken cancellationToken = default) =>
        await _db.Set<App>()
            .AsNoTracking()
            .Where(static x => x.IsEnabled)
            .OrderBy(static x => x.Key)
            .Select(static x => x.Key)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string appKey, CancellationToken cancellationToken = default) =>
        _db.Set<App>().AsNoTracking().AnyAsync(x => x.Key == appKey, cancellationToken);

    /// <inheritdoc />
    public async Task EnsureAsync(string appKey, string name, CancellationToken cancellationToken = default)
    {
        if (await ExistsAsync(appKey, cancellationToken))
        {
            return;
        }

        _db.Set<App>().Add(new App
        {
            Key = appKey,
            Name = name,
            Description = "平台种子数据",
        });
        await _db.SaveChangesAsync(cancellationToken);
    }
}
