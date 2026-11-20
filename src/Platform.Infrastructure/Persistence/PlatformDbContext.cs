using Microsoft.EntityFrameworkCore;
using Platform.Kernel.Abstractions;
using Platform.Infrastructure.Persistence.Configurations;
using Platform.Infrastructure.Persistence.Entities;

namespace Platform.Infrastructure.Persistence;

/// <summary>
/// 平台唯一 DbContext（模块化单体共用一库，但表按模块归属）。
/// </summary>
/// <remarks>
/// **模块可摘除**：模块自己的实体配置通过 <see cref="IPlatformModelContributor"/> 注入，
/// 而不是在这里逐模块列 <c>DbSet</c>。摘掉一个模块 = 去掉 csproj 引用 + 集中清单一行，
/// DbContext 无需改动。
/// </remarks>
public sealed class PlatformDbContext : DbContext
{
    private readonly IEnumerable<IPlatformModelContributor> _contributors;
    private readonly IClock _clock;

    /// <summary>构造。</summary>
    public PlatformDbContext(
        DbContextOptions<PlatformDbContext> options,
        IEnumerable<IPlatformModelContributor> contributors,
        IClock clock)
        : base(options)
    {
        _contributors = contributors;
        _clock = clock;
    }

    /// <summary>审计日志。</summary>
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>幂等记录。</summary>
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    /// <summary>模块清理宽限期配置（横切表）。</summary>
    public DbSet<RetentionSettingsRow> RetentionSettings => Set<RetentionSettingsRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        // 横切表
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlatformDbContext).Assembly);

        // 各模块贡献的实体
        foreach (var contributor in _contributors.OrderBy(static c => c.Order))
        {
            contributor.Configure(modelBuilder);
        }
    }

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAuditFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampAuditFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// 统一填 <see cref="IAuditableEntity"/> 时间戳。
    /// 单靠各服务自觉填会在"漏填即错"的地方出错（例如批量改状态），因此下沉到上下文。
    /// </summary>
    private void StampAuditFields()
    {
        var now = _clock.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    break;
                default:
                    break;
            }
        }
    }
}

/// <summary>
/// 模块向共享 DbContext 贡献实体映射。实现类必须放在模块工程内，且不得依赖 DbContext
/// （否则会形成构造期循环依赖）。
/// </summary>
public interface IPlatformModelContributor
{
    /// <summary>顺序（小的先执行），仅在少数需要覆盖默认映射时有用。</summary>
    int Order => 0;

    /// <summary>配置本模块实体。</summary>
    void Configure(ModelBuilder modelBuilder);
}
