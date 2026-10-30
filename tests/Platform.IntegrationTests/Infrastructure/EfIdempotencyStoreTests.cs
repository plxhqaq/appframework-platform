using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Idempotency;
using Platform.Infrastructure.Persistence.Entities;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>幂等存储：占位互斥、首次响应回放、失败释放、过期重新占位。</summary>
public sealed class EfIdempotencyStoreTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    [Fact]
    public async Task 同一键第二次占位失败且只有一条记录()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var store = new EfIdempotencyStore(db, clock);

        Assert.True(await store.TryReserveAsync("apps", "key-1", Ttl));
        Assert.False(await store.TryReserveAsync("apps", "key-1", Ttl));

        var records = await db.IdempotencyRecords.AsNoTracking().ToListAsync();
        var record = Assert.Single(records);
        Assert.Equal(IdempotencyRecord.StatusReserved, record.Status);
        Assert.Equal(clock.UtcNow.Add(Ttl), record.ExpiresAt);
    }

    [Fact]
    public async Task 不同作用域的同名键互不影响()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var store = new EfIdempotencyStore(db, clock);

        Assert.True(await store.TryReserveAsync("apps", "same-key", Ttl));
        Assert.True(await store.TryReserveAsync("identity", "same-key", Ttl));

        Assert.Equal(2, await db.IdempotencyRecords.CountAsync());
    }

    [Fact]
    public async Task 完成后可回放首次响应()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var store = new EfIdempotencyStore(db, clock);
        var response = new Response { Id = "app-1", Name = "演示" };

        await store.TryReserveAsync("apps", "key-1", Ttl);
        await store.SaveAsync("apps", "key-1", response, Ttl);

        var replayed = await store.GetAsync<Response>("apps", "key-1");

        Assert.NotNull(replayed);
        Assert.Equal(response.Id, replayed.Id);
        Assert.Equal(response.Name, replayed.Name);
        Assert.False(await store.TryReserveAsync("apps", "key-1", Ttl));
    }

    [Fact]
    public async Task 未完成的占位不产生回放()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var store = new EfIdempotencyStore(db, clock);

        await store.TryReserveAsync("apps", "key-1", Ttl);

        Assert.Null(await store.GetAsync<Response>("apps", "key-1"));
    }

    [Fact]
    public async Task 失败释放后允许同键重新执行()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var store = new EfIdempotencyStore(db, clock);

        await store.TryReserveAsync("apps", "key-1", Ttl);
        await store.ReleaseAsync("apps", "key-1");

        Assert.Empty(await db.IdempotencyRecords.ToListAsync());
        Assert.True(await store.TryReserveAsync("apps", "key-1", Ttl));
    }

    [Fact]
    public async Task 释放不得删除已完成结果()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var store = new EfIdempotencyStore(db, clock);

        await store.TryReserveAsync("apps", "key-1", Ttl);
        await store.SaveAsync("apps", "key-1", new Response { Id = "app-1" }, Ttl);
        await store.ReleaseAsync("apps", "key-1");

        Assert.NotNull(await store.GetAsync<Response>("apps", "key-1"));
        Assert.False(await store.TryReserveAsync("apps", "key-1", Ttl));
    }

    [Fact]
    public async Task 过期占位允许同键重新占位()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var store = new EfIdempotencyStore(db, clock);

        await store.TryReserveAsync("apps", "key-1", Ttl);
        clock.Advance(Ttl + TimeSpan.FromSeconds(1));

        Assert.True(await store.TryReserveAsync("apps", "key-1", Ttl));

        var record = await db.IdempotencyRecords.AsNoTracking().SingleAsync();
        Assert.Equal(clock.UtcNow.Add(Ttl), record.ExpiresAt);
    }

    private sealed record Response
    {
        public string? Id { get; init; }

        public string? Name { get; init; }
    }
}
