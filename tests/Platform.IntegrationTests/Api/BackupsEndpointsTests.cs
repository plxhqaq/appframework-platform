using System.Net;
using System.Net.Http.Headers;
using Platform.Contracts;
using Platform.Contracts.Backups;
using Platform.Kernel.Abstractions;
using Platform.Modules.Devices.Services;
using Platform.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 备份模块全链路：命名空间配额、内联/转存阈值、下载（ETag/Range/RFC 5987）、
/// 兼容矩阵与下发回执、软删除与跨应用隔离。
/// </summary>
public sealed class BackupsEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private const string DemoAppKey = "demo";

    private readonly PlatformApiFactory _factory;

    public BackupsEndpointsTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 小于阈值内联存储且下载内容一致并可软删除()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateNamespaceAsync(client, token, "inline-backup", "内联备份");
        const string payload = """{"theme":"dark","retries":3}""";

        var backup = await CreateBackupAsync(
            client, token, "inline-backup", "inline-1", payload, frameworkVersion: "1.0.0");

        Assert.True(backup.HasInline);
        Assert.False(backup.HasFile);
        Assert.Equal(payload.Length, backup.SizeBytes);
        Assert.NotNull(backup.Sha256);
        Assert.Equal(BackupConventions.DefaultChannel, backup.Channel);

        var list = await IdentityTestHelpers.ReadDataAsync<PageResult<BackupDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/backups?page=1&pageSize=20", token)));
        Assert.Contains(list.Items, x => x.Id == backup.Id);

        var download = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/admin/backups/{backup.Id}/download", token));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(payload, await download.Content.ReadAsStringAsync());
        Assert.Equal(backup.Sha256, download.Headers.GetValues("X-Content-Sha256").Single());

        await IdentityTestHelpers.ReadDataAsync<BackupDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Delete, $"/api/v1/admin/backups/{backup.Id}", token)));

        var gone = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/admin/backups/{backup.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.BackupsNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(gone, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task 超过64KB转存且下载支持ETag与Range与中文文件名()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateNamespaceAsync(client, token, "blob-backup", "大备份");
        var payload = "\"" + new string('x', (64 * 1024) + 1024) + "\"";
        Assert.True(payload.Length > BackupConventions.InlineMaxBytes);

        var backup = await CreateBackupAsync(
            client, token, "blob-backup", "中文配置备份", payload, frameworkVersion: "1.0.0");
        Assert.False(backup.HasInline);
        Assert.True(backup.HasFile);
        Assert.NotNull(backup.FileName);
        Assert.Contains("中文配置备份", backup.FileName, StringComparison.Ordinal);

        var download = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/admin/backups/{backup.Id}/download", token));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal($"\"{backup.Sha256}\"", download.Headers.ETag!.Tag);
        Assert.Equal(payload, await download.Content.ReadAsStringAsync());
        // RFC 5987：中文文件名走 filename*，客户端不会拿到乱码
        Assert.NotNull(download.Content.Headers.ContentDisposition);
        Assert.False(string.IsNullOrEmpty(download.Content.Headers.ContentDisposition!.FileNameStar));

        using var rangeRequest = IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/admin/backups/{backup.Id}/download", token);
        rangeRequest.Headers.Range = new RangeHeaderValue(1, 3);
        var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal("xxx", await range.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 配额渠道与载荷校验分别在写入前拒绝()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateNamespaceAsync(client, token, "quota-backup", "配额", maxVersions: 1);
        await CreateBackupAsync(client, token, "quota-backup", "first", """{"a":1}""");

        var overQuota = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/backups",
            token,
            new CreateBackupRequest(
                "quota-backup", "config", "second", null, null, 1, "1.0.0", null, """{"a":2}""", null)));
        Assert.Equal(
            (int)ApiErrorCode.BackupsVersionQuotaExceeded,
            await IdentityTestHelpers.ReadErrorCodeAsync(overQuota, HttpStatusCode.Conflict));

        await CreateNamespaceAsync(client, token, "channel-backup", "渠道");
        var mismatch = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/backups",
            token,
            new CreateBackupRequest(
                "channel-backup", "config", "beta-payload", null, null, 1, "1.0.0", "beta",
                """{"a":1}""", null)));
        Assert.Equal(
            (int)ApiErrorCode.BackupsChannelMismatch,
            await IdentityTestHelpers.ReadErrorCodeAsync(mismatch, HttpStatusCode.Conflict));

        var badSha = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/backups",
            token,
            new CreateBackupRequest(
                "channel-backup", "config", "bad-sha", null, null, 1, "1.0.0", null,
                """{"a":1}""", new string('0', 64))));
        Assert.Equal(
            (int)ApiErrorCode.BackupsPayloadInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(badSha, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task 下发需要在线连接且兼容矩阵与回执按超时生效()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var coordinator = _factory.Services.GetRequiredService<IPlatformPushCoordinator>();
        var registry = _factory.Services.GetRequiredService<InstallationConnectionRegistry>();

        await CreateNamespaceAsync(client, token, "push-backup", "下发");
        var backup = await CreateBackupAsync(
            client, token, "push-backup", "push-source", """{"a":1}""", frameworkVersion: "1.0.0");

        // 目标留空且未显式广播：直接拒绝，不再隐式下发到全部在线连接
        var blankTarget = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/backups/{backup.Id}/push",
            token,
            new PushBackupRequest(null, null, "1.1.0", false, false, null, false)));
        Assert.Equal(
            (int)ApiErrorCode.ValidationFailed,
            await IdentityTestHelpers.ReadErrorCodeAsync(blankTarget, HttpStatusCode.BadRequest));

        var noConnection = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/backups/{backup.Id}/push",
            token,
            new PushBackupRequest(
                null, null, "1.1.0", false, false, null, false, BroadcastToApp: true)));
        Assert.Equal(
            (int)ApiErrorCode.BackupsPushRejected,
            await IdentityTestHelpers.ReadErrorCodeAsync(noConnection, HttpStatusCode.Conflict));

        registry.Register(DemoAppKey, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "push-test-connection");
        try
        {
            var channelMismatch = await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/backups/{backup.Id}/push",
                token,
                new PushBackupRequest(
                    null, "beta", "1.1.0", false, true, null, false, BroadcastToApp: true)));
            Assert.Equal(
                (int)ApiErrorCode.BackupsChannelMismatch,
                await IdentityTestHelpers.ReadErrorCodeAsync(channelMismatch, HttpStatusCode.Conflict));

            var incompatible = await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/backups/{backup.Id}/push",
                token,
                new PushBackupRequest(
                    null, null, "2.0.0", false, false, null, false, BroadcastToApp: true)));
            Assert.Equal(
                (int)ApiErrorCode.BackupsSchemaIncompatible,
                await IdentityTestHelpers.ReadErrorCodeAsync(incompatible, HttpStatusCode.UnprocessableEntity));

            // 广播没有单一目标安装实例：带"目标当前哈希"无法落到某个安装实例，直接拒绝
            var broadcastWithHash = await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/backups/{backup.Id}/push",
                token,
                new PushBackupRequest(
                    null, null, "1.1.0", false, false, new string('a', 64), false,
                    BroadcastToApp: true)));
            Assert.Equal(
                (int)ApiErrorCode.ValidationFailed,
                await IdentityTestHelpers.ReadErrorCodeAsync(broadcastWithHash, HttpStatusCode.BadRequest));

            // 目标回执：先在后台排一个 ack，再发起下发
            var ackTask = Task.Run(async () =>
            {
                await Task.Delay(200);
                coordinator.CompleteAck(DemoAppKey, backup.Id, true, "applied");
            });
            var pushed = await IdentityTestHelpers.ReadDataAsync<PushBackupResult>(
                await client.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    $"/api/v1/admin/backups/{backup.Id}/push",
                    token,
                    new PushBackupRequest(
                        null, null, "1.1.0", false, false, null, false,
                        BroadcastToApp: true))));
            await ackTask;
            Assert.True(pushed.Acked);
            Assert.True(pushed.Success);
            Assert.Null(pushed.SnapshotBackupId);
            Assert.Null(pushed.Targets);

            // 没有任何回执：按配置的 1 秒超时返回 504
            var timeout = await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/backups/{backup.Id}/push",
                token,
                new PushBackupRequest(
                    null, null, "1.1.0", false, false, null, false, BroadcastToApp: true)));
            Assert.Equal(
                (int)ApiErrorCode.BackupsAckTimeout,
                await IdentityTestHelpers.ReadErrorCodeAsync(timeout, HttpStatusCode.GatewayTimeout));
        }
        finally
        {
            registry.Unregister("push-test-connection");
        }
    }

    [Fact]
    public async Task 命名空间删除按备份留痕受控()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var busy = await CreateNamespaceAsync(client, token, "deletable-busy", "待清理命名空间");
        var backup = await CreateBackupAsync(client, token, "deletable-busy", "配置一", "payload");

        var rejected = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/backups/namespaces/{busy.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.Conflict,
            await IdentityTestHelpers.ReadErrorCodeAsync(rejected, HttpStatusCode.Conflict));

        // 软删除的备份在保留期清理前仍算留痕
        await IdentityTestHelpers.ReadDataAsync<BackupDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Delete, $"/api/v1/admin/backups/{backup.Id}", token)));
        var stillBusy = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/backups/namespaces/{busy.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.Conflict,
            await IdentityTestHelpers.ReadErrorCodeAsync(stillBusy, HttpStatusCode.Conflict));

        // 空命名空间可直接删除
        var empty = await CreateNamespaceAsync(client, token, "deletable-empty", "空命名空间");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/backups/namespaces/{empty.Id}", token))).StatusCode);

        var listed = await IdentityTestHelpers.ReadDataAsync<List<BackupNamespaceDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/backups/namespaces", token)));
        Assert.DoesNotContain(listed, x => x.Id == empty.Id);

        var gone = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/backups/namespaces/{empty.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.BackupsNamespaceNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(gone, HttpStatusCode.NotFound));
    }

    private static async Task<BackupNamespaceDto> CreateNamespaceAsync(
        HttpClient client,
        string token,
        string key,
        string name,
        int? maxVersions = null)
    {
        return await IdentityTestHelpers.ReadDataAsync<BackupNamespaceDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/backups/namespaces",
                token,
                new CreateBackupNamespaceRequest(key, name, "集成测试", null, maxVersions))));
    }

    private static async Task<BackupDto> CreateBackupAsync(
        HttpClient client,
        string token,
        string namespaceKey,
        string name,
        string payload,
        string frameworkVersion = "1.0.0",
        Guid? installationId = null)
    {
        return await IdentityTestHelpers.ReadDataAsync<BackupDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/backups",
                token,
                new CreateBackupRequest(
                    namespaceKey,
                    BackupConventions.KindConfig,
                    name,
                    installationId,
                    null,
                    1,
                    frameworkVersion,
                    null,
                    payload,
                    null))));
    }
}
