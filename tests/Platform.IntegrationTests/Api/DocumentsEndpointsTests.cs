using System.Net;
using System.Net.Http.Headers;
using Platform.Contracts;
using Platform.Contracts.Documents;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 文档模块全链路：服务端强制可见性、客户端清单过滤、修订历史、下载与软删除。
/// </summary>
public sealed class DocumentsEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public DocumentsEndpointsTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 默认内部可见且放开后才能进入客户端清单并下载()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateCollectionAsync(client, token, "manual", "用户手册");
        var content = "manual-v1"u8.ToArray();
        var document = await UploadAsync(
            client, token, "manual", "使用手册.pdf", content, visibility: null, title: "使用手册");

        Assert.Equal(DocumentConventions.VisibilityInternal, document.Visibility);
        Assert.Equal(1, document.Revision);
        Assert.Single(document.Revisions);

        var hidden = await GetManifestAsync(client, token);
        Assert.DoesNotContain(hidden.Items, x => x.DocumentId == document.Id);

        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/client/documents/{document.Id}/download", token));
        Assert.Equal(
            (int)ApiErrorCode.DocumentsVisibilityDenied,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.Forbidden));

        await IdentityTestHelpers.ReadDataAsync<DocumentDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/documents/{document.Id}",
                token,
                new UpdateDocumentRequest(null, DocumentConventions.VisibilityClient))));

        var manifest = await GetManifestAsync(client, token);
        var item = Assert.Single(manifest.Items, x => x.DocumentId == document.Id);
        Assert.Equal("manual", item.CollectionKey);
        Assert.Equal("使用手册", item.Title);
        Assert.Equal(content.Length, item.SizeBytes);

        var download = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, item.DownloadUrl, token));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(content, await download.Content.ReadAsByteArrayAsync());
        // RFC 5987：中文文件名
        Assert.NotNull(download.Content.Headers.ContentDisposition);
        Assert.False(string.IsNullOrEmpty(download.Content.Headers.ContentDisposition!.FileNameStar));
    }

    [Fact]
    public async Task 适用版本可上传更新清空并随客户端清单下发()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateCollectionAsync(client, token, "release-guide", "发布手册");

        var legacy = await UploadAsync(
            client, token, "release-guide", "legacy.txt", "legacy"u8.ToArray());
        Assert.Null(legacy.ApplicableVersion);

        var document = await UploadAsync(
            client, token, "release-guide", "guide.txt", "guide-v3"u8.ToArray(),
            visibility: DocumentConventions.VisibilityClient,
            applicableVersion: "3.2.0");
        Assert.Equal("3.2.0", document.ApplicableVersion);

        var manifestItem = Assert.Single(
            (await GetManifestAsync(client, token)).Items,
            x => x.DocumentId == document.Id);
        Assert.Equal("3.2.0", manifestItem.ApplicableVersion);

        var updated = await IdentityTestHelpers.ReadDataAsync<DocumentDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/documents/{document.Id}",
                token,
                new UpdateDocumentRequest(null, null, "3.3.0"))));
        Assert.Equal("3.3.0", updated.ApplicableVersion);

        var tooLong = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/documents/{document.Id}",
            token,
            new UpdateDocumentRequest(
                null,
                null,
                new string('v', DocumentConventions.MaxApplicableVersionLength + 1))));
        Assert.Equal(
            (int)ApiErrorCode.ValidationFailed,
            await IdentityTestHelpers.ReadErrorCodeAsync(tooLong, HttpStatusCode.BadRequest));

        var cleared = await IdentityTestHelpers.ReadDataAsync<DocumentDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/documents/{document.Id}",
                token,
                new UpdateDocumentRequest(null, null, string.Empty))));
        Assert.Null(cleared.ApplicableVersion);

        var list = await IdentityTestHelpers.ReadDataAsync<PageResult<DocumentSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/documents?collectionKey=release-guide", token)));
        Assert.Null(list.Items.Single(x => x.Id == legacy.Id).ApplicableVersion);
        Assert.Null(list.Items.Single(x => x.Id == document.Id).ApplicableVersion);
    }

    [Fact]
    public async Task 每次上传生成新修订且旧修订仍可下载删除后不可见()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateCollectionAsync(client, token, "changelog", "变更记录");
        var first = "release-notes-v1"u8.ToArray();
        var second = "release-notes-v2-longer"u8.ToArray();

        var document = await UploadAsync(client, token, "changelog", "notes.txt", first);
        var updated = await AddRevisionAsync(client, token, document.Id, "notes.txt", second);

        Assert.Equal(2, updated.Revision);
        Assert.Equal(2, updated.Revisions.Count);
        Assert.Contains(updated.Revisions, x => x.Revision == 1);
        Assert.Contains(updated.Revisions, x => x.Revision == 2);

        var latest = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/admin/documents/{document.Id}/download", token));
        Assert.Equal(second, await latest.Content.ReadAsByteArrayAsync());

        var revisionOne = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/admin/documents/{document.Id}/revisions/1/download",
            token));
        Assert.Equal(first, await revisionOne.Content.ReadAsByteArrayAsync());

        var missingRevision = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/admin/documents/{document.Id}/revisions/9/download",
            token));
        Assert.Equal(
            (int)ApiErrorCode.DocumentsRevisionNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(missingRevision, HttpStatusCode.NotFound));

        await IdentityTestHelpers.ReadDataAsync<DocumentSummaryDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Delete, $"/api/v1/admin/documents/{document.Id}", token)));

        var gone = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/admin/documents/{document.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.DocumentsNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(gone, HttpStatusCode.NotFound));

        var goneDownload = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/admin/documents/{document.Id}/download", token));
        Assert.Equal(
            (int)ApiErrorCode.DocumentsNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(goneDownload, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task 集合停用后客户端清单与下载都被服务端拒绝()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var collection = await CreateCollectionAsync(client, token, "restricted", "受控资料");
        var document = await UploadAsync(
            client, token, "restricted", "policy.md", "policy"u8.ToArray(),
            visibility: DocumentConventions.VisibilityClient);

        Assert.Contains((await GetManifestAsync(client, token)).Items, x => x.DocumentId == document.Id);

        await IdentityTestHelpers.ReadDataAsync<DocumentCollectionDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/documents/collections/{collection.Id}",
                token,
                new UpdateDocumentCollectionRequest(null, null, false))));

        Assert.DoesNotContain((await GetManifestAsync(client, token)).Items, x => x.DocumentId == document.Id);

        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/client/documents/{document.Id}/download", token));
        Assert.Equal(
            (int)ApiErrorCode.DocumentsVisibilityDenied,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.Forbidden));
    }

    private static async Task<DocumentCollectionDto> CreateCollectionAsync(
        HttpClient client, string token, string key, string name) =>
        await IdentityTestHelpers.ReadDataAsync<DocumentCollectionDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/documents/collections",
                token,
                new CreateDocumentCollectionRequest(key, name, "集成测试"))));

    private static async Task<ClientDocumentManifest> GetManifestAsync(HttpClient client, string token) =>
        await IdentityTestHelpers.ReadDataAsync<ClientDocumentManifest>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/client/documents/manifest", token)));

    private static async Task<DocumentDetailDto> UploadAsync(
        HttpClient client,
        string token,
        string collectionKey,
        string fileName,
        byte[] content,
        string? visibility = null,
        string? title = null,
        string? applicableVersion = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(collectionKey), "collectionKey" },
            { new StringContent(fileName), "fileName" },
        };
        if (visibility is not null)
        {
            form.Add(new StringContent(visibility), "visibility");
        }

        if (title is not null)
        {
            form.Add(new StringContent(title), "title");
        }

        if (applicableVersion is not null)
        {
            form.Add(new StringContent(applicableVersion), "applicableVersion");
        }

        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/documents")
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Assert.Fail($"upload failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        return await IdentityTestHelpers.ReadDataAsync<DocumentDetailDto>(response);
    }

    private static async Task<DocumentDetailDto> AddRevisionAsync(
        HttpClient client, string token, Guid documentId, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", fileName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/admin/documents/{documentId}/revisions")
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await IdentityTestHelpers.ReadDataAsync<DocumentDetailDto>(await client.SendAsync(request));
    }
}
