using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Contracts;
using Platform.Contracts.Audit;
using Platform.Contracts.Versions;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 完整全量包组合下载：bundle-ticket 校验（就绪全量包、声明覆盖、路径冲突）、
/// zip 布局与 media-order.json、缓存删除后重建的字节一致性、票据下载与审计。
/// </summary>
public sealed class PackageBundleEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private static readonly string[] BasicLayout =
    [
        "Setup-bundle-basic-1.0.0.exe",
        "media/drv-x/run.ps1",
        "media/drv-x/x.dll",
        "media/drv-y/y.dll",
        "media/media-order.json",
    ];

    private static readonly string[] DeclaredLayout =
    [
        "Setup-bundle-decl-1.0.0.zip",
        "media/drv-a/a.dll",
        "media/media-order.json",
    ];

    private static readonly string[] DedupLayout =
    [
        "Setup-bundle-dup-1.0.0.exe",
        "media/shared/z.dll",
        "media/media-order.json",
    ];

    private static readonly string[] InstallOrderX = ["step-x1", "step-x2"];

    private static readonly string[] PackageOrderX = ["numpy", "scikit_learn"];

    private readonly PlatformApiFactory _factory;

    public PackageBundleEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 组合票据下载布局正确且删缓存后字节一致()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "bundle-basic";
        await CreateChannelAsync(client, token, channelKey);

        // 两份媒体：drv-x（两文件 + 显式顺序 + wheel 序）、drv-y（单文件无顺序）
        var xZip = BuildMediaZip((
            "drv-x", "X 驱动", "1.0.0",
            [("media/drv-x/x.dll", "x-binary"), ("media/drv-x/run.ps1", "Write-Host x")],
            ["step-x1", "step-x2"]),
            PackageOrderX);
        var xSha = await UploadAsync(client, token, xZip);
        var mediaX = await RegisterMediaAsync(
            client, token, new RegisterDriverMediaRequest(null, xSha, xZip.Length, "media-drv-x.zip"));

        var yZip = BuildMediaZip(("drv-y", "Y 驱动", null, [("media/drv-y/y.dll", "y-binary")], null));
        var ySha = await UploadAsync(client, token, yZip);
        var mediaY = await RegisterMediaAsync(
            client, token, new RegisterDriverMediaRequest(null, ySha, yZip.Length, "media-drv-y.zip"));

        // exe 全量包，人工登记（无清单 = 无声明，老包语义按"任选"）
        var exeBytes = "bundle-installer-exe-binary"u8.ToArray();
        var exeSha = await UploadAsync(client, token, exeBytes);
        var package = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            exeSha,
            exeBytes.Length,
            "Setup-bundle-basic-1.0.0.exe",
            VersionConventions.PackageKindFull,
            Target: "win-x64",
            Format: "exe",
            ChannelKey: channelKey,
            Version: "1.0.0",
            Publish: true));

        var ticket = await ReadDataAsync<DownloadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/client/versions/packages/{package.Id}/bundle-ticket",
                token,
                new CreatePackageBundleRequest([mediaX.Id, mediaY.Id]))));
        Assert.Equal("Setup-bundle-basic-1.0.0.exe.zip", ticket.FileName);
        Assert.True(ticket.SizeBytes > exeBytes.Length);

        // 组合哈希可离线复算：sha256(包sha:排序去重媒体sha拼接)
        var expectedHash = Sha256Text(
            $"{exeSha}:{string.Join(':', new[] { xSha, ySha }.OrderBy(static x => x, StringComparer.Ordinal))}");

        var first = await client.GetAsync(ticket.Url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("application/zip", first.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"\"{expectedHash}-zip\"", first.Headers.ETag!.Tag);
        Assert.Equal(expectedHash, first.Headers.GetValues("X-Content-Sha256").Single());
        var firstBytes = await first.Content.ReadAsByteArrayAsync();

        using (var archive = new ZipArchive(new MemoryStream(firstBytes), ZipArchiveMode.Read))
        {
            // 条目确定序：包根条目 → 媒体（按媒体键）内按路径排序 → 顺序清单
            Assert.Equal(
                BasicLayout,
                archive.Entries.Select(static e => e.FullName).ToArray());

            Assert.Equal(exeBytes, await ReadDataAsync(archive.GetEntry("Setup-bundle-basic-1.0.0.exe")!));
            Assert.Equal(
                "x-binary",
                Encoding.UTF8.GetString(await ReadDataAsync(archive.GetEntry("media/drv-x/x.dll")!)));
            // 媒体 zip 的清单本身不进组合
            Assert.Null(archive.GetEntry("af-media.json"));

            using var orderStream = archive.GetEntry("media/media-order.json")!.Open();
            using var order = await JsonDocument.ParseAsync(orderStream);
            Assert.Equal(VersionConventions.MediaOrderSchemaV1, order.RootElement.GetProperty("schema").GetString());
            var entries = order.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            Assert.Equal(2, entries.Length);
            Assert.Equal("drv-x", entries[0].GetProperty("mediaKey").GetString());
            Assert.Equal(mediaX.ManifestHash, entries[0].GetProperty("manifestHash").GetString());
            Assert.Equal(
                InstallOrderX,
                entries[0].GetProperty("installOrder").EnumerateArray().Select(static e => e.GetString()));
            Assert.Equal(
                PackageOrderX,
                entries[0].GetProperty("packageOrder").EnumerateArray().Select(static e => e.GetString()));
            Assert.Equal("drv-y", entries[1].GetProperty("mediaKey").GetString());
            Assert.Empty(entries[1].GetProperty("installOrder").EnumerateArray());
            // 未声明 wheel 序的媒体不带 packageOrder 键（WhenWritingNull）
            Assert.False(entries[1].TryGetProperty("packageOrder", out _));
        }

        // 票据下载支持 Range / 304（缓存文件与源内容同走 blob 直读）
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, ticket.Url);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);
        var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal($"bytes 0-3/{firstBytes.Length}", range.Content.Headers.ContentRange!.ToString());

        using var cachedRequest = new HttpRequestMessage(HttpMethod.Get, ticket.Url);
        cachedRequest.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{expectedHash}-zip\""));
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(cachedRequest)).StatusCode);

        // 组合缓存是纯派生内容：删掉后原样重建，字节一致
        var cacheFile = Directory.EnumerateFiles(_factory.BlobRoot, "*.zip", SearchOption.AllDirectories)
            .Single(path => Path.GetFileName(path) == $"{expectedHash}.zip");
        File.Delete(cacheFile);

        var second = await client.GetAsync(ticket.Url);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(expectedHash, second.Headers.GetValues("X-Content-Sha256").Single());
        Assert.Equal(firstBytes, await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task 声明覆盖缺项被拒allowMissing放行并记审计()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "bundle-decl";
        await CreateChannelAsync(client, token, channelKey);

        var aZip = BuildMediaZip(("drv-a", "A 驱动", null, [("media/drv-a/a.dll", "alpha")], ["drv-a"]));
        var aSha = await UploadAsync(client, token, aZip);
        var mediaA = await RegisterMediaAsync(
            client, token, new RegisterDriverMediaRequest(null, aSha, aZip.Length, "media-drv-a.zip"));

        var packageBytes = BuildZip(
            ("af-package.json", DeclaredFullManifest(channelKey, "1.0.0", [("drv-a", ["media/drv-a"])])),
            ("payload.txt", "decl-package"));
        var packageSha = await UploadAsync(client, token, packageBytes);
        var package = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            packageSha,
            packageBytes.Length,
            "Setup-bundle-decl-1.0.0.zip",
            VersionConventions.PackageKindFull,
            Target: "win-x64",
            Format: "zip",
            Publish: true));

        // 未选声明媒体：默认拒绝
        var missing = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/client/versions/packages/{package.Id}/bundle-ticket",
            token,
            new CreatePackageBundleRequest([])));
        Assert.Equal(
            (int)ApiErrorCode.VersionsBundleMediaMissing,
            await IdentityTestHelpers.ReadErrorCodeAsync(missing, HttpStatusCode.BadRequest));

        // allowMissing 强过：只有包条目，无媒体、无顺序清单
        var forced = await ReadDataAsync<DownloadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/client/versions/packages/{package.Id}/bundle-ticket",
                token,
                new CreatePackageBundleRequest([], AllowMissing: true))));
        var forcedBytes = await (await client.GetAsync(forced.Url)).Content.ReadAsByteArrayAsync();
        using (var archive = new ZipArchive(new MemoryStream(forcedBytes), ZipArchiveMode.Read))
        {
            var entry = Assert.Single(archive.Entries);
            Assert.Equal("Setup-bundle-decl-1.0.0.zip", entry.FullName);
        }

        var audit = await ReadDataAsync<PageResult<AuditLogDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/audit-logs?action=versions.package.bundle&pageSize=50",
                token)));
        Assert.Contains(
            audit.Items,
            item => item.TargetId == package.Id.ToString("D")
                    && item.Result == "success"
                    && item.Message!.Contains("drv-a"));

        // 选齐声明媒体：正常组合
        var ok = await ReadDataAsync<DownloadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/client/versions/packages/{package.Id}/bundle-ticket",
                token,
                new CreatePackageBundleRequest([mediaA.Id]))));
        var okBytes = await (await client.GetAsync(ok.Url)).Content.ReadAsByteArrayAsync();
        using var okArchive = new ZipArchive(new MemoryStream(okBytes), ZipArchiveMode.Read);
        Assert.Equal(
            DeclaredLayout,
            okArchive.Entries.Select(static e => e.FullName).ToArray());
    }

    [Fact]
    public async Task 同路径异内容冲突409同内容去重只写一份()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "bundle-dup";
        await CreateChannelAsync(client, token, channelKey);

        var c1Zip = BuildMediaZip(("drv-c1", "C1", null, [("media/shared/z.dll", "content-one")], null));
        var c1Sha = await UploadAsync(client, token, c1Zip);
        var media1 = await RegisterMediaAsync(
            client, token, new RegisterDriverMediaRequest(null, c1Sha, c1Zip.Length, "media-drv-c1.zip"));

        var c2Zip = BuildMediaZip(("drv-c2", "C2", null, [("media/shared/z.dll", "content-two")], null));
        var c2Sha = await UploadAsync(client, token, c2Zip);
        var media2 = await RegisterMediaAsync(
            client, token, new RegisterDriverMediaRequest(null, c2Sha, c2Zip.Length, "media-drv-c2.zip"));

        // 与 c1 声明同一文件同内容（媒体 zip 因清单 componentId 不同而不同，但文件级 sha 一致）
        var c3Zip = BuildMediaZip(("drv-c3", "C3", null, [("media/shared/z.dll", "content-one")], null));
        var c3Sha = await UploadAsync(client, token, c3Zip);
        var media3 = await RegisterMediaAsync(
            client, token, new RegisterDriverMediaRequest(null, c3Sha, c3Zip.Length, "media-drv-c3.zip"));

        var exeBytes = "bundle-dup-exe"u8.ToArray();
        var exeSha = await UploadAsync(client, token, exeBytes);
        var package = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            exeSha,
            exeBytes.Length,
            "Setup-bundle-dup-1.0.0.exe",
            VersionConventions.PackageKindFull,
            Target: "win-x64",
            Format: "exe",
            ChannelKey: channelKey,
            Version: "1.0.0",
            Publish: true));

        // 同路径不同内容：409
        var conflict = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/client/versions/packages/{package.Id}/bundle-ticket",
            token,
            new CreatePackageBundleRequest([media1.Id, media2.Id])));
        Assert.Equal(
            (int)ApiErrorCode.VersionsBundlePathConflict,
            await IdentityTestHelpers.ReadErrorCodeAsync(conflict, HttpStatusCode.Conflict));

        // 同路径同内容（同 sha）：去重只写一份
        var dedup = await ReadDataAsync<DownloadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/client/versions/packages/{package.Id}/bundle-ticket",
                token,
                new CreatePackageBundleRequest([media1.Id, media3.Id]))));
        var bytes = await (await client.GetAsync(dedup.Url)).Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Single(archive.Entries, static e => e.FullName == "media/shared/z.dll");
        Assert.Equal(
            DedupLayout,
            archive.Entries.Select(static e => e.FullName).ToArray());
    }

    [Fact]
    public async Task 非全量包与非法媒体输入统一拒绝()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "bundle-reject";
        await CreateChannelAsync(client, token, channelKey);

        // 增量包：kind 校验先于就绪状态
        var payloadBytes = "reject-app-binary"u8.ToArray();
        var payloadSha = await UploadAsync(client, token, payloadBytes);
        var incrementalManifest = $$"""
        {
          "schema": "af-package/1",
          "kind": "incremental",
          "channelKey": "{{channelKey}}",
          "version": "4.0.0",
          "runtimeIdentifier": "win-x64",
          "files": [
            { "path": "App.exe", "sha256": "{{payloadSha}}", "sizeBytes": {{payloadBytes.Length}} }
          ]
        }
        """;
        var incrementalBytes = BuildZip(("af-package.json", incrementalManifest), ("App.exe", "reject-app-binary"));
        var incrementalSha = await UploadAsync(client, token, incrementalBytes);
        var incremental = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            incrementalSha,
            incrementalBytes.Length,
            "Update-bundle-reject-4.0.0.zip",
            VersionConventions.PackageKindIncremental,
            Target: "win-x64",
            Format: "zip"));

        var kindRejected = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/client/versions/packages/{incremental.Id}/bundle-ticket",
            token,
            new CreatePackageBundleRequest([])));
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageKindUnsupported,
            await IdentityTestHelpers.ReadErrorCodeAsync(kindRejected, HttpStatusCode.BadRequest));

        // 其余输入校验用一个就绪的全量包，避免先撞上 kind/状态检查
        var exeBytes = "reject-full-exe"u8.ToArray();
        var exeSha = await UploadAsync(client, token, exeBytes);
        var package = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            exeSha,
            exeBytes.Length,
            "Setup-bundle-reject-1.0.0.exe",
            VersionConventions.PackageKindFull,
            Target: "win-x64",
            Format: "exe",
            ChannelKey: channelKey,
            Version: "1.0.0",
            Publish: true));

        // 不存在的媒体 id：404
        var mediaMissing = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/client/versions/packages/{package.Id}/bundle-ticket",
            token,
            new CreatePackageBundleRequest([Guid.CreateVersion7()])));
        Assert.Equal(
            (int)ApiErrorCode.VersionsMediaNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(mediaMissing, HttpStatusCode.NotFound));

        // 空 GUID：400
        var emptyGuid = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/client/versions/packages/{package.Id}/bundle-ticket",
            token,
            new CreatePackageBundleRequest([Guid.Empty])));
        Assert.Equal(
            (int)ApiErrorCode.ValidationFailed,
            await IdentityTestHelpers.ReadErrorCodeAsync(emptyGuid, HttpStatusCode.BadRequest));

        // 超过组合上限：400
        var tooMany = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/client/versions/packages/{package.Id}/bundle-ticket",
            token,
            new CreatePackageBundleRequest(
                Enumerable.Range(0, VersionConventions.MaxBundleMediaPackages + 1)
                    .Select(static _ => Guid.CreateVersion7())
                    .ToArray())));
        Assert.Equal(
            (int)ApiErrorCode.ValidationFailed,
            await IdentityTestHelpers.ReadErrorCodeAsync(tooMany, HttpStatusCode.BadRequest));
    }

    private static string DeclaredFullManifest(
        string channelKey,
        string version,
        (string ComponentId, string[] Paths)[] requirements)
    {
        var requirementsJson = string.Join(
            ",",
            requirements.Select(static r =>
                $$"""{"componentId":"{{r.ComponentId}}","paths":[{{string.Join(",", r.Paths.Select(static p => $"\"{p}\""))}}]}"""));
        return $$"""
        {
          "schema": "af-package/1",
          "kind": "full",
          "channelKey": "{{channelKey}}",
          "version": "{{version}}",
          "runtimeIdentifier": "win-x64",
          "files": [],
          "mediaRequirements": [{{requirementsJson}}]
        }
        """;
    }

    /// <summary>按 pack-media.ps1 的口径构造一份合法媒体 zip（清单 + 声明文件）。</summary>
    private static byte[] BuildMediaZip(
        (string ComponentId, string Label, string? Version, (string Path, string Content)[] Files, string[]? InstallOrder) package,
        string[]? packageOrder = null)
    {
        var fileLines = package.Files.Select(f =>
            $"    {{ \"path\": \"{f.Path}\", \"sha256\": \"{Sha256(Encoding.UTF8.GetBytes(f.Content))}\", \"sizeBytes\": {Encoding.UTF8.GetByteCount(f.Content)} }}");
        var versionField = package.Version is null ? string.Empty : $",\n  \"version\": \"{package.Version}\"";
        var orderField = package.InstallOrder is null
            ? string.Empty
            : $",\n  \"installOrder\": [{string.Join(", ", package.InstallOrder.Select(static x => $"\"{x}\""))}]";
        var packageOrderField = packageOrder is null
            ? string.Empty
            : $",\n  \"packageOrder\": [{string.Join(", ", packageOrder.Select(static x => $"\"{x}\""))}]";
        var manifest = $$"""
        {
          "schema": "af-media/1",
          "componentId": "{{package.ComponentId}}",
          "label": "{{package.Label}}"{{versionField}},
          "rootPrefix": "media/",
          "files": [
        {{string.Join(",\n", fileLines)}}
          ]{{orderField}}{{packageOrderField}}
        }
        """;
        var entries = new List<(string, string)> { ("af-media.json", manifest) };
        entries.AddRange(package.Files);
        return BuildZip([.. entries]);
    }

    private static async Task<byte[]> ReadDataAsync(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
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

    private static async Task<string> LoginAsync(HttpClient client) =>
        await IdentityTestHelpers.LoginAsAdminAsync(client);

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

    private static async Task<ReleasePackageDto> RegisterAsync(
        HttpClient client,
        string token,
        RegisterReleasePackageRequest request) =>
        await ReadDataAsync<ReleasePackageDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/packages",
                token,
                request)));

    private static async Task<DriverMediaDto> RegisterMediaAsync(
        HttpClient client,
        string token,
        RegisterDriverMediaRequest request) =>
        await ReadDataAsync<DriverMediaDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/media",
                token,
                request)));

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

    private static string Sha256Text(string text) => Sha256(Encoding.UTF8.GetBytes(text));
}
