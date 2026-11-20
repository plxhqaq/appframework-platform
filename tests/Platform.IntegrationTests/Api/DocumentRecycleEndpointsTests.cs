using System.Net;
using System.Net.Http.Headers;
using Platform.Contracts;
using Platform.Contracts.Documents;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.Contracts.Retention;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>文档回收站端点：删除→列表→恢复；文件缺失拒绝恢复；清理与权限门禁。</summary>
public sealed class DocumentRecycleEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public DocumentRecycleEndpointsTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 删除后可见且恢复回列表()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateCollectionAsync(client, token, "recycle-docs");
        var document = await UploadAsync(client, token, "recycle-docs", "手册.txt");
        await DeleteAsync(client, token, document.Id);

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<DeletedDocumentDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/documents/deleted?page=1&pageSize=20", token)));
        var row = Assert.Single(page.Items, x => x.Id == document.Id);
        Assert.True(row.FileIntact);
        Assert.Equal("手册.txt", row.FileName);

        var restored = await IdentityTestHelpers.ReadDataAsync<DocumentSummaryDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/documents/{document.Id}/restore", token)));
        Assert.Equal(document.Title, restored.Title);

        var list = await IdentityTestHelpers.ReadDataAsync<PageResult<DocumentSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/documents?page=1&pageSize=20", token)));
        Assert.Contains(list.Items, x => x.Id == document.Id);
    }

    [Fact]
    public async Task 文件缺失拒绝恢复且可立即清理()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateCollectionAsync(client, token, "recycle-docs-gone");
        var document = await UploadAsync(client, token, "recycle-docs-gone", "gone.txt");
        await DeleteAsync(client, token, document.Id);

        // 只删这一条文档自己的物理文件（含修订）：同一宿主的其它用例仍在共享同一个 Blob 根目录。
        var documentDirectory = Path.Combine(
            _factory.BlobRoot,
            "documents",
            IdentityConventions.DemoAppKey,
            "recycle-docs-gone",
            $"{document.Id:N}");
        Assert.True(Directory.Exists(documentDirectory), "文档 blob 目录不存在。");
        Directory.Delete(documentDirectory, recursive: true);

        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post, $"/api/v1/admin/documents/{document.Id}/restore", token));
        Assert.Equal(
            (int)ApiErrorCode.DocumentsFileReclaimed,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.Conflict));

        var purged = await IdentityTestHelpers.ReadDataAsync<PurgeResultDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/documents/{document.Id}/purge", token)));
        Assert.Equal(2, purged.RowsDeleted);

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<DeletedDocumentDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/documents/deleted?page=1&pageSize=20", token)));
        Assert.DoesNotContain(page.Items, x => x.Id == document.Id);
    }

    [Fact]
    public async Task 全部清理删除当前应用回收站行()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateCollectionAsync(client, token, "recycle-docs-all");
        var first = await UploadAsync(client, token, "recycle-docs-all", "a.txt");
        var second = await UploadAsync(client, token, "recycle-docs-all", "b.txt");
        await DeleteAsync(client, token, first.Id);
        await DeleteAsync(client, token, second.Id);

        var purged = await IdentityTestHelpers.ReadDataAsync<PurgeResultDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, "/api/v1/admin/documents/purge-all", token)));
        Assert.Equal(4, purged.RowsDeleted);

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<DeletedDocumentDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/documents/deleted?page=1&pageSize=20", token)));
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task 只读角色看不到回收站也删不掉()
    {
        using var client = _factory.CreateClient();
        var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(client);

        // 应用内只读角色：能进应用、能读文档，但没有恢复/清理权限。
        // 内置 viewer 角色不能登录 demo 应用，这里用应用级只读角色验证 403。
        var readerRole = await IdentityTestHelpers.ReadDataAsync<RoleSummaryDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/identity/roles",
                adminToken,
                new CreateRoleRequest(
                    "doc-recycle-reader-role",
                    DisplayName: "文档只读",
                    Scope: IdentityConventions.ScopeApp,
                    AppKey: IdentityConventions.DemoAppKey,
                    Permissions: [PermissionCatalog.DocumentRead]))));
        await IdentityTestHelpers.CreateUserAsync(
            client, adminToken, "doc-recycle-reader", "Viewer@12345", roleIds: [readerRole.Id]);
        var viewerToken = (await IdentityTestHelpers.LoginAsync(
            client, "doc-recycle-reader", "Viewer@12345", IdentityConventions.DemoAppKey)).AccessToken;

        var list = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/admin/documents/deleted?page=1&pageSize=20", viewerToken));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(list, HttpStatusCode.Forbidden));

        var purge = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post, $"/api/v1/admin/documents/{Guid.NewGuid()}/purge", viewerToken));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(purge, HttpStatusCode.Forbidden));
    }

    private static async Task CreateCollectionAsync(HttpClient client, string token, string key) =>
        await IdentityTestHelpers.ReadDataAsync<DocumentCollectionDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/documents/collections",
                token,
                new CreateDocumentCollectionRequest(key, key, null))));

    private static async Task<DocumentDetailDto> UploadAsync(
        HttpClient client,
        string token,
        string collectionKey,
        string fileName)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(collectionKey), "collectionKey");
        form.Add(new StringContent(Path.GetFileNameWithoutExtension(fileName)), "title");
        form.Add(new StringContent(DocumentConventions.VisibilityClient), "visibility");
        var file = new ByteArrayContent("document"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/documents")
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await IdentityTestHelpers.ReadDataAsync<DocumentDetailDto>(await client.SendAsync(request));
    }

    private static async Task DeleteAsync(HttpClient client, string token, Guid id)
    {
        var response = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/documents/{id}", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
