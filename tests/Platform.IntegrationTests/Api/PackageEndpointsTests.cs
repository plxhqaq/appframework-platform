using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Platform.Contracts;
using Platform.Contracts.Audit;
using Platform.Contracts.Versions;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 发布包登记链路：清单自动识别、人工登记、下载（Range / ETag / 304）、
/// check 安装包回退、冲突与删除约束。
/// </summary>
public sealed class PackageEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private static readonly byte[] AppBytes = "package-app-binary"u8.ToArray();
    private static readonly byte[] ManualBytes = "not-a-pe-installer-payload"u8.ToArray();

    private readonly PlatformApiFactory _factory;

    public PackageEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 登记全量包后检查更新下载并归档回退()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "pkg-full";

        await CreateChannelAsync(client, token, channelKey);

        // 上一版带文件清单：归档 3.0.0 后 check 回退到它
        var appSha = await UploadAsync(client, token, AppBytes);
        _ = await CreateReleaseAsync(
            client,
            token,
            channelKey,
            "2.0.0",
            publish: true,
            Artifact("win-x64", ("App.exe", AppBytes, appSha)));

        var packageBytes = BuildZip(
            ("af-package.json", FullManifest(channelKey, "3.0.0", "win-x64", appSha)),
            ("payload.txt", "package payload"));
        var packageSha = await UploadAsync(client, token, packageBytes);

        var registered = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            packageSha,
            packageBytes.Length,
            "Setup-pkg-full-3.0.0.zip",
            "full",
            Target: "win-x64",
            Format: "zip",
            Publish: true));

        Assert.Equal(VersionConventions.PackageStatusReady, registered.Status);
        Assert.Equal(VersionConventions.PackageKindFull, registered.Kind);
        Assert.Equal(VersionConventions.ManifestSourceZip, registered.ManifestSource);
        Assert.Equal("3.0.0", registered.Version);
        Assert.Equal(channelKey, registered.ChannelKey);
        Assert.Equal("win-x64", registered.Target);
        Assert.NotNull(registered.ManifestHash);
        Assert.False(registered.Reused);

        var listed = await ReadDataAsync<PageResult<ReleasePackageDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/admin/versions/packages?channelKey={channelKey}&kind=full&status=ready",
                token)));
        Assert.Contains(listed.Items, x => x.Id == registered.Id);

        var detail = await ReadDataAsync<ReleasePackageDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/admin/versions/packages/{registered.Id}",
                token)));
        Assert.Equal(packageSha, detail.Sha256);
        Assert.Equal("Setup-pkg-full-3.0.0.zip", detail.FileName);

        // 没有任何 artifact 也能通过就绪的全量包下发更新
        var check = await CheckAsync(client, token, channelKey, "2.0.0");
        Assert.True(check.HasUpdate);
        Assert.Equal("3.0.0", check.LatestVersion);
        Assert.Null(check.Release);
        Assert.NotNull(check.Installer);
        Assert.Equal(registered.Id, check.Installer!.Id);
        Assert.Equal(packageSha, check.Installer.Sha256);
        Assert.Null(check.IncrementalPackage);

        var same = await CheckAsync(client, token, channelKey, "3.0.0");
        Assert.False(same.HasUpdate);
        Assert.NotNull(same.Installer);

        var download = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/packages/{registered.Id}/download",
            token));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal($"\"{packageSha}\"", download.Headers.ETag!.Tag);
        Assert.Contains(
            "Setup-pkg-full-3.0.0.zip",
            download.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal(packageSha, download.Headers.GetValues("X-Content-Sha256").Single());
        Assert.Equal(packageBytes, await download.Content.ReadAsByteArrayAsync());

        using var rangeRequest = IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/packages/{registered.Id}/download",
            token);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);
        var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal($"bytes 0-3/{packageBytes.Length}", range.Content.Headers.ContentRange!.ToString());

        using var cachedRequest = IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/packages/{registered.Id}/download",
            token);
        cachedRequest.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{packageSha}\""));
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(cachedRequest)).StatusCode);

        await ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/versions/releases/{registered.ReleaseId}/archive",
                token)));

        var rollback = await CheckAsync(client, token, channelKey, "3.0.0");
        Assert.False(rollback.HasUpdate);
        Assert.True(rollback.IsDowngrade);
        Assert.Equal("2.0.0", rollback.LatestVersion);
        Assert.NotNull(rollback.Release);
        Assert.Null(rollback.Installer);

        var archivedDownload = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/packages/{registered.Id}/download",
            token));
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(archivedDownload, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task 管理台下载票据把非zip安装包打成zip且匿名可续传()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "pkg-ticket";

        await CreateChannelAsync(client, token, channelKey);

        var exeBytes = "browser-download-installer-binary"u8.ToArray();
        var exeSha = await UploadAsync(client, token, exeBytes);
        var exePackage = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            exeSha,
            exeBytes.Length,
            "Setup-pkg-ticket-1.0.0.exe",
            VersionConventions.PackageKindFull,
            Target: "win-x64",
            Format: "exe",
            ChannelKey: channelKey,
            Version: "1.0.0",
            Publish: true));

        var ticket = await ReadDataAsync<DownloadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/client/versions/packages/{exePackage.Id}/download-ticket",
                token)));
        Assert.StartsWith("/api/v1/client/versions/downloads/", ticket.Url);
        Assert.Equal("Setup-pkg-ticket-1.0.0.exe.zip", ticket.FileName);
        Assert.True(ticket.SizeBytes > exeBytes.Length);
        Assert.True(ticket.ExpiresAt > DateTimeOffset.UtcNow);

        // 浏览器不能随 <a> 带 Bearer，票据 GET 必须匿名可下载
        var download = await client.GetAsync(ticket.Url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"\"{exeSha}-zip\"", download.Headers.ETag!.Tag);
        Assert.Equal(exeSha, download.Headers.GetValues("X-Content-Sha256").Single());
        Assert.Contains(
            "Setup-pkg-ticket-1.0.0.exe.zip",
            download.Content.Headers.ContentDisposition!.ToString());

        var zipBytes = await download.Content.ReadAsByteArrayAsync();
        using (var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
        {
            var entry = Assert.Single(archive.Entries);
            Assert.Equal("Setup-pkg-ticket-1.0.0.exe", entry.FullName);
            using var entryStream = entry.Open();
            using var entryBuffer = new MemoryStream();
            await entryStream.CopyToAsync(entryBuffer);
            Assert.Equal(exeBytes, entryBuffer.ToArray());
        }

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, ticket.Url);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);
        var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal($"bytes 0-3/{zipBytes.Length}", range.Content.Headers.ContentRange!.ToString());

        using var cachedRequest = new HttpRequestMessage(HttpMethod.Get, ticket.Url);
        cachedRequest.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{exeSha}-zip\""));
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(cachedRequest)).StatusCode);

        // SDK/客户端仍走原始端点：exe 原样下发，不被替换为 zip
        var raw = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/packages/{exePackage.Id}/download",
            token));
        Assert.Equal("application/octet-stream", raw.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"\"{exeSha}\"", raw.Headers.ETag!.Tag);
        Assert.Equal(exeBytes, await raw.Content.ReadAsByteArrayAsync());

        // zip 源保持原样，不再二次打包
        var zipBytesSource = BuildZip(
            ("af-package.json", FullManifest(channelKey, "2.0.0", "win-x64", null)),
            ("payload.txt", "already-zipped"));
        var zipSha = await UploadAsync(client, token, zipBytesSource);
        var zipPackage = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            zipSha,
            zipBytesSource.Length,
            "Setup-pkg-ticket-2.0.0.zip",
            VersionConventions.PackageKindFull,
            Target: "win-x64",
            Format: "zip",
            ChannelKey: channelKey,
            Version: "2.0.0",
            Publish: true));

        var zipTicket = await ReadDataAsync<DownloadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/client/versions/packages/{zipPackage.Id}/download-ticket",
                token)));
        Assert.Equal("Setup-pkg-ticket-2.0.0.zip", zipTicket.FileName);
        Assert.Equal(zipBytesSource.Length, zipTicket.SizeBytes);

        var zipDownload = await client.GetAsync(zipTicket.Url);
        Assert.Equal(HttpStatusCode.OK, zipDownload.StatusCode);
        Assert.Equal("application/zip", zipDownload.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"\"{zipSha}\"", zipDownload.Headers.ETag!.Tag);
        Assert.Equal(zipBytesSource, await zipDownload.Content.ReadAsByteArrayAsync());

        // 伪造票据匿名访问直接 401；无权限也不能签票
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/client/versions/downloads/not-a-ticket")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsync(
                $"/api/v1/client/versions/packages/{exePackage.Id}/download-ticket",
                content: null)).StatusCode);
    }

    [Fact]
    public async Task 无清单包可人工登记且冲突与删除受控()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "pkg-manual";

        await CreateChannelAsync(client, token, channelKey);
        var manualSha = await UploadAsync(client, token, ManualBytes);

        // 无清单 + 未声明 kind：不猜，提示人工登记口径
        var missingKind = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/packages",
            token,
            new RegisterReleasePackageRequest(
                manualSha,
                ManualBytes.Length,
                "Setup-pkg-manual-1.0.0.exe",
                Kind: null,
                Target: "win-x64",
                Format: "exe",
                ChannelKey: channelKey,
                Version: "1.0.0")));
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageManifestMissing,
            await IdentityTestHelpers.ReadErrorCodeAsync(missingKind, HttpStatusCode.UnprocessableEntity));

        // 增量包的归档自身非法（不是 zip）：先按清单无效拒绝，不掩盖成「缺清单」
        var incremental = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/packages",
            token,
            new RegisterReleasePackageRequest(
                manualSha,
                ManualBytes.Length,
                "Update-pkg-manual-1.0.0.zip",
                VersionConventions.PackageKindIncremental,
                Target: "win-x64",
                Format: "zip",
                ChannelKey: channelKey,
                Version: "1.0.0")));
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageManifestInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(incremental, HttpStatusCode.UnprocessableEntity));

        var registered = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            manualSha,
            ManualBytes.Length,
            "Setup-pkg-manual-1.0.0.exe",
            VersionConventions.PackageKindFull,
            Target: "win-x64",
            Format: "exe",
            ChannelKey: channelKey,
            Version: "1.0.0",
            Publish: true));

        Assert.Equal(VersionConventions.PackageStatusReady, registered.Status);
        Assert.Equal(VersionConventions.ManifestSourceManual, registered.ManifestSource);
        Assert.Null(registered.ManifestHash);
        Assert.Equal("1.0.0", registered.Version);

        var check = await CheckAsync(client, token, channelKey, null);
        Assert.True(check.HasUpdate);
        Assert.NotNull(check.Installer);
        Assert.Equal(manualSha, check.Installer!.Sha256);

        // 同内容同槽位：幂等复用
        var replay = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            manualSha,
            ManualBytes.Length,
            "Setup-pkg-manual-1.0.0.exe",
            VersionConventions.PackageKindFull,
            Target: "win-x64",
            Format: "exe",
            ChannelKey: channelKey,
            Version: "1.0.0"));
        Assert.Equal(registered.Id, replay.Id);
        Assert.True(replay.Reused);

        // 不同内容占同一槽位：冲突
        var otherBytes = "another-installer-payload"u8.ToArray();
        var otherSha = await UploadAsync(client, token, otherBytes);
        var conflict = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/packages",
            token,
            new RegisterReleasePackageRequest(
                otherSha,
                otherBytes.Length,
                "Setup-pkg-manual-1.0.0.exe",
                VersionConventions.PackageKindFull,
                Target: "win-x64",
                Format: "exe",
                ChannelKey: channelKey,
                Version: "1.0.0")));
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageManifestConflict,
            await IdentityTestHelpers.ReadErrorCodeAsync(conflict, HttpStatusCode.Conflict));

        // 已发布版本的包不能删；草稿可以
        var publishedDelete = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete,
            $"/api/v1/admin/versions/packages/{registered.Id}",
            token));
        Assert.Equal(
            (int)ApiErrorCode.VersionsReleaseImmutable,
            await IdentityTestHelpers.ReadErrorCodeAsync(publishedDelete, HttpStatusCode.Conflict));

        var draftBytes = "draft-installer-payload"u8.ToArray();
        var draftSha = await UploadAsync(client, token, draftBytes);
        var draft = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            draftSha,
            draftBytes.Length,
            "Setup-pkg-manual-2.0.0.exe",
            VersionConventions.PackageKindFull,
            Target: "win-x64",
            Format: "exe",
            ChannelKey: channelKey,
            Version: "2.0.0"));

        var deleted = await ReadDataAsync<ReleasePackageDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Delete,
                $"/api/v1/admin/versions/packages/{draft.Id}",
                token)));
        Assert.Equal(draft.Id, deleted.Id);
    }

    [Fact]
    public async Task 登记安装包时渠道不存在自动创建并可被客户端命中()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "pkg-auto-channel";

        // 不预先建渠道：渠道键只出现在包内清单里，上传即自动建渠道，免得手输渠道键出错。
        var packageBytes = BuildZip(
            ("af-package.json", FullManifest(channelKey, "1.0.0", "win-x64", null)),
            ("payload.txt", "auto-channel"));
        var packageSha = await UploadAsync(client, token, packageBytes);

        var registered = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            packageSha,
            packageBytes.Length,
            $"Setup-{channelKey}-1.0.0.zip",
            VersionConventions.PackageKindFull,
            Target: "win-x64",
            Format: "zip",
            Publish: true));
        Assert.Equal(channelKey, registered.ChannelKey);
        Assert.Equal("1.0.0", registered.Version);

        var channels = await ReadDataAsync<List<ReleaseChannelDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/versions/channels",
                token)));
        var auto = Assert.Single(channels, x => x.Key == channelKey);
        Assert.True(auto.AutoCreated);
        Assert.Equal(channelKey, auto.Name);

        var check = await CheckAsync(client, token, channelKey, "0.9.0");
        Assert.True(check.HasUpdate);
        Assert.Equal("1.0.0", check.LatestVersion);
    }

    [Fact]
    public async Task 清单应用渠道版本不一致分别拒绝()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "pkg-mismatch";

        await CreateChannelAsync(client, token, channelKey);

        var appBytes = BuildZip(
            ("af-package.json", FullManifest(channelKey, "1.0.0", "win-x64", null, appKey: "another-app")),
            ("payload.txt", "wrong-app"));
        var appSha = await UploadAsync(client, token, appBytes);
        var appMismatch = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/packages",
            token,
            new RegisterReleasePackageRequest(
                appSha, appBytes.Length, "Setup-pkg-mismatch-1.0.0.zip", "full",
                Target: "win-x64", Format: "zip", Publish: true)));
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageAppMismatch,
            await IdentityTestHelpers.ReadErrorCodeAsync(appMismatch, HttpStatusCode.UnprocessableEntity));

        var channelBytes = BuildZip(
            ("af-package.json", FullManifest("pkg-other", "1.0.0", "win-x64", null)),
            ("payload.txt", "wrong-channel"));
        var channelSha = await UploadAsync(client, token, channelBytes);
        var channelMismatch = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/packages",
            token,
            new RegisterReleasePackageRequest(
                channelSha, channelBytes.Length, "Setup-pkg-mismatch-1.0.0.zip", "full",
                Target: "win-x64", Format: "zip", ChannelKey: channelKey, Publish: true)));
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageChannelVersionMismatch,
            await IdentityTestHelpers.ReadErrorCodeAsync(channelMismatch, HttpStatusCode.UnprocessableEntity));

        var versionBytes = BuildZip(
            ("af-package.json", FullManifest(channelKey, "1.0.0", "win-x64", null)),
            ("payload.txt", "wrong-version"));
        var versionSha = await UploadAsync(client, token, versionBytes);
        var versionMismatch = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/packages",
            token,
            new RegisterReleasePackageRequest(
                versionSha, versionBytes.Length, "Setup-pkg-mismatch-1.0.1.zip", "full",
                Target: "win-x64", Format: "zip", ChannelKey: channelKey, Version: "1.0.1", Publish: true)));
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageChannelVersionMismatch,
            await IdentityTestHelpers.ReadErrorCodeAsync(versionMismatch, HttpStatusCode.UnprocessableEntity));
    }

    [Fact]
    public async Task 幂等键重放返回同一登记并写审计()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "pkg-idem";

        await CreateChannelAsync(client, token, channelKey);
        var sha = await UploadAsync(client, token, ManualBytes);
        var body = new RegisterReleasePackageRequest(
            sha,
            ManualBytes.Length,
            "Setup-pkg-idem-1.0.0.exe",
            VersionConventions.PackageKindFull,
            Target: "win-x64",
            Format: "exe",
            ChannelKey: channelKey,
            Version: "1.0.0",
            Publish: true);

        var first = await SendRegisterAsync(client, token, body, "pkg-idem-request-1");
        var replay = await SendRegisterAsync(client, token, body, "pkg-idem-request-1");

        Assert.Equal(first.Id, replay.Id);
        var listed = await ReadDataAsync<PageResult<ReleasePackageDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/admin/versions/packages?channelKey={channelKey}&pageSize=50",
                token)));
        Assert.Single(listed.Items);

        var audit = await ReadDataAsync<PageResult<AuditLogDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/audit-logs?action=versions.package.register&pageSize=50",
                token)));
        Assert.Contains(
            audit.Items,
            item => item.TargetId == first.Id.ToString() && item.Result == "success");
    }

    [Fact]
    public async Task 增量包登记为待摄取且草稿版本不可下载()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "pkg-incremental";

        await CreateChannelAsync(client, token, channelKey);
        var payloadSha = await UploadAsync(client, token, AppBytes);
        var incrementalManifest = $$"""
        {
          "schema": "af-package/1",
          "kind": "incremental",
          "channelKey": "{{channelKey}}",
          "version": "4.0.0",
          "runtimeIdentifier": "win-x64",
          "files": [
            { "path": "App.exe", "sha256": "{{payloadSha}}", "sizeBytes": {{AppBytes.Length}} }
          ]
        }
        """;
        var packageBytes = BuildZip(
            ("af-package.json", incrementalManifest),
            ("App.exe", Encoding.UTF8.GetString(AppBytes)));
        var packageSha = await UploadAsync(client, token, packageBytes);

        var registered = await RegisterAsync(client, token, new RegisterReleasePackageRequest(
            packageSha,
            packageBytes.Length,
            "Update-pkg-incremental-4.0.0.zip",
            VersionConventions.PackageKindIncremental,
            Target: "win-x64",
            Format: "zip"));

        Assert.Equal(VersionConventions.PackageStatusPending, registered.Status);
        Assert.Null(registered.CompletedAt);

        var download = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/packages/{registered.Id}/download",
            token));
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(download, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task 无上传权限不能登记且跨应用看不到包()
    {
        using var client = _factory.CreateClient();
        var adminToken = await LoginAsync(client);
        const string channelKey = "pkg-permission";

        await CreateChannelAsync(client, adminToken, channelKey);
        var sha = await UploadAsync(client, adminToken, ManualBytes);

        const string viewerPassword = "Viewer@12345";
        _ = await IdentityTestHelpers.CreateUserAsync(
            client,
            adminToken,
            "versions-pkg-viewer",
            viewerPassword,
            "发布包只读");
        var viewer = await IdentityTestHelpers.LoginAsync(client, "versions-pkg-viewer", viewerPassword);

        var forbidden = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/packages",
            viewer.AccessToken,
            new RegisterReleasePackageRequest(
                sha,
                ManualBytes.Length,
                "Setup-pkg-permission-1.0.0.exe",
                "full",
                Target: "win-x64",
                Format: "exe",
                ChannelKey: channelKey,
                Version: "1.0.0")));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var anonymous = await client.GetAsync(
            $"/api/v1/client/versions/packages/{Guid.CreateVersion7()}/download");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private static string FullManifest(
        string channelKey,
        string version,
        string runtimeIdentifier,
        string? fileSha,
        string? appKey = null)
    {
        var appKeyField = appKey is null ? string.Empty : $",\n  \"appKey\": \"{appKey}\"";
        var files = fileSha is null
            ? "[]"
            : $"[{{\"path\": \"app/App.exe\", \"sha256\": \"{fileSha}\", \"sizeBytes\": {AppBytes.Length}}}]";
        return $$"""
        {
          "schema": "af-package/1",
          "kind": "full"{{appKeyField}},
          "channelKey": "{{channelKey}}",
          "version": "{{version}}",
          "runtimeIdentifier": "{{runtimeIdentifier}}",
          "entryPoint": "App.exe",
          "files": {{files}}
        }
        """;
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

    private static async Task<ReleaseDetailDto> CreateReleaseAsync(
        HttpClient client,
        string token,
        string channelKey,
        string version,
        bool publish,
        CreateReleaseArtifactRequest artifact) =>
        await ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/releases",
                token,
                new CreateReleaseRequest(channelKey, version, "集成测试版本", publish, [artifact]))));

    private static CreateReleaseArtifactRequest Artifact(
        string runtimeIdentifier,
        params (string Path, byte[] Content, string Sha)[] files) =>
        new(
            runtimeIdentifier,
            [.. files.Select(static file =>
                new ReleaseFileInput(file.Path, file.Sha, file.Content.Length))]);

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

    private static async Task<ReleasePackageDto> SendRegisterAsync(
        HttpClient client,
        string token,
        RegisterReleasePackageRequest request,
        string idempotencyKey)
    {
        using var message = IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/packages",
            token,
            request);
        message.Headers.Add("Idempotency-Key", idempotencyKey);
        return await ReadDataAsync<ReleasePackageDto>(await client.SendAsync(message));
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

    private static async Task<CheckUpdateResponse> CheckAsync(
        HttpClient client,
        string token,
        string channelKey,
        string? currentVersion) =>
        await ReadDataAsync<CheckUpdateResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/client/versions/check",
                token,
                new CheckUpdateRequest(channelKey, currentVersion, "win-x64"))));

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response) =>
        await IdentityTestHelpers.ReadDataAsync<T>(response);

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
