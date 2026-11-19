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
/// 驱动媒体包链路：af-media/1 清单登记（逐文件复核）、内容复用与键冲突、
/// 激活互斥、下载（Range / ETag / 票据）、删除。
/// </summary>
public sealed class DriverMediaEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public DriverMediaEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 登记媒体包后可下载激活互斥并可删除()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);

        var zip = BuildMediaZip(
            ("drv-a", "A 驱动", "1.0.0",
             [("media/drv-a/a.dll", "alpha-driver-binary"), ("media/drv-a/lib.ini", "[a]\norder=1")],
             ["drv-a"]));
        var sha = await UploadAsync(client, token, zip);

        var first = await RegisterMediaAsync(client, token, new RegisterDriverMediaRequest(
            null, sha, zip.Length, "media-drv-a.zip"));
        Assert.Equal("drv-a", first.MediaKey);
        Assert.Equal("A 驱动", first.Name);
        Assert.Equal("1.0.0", first.Version);
        Assert.True(first.IsActive);
        Assert.Equal(2, first.FileCount);
        Assert.Equal(sha, first.Sha256);
        Assert.Equal("media-drv-a.zip", first.FileName);
        Assert.NotNull(first.ManifestHash);
        Assert.Equal("drv-a", Assert.Single(first.InstallOrder));
        Assert.False(first.Reused);

        var listed = await ReadDataAsync<PageResult<DriverMediaDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/versions/media?mediaKey=drv-a", token)));
        Assert.Contains(listed.Items, x => x.Id == first.Id);

        // 第二个版本登记后不抢占启用位
        var zip2 = BuildMediaZip(("drv-a", "A 驱动", "2.0.0", [("media/drv-a/a.dll", "alpha-v2")], null));
        var sha2 = await UploadAsync(client, token, zip2);
        var second = await RegisterMediaAsync(client, token, new RegisterDriverMediaRequest(
            null, sha2, zip2.Length, "media-drv-a-2.zip"));
        Assert.False(second.IsActive);

        var activated = await ReadDataAsync<DriverMediaDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/versions/media/{second.Id}/activate", token)));
        Assert.True(activated.IsActive);

        var activeOnly = await ReadDataAsync<PageResult<DriverMediaDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/versions/media?mediaKey=drv-a&activeOnly=true", token)));
        Assert.Equal(second.Id, Assert.Single(activeOnly.Items).Id);

        var download = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/client/versions/media/{first.Id}/download", token));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal($"\"{sha}\"", download.Headers.ETag!.Tag);
        Assert.Equal(sha, download.Headers.GetValues("X-Content-Sha256").Single());
        Assert.Contains("media-drv-a.zip", download.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal(zip, await download.Content.ReadAsByteArrayAsync());

        using var rangeRequest = IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/client/versions/media/{first.Id}/download", token);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);
        var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal($"bytes 0-3/{zip.Length}", range.Content.Headers.ContentRange!.ToString());

        using var cachedRequest = IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/client/versions/media/{first.Id}/download", token);
        cachedRequest.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{sha}\""));
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(cachedRequest)).StatusCode);

        // 浏览器票据：zip 原样下发
        var ticket = await ReadDataAsync<DownloadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/client/versions/media/{first.Id}/download-ticket", token)));
        var ticketed = await client.GetAsync(ticket.Url);
        Assert.Equal(HttpStatusCode.OK, ticketed.StatusCode);
        Assert.Equal("application/zip", ticketed.Content.Headers.ContentType!.MediaType);
        Assert.Equal(zip, await ticketed.Content.ReadAsByteArrayAsync());

        await ReadDataAsync<bool>(await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/versions/media/{first.Id}", token)));

        var gone = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/admin/versions/media/{first.Id}", token));
        Assert.Equal((int)ApiErrorCode.VersionsMediaNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(gone, HttpStatusCode.NotFound));

        var goneDownload = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, $"/api/v1/client/versions/media/{first.Id}/download", token));
        Assert.Equal((int)ApiErrorCode.VersionsMediaNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(goneDownload, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task 清单与内容不一致的媒体包被拒绝()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);

        // 清单声明的 sha 与实际文件内容不符
        var (manifest, _) = MediaManifestEntry(("drv-bad", "坏包", null, [("media/drv-bad/x.dll", "real")], null));
        var tampered = BuildZip(
            ("af-media.json", manifest),
            ("media/drv-bad/x.dll", "other-content"));
        var tamperedSha = await UploadAsync(client, token, tampered);
        var tamperResponse = await SendRegisterMediaAsync(client, token, new RegisterDriverMediaRequest(
            null, tamperedSha, tampered.Length, "media-drv-bad.zip"));
        Assert.Equal((int)ApiErrorCode.VersionsMediaManifestInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(tamperResponse, HttpStatusCode.UnprocessableEntity));

        // zip 里有清单未声明的文件
        var undeclared = BuildZip(
            ("af-media.json", MediaManifestEntry(("drv-bad2", "多文件", null, [("media/drv-bad2/x.dll", "one")], null)).Manifest),
            ("media/drv-bad2/x.dll", "one"),
            ("media/drv-bad2/secret.dll", "sneaky"));
        var undeclaredSha = await UploadAsync(client, token, undeclared);
        var undeclaredResponse = await SendRegisterMediaAsync(client, token, new RegisterDriverMediaRequest(
            null, undeclaredSha, undeclared.Length, "media-drv-bad2.zip"));
        Assert.Equal((int)ApiErrorCode.VersionsMediaManifestInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(undeclaredResponse, HttpStatusCode.UnprocessableEntity));

        // 清单声明越出 media/ 前缀
        var escapeZip = BuildMediaZip(("drv-esc", "越界", null, [("outside/x.dll", "one")], null));
        var escapeSha = await UploadAsync(client, token, escapeZip);
        var escapeResponse = await SendRegisterMediaAsync(client, token, new RegisterDriverMediaRequest(
            null, escapeSha, escapeZip.Length, "media-drv-esc.zip"));
        Assert.Equal((int)ApiErrorCode.VersionsMediaManifestInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(escapeResponse, HttpStatusCode.UnprocessableEntity));

        // 没有 af-media.json 的普通 zip
        var plain = BuildZip(("readme.txt", "not a media package"));
        var plainSha = await UploadAsync(client, token, plain);
        var plainResponse = await SendRegisterMediaAsync(client, token, new RegisterDriverMediaRequest(
            null, plainSha, plain.Length, "plain.zip"));
        Assert.Equal((int)ApiErrorCode.VersionsMediaManifestInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(plainResponse, HttpStatusCode.UnprocessableEntity));
    }

    [Fact]
    public async Task 同内容复用与同键异内容冲突()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);

        var zip = BuildMediaZip(("drv-c", "C 驱动", null, [("media/drv-c/c.dll", "payload-c")], null));
        var sha = await UploadAsync(client, token, zip);

        var first = await RegisterMediaAsync(client, token, new RegisterDriverMediaRequest(
            "drv-c", sha, zip.Length, "media-drv-c.zip"));
        Assert.False(first.Reused);

        var again = await RegisterMediaAsync(client, token, new RegisterDriverMediaRequest(
            "drv-c", sha, zip.Length, "media-drv-c.zip"));
        Assert.True(again.Reused);
        Assert.Equal(first.Id, again.Id);

        // 同一 mediaKey + 未指定版本（清单也无版本 → 空串槽位）换内容：冲突
        var other = BuildMediaZip(("drv-c", "C 驱动", null, [("media/drv-c/c.dll", "payload-c-v2")], null));
        var otherSha = await UploadAsync(client, token, other);
        var conflict = await SendRegisterMediaAsync(client, token, new RegisterDriverMediaRequest(
            "drv-c", otherSha, other.Length, "media-drv-c.zip"));
        Assert.Equal((int)ApiErrorCode.VersionsMediaKeyExists,
            await IdentityTestHelpers.ReadErrorCodeAsync(conflict, HttpStatusCode.Conflict));

        // 请求 mediaKey 与清单 componentId 不一致：400
        var mismatch = await SendRegisterMediaAsync(client, token, new RegisterDriverMediaRequest(
            "drv-other", sha, zip.Length, "media-drv-c.zip"));
        Assert.Equal((int)ApiErrorCode.ValidationFailed,
            await IdentityTestHelpers.ReadErrorCodeAsync(mismatch, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task 内容未上传时拒绝登记()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);

        var zip = BuildMediaZip(("drv-d", "D 驱动", null, [("media/drv-d/d.dll", "never-uploaded")], null));
        var response = await SendRegisterMediaAsync(client, token, new RegisterDriverMediaRequest(
            null, Sha256(zip), zip.Length, "media-drv-d.zip"));
        Assert.Equal((int)ApiErrorCode.VersionsPackageHashMismatch,
            await IdentityTestHelpers.ReadErrorCodeAsync(response, HttpStatusCode.UnprocessableEntity));
    }

    private static async Task<string> LoginAsync(HttpClient client) =>
        await IdentityTestHelpers.LoginAsAdminAsync(client);

    private static async Task<DriverMediaDto> RegisterMediaAsync(
        HttpClient client,
        string token,
        RegisterDriverMediaRequest request) =>
        await ReadDataAsync<DriverMediaDto>(await SendRegisterMediaAsync(client, token, request));

    private static async Task<HttpResponseMessage> SendRegisterMediaAsync(
        HttpClient client,
        string token,
        RegisterDriverMediaRequest request) =>
        await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post, "/api/v1/admin/versions/media", token, request));

    /// <summary>按 pack-media.ps1 的口径构造一份合法媒体 zip（清单 + 声明文件）。</summary>
    private static byte[] BuildMediaZip(
        (string ComponentId, string Label, string? Version, (string Path, string Content)[] Files, string[]? InstallOrder) package)
    {
        var (manifest, files) = MediaManifestEntry(package);
        var entries = new List<(string, string)> { ("af-media.json", manifest) };
        entries.AddRange(files);
        return BuildZip([.. entries]);
    }

    private static (string Manifest, (string Path, string Content)[] Files) MediaManifestEntry(
        (string ComponentId, string Label, string? Version, (string Path, string Content)[] Files, string[]? InstallOrder) package)
    {
        var fileLines = package.Files.Select(f =>
            $"    {{ \"path\": \"{f.Path}\", \"sha256\": \"{Sha256(Encoding.UTF8.GetBytes(f.Content))}\", \"sizeBytes\": {Encoding.UTF8.GetByteCount(f.Content)} }}");
        var versionField = package.Version is null ? string.Empty : $",\n  \"version\": \"{package.Version}\"";
        var orderField = package.InstallOrder is null
            ? string.Empty
            : $",\n  \"installOrder\": [{string.Join(", ", package.InstallOrder.Select(static x => $"\"{x}\""))}]";
        var manifest = $$"""
        {
          "schema": "af-media/1",
          "componentId": "{{package.ComponentId}}",
          "label": "{{package.Label}}"{{versionField}},
          "rootPrefix": "media/",
          "files": [
        {{string.Join(",\n", fileLines)}}
          ]{{orderField}}
        }
        """;
        return (manifest, package.Files);
    }

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

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
