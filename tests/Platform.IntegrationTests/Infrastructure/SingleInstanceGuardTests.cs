using Platform.Infrastructure.Deployment;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>单实例守卫：非 PostgreSQL 提供程序下必须跳过，不能把测试宿主挡在启动阶段。</summary>
public sealed class SingleInstanceGuardTests
{
    [Fact]
    public async Task AcquireIsSkippedOnInMemoryProvider()
    {
        await using var db = TestDbContext.Create(new TestClock());
        var guard = new SingleInstanceGuard(db);

        Assert.False(await guard.AcquireAsync());
        await guard.ReleaseAsync();
    }
}
