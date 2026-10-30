using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Modules;
using Platform.Infrastructure.DependencyInjection;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.DependencyInjection;
using Platform.Kernel.Modules;

namespace Platform.Api.Persistence;

/// <summary>
/// 设计时上下文工厂：<c>dotnet ef migrations add</c> 从这里构造上下文。
/// </summary>
/// <remarks>
/// 必须放在宿主工程而不是 <c>Platform.Infrastructure</c>：迁移要包含各模块贡献的实体，
/// 而模块 contributor 只有在宿主装配了集中清单后才可解析。放在 Infrastructure 会导致
/// 迁移只生成横切表（audit_logs / idempotency_records），模块表全部漏掉。
/// </remarks>
public sealed class PlatformApiDesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    private const string DesignTimeSigningKey = "design-time-only-signing-key-at-least-32-chars";

    /// <inheritdoc />
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("PLATFORM_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=appframework_platform;Username=postgres;Password=postgres";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = connectionString,
                // 认证装配要求密钥长度 ≥32；设计时不发令牌，只用于通过启动期校验。
                ["Jwt:SigningKey"] = DesignTimeSigningKey,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformKernel();
        services.AddPlatformInfrastructure(configuration);
        services.AddPlatformModules(PlatformModuleCatalog.Modules, configuration);

        using var provider = services.BuildServiceProvider();
        // 物化后再释放 provider：DbContext 需要 contributor 实例在整个迁移过程中可用。
        var contributors = provider.GetServices<IPlatformModelContributor>().ToList();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new PlatformDbContext(options, contributors, new SystemClock());
    }
}
