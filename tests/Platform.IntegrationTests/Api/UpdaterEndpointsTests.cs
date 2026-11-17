using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 更新器版本线：独立登记、客户端查询/下载、语义版本排序、同 RID 优先与归档回退。
/// 每个用例用独立宿主，避免「最新更新器」查询互相影响。
/// </summary>
public sealed class UpdaterEndpointsTests
{
    [Fact]
    public async Task 更新器登记后可查询下载与归档回退()
    {
        using var factory = new PlatformApiFactory();
        using var client = factory.CreateClient();
        var token = await LoginAsync(client);

        var payload = "updater-binary-2"u8.ToArray();
        var zip = BuildUpdaterZip("2.0.0", "win-x64", ("Updater.exe", payload));
        var sha = await UploadAsync(client, token, zip);

        var registered = await RegisterAsync(client, token, new RegisterUpdaterRequest(
            "win-x64",
            Version: null,
            FileName: "AppFramework.Updater-2.0.0.zip",
            Sha256: sha,
            SizeBytes: zip.Length));

        Assert.Equal("2.0.0", registered.Version);
        Assert.Equal("win-x64", registered.Target);
        Assert.Equal(VersionConventions.StatusPublished, registered.Status);
        Assert.NotNull(registered.PublishedAt);
        Assert.Null(registered.ManifestHash);

        var listed = await ReadDataAsync<PageResult<UpdaterInfoDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/versions/updaters?target=win-x64",
                token)));
        Assert.Contains(listed.Items, x => x.Id == registered.Id);

        var latest = await ReadDataAsync<UpdaterInfoDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/client/versions/updater?runtimeIdentifier=win-x64",
                token)));
        Assert.Equal(registered.Id, latest.Id);
        Assert.Equal("2.0.0", latest.Version);

        var download = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/updaters/{registered.Id}/download",
            token));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal($"\"{sha}\"", download.Headers.ETag!.Tag);
        Assert.Equal(sha, download.Headers.GetValues("X-Content-Sha256").Single());
        Assert.Contains(
            "AppFramework.Updater-2.0.0.zip",
            download.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal(zip, await download.Content.ReadAsByteArrayAsync());

        using var rangeRequest = IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/updaters/{registered.Id}/download",
            token);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);
        var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);

        using var cachedRequest = IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/updaters/{registered.Id}/download",
            token);
        cachedRequest.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{sha}\""));
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(cachedRequest)).StatusCode);

        // 同内容重复登记：复用同一行
        var replay = await RegisterAsync(client, token, new RegisterUpdaterRequest(
            "win-x64", "2.0.0", "AppFramework.Updater-2.0.0.zip", sha, zip.Length));
        Assert.Equal(registered.Id, replay.Id);

        // 同版本不同内容：冲突
        var otherZip = BuildUpdaterZip("2.0.0", "win-x64", ("Updater.exe", "other-binary"u8.ToArray()));
        var otherSha = await UploadAsync(client, token, otherZip);
        var conflict = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/updaters",
            token,
            new RegisterUpdaterRequest("win-x64", null, "AppFramework.Updater-2.0.0.zip", otherSha, otherZip.Length)));
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageManifestConflict,
            await IdentityTestHelpers.ReadErrorCodeAsync(conflict, HttpStatusCode.Conflict));

        // 本体没有版本时也返回 200，只带更新器
        const string channelKey = "upd-check";
        _ = await CreateChannelAsync(client, token, channelKey);
        var check = await CheckAsync(client, token, channelKey, "1.0.0", "1.0.0");
        Assert.False(check.HasUpdate);
        Assert.Null(check.Release);
        Assert.Null(check.LatestVersion);
        Assert.True(check.HasUpdaterUpdate);
        Assert.NotNull(check.Updater);
        Assert.Equal("2.0.0", check.Updater!.Version);

        var upToDate = await CheckAsync(client, token, channelKey, "1.0.0", "2.0.0");
        Assert.False(upToDate.HasUpdaterUpdate);

        var archived = await ReadDataAsync<UpdaterInfoDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/versions/updaters/{registered.Id}/archive",
                token)));
        Assert.Equal(VersionConventions.StatusArchived, archived.Status);

        var missing = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            "/api/v1/client/versions/updater?runtimeIdentifier=win-x64",
            token));
        Assert.Equal(
            (int)ApiErrorCode.VersionsUpdaterNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(missing, HttpStatusCode.NotFound));

        var archivedDownload = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/updaters/{registered.Id}/download",
            token));
        Assert.Equal(
            (int)ApiErrorCode.VersionsUpdaterNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(archivedDownload, HttpStatusCode.NotFound));

        // 归档后既无版本也无更新器：恢复 212 口径
        var noContent = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/versions/check",
            token,
            new CheckUpdateRequest(channelKey, "1.0.0", "win-x64", "1.0.0")));
        Assert.Equal(
            (int)ApiErrorCode.VersionsNoPublishedRelease,
            await IdentityTestHelpers.ReadErrorCodeAsync(noContent, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task 管理台更新器下载票据把非zip打成zip且归档后失效()
    {
        using var factory = new PlatformApiFactory();
        using var client = factory.CreateClient();
        var token = await LoginAsync(client);

        var raw = "raw-updater-binary"u8.ToArray();
        var sha = await UploadAsync(client, token, raw);
        var registered = await RegisterAsync(client, token, new RegisterUpdaterRequest(
            "win-x64",
            "5.0.0",
            "AppFramework.Updater-5.0.0.bin",
            sha,
            raw.Length));

        var ticket = await ReadDataAsync<DownloadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/client/versions/updaters/{registered.Id}/download-ticket",
                token)));
        Assert.StartsWith("/api/v1/client/versions/downloads/", ticket.Url);
        Assert.Equal("AppFramework.Updater-5.0.0.bin.zip", ticket.FileName);
        Assert.True(ticket.SizeBytes > raw.Length);

        var download = await client.GetAsync(ticket.Url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"\"{sha}-zip\"", download.Headers.ETag!.Tag);
        Assert.Equal(sha, download.Headers.GetValues("X-Content-Sha256").Single());

        var zipBytes = await download.Content.ReadAsByteArrayAsync();
        using (var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
        {
            var entry = Assert.Single(archive.Entries);
            Assert.Equal("AppFramework.Updater-5.0.0.bin", entry.FullName);
            using var entryStream = entry.Open();
            using var entryBuffer = new MemoryStream();
            await entryStream.CopyToAsync(entryBuffer);
            Assert.Equal(raw, entryBuffer.ToArray());
        }

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, ticket.Url);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);
        var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal($"bytes 0-3/{zipBytes.Length}", range.Content.Headers.ContentRange!.ToString());

        // SDK 原始端点保持二进制原样
        var rawDownload = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/updaters/{registered.Id}/download",
            token));
        Assert.Equal("application/octet-stream", rawDownload.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"\"{sha}\"", rawDownload.Headers.ETag!.Tag);
        Assert.Equal(raw, await rawDownload.Content.ReadAsByteArrayAsync());

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsync(
                $"/api/v1/client/versions/updaters/{registered.Id}/download-ticket",
                content: null)).StatusCode);

        // 票据只证明「谁签发过」，GET 仍按当前状态校验：归档后立即失效
        await ArchiveAsync(client, token, registered.Id);
        var archived = await client.GetAsync(ticket.Url);
        Assert.Equal(
            (int)ApiErrorCode.VersionsUpdaterNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(archived, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task 同RID优先且归档后逐级回退()
    {
        using var factory = new PlatformApiFactory();
        using var client = factory.CreateClient();
        var token = await LoginAsync(client);

        var any = await RegisterVersionAsync(client, token, "1.0.0", null, ("Updater.exe", "any-updater"u8.ToArray()));
        var older = await RegisterVersionAsync(client, token, "0.9.0", "win-x64", ("Updater.exe", "old-updater"u8.ToArray()));
        var newest = await RegisterVersionAsync(client, token, "3.0.0", "win-x64", ("Updater.exe", "new-updater"u8.ToArray()));

        var winLatest = await ReadDataAsync<UpdaterInfoDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/client/versions/updater?runtimeIdentifier=win-x64",
                token)));
        Assert.Equal(newest.Id, winLatest.Id);

        // any 包对其他 RID 兜底
        var linuxLatest = await ReadDataAsync<UpdaterInfoDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/client/versions/updater?runtimeIdentifier=linux-x64",
                token)));
        Assert.Equal(any.Id, linuxLatest.Id);

        // any 本身也是合法 target：管理台按「兜底档」查最新版本时不能报 400。
        Assert.Equal(any.Id, (await GetLatestAsync(client, token, VersionConventions.PackageTargetAny)).Id);

        var unsupported = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            "/api/v1/client/versions/updater?runtimeIdentifier=win-mips",
            token));
        Assert.Equal(
            (int)ApiErrorCode.VersionsRuntimeIdentifierUnsupported,
            await IdentityTestHelpers.ReadErrorCodeAsync(unsupported, HttpStatusCode.BadRequest));

        const string channelKey = "upd-order";
        _ = await CreateChannelAsync(client, token, channelKey);
        Assert.True((await CheckAsync(client, token, channelKey, "1.0.0", "2.9.9")).HasUpdaterUpdate);
        Assert.False((await CheckAsync(client, token, channelKey, "1.0.0", "3.0.0")).HasUpdaterUpdate);
        Assert.False((await CheckAsync(client, token, channelKey, "1.0.0", "9.0.0")).HasUpdaterUpdate);
        Assert.True((await CheckAsync(client, token, channelKey, "1.0.0", null)).HasUpdaterUpdate);

        await ArchiveAsync(client, token, newest.Id);
        Assert.Equal(
            older.Id,
            (await GetLatestAsync(client, token)).Id);
    }

    [Fact]
    public async Task 更新器包类型与版本来源受限()
    {
        using var factory = new PlatformApiFactory();
        using var client = factory.CreateClient();
        var token = await LoginAsync(client);

        // 清单 kind 不是 updater
        var fullZip = BuildZip(
            ("af-package.json", """
            {
              "schema": "af-package/1",
              "kind": "full",
              "version": "1.0.0"
            }
            """),
            ("App.exe", "not-an-updater"u8.ToArray()));
        var fullSha = await UploadAsync(client, token, fullZip);
        var kindMismatch = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/updaters",
            token,
            new RegisterUpdaterRequest("win-x64", null, "updater.zip", fullSha, fullZip.Length)));
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageKindUnsupported,
            await IdentityTestHelpers.ReadErrorCodeAsync(kindMismatch, HttpStatusCode.BadRequest));

        // 非 PE 且未显式给版本：无法确定版本
        var rawBytes = "plain-binary-without-version"u8.ToArray();
        var rawSha = await UploadAsync(client, token, rawBytes);
        var unknownVersion = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/updaters",
            token,
            new RegisterUpdaterRequest("win-x64", null, "updater.bin", rawSha, rawBytes.Length)));
        Assert.Equal(HttpStatusCode.BadRequest, unknownVersion.StatusCode);

        // 显式版本可用；target 缺省回退 any
        var withVersion = await ReadDataAsync<UpdaterInfoDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/updaters",
                token,
                new RegisterUpdaterRequest(null, "4.0.0", "updater.bin", rawSha, rawBytes.Length))));
        Assert.Equal("4.0.0", withVersion.Version);
        Assert.Equal(VersionConventions.PackageTargetAny, withVersion.Target);
    }

    private static async Task<UpdaterInfoDto> RegisterVersionAsync(
        HttpClient client,
        string token,
        string version,
        string? target,
        params (string Name, byte[] Content)[] files)
    {
        var zip = BuildUpdaterZip(version, target, files);
        var sha = await UploadAsync(client, token, zip);
        return await RegisterAsync(client, token, new RegisterUpdaterRequest(
            target,
            Version: null,
            FileName: $"AppFramework.Updater-{version}.zip",
            Sha256: sha,
            SizeBytes: zip.Length));
    }

    private static async Task ArchiveAsync(HttpClient client, string token, Guid id) =>
        await ReadDataAsync<UpdaterInfoDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/versions/updaters/{id}/archive",
                token)));

    private static async Task<UpdaterInfoDto> GetLatestAsync(HttpClient client, string token, string rid = "win-x64") =>
        await ReadDataAsync<UpdaterInfoDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/client/versions/updater?runtimeIdentifier={rid}",
                token)));

    private static byte[] BuildUpdaterZip(
        string version,
        string? target,
        params (string Name, byte[] Content)[] files)
    {
        var targetField = target is null ? string.Empty : $",\n  \"runtimeIdentifier\": \"{target}\"";
        var manifest = $$"""
        {
          "schema": "af-package/1",
          "kind": "updater",
          "version": "{{version}}"{{targetField}},
          "entryPoint": "Updater.exe",
          "files": []
        }
        """;
        return BuildZip([("af-package.json", Encoding.UTF8.GetBytes(manifest)), .. files]);
    }

    private static byte[] BuildZip(params (string Name, object Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(content is string text ? Encoding.UTF8.GetBytes(text) : (byte[])content);
            }
        }

        return buffer.ToArray();
    }

    private static async Task<UpdaterInfoDto> RegisterAsync(
        HttpClient client,
        string token,
        RegisterUpdaterRequest request) =>
        await ReadDataAsync<UpdaterInfoDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/updaters",
                token,
                request)));

    private static async Task<CheckUpdateResponse> CheckAsync(
        HttpClient client,
        string token,
        string channelKey,
        string? currentVersion,
        string? updaterVersion) =>
        await ReadDataAsync<CheckUpdateResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/client/versions/check",
                token,
                new CheckUpdateRequest(channelKey, currentVersion, "win-x64", updaterVersion))));

    private static async Task<ReleaseChannelDto> CreateChannelAsync(
        HttpClient client,
        string token,
        string key) =>
        await ReadDataAsync<ReleaseChannelDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/channels",
                token,
                new CreateReleaseChannelRequest(key, $"{key} 渠道", "集成测试"))));

    private static async Task<string> LoginAsync(HttpClient client) =>
        await IdentityTestHelpers.LoginAsAdminAsync(client);

    private static async Task<string> UploadAsync(HttpClient client, string token, byte[] content)
    {
        var sha = Sha256(content);
        var ticket = await ReadDataAsync<UploadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/uploads",
                token,
                new CreateUploadTicketRequest(sha, content.Length))));

        using var request = new HttpRequestMessage(HttpMethod.Put, ticket.UploadUrl)
        {
            Content = new ByteArrayContent(content),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var result = await ReadDataAsync<UploadResultDto>(await client.SendAsync(request));
        Assert.Equal(sha, result.Sha256);
        return sha;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response) =>
        await IdentityTestHelpers.ReadDataAsync<T>(response);

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
