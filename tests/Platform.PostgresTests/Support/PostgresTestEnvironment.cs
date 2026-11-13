using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Apps.Persistence;
using Platform.Modules.Backups.Persistence;
using Platform.Modules.Documents.Persistence;
using Platform.Modules.Identity.Persistence;
using Platform.Modules.Licensing.Persistence;
using Platform.Modules.Versions.Persistence;
using Platform.Modules.Webhooks.Persistence;
using Testcontainers.PostgreSql;

namespace Platform.PostgresTests.Support;

/// <summary>固定时钟；并发用例只需要一个可预测的时间基准。</summary>
internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// 进程级单例 PostgreSQL：第一次取连接串时准备库并跑完全部迁移。
/// 默认用 Testcontainers 起容器；设了 <c>POSTGRES_TEST_CONNECTION</c> 则改为在给定实例上
/// 建一个临时库（退出时删除），供拉不到 Docker Hub 的内网环境使用。
/// 不在测试夹具里启动，是为了让环境不可用时用例整体跳过而不触发容器创建。
/// </summary>
internal static class PostgresTestEnvironment
{
    private static readonly Lazy<Task<string>> ConnectionString =
        new(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Task<string> GetConnectionStringAsync() => ConnectionString.Value;

    public static async Task<PlatformDbContext> CreateDbContextAsync(IClock? clock = null) =>
        CreateDbContext(await GetConnectionStringAsync(), clock ?? new TestClock());

    public static PlatformDbContext CreateDbContext(string connectionString, IClock clock) =>
        new(
            new DbContextOptionsBuilder<PlatformDbContext>()
                .UseNpgsql(connectionString)
                .Options,
            [
                new AppsModelContributor(), new IdentityModelContributor(), new VersionsModelContributor(),
                new LicensingModelContributor(), new BackupsModelContributor(), new DocumentsModelContributor(),
                new WebhooksModelContributor(),
            ],
            clock);

    private static async Task<string> StartAsync()
    {
        var external = Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");
        return string.IsNullOrWhiteSpace(external)
            ? await StartContainerAsync()
            : await StartExternalAsync(external);
    }

    private static async Task<string> StartContainerAsync()
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("platform_tests")
            .WithUsername("platform")
            .WithPassword("platform")
            .Build();

        await container.StartAsync();
        var connectionString = container.GetConnectionString();
        await using var db = CreateDbContext(connectionString, new TestClock());
        await db.Database.MigrateAsync();
        return connectionString;
    }

    private static async Task<string> StartExternalAsync(string adminConnectionString)
    {
        var database = "platform_tests_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(adminConnectionString);

        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => DropDatabase(admin, database);

        var connectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        {
            Database = database,
        }.ConnectionString;

        await using var db = CreateDbContext(connectionString, new TestClock());
        await db.Database.MigrateAsync();
        return connectionString;
    }

    private static void DropDatabase(NpgsqlConnectionStringBuilder admin, string database)
    {
        try
        {
            using var connection = new NpgsqlConnection(admin.ConnectionString);
            connection.Open();
            using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", connection);
            drop.ExecuteNonQuery();
        }
        catch (NpgsqlException)
        {
            // 进程退出阶段的尽力清理，删不掉就留给运维手工处理，不要盖住测试结果
        }
    }
}

/// <summary>并发用例只需要认领与失败路径，不会读写 Blob。</summary>
internal sealed class UnusedBlobStorage : IBlobStorage
{
    public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<long> GetLengthAsync(string relativePath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Stream> OpenRangeAsync(
        string relativePath,
        long offset,
        long? count = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<BlobWriteResult> WriteAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<BlobWriteResult> WritePendingAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task PublishAsync(string relativePath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DiscardPendingAsync(string relativePath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>磁盘容量不限，避免容量判断干扰认领语义。</summary>
internal sealed class UnlimitedStorageCapacity : IStorageCapacity
{
    public Task<long> GetAvailableFreeBytesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(long.MaxValue);
}
