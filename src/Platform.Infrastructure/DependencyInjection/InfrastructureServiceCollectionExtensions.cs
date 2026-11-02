using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Infrastructure.Auditing;
using Platform.Infrastructure.Idempotency;
using Platform.Infrastructure.Persistence;
using Platform.Infrastructure.Storage;
using Platform.Kernel.Abstractions;

namespace Platform.Infrastructure.DependencyInjection;

/// <summary>基础设施注册（数据库 + Blob 存储 + 健康检查）。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>连接串配置键。</summary>
    public const string ConnectionStringName = "Platform";

    /// <summary>
    /// 注册 PostgreSQL 与本地 Blob 存储。
    /// </summary>
    /// <exception cref="InvalidOperationException">未配置连接串时抛出（启动即失败，避免跑起来才发现连不上库）。</exception>
    public static IServiceCollection AddPlatformInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"缺少连接串 ConnectionStrings:{ConnectionStringName}。请在 appsettings 或环境变量 "
                + $"ConnectionStrings__{ConnectionStringName} 中配置 PostgreSQL。");
        }

        services.AddDbContext<PlatformDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.CommandTimeout(60)));

        services.Configure<BlobStorageOptions>(configuration.GetSection(BlobStorageOptions.SectionName));
        services.AddSingleton<IBlobStorage, LocalBlobStorage>();

        // 横切能力：审计与幂等都依赖业务 DbContext，必须随请求作用域创建。
        services.AddScoped<IAuditWriter, EfAuditWriter>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();

        services.AddHealthChecks()
            .AddDbContextCheck<PlatformDbContext>("postgres", tags: ["ready"]);

        return services;
    }
}
