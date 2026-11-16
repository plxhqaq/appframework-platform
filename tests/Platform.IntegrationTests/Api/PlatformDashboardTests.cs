using System.Net;
using Platform.Contracts.Dashboard;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>总览仪表盘：鉴权与系统/平台/设备统计字段。</summary>
public sealed class PlatformDashboardTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public PlatformDashboardTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 匿名不可访问仪表盘()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/platform/dashboard");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 登录后可读取系统平台与设备统计()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var board = await IdentityTestHelpers.ReadDataAsync<PlatformDashboardDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/platform/dashboard", token)));

        Assert.Equal("ok", board.System.Status);
        Assert.InRange(board.System.CpuUsagePercent, 0, 100);
        Assert.InRange(board.System.MemoryUsagePercent, 0, 100);
        Assert.True(board.System.ProcessorCount >= 1);
        Assert.True(board.System.WorkingSetBytes > 0);
        Assert.True(board.System.TotalMemoryBytes > 0);
        if (board.System.ServerCpuUsagePercent is { } serverCpu)
        {
            Assert.InRange(serverCpu, 0, 100);
        }

        if (board.System is
            { ServerTotalMemoryBytes: { } serverTotal, ServerAvailableMemoryBytes: { } serverAvailable })
        {
            Assert.True(serverTotal > 0);
            Assert.InRange(serverAvailable, 0, serverTotal);
            Assert.InRange(board.System.ServerMemoryUsagePercent!.Value, 0, 100);
        }
        Assert.True(board.UptimeSeconds >= 0);
        Assert.True(board.StartedAt <= board.ServerTime);
        Assert.True(board.BootTime <= board.ServerTime);
        Assert.True(board.SystemUptimeSeconds >= 0);
        Assert.True(board.SystemUptimeSeconds >= board.UptimeSeconds);

        Assert.True(board.Devices.Total >= 0);
        Assert.True(board.Devices.Online <= board.Devices.Total);
        Assert.True(board.Devices.Connected >= 0);

        Assert.False(string.IsNullOrWhiteSpace(board.Platform.MachineName));
        Assert.False(string.IsNullOrWhiteSpace(board.Platform.OsDescription));
        Assert.False(string.IsNullOrWhiteSpace(board.Platform.RuntimeVersion));
        Assert.False(string.IsNullOrWhiteSpace(board.Platform.PlatformVersion));
        Assert.StartsWith("1.0.0.", board.Platform.PlatformVersion, StringComparison.Ordinal);
        Assert.Equal(4, board.Platform.PlatformVersion.Split('.').Length);
        Assert.True(board.Platform.ModuleCount >= 8);
    }
}
