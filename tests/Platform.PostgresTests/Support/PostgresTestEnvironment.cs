using Microsoft.EntityFrameworkCore;
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
/// 进程级单例 PostgreSQL 容器：第一次取连接串时启动容器并跑完全部迁移。
/// 不在测试夹具里启动，是为了让 Docker 不可用时用例整体跳过而不触发容器创建。
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
