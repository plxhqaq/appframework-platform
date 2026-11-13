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
public class PlatformApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"api-tests-{Guid.NewGuid():N}";
    private readonly string _blobRoot = Path.Combine(
        Path.GetTempPath(), "platform-blob-tests", Guid.NewGuid().ToString("N"));

    /// <summary>子类按用例族追加/覆盖配置（键与 appsettings 同名）。</summary>
    protected virtual void ConfigureTestSettings(IDictionary<string, string?> settings)
    {
    }

    /// <summary>子类在基础装配完成后追加/替换服务（例如可控时钟）。</summary>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // 集成测试不需要日志输出，避免每个宿主往 logs/ 写文件
        builder.ConfigureLogging(static logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                // InMemory 不支持 Migrate，也不能让测试去连真实库
                ["Database:AutoMigrate"] = "false",
                ["Jwt:SigningKey"] = "integration-test-signing-key-at-least-32-chars",
                ["Jwt:AllowDevTokenEndpoint"] = "true",
                // 停用应用要立即反映到请求路径，缓存窗口压到 0 让用例不依赖时间
                ["AppContextGuard:CacheSeconds"] = "0",
                // 种子提供确定性的 admin 与内置角色；口令只用于测试库
                ["Seed:Enabled"] = "true",
                ["Seed:AdminPassword"] = "Admin@12345",
                // 测试不需要抗暴力强度，压到最低合法参数让每个用例快一个数量级
                ["Argon2:DegreeOfParallelism"] = "1",
                ["Argon2:MemorySizeKiB"] = "8192",
                ["Argon2:Iterations"] = "1",
                ["Argon2:HashLength"] = "32",
                ["Argon2:SaltLength"] = "16",
                // 并发用例需要一次性发几十个兑换请求，放大匿名入口配额
                ["RateLimits:SingleLoginRedeemPerMinute"] = "1000",
                ["RateLimits:QrSessionPerMinute"] = "1000",
                // 每个宿主用独立的 Blob 根目录，避免测试之间互相看到对方上传的内容
                ["BlobStorage:RootPath"] = _blobRoot,
                // 授权模块需要签发私钥才处于 managed 状态；测试用进程内生成的密钥对
                ["Licensing:SigningKeyPkcs8"] = TestLicenseKeys.PrivateKeyPkcs8Base64,
                // 下发回执超时压到 1 秒，超时用例不必真等一分钟
                ["Backups:PushAckTimeoutSeconds"] = "1",
                ["Backups:CleanupGraceMinutes"] = "1",
                ["Documents:CleanupGraceMinutes"] = "1",
                // Webhook 后台循环在测试里关闭，用例直接驱动 IWebhookDispatcher
                ["Webhooks:DispatcherEnabled"] = "false",
                // 摄取循环在测试里关闭，用例直接驱动 IReleasePackageIngestionService，避免时序抖动
                ["Versions:IngestionEnabled"] = "false",
                // 测试机的临时盘容量不可控，磁盘保护用配置关掉；默认值在生产仍是 5 GiB
                ["Versions:MinFreeDiskBytes"] = "0",
                // 登记类用例在一次宿主内会连续发多个请求，放大版本入口配额避免误触 429
                ["RateLimits:VersionsUploadTicketPerMinute"] = "1000",
                ["RateLimits:VersionsPackageFinalizePerMinute"] = "1000",
            };

            ConfigureTestSettings(settings);
            configuration.AddInMemoryCollection(settings);
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
            ConfigureTestServices(services);
        });
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_blobRoot))
            {
                Directory.Delete(_blobRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不掩盖真实断言结果
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }
}
