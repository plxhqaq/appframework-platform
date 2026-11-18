using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts.Identity;
using Platform.Contracts.Webhooks;
using Platform.Infrastructure.Persistence;
using Platform.IntegrationTests.Support;
using Platform.Modules.Identity.Domain;
using Platform.Modules.Identity.Options;
using Platform.Modules.Identity.Services;
using Platform.Modules.Webhooks.Domain;
using Platform.Modules.Webhooks.Options;
using Platform.Modules.Webhooks.Services;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>身份与 Webhook 过期数据清理：保留期内、锁定中、待重试的数据都不能被误删。</summary>
public sealed class ExpiredDataCleanupTests
{
    [Fact]
    public async Task 身份过期数据按保留期清理且锁定中的节流保留()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var now = clock.UtcNow;

        db.Add(new RefreshToken
        {
            UserId = Guid.NewGuid(),
            FamilyId = Guid.NewGuid(),
            TokenHash = new string('a', 64),
            ExpiresAt = now.AddDays(-10),
        });
        db.Add(new RefreshToken
        {
            UserId = Guid.NewGuid(),
            FamilyId = Guid.NewGuid(),
            TokenHash = new string('b', 64),
            ExpiresAt = now.AddDays(10),
        });

        db.Add(new SingleLoginKey { Key = "111111", AppKey = "demo", CreatedBy = Guid.NewGuid(), ExpiresAt = now.AddHours(-30) });
        db.Add(new SingleLoginKey { Key = "222222", AppKey = "demo", CreatedBy = Guid.NewGuid(), ExpiresAt = now.AddHours(1) });

        db.Add(new QrLoginSession { Status = QrLoginStatuses.Pending, AppKey = "demo", ExpiresAt = now.AddHours(-30) });
        db.Add(new QrLoginSession
        {
            Status = QrLoginStatuses.Approved,
            AppKey = "demo",
            ExpiresAt = now.AddMinutes(-5),
            TokenExpiresAt = now.AddHours(1),
        });

        db.Add(new LoginThrottle
        {
            Dimension = LoginThrottle.DimensionAccount,
            Key = "stale",
            WindowStart = now.AddHours(-30),
            FailureCount = 3,
        });
        db.Add(new LoginThrottle
        {
            Dimension = LoginThrottle.DimensionAccount,
            Key = "locked",
            WindowStart = now.AddHours(-30),
            LockedUntil = now.AddMinutes(10),
            FailureCount = 10,
        });
        db.Add(new LoginThrottle
        {
            Dimension = LoginThrottle.DimensionIp,
            Key = "10.0.0.1",
            WindowStart = now.AddMinutes(-5),
            FailureCount = 1,
        });
        await db.SaveChangesAsync();

        var task = new IdentityCleanupTask(
            db,
            clock,
            Options.Create(new IdentityCleanupOptions()),
            Options.Create(new LoginThrottleOptions()));
        var result = await task.RunAsync();

        Assert.Equal(4, result.RowsDeleted);
        Assert.Equal(1, await db.Set<RefreshToken>().CountAsync());
        Assert.Equal(1, await db.Set<SingleLoginKey>().CountAsync());
        Assert.Equal(1, await db.Set<QrLoginSession>().CountAsync());
        Assert.Equal(2, await db.Set<LoginThrottle>().CountAsync());
        Assert.True(await db.Set<LoginThrottle>().AnyAsync(x => x.Key == "locked"));
    }

    [Fact]
    public async Task Webhook终态投递按保留期清理而待重试记录保留()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var subscriptionId = Guid.NewGuid();

        AddDelivery(db, subscriptionId, WebhookConventions.StatusSucceeded);
        AddDelivery(db, subscriptionId, WebhookConventions.StatusDead);
        AddDelivery(db, subscriptionId, WebhookConventions.StatusFailed);
        AddDelivery(db, subscriptionId, WebhookConventions.StatusPending);
        await db.SaveChangesAsync();

        // 保留期后再产生一条成功投递：旧终态应被清理，新记录与待重试队列不受影响
        clock.Advance(TimeSpan.FromDays(40));
        AddDelivery(db, subscriptionId, WebhookConventions.StatusSucceeded);
        await db.SaveChangesAsync();

        var task = new WebhookDeliveryCleanupTask(
            db,
            clock,
            Options.Create(new WebhooksOptions { DeliveryRetentionDays = 30 }));
        var result = await task.RunAsync();

        Assert.Equal(2, result.RowsDeleted);
        Assert.Equal(3, await db.Set<WebhookDelivery>().CountAsync());
        Assert.Equal(
            1,
            await db.Set<WebhookDelivery>().CountAsync(x => x.Status == WebhookConventions.StatusPending));
        Assert.Equal(
            1,
            await db.Set<WebhookDelivery>().CountAsync(x => x.Status == WebhookConventions.StatusFailed));
    }

    private static void AddDelivery(PlatformDbContext db, Guid subscriptionId, string status) =>
        db.Add(new WebhookDelivery
        {
            AppKey = "demo",
            SubscriptionId = subscriptionId,
            EventType = "license.expiring",
            PayloadJson = "{}",
            Status = status,
        });
}
