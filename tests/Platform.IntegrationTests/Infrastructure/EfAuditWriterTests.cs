using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Infrastructure.Auditing;
using Platform.IntegrationTests.Support;
using Platform.Kernel.Abstractions;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>审计写入落库与失败不阻断主业务两条不变式。</summary>
public sealed class EfAuditWriterTests
{
    [Fact]
    public async Task 审计条目完整落库()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var writer = new EfAuditWriter(db, NullLogger<EfAuditWriter>.Instance);

        await writer.WriteAsync(new AuditEntry
        {
            AppKey = "demo",
            ActorId = "user-1",
            ActorName = "管理员",
            Action = "apps.apikey.create",
            TargetType = "app_api_key",
            TargetId = "key-1",
            Result = AuditResults.Success,
            Message = "created",
            IpAddress = "10.0.0.8",
            UserAgent = "test-agent",
            TraceId = "trace-1",
            DetailJson = "{\"scope\":\"read\"}",
        });

        var record = await db.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal("demo", record.AppKey);
        Assert.Equal("user-1", record.ActorId);
        Assert.Equal("管理员", record.ActorName);
        Assert.Equal("apps.apikey.create", record.Action);
        Assert.Equal("app_api_key", record.TargetType);
        Assert.Equal("key-1", record.TargetId);
        Assert.Equal(AuditResults.Success, record.Result);
        Assert.Equal("10.0.0.8", record.IpAddress);
        Assert.Equal("test-agent", record.UserAgent);
        Assert.Equal("trace-1", record.TraceId);
        Assert.Equal("{\"scope\":\"read\"}", record.DetailJson);
        Assert.Equal(clock.UtcNow, record.CreatedAt);
    }

    [Fact]
    public async Task 数据库不可用时只记日志不抛出()
    {
        var clock = new TestClock();
        var db = TestDbContext.Create(clock);
        var writer = new EfAuditWriter(db, NullLogger<EfAuditWriter>.Instance);
        await db.DisposeAsync();

        // 已释放的上下文会让 SaveChanges 抛 ObjectDisposedException；
        // 审计是旁路能力，异常必须被吞掉，否则会把登录/发布等主业务一起拖死。
        var exception = await Record.ExceptionAsync(() => writer.WriteAsync(new AuditEntry
        {
            Action = "identity.login",
            Result = AuditResults.Failure,
        }));

        Assert.Null(exception);
    }
}
