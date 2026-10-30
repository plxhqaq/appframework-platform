using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Errors;
using Platform.Modules.Identity.Domain;
using Platform.Modules.Identity.Options;
using Platform.Modules.Identity.Services;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Identity;

/// <summary>
/// 登录节流的两个维度必须互补：账号维度拦"换 IP 撞同一账号"，
/// IP 维度拦"同一 IP 撞多个账号"。这里用可拨时钟验证锁定与自然解锁。
/// </summary>
public sealed class LoginThrottleServiceTests
{
    private static readonly LoginThrottleOptions ThrottleOptions = new()
    {
        MaxFailuresBeforeLockout = 3,
        LockoutMinutes = 15,
        TrackingWindowMinutes = 15,
    };

    [Fact]
    public async Task 达到阈值后锁定且锁定窗口过后自动解锁()
    {
        var clock = new TestClock();
        var service = CreateService(TestDbContext.Create(clock), clock);

        for (var i = 0; i < ThrottleOptions.MaxFailuresBeforeLockout; i++)
        {
            await service.RegisterFailureAsync("alice", "10.0.0.1");
        }

        var locked = await Assert.ThrowsAsync<BusinessException>(
            () => service.EnsureNotLockedAsync("alice", "10.0.0.1"));
        Assert.Equal(ApiErrorCode.IdentityAccountThrottled, locked.Code);

        clock.Advance(TimeSpan.FromMinutes(ThrottleOptions.LockoutMinutes + 1));
        await service.EnsureNotLockedAsync("alice", "10.0.0.1");
    }

    [Fact]
    public async Task 换IP撞同一账号仍会被账号维度锁定()
    {
        var clock = new TestClock();
        var service = CreateService(TestDbContext.Create(clock), clock);

        for (var i = 0; i < ThrottleOptions.MaxFailuresBeforeLockout; i++)
        {
            await service.RegisterFailureAsync("bob", $"10.0.0.{i}");
        }

        var locked = await Assert.ThrowsAsync<BusinessException>(
            () => service.EnsureNotLockedAsync("bob", "10.0.0.99"));
        Assert.Equal(ApiErrorCode.IdentityAccountThrottled, locked.Code);
    }

    [Fact]
    public async Task 登录成功只清除账号维度并保留IP维度()
    {
        var clock = new TestClock();
        var db = TestDbContext.Create(clock);
        var service = CreateService(db, clock);

        await service.RegisterFailureAsync("alice", "10.0.0.1");
        await service.RegisterFailureAsync("alice", "10.0.0.1");
        await service.RegisterSuccessAsync("alice");

        Assert.Empty(await db.Set<LoginThrottle>()
            .Where(x => x.Dimension == LoginThrottle.DimensionAccount && x.Key == "alice")
            .ToListAsync());
        Assert.Single(await db.Set<LoginThrottle>()
            .Where(x => x.Dimension == LoginThrottle.DimensionIp && x.Key == "10.0.0.1")
            .ToListAsync());
    }

    private static LoginThrottleService CreateService(PlatformDbContext db, TestClock clock) =>
        new(db, clock, Options.Create(ThrottleOptions));
}
