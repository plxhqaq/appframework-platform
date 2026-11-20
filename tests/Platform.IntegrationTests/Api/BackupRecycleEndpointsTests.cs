using System.Net;
using Platform.Contracts;
using Platform.Contracts.Backups;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.Contracts.Retention;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>备份回收站端点：删除→列表→恢复→回到列表；文件缺失拒绝恢复；清理与权限门禁。</summary>
public sealed class BackupRecycleEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public BackupRecycleEndpointsTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 删除后在回收站可见且可恢复到列表()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateNamespaceAsync(client, token, "recycle-inline");
        const string payload = """{"theme":"dark"}""";
        var backup = await CreateBackupAsync(client, token, "recycle-inline", "recycle-1", payload);
        await DeleteBackupAsync(client, token, backup.Id);

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<DeletedBackupDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/backups/deleted?page=1&pageSize=20", token)));
        var row = Assert.Single(page.Items, x => x.Id == backup.Id);
        Assert.True(row.FileIntact);
        Assert.InRange(row.RemainingGraceMinutes, 0, 1);

        var restored = await IdentityTestHelpers.ReadDataAsync<BackupDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/backups/{backup.Id}/restore", token)));
        Assert.Equal(backup.Name, restored.Name);

        var list = await IdentityTestHelpers.ReadDataAsync<PageResult<BackupDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/backups?page=1&pageSize=20", token)));
        Assert.Contains(list.Items, x => x.Id == backup.Id);
    }

    [Fact]
    public async Task 文件被回收后拒绝恢复且可立即清理()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateNamespaceAsync(client, token, "recycle-blob");
        var payload = "\"" + new string('x', (64 * 1024) + 1024) + "\"";
        var backup = await CreateBackupAsync(client, token, "recycle-blob", "recycle-big", payload);
        await DeleteBackupAsync(client, token, backup.Id);

        // 只删这一条备份的物理文件：同一宿主的其它用例仍在共享同一个 Blob 根目录。
        var blobFiles = Directory.GetFiles(
            _factory.BlobRoot, $"{backup.Id:N}*.json", SearchOption.AllDirectories);
        Assert.NotEmpty(blobFiles);
        foreach (var file in blobFiles)
        {
            File.Delete(file);
        }

        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post, $"/api/v1/admin/backups/{backup.Id}/restore", token));
        Assert.Equal(
            (int)ApiErrorCode.BackupsBlobReclaimed,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.Conflict));

        var purged = await IdentityTestHelpers.ReadDataAsync<PurgeResultDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/backups/{backup.Id}/purge", token)));
        Assert.Equal(1, purged.RowsDeleted);

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<DeletedBackupDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/backups/deleted?page=1&pageSize=20", token)));
        Assert.DoesNotContain(page.Items, x => x.Id == backup.Id);
    }

    [Fact]
    public async Task 全部清理删除当前应用回收站行()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateNamespaceAsync(client, token, "recycle-all");
        var first = await CreateBackupAsync(client, token, "recycle-all", "all-1", """{"a":1}""");
        var second = await CreateBackupAsync(client, token, "recycle-all", "all-2", """{"a":2}""");
        await DeleteBackupAsync(client, token, first.Id);
        await DeleteBackupAsync(client, token, second.Id);

        var purged = await IdentityTestHelpers.ReadDataAsync<PurgeResultDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, "/api/v1/admin/backups/purge-all", token)));
        Assert.Equal(2, purged.RowsDeleted);

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<DeletedBackupDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/backups/deleted?page=1&pageSize=20", token)));
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task 只读角色看不到回收站也删不掉()
    {
        using var client = _factory.CreateClient();
        var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(client);

        // 应用内只读角色：能进应用、能读备份，但没有恢复/清理权限。
        var readerRole = await IdentityTestHelpers.ReadDataAsync<RoleSummaryDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/identity/roles",
                adminToken,
                new CreateRoleRequest(
                    "recycle-reader",
                    DisplayName: "备份只读",
                    Scope: IdentityConventions.ScopeApp,
                    AppKey: IdentityConventions.DemoAppKey,
                    Permissions: [PermissionCatalog.BackupNamespaceRead, PermissionCatalog.BackupRead]))));
        await IdentityTestHelpers.CreateUserAsync(
            client, adminToken, "recycle-reader", "Viewer@12345", roleIds: [readerRole.Id]);
        var viewerToken = (await IdentityTestHelpers.LoginAsync(
            client, "recycle-reader", "Viewer@12345", IdentityConventions.DemoAppKey)).AccessToken;

        var list = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/admin/backups/deleted?page=1&pageSize=20", viewerToken));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(list, HttpStatusCode.Forbidden));

        var purge = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post, $"/api/v1/admin/backups/{Guid.NewGuid()}/purge", viewerToken));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(purge, HttpStatusCode.Forbidden));
    }

    private static async Task CreateNamespaceAsync(HttpClient client, string token, string key)
    {
        await IdentityTestHelpers.ReadDataAsync<BackupNamespaceDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/backups/namespaces",
                token,
                new CreateBackupNamespaceRequest(key, key, null, null, null))));
    }

    private static async Task<BackupDto> CreateBackupAsync(
        HttpClient client, string token, string namespaceKey, string name, string payload) =>
        await IdentityTestHelpers.ReadDataAsync<BackupDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/backups",
                token,
                new CreateBackupRequest(
                    namespaceKey, BackupConventions.KindConfig, name, null, null, 1, "1.0.0", null, payload, null))));

    private static async Task DeleteBackupAsync(HttpClient client, string token, Guid id)
    {
        var response = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/backups/{id}", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
