using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AppFramework.Platform.Client;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Backups;
using Platform.Contracts.Documents;
using Platform.Contracts.Identity;
using Platform.Contracts.Licensing;
using Platform.Contracts.Permissions;
using Platform.Contracts.Versions;
using Platform.IntegrationTests.Support;
using Platform.Modules.Versions.Services;

namespace Platform.IntegrationTests.Client;

/// <summary>
/// 客户端 SDK 的真机链路：信封拆解、错误归一、令牌自动续期、下载完整性校验与授权快照验签。
/// 这些断言只在真实宿主上有意义，因此跑在 WebApplicationFactory 上而不是 mock。
/// </summary>
public sealed class PlatformClientTests : IClassFixture<PlatformApiFactory>, IDisposable
{
    private const string AppKey = "demo";
    private static readonly byte[] ReleaseBytes = "sdk-release-payload"u8.ToArray();
    private static readonly byte[] DocumentBytes = "sdk-manual-content"u8.ToArray();

    private readonly PlatformApiFactory _factory;
    private readonly string _downloadDir;

    public PlatformClientTests(PlatformApiFactory factory)
    {
        _factory = factory;
        _downloadDir = Path.Combine(Path.GetTempPath(), "platform-sdk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_downloadDir);
    }

    [Fact]
    public async Task 登录后自述并判定权限与模块清单()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        {
            var session = await sdk.SignInAsync("admin", IdentityTestHelpers.AdminPassword, AppKey);
            Assert.Equal("admin", session.User.UserName);
            Assert.NotNull(sdk.AccessToken);
            Assert.NotNull(sdk.RefreshToken);

            var principal = await sdk.GetWhoAmIAsync();
            Assert.True(principal.IsAuthenticated);
            Assert.Equal(AppKey, principal.AppKey);
            Assert.Equal("admin", principal.UserName);

            Assert.True(sdk.HasPermission(PermissionCatalog.VersionsReleaseRead));
            // 平台管理员角色带 `*`：与 HttpCurrentPrincipal.HasPermission 同语义，未知码也判为已授权
            Assert.True(sdk.HasPermission("does.not.exist"));

            var modules = await sdk.GetModulesAsync();
            Assert.Contains(PlatformModuleKeys.Versions, modules.Select(x => x.Key), StringComparer.Ordinal);

            var permissions = await sdk.GetPermissionsAsync();
            Assert.Contains(PermissionCatalog.LicenseRead, permissions.Select(x => x.Code), StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task 口令错误抛平台异常并保留错误码与状态码()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        {
            var failure = await Assert.ThrowsAsync<PlatformClientException>(
                () => sdk.SignInAsync("admin", "not-the-password"));
            Assert.Equal(ApiErrorCode.IdentityInvalidCredentials, failure.Code);
            Assert.Equal((int)HttpStatusCode.Unauthorized, failure.StatusCode);
            Assert.Null(sdk.AccessToken);
        }
    }

    [Fact]
    public async Task 访问令牌损坏时用刷新令牌自动续期并重试()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        {
            await sdk.SignInAsync("admin", IdentityTestHelpers.AdminPassword, AppKey);

            // 模拟令牌过期：塞一个坏令牌，业务调用必须靠刷新链路自愈
            sdk.UseAccessToken("this-is-not-a-jwt");
            var principal = await sdk.GetWhoAmIAsync();
            Assert.True(principal.IsAuthenticated);
            Assert.Equal("admin", principal.UserName);
            Assert.NotNull(sdk.AccessToken);
        }
    }

    [Fact]
    public async Task 服务令牌换发后可访问应用级端点但没有用户权限集()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        {
            const string serviceAppKey = "sdk-m2m";
            var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(raw);
            var app = await IdentityTestHelpers.ReadDataAsync<AppDetailDto>(
                await raw.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post, "/api/v1/admin/apps", adminToken, new CreateAppRequest(serviceAppKey, "SDK M2M"))));
            var key = await IdentityTestHelpers.ReadDataAsync<CreatedApiKeyDto>(
                await raw.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    $"/api/v1/admin/apps/{app.Id}/keys",
                    adminToken,
                    new CreateApiKeyRequest("sdk", [PermissionCatalog.VersionsReleaseRead]))));

            var token = await sdk.RequestServiceTokenAsync(serviceAppKey, key.Plaintext);
            Assert.Equal(serviceAppKey, token.AppKey);

            var principal = await sdk.GetWhoAmIAsync();
            Assert.True(principal.IsService);
            Assert.Equal(serviceAppKey, principal.AppKey);
            Assert.False(sdk.HasPermission(PermissionCatalog.VersionsReleaseRead));
        }
    }

    [Fact]
    public async Task 单次密钥兑换得到临时主体()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        {
            var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(raw);
            var issued = await IdentityTestHelpers.ReadDataAsync<SingleLoginKeyDto>(
                await raw.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    "/api/v1/admin/identity/singlekeys",
                    adminToken,
                    new IssueSingleLoginKeyRequest(30, "sdk-line-1"))));

            var session = await sdk.RedeemSingleKeyAsync(issued.Key, "sdk-line-1");
            // 临时令牌绑定签发人：管理员签发的密钥得到管理员自己的用户名与权限集。
            Assert.Equal(IdentityTestHelpers.AdminUserName, session.User.UserName);
            Assert.Contains(PermissionCatalog.Wildcard, session.User.Permissions);

            var principal = await sdk.GetWhoAmIAsync();
            Assert.True(principal.IsTemporary);

            // 临时身份由端点拒绝管理操作，而不是靠权限集裁剪。
            Assert.True(sdk.HasPermission(PermissionCatalog.LicenseRead));
            Assert.True(sdk.HasPermission(PermissionCatalog.IdentityUserRead));
        }
    }

    [Fact]
    public async Task 扫码等待超时后审批再轮询即取回临时令牌()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        {
            var created = await sdk.CreateQrLoginSessionAsync("sdk-qr", "SDK 操作台");
            Assert.EndsWith(
                QrLoginConventions.ApprovalPath + created.RequestId.ToString("N"),
                created.Payload,
                StringComparison.Ordinal);

            await Assert.ThrowsAsync<TimeoutException>(
                () => sdk.WaitForQrLoginApprovalAsync(created.RequestId, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50)));

            var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(raw);
            await IdentityTestHelpers.ReadDataAsync<QrSessionSummaryDto>(
                await raw.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    $"/api/v1/admin/identity/qr/sessions/{created.RequestId:D}/approve",
                    adminToken,
                    new ApproveQrSessionRequest(30))));

            var session = await sdk.WaitForQrLoginApprovalAsync(
                created.RequestId, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50));
            // 临时令牌绑定审批人：管理员批准后得到管理员自己的用户名。
            Assert.Equal(IdentityTestHelpers.AdminUserName, session.User.UserName);
            var principal = await sdk.GetWhoAmIAsync();
            Assert.True(principal.IsTemporary);
        }
    }

    [Fact]
    public async Task 检查更新读清单并按内容哈希下载落盘()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        {
            var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(raw);
            await CreateChannelAsync(raw, adminToken, "sdk-stable");
            var sha = await UploadAsync(raw, adminToken, ReleaseBytes);
            await CreateReleaseAsync(raw, adminToken, "sdk-stable", "1.2.0", sha, ReleaseBytes.Length);

            await sdk.SignInAsync("admin", IdentityTestHelpers.AdminPassword, AppKey);

            var check = await sdk.CheckUpdateAsync("sdk-stable", "1.1.0", "win-x64");
            Assert.True(check.HasUpdate);
            Assert.Equal("1.2.0", check.Release!.Version);

            var manifest = await sdk.GetReleaseManifestAsync("sdk-stable", "1.2.0", "win-x64");
            Assert.Equal(check.Release.ManifestHash, manifest.ManifestHash);

            var target = Path.Combine(_downloadDir, "App.exe");
            Assert.True(await sdk.DownloadVersionFileAsync(sha, target));
            Assert.Equal(ReleaseBytes, await File.ReadAllBytesAsync(target));

            // 同一 ETag 再下载：服务端 304，本地文件不动
            Assert.False(await sdk.DownloadVersionFileAsync(sha, target, ifNoneMatch: sha));
            Assert.Equal(ReleaseBytes, await File.ReadAllBytesAsync(target));

            // 失败路径只留临时文件，成功路径必须没有残留
            Assert.Empty(Directory.GetFiles(_downloadDir, "*.tmp"));
        }
    }

    [Fact]
    public async Task 更新器与增量包可经SDK查询并校验下载()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        {
            var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(raw);
            const string channelKey = "sdk-inc";
            await CreateChannelAsync(raw, adminToken, channelKey);

            var updaterZip = BuildZip(
                ("af-package.json", """
                {
                  "schema": "af-package/1",
                  "kind": "updater",
                  "version": "2.0.0",
                  "runtimeIdentifier": "win-x64",
                  "entryPoint": "Updater.exe"
                }
                """),
                ("Updater.exe", Encoding.UTF8.GetString(ReleaseBytes)));
            var updaterSha = await UploadAsync(raw, adminToken, updaterZip);
            var updater = await IdentityTestHelpers.ReadDataAsync<UpdaterInfoDto>(
                await raw.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    "/api/v1/admin/versions/updaters",
                    adminToken,
                    new RegisterUpdaterRequest(
                        "win-x64", null, "AppFramework.Updater-2.0.0.zip", updaterSha, updaterZip.Length))));
            Assert.Equal("2.0.0", updater.Version);

            var payloadSha = Convert.ToHexString(SHA256.HashData(ReleaseBytes)).ToLowerInvariant();
            var manifest = $$"""
            {
              "schema": "af-package/1",
              "kind": "incremental",
              "channelKey": "{{channelKey}}",
              "version": "3.0.0",
              "runtimeIdentifier": "win-x64",
              "files": [
                { "path": "App.exe", "sha256": "{{payloadSha}}", "sizeBytes": {{ReleaseBytes.Length}} }
              ]
            }
            """;
            var incrementalZip = BuildZip(
                ("af-package.json", manifest),
                ("App.exe", Encoding.UTF8.GetString(ReleaseBytes)));
            var incrementalSha = await UploadAsync(raw, adminToken, incrementalZip);
            var registered = await IdentityTestHelpers.ReadDataAsync<ReleasePackageDto>(
                await raw.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    "/api/v1/admin/versions/packages",
                    adminToken,
                    new RegisterReleasePackageRequest(
                        incrementalSha,
                        incrementalZip.Length,
                        $"Update-{channelKey}-3.0.0.zip",
                        VersionConventions.PackageKindIncremental,
                        Target: "win-x64",
                        Format: "zip",
                        ChannelKey: channelKey,
                        Version: "3.0.0",
                        Publish: true))));

            // 宿主里摄取循环关闭，直接驱动一次
            using (var scope = _factory.Services.CreateScope())
            {
                var ingestion = scope.ServiceProvider.GetRequiredService<IReleasePackageIngestionService>();
                var processed = await ingestion.ProcessNextAsync();
                Assert.NotNull(processed);
                Assert.Equal(registered.Id, processed!.Id);
                Assert.Equal(VersionConventions.PackageStatusReady, processed.Status);
            }

            await sdk.SignInAsync("admin", IdentityTestHelpers.AdminPassword, AppKey);

            var latestUpdater = await sdk.GetLatestUpdaterAsync("win-x64");
            Assert.Equal(updater.Id, latestUpdater.Id);

            var updaterPath = Path.Combine(_downloadDir, "updater.zip");
            Assert.True(await sdk.DownloadUpdaterAsync(updater.Id, updaterPath, updaterSha));
            Assert.Equal(updaterZip, await File.ReadAllBytesAsync(updaterPath));
            Assert.False(await sdk.DownloadUpdaterAsync(updater.Id, updaterPath, updaterSha, ifNoneMatch: updaterSha));

            var check = await sdk.CheckUpdateAsync(channelKey, "2.9.0", "win-x64", updaterVersion: "1.0.0");
            Assert.True(check.HasUpdate);
            Assert.True(check.HasUpdaterUpdate);
            Assert.NotNull(check.IncrementalPackage);

            var packagePath = Path.Combine(_downloadDir, "incremental.zip");
            Assert.True(await sdk.DownloadReleasePackageAsync(
                check.IncrementalPackage!.Id, packagePath, incrementalSha));
            Assert.Equal(incrementalZip, await File.ReadAllBytesAsync(packagePath));

            var upToDate = await sdk.CheckUpdateAsync(channelKey, "3.0.0", "win-x64", updaterVersion: "2.0.0");
            Assert.False(upToDate.HasUpdaterUpdate);
            Assert.Null(upToDate.IncrementalPackage);

            Assert.Empty(Directory.GetFiles(_downloadDir, "*.tmp"));
        }
    }

    [Fact]
    public async Task 文档哈希不符时拒绝落盘且不留下半个文件()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        {
            var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(raw);
            await IdentityTestHelpers.ReadDataAsync<DocumentCollectionDto>(
                await raw.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    "/api/v1/admin/documents/collections",
                    adminToken,
                    new CreateDocumentCollectionRequest("sdk-manual", "SDK 手册", null))));
            var document = await UploadDocumentAsync(raw, adminToken, "sdk-manual", DocumentBytes);

            await sdk.SignInAsync("admin", IdentityTestHelpers.AdminPassword, AppKey);

            var manifest = await sdk.GetDocumentManifestAsync();
            var item = Assert.Single(manifest.Items, x => x.DocumentId == document.Id);
            Assert.Equal(DocumentConventions.VisibilityClient, document.Visibility);

            var wrongTarget = Path.Combine(_downloadDir, "manual-wrong.pdf");
            var failure = await Assert.ThrowsAsync<PlatformClientException>(
                () => sdk.DownloadDocumentAsync(document.Id, wrongTarget, new string('0', 64)));
            Assert.Equal(ApiErrorCode.VersionsPackageHashMismatch, failure.Code);
            Assert.False(File.Exists(wrongTarget));
            Assert.Empty(Directory.GetFiles(_downloadDir, "*.tmp"));

            var target = Path.Combine(_downloadDir, "manual.pdf");
            Assert.True(await sdk.DownloadDocumentAsync(document.Id, target, item.Sha256));
            Assert.Equal(DocumentBytes, await File.ReadAllBytesAsync(target));

            // internal 文档不在客户端清单里，下载也必须被服务端拒绝
            var internalDocument = await UploadDocumentAsync(raw, adminToken, "sdk-manual", DocumentBytes, visibility: DocumentConventions.VisibilityInternal);
            var refused = await Assert.ThrowsAsync<PlatformClientException>(
                () => sdk.DownloadDocumentAsync(internalDocument.Id, Path.Combine(_downloadDir, "internal.pdf")));
            Assert.Equal(ApiErrorCode.DocumentsVisibilityDenied, refused.Code);
        }
    }

    [Fact]
    public async Task 授权快照验签通过并拦截机器码与应用键不符()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        {
            var machineCode = MachineCodeOf("sdk-machine");
            var otherMachine = MachineCodeOf("sdk-machine-2");
            var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(raw);
            await IdentityTestHelpers.ReadDataAsync<LicenseProductDto>(
                await raw.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    "/api/v1/admin/licensing/products",
                    adminToken,
                    new CreateLicenseProductRequest("sdk-pro", "SDK 旗舰版", null))));
            await IdentityTestHelpers.ReadDataAsync<LicenseEntitlementDto>(
                await raw.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    "/api/v1/admin/licensing/entitlements",
                    adminToken,
                    new IssueLicenseRequest("sdk-pro", machineCode, null, null, 30, null, null))));

            await sdk.SignInAsync("admin", IdentityTestHelpers.AdminPassword, AppKey);

            var status = await sdk.GetLicenseStatusAsync("sdk-pro", machineCode);
            Assert.Equal(LicenseConventions.StatusActive, status.Status);
            Assert.NotNull(status.Snapshot);

            var verified = LicenseVerifier.Verify(status, AppKey, "sdk-pro", machineCode);
            Assert.True(verified.IsValid);
            Assert.Equal(LicenseConventions.KindOfficial, verified.Payload!.Kind);

            Assert.False(LicenseVerifier.Verify(status, "other-app", "sdk-pro", machineCode).IsValid);
            var machineMismatch = LicenseVerifier.Verify(status, AppKey, "sdk-pro", otherMachine);
            Assert.False(machineMismatch.IsValid);
            Assert.Contains("机器码", machineMismatch.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 离线码本地验签与在线校验结论一致()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        {
            var machineCode = MachineCodeOf("sdk-offline");
            var otherMachine = MachineCodeOf("sdk-offline-2");
            var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(raw);
            await IdentityTestHelpers.ReadDataAsync<LicenseProductDto>(
                await raw.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    "/api/v1/admin/licensing/products",
                    adminToken,
                    new CreateLicenseProductRequest("sdk-offline-pro", "SDK 离线版", null))));
            var issued = await IdentityTestHelpers.ReadDataAsync<OfflineLicenseCodeDto>(
                await raw.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    "/api/v1/admin/licensing/offline-codes",
                    adminToken,
                    new IssueOfflineLicenseCodeRequest("sdk-offline-pro", machineCode, null, 30, null, null))));

            await sdk.SignInAsync("admin", IdentityTestHelpers.AdminPassword, AppKey);

            var keys = (await sdk.GetTrustedSigningKeysAsync()).Select(x => x.PublicKey).ToArray();
            Assert.NotEmpty(keys);

            var local = LicenseVerifier.VerifyOfflineCode(issued.Code, machineCode, "sdk-offline-pro", keys);
            Assert.True(local.IsValid);
            Assert.False(LicenseVerifier.VerifyOfflineCode(issued.Code, otherMachine, "sdk-offline-pro", keys).IsValid);
            Assert.False(LicenseVerifier.VerifyOfflineCode(issued.FormattedCode, machineCode, "other-product", keys).IsValid);

            var online = await sdk.VerifyOfflineLicenseCodeAsync(issued.FormattedCode, machineCode, "sdk-offline-pro");
            Assert.True(online.IsValid);
        }
    }

    [Fact]
    public async Task 读取下发备份的元内容与内容()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        {
            var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(raw);
            await IdentityTestHelpers.ReadDataAsync<BackupNamespaceDto>(
                await raw.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    "/api/v1/admin/backups/namespaces",
                    adminToken,
                    new CreateBackupNamespaceRequest("sdk-global", "SDK 全局配置", null, null, null))));
            var backup = await IdentityTestHelpers.ReadDataAsync<BackupDto>(
                await raw.SendAsync(IdentityTestHelpers.Authorized(
                    HttpMethod.Post,
                    "/api/v1/admin/backups",
                    adminToken,
                    new CreateBackupRequest(
                        "sdk-global",
                        BackupConventions.KindConfig,
                        "sdk-theme",
                        null,
                        null,
                        null,
                        1,
                        "3.2.0",
                        "default",
                        """{"theme":"dark"}""",
                        null))));

            await sdk.SignInAsync("admin", IdentityTestHelpers.AdminPassword, AppKey);

            var metadata = await sdk.GetBackupAsync(backup.Id);
            Assert.Equal("sdk-theme", metadata.Name);
            Assert.True(metadata.HasInline);

            var content = await sdk.ReadBackupContentAsync(backup.Id);
            Assert.Contains("\"theme\":\"dark\"", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 下载类端点的业务错误也归一成平台异常()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        {
            await sdk.SignInAsync("admin", IdentityTestHelpers.AdminPassword, AppKey);

            // 检查更新先查渠道；清单按「渠道键 + 版本」联合命中，渠道不存在时归为版本不存在
            var missingChannel = await Assert.ThrowsAsync<PlatformClientException>(
                () => sdk.CheckUpdateAsync("no-such-channel", "1.0.0", "win-x64"));
            Assert.Equal(ApiErrorCode.VersionsChannelNotFound, missingChannel.Code);
            Assert.Equal((int)HttpStatusCode.NotFound, missingChannel.StatusCode);

            var missingRelease = await Assert.ThrowsAsync<PlatformClientException>(
                () => sdk.GetReleaseManifestAsync("no-such-channel", "1.0.0", "win-x64"));
            Assert.Equal(ApiErrorCode.VersionsReleaseNotFound, missingRelease.Code);
            Assert.Equal((int)HttpStatusCode.NotFound, missingRelease.StatusCode);

            var missingBackup = await Assert.ThrowsAsync<PlatformClientException>(
                () => sdk.ReadBackupContentAsync(Guid.NewGuid()));
            Assert.Equal(ApiErrorCode.BackupsNotFound, missingBackup.Code);
        }
    }

    [Fact]
    public async Task 登出后凭据清空且刷新令牌被服务端吊销()
    {
        var (raw, sdk) = CreatePair();
        using (raw)
        using (var nextLaunch = new PlatformClient(new PlatformClientOptions { BaseUrl = "http://localhost" }, raw))
        {
            await sdk.SignInAsync("admin", IdentityTestHelpers.AdminPassword, AppKey);
            var refreshToken = sdk.RefreshToken!;
            Assert.NotEmpty(refreshToken);

            await sdk.SignOutAsync();
            Assert.Null(sdk.AccessToken);
            Assert.Null(sdk.RefreshToken);

            // 模拟「把刷新令牌存进凭据库、下次启动回填」：登出已吊销它，续期必须失败而不是被自动重试掩盖
            nextLaunch.RefreshToken = refreshToken;
            var failure = await Assert.ThrowsAsync<PlatformClientException>(() => nextLaunch.RefreshAsync());
            Assert.Equal(ApiErrorCode.IdentityRefreshTokenRevoked, failure.Code);
        }
    }

    [Fact]
    public async Task 平台地址必须带协议否则拒绝构造()
    {
        Assert.ThrowsAny<ArgumentException>(() => new PlatformClient(new PlatformClientOptions { BaseUrl = "localhost:5080" }));
    }

    private (HttpClient Raw, PlatformClient Sdk) CreatePair()
    {
        // 复用宿主的 HttpClient：SDK 不拥有它，因此超时/代理与其余集成测试保持一致
        var raw = _factory.CreateClient();
        return (raw, new PlatformClient(new PlatformClientOptions { BaseUrl = "http://localhost" }, raw));
    }

    private static string MachineCodeOf(string seed) =>
        Base32Crockford.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, LicenseConventions.MachineCodeBytes));

    private static async Task<ReleaseChannelDto> CreateChannelAsync(HttpClient client, string token, string key) =>
        await IdentityTestHelpers.ReadDataAsync<ReleaseChannelDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/channels",
                token,
                new CreateReleaseChannelRequest(key, $"{key} 渠道", "SDK 测试"))));

    private static async Task<string> UploadAsync(HttpClient client, string token, byte[] content)
    {
        var sha = Convert.ToHexString(SHA256.HashData(content));
        var ticket = await IdentityTestHelpers.ReadDataAsync<UploadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/uploads",
                token,
                new CreateUploadTicketRequest(sha, content.Length))));

        using var upload = new HttpRequestMessage(HttpMethod.Put, ticket.UploadUrl)
        {
            Content = new ByteArrayContent(content),
        };
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await IdentityTestHelpers.ReadDataAsync<UploadResultDto>(await client.SendAsync(upload));
        return sha.ToLowerInvariant();
    }

    private static async Task<ReleaseDetailDto> CreateReleaseAsync(
        HttpClient client,
        string token,
        string channelKey,
        string version,
        string sha,
        long sizeBytes) =>
        await IdentityTestHelpers.ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/releases",
                token,
                new CreateReleaseRequest(
                    channelKey,
                    version,
                    "SDK 测试版本",
                    true,
                    [new CreateReleaseArtifactRequest("win-x64", [new ReleaseFileInput("App/App.exe", sha, sizeBytes)])]))));

    private static async Task<DocumentDetailDto> UploadDocumentAsync(
        HttpClient client,
        string token,
        string collectionKey,
        byte[] content,
        string? visibility = DocumentConventions.VisibilityClient)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(collectionKey), "collectionKey" },
        };
        form.Add(new StringContent("SDK 手册"), "title");
        if (visibility is not null)
        {
            form.Add(new StringContent(visibility), "visibility");
        }

        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "manual.pdf");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/documents")
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await IdentityTestHelpers.ReadDataAsync<DocumentDetailDto>(await client.SendAsync(request));
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_downloadDir))
        {
            Directory.Delete(_downloadDir, recursive: true);
        }
    }
}
