using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Platform.Infrastructure.Persistence;

namespace Platform.IntegrationTests.Support;

/// <summary>
/// 宿主级集成测试：用 InMemory 替换 PostgreSQL，其余装配（认证、权限、模块、限流）保持真实。
/// </summary>
public sealed class PlatformApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"api-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // 集成测试不需要日志输出，避免每个宿主往 logs/ 写文件
        builder.ConfigureLogging(static logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // InMemory 不支持 Migrate，也不能让测试去连真实库
                ["Database:AutoMigrate"] = "false",
                ["Jwt:SigningKey"] = "integration-test-signing-key-at-least-32-chars",
                ["Jwt:AllowDevTokenEndpoint"] = "true",
                // 种子提供确定性的 admin 与内置角色；口令只用于测试库
                ["Seed:Enabled"] = "true",
                ["Seed:AdminPassword"] = "Admin@12345",
                // 测试不需要抗暴力强度，压到最低合法参数让每个用例快一个数量级
                ["Argon2:DegreeOfParallelism"] = "1",
                ["Argon2:MemorySizeKiB"] = "8192",
                ["Argon2:Iterations"] = "1",
                ["Argon2:HashLength"] = "32",
                ["Argon2:SaltLength"] = "16",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILoggerProvider>();
            // 先摘掉 Npgsql 的注册（含 EF 9+ 的 options 配置器），再挂 InMemory
            services.RemoveAll<DbContextOptions<PlatformDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<PlatformDbContext>();
            services.RemoveAll<IDbContextOptionsConfiguration<PlatformDbContext>>();
            services.AddDbContext<PlatformDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }
}
