using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Kernel.Errors;
using Platform.Modules.Identity.Options;
using Platform.Modules.Identity.Services;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Identity;

/// <summary>
/// 现场找回管理员口令（<c>--reset-admin-password</c>）的三条底线：
/// 种子器能被宿主容器解析出来、能覆盖已有口令并作废旧会话、能解开账号锁定。
/// 少任何一条，运维就只能删库重种。
/// </summary>
public sealed class IdentityAdminPasswordResetTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public IdentityAdminPasswordResetTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void 一次性入口能从宿主解析到种子器()
    {
        // AddHostedService<T>() 只登记 IHostedService，不登记 T 本身；
        // Program.cs 里的 --reset-admin-password 走 GetRequiredService<T>，漏登记线上直接抛 InvalidOperationException。
        Assert.NotNull(_factory.Services.GetRequiredService<IdentitySeeder>());
    }

    [Fact]
    public async Task 重置覆盖已有口令并吊销旧会话()
    {
        using var client = _factory.CreateClient();
        var login = await IdentityTestHelpers.LoginAsync(
            client, IdentityTestHelpers.AdminUserName, IdentityTestHelpers.AdminPassword);

        await Seeder.ResetAdminPasswordAsync();

        var stale = await client.PostAsJsonAsync(
            "/api/v1/identity/refresh",
            new RefreshTokenRequest(login.RefreshToken));
        Assert.Equal(
            (int)ApiErrorCode.IdentityRefreshTokenRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(stale, HttpStatusCode.Unauthorized));

        // 口令对回 Seed:AdminPassword，且仍然强制改密
        var after = await IdentityTestHelpers.LoginAsync(
            client, IdentityTestHelpers.AdminUserName, IdentityTestHelpers.AdminPassword);
        Assert.True(after.User.MustChangePassword);
    }

    [Fact]
    public async Task 重置解开账号维度的登录锁定()
    {
        using var scope = _factory.Services.CreateAsyncScope();
        var throttle = scope.ServiceProvider.GetRequiredService<ILoginThrottleService>();
        var threshold = scope.ServiceProvider.GetRequiredService<IOptions<LoginThrottleOptions>>()
            .Value.MaxFailuresBeforeLockout;

        // 只打账号维度：IP 维度的锁定连登录成功都不清除，那是设计如此
        for (var i = 0; i < threshold; i++)
        {
            await throttle.RegisterFailureAsync(IdentityTestHelpers.AdminUserName, null);
        }

        await Assert.ThrowsAsync<BusinessException>(
            () => throttle.EnsureNotLockedAsync(IdentityTestHelpers.AdminUserName, null));

        await Seeder.ResetAdminPasswordAsync();

        await throttle.EnsureNotLockedAsync(IdentityTestHelpers.AdminUserName, null);
    }

    private IdentitySeeder Seeder => _factory.Services.GetRequiredService<IdentitySeeder>();
}
