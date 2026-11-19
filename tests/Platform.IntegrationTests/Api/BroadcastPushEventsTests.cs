using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Endpoints;
using Platform.Contracts.Apps;
using Platform.Contracts.Devices;
using Platform.Contracts.Documents;
using Platform.Contracts.Hubs;
using Platform.Contracts.Versions;
using Platform.IntegrationTests.Support;
using Platform.Modules.Versions.Services;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// W3 广播事件三件套：ReleasePublished / DocumentChanged / PermissionManifestChanged
/// 走 devices app 组、跨应用不可见、只提示不回写事实；摄取进度走 AdminHub versions 域。
/// 验收口径：发布新版本 → 在线实例收到提示并回拉 check 命中。
/// </summary>
public sealed class BroadcastPushEventsTests : IClassFixture<PlatformApiFactory>
{
    private const string DemoAppKey = "demo";

    private readonly PlatformApiFactory _factory;

    public BroadcastPushEventsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 创建即发布的新版本触达在线实例且回拉check命中()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var target = await ConnectInstallationAsync(client, admin, DemoAppKey, "BC-V1-A");
        var otherKey = $"bc-iso-{Guid.NewGuid().ToString("N")[..8]}";
        await EnsureAppAsync(client, admin, otherKey);
        var other = await ConnectInstallationAsync(client, admin, otherKey, "BC-V1-B");
        other.Connection.On<ReleasePublishedNotification>(DeviceHubEvents.ReleasePublished, _ => Interlocked.Increment(ref _leaked));

        var notification = new TaskCompletionSource<ReleasePublishedNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        target.Connection.On<ReleasePublishedNotification>(
            DeviceHubEvents.ReleasePublished, x => { notification.TrySetResult(x); });

        var channelKey = $"bc-v1-{Guid.NewGuid().ToString("N")[..8]}";
        var version = "9.1.0";
        await CreateChannelAsync(client, admin, channelKey);
        var sha = await UploadBytesAsync(client, admin, "payload-v910"u8.ToArray());
        await CreateReleaseAsync(client, admin, channelKey, version, publish: true, Artifact("win-x64", ("App.exe", "payload-v910"u8.ToArray(), sha)));

        var received = await notification.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DemoAppKey, received.AppKey);
        Assert.Equal(channelKey, received.ChannelKey);
        Assert.Equal(version, received.Version);

        // 推送只是提示：事实以回拉 check 为准。
        var check = await CheckAsync(client, admin, channelKey, "1.0.0");
        Assert.True(check.HasUpdate);
        Assert.Equal(version, check.Release!.Version);

        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref _leaked));

        await target.Connection.DisposeAsync();
        await other.Connection.DisposeAsync();
    }

    [Fact]
    public async Task 草稿发布触达一次且重复发布不重发()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var target = await ConnectInstallationAsync(client, admin, DemoAppKey, "BC-V2-A");
        var count = 0;
        var first = new TaskCompletionSource<ReleasePublishedNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        target.Connection.On<ReleasePublishedNotification>(
            DeviceHubEvents.ReleasePublished, x =>
            {
                Interlocked.Increment(ref count);
                first.TrySetResult(x);
            });

        var channelKey = $"bc-v2-{Guid.NewGuid().ToString("N")[..8]}";
        await CreateChannelAsync(client, admin, channelKey);
        var sha = await UploadBytesAsync(client, admin, "payload-draft"u8.ToArray());
        var draft = await CreateReleaseAsync(client, admin, channelKey, "9.2.0", publish: false, Artifact("win-x64", ("App.exe", "payload-draft"u8.ToArray(), sha)));

        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref count));

        await IdentityTestHelpers.ReadDataAsync<ReleaseDetailDto>(await client.SendAsync(
            IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/versions/releases/{draft.Id}/publish", admin)));
        var received = await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("9.2.0", received.Version);

        // 已发布再调发布端点是幂等早退，不重复提示。
        await IdentityTestHelpers.ReadDataAsync<ReleaseDetailDto>(await client.SendAsync(
            IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/versions/releases/{draft.Id}/publish", admin)));
        await Task.Delay(500);
        Assert.Equal(1, Volatile.Read(ref count));

        await target.Connection.DisposeAsync();
    }

    [Fact]
    public async Task 客户端可见文档增删改触达而内部文档静默()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var target = await ConnectInstallationAsync(client, admin, DemoAppKey, "BC-D1-A");
        var events = new ConcurrentQueue<DocumentChangedNotification>();
        target.Connection.On<DocumentChangedNotification>(
            DeviceHubEvents.DocumentChanged, x => events.Enqueue(x));

        var collectionKey = $"bc-d1-{Guid.NewGuid().ToString("N")[..8]}";
        await IdentityTestHelpers.ReadDataAsync<DocumentCollectionDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/documents/collections",
                admin,
                new CreateDocumentCollectionRequest(collectionKey, collectionKey, "广播集成测试"))));

        // internal 文档（默认可见性）上传：不打扰客户端。
        var internalDoc = await UploadDocumentAsync(
            client, admin, collectionKey, "internal.txt", "secret"u8.ToArray());
        await Task.Delay(500);
        Assert.True(events.IsEmpty);

        // client 文档上传 → 提示。
        var clientDoc = await UploadDocumentAsync(
            client, admin, collectionKey, "manual.pdf", "manual-v1"u8.ToArray(),
            visibility: DocumentConventions.VisibilityClient);
        await WaitUntilAsync(() => !events.IsEmpty, "client 文档上传未触发 DocumentChanged");
        var uploadEvent = DequeueOne(events);
        Assert.Equal(DemoAppKey, uploadEvent.AppKey);
        Assert.Equal(collectionKey, uploadEvent.CollectionKey);

        // 新修订 → 再次提示。
        await IdentityTestHelpers.ReadDataAsync<DocumentDetailDto>(await client.SendAsync(
            DocumentRevisionRequest(admin, clientDoc.Id, "manual-v2"u8.ToArray())));
        await WaitUntilAsync(() => !events.IsEmpty, "新修订未触发 DocumentChanged");
        DequeueOne(events);

        // internal → client 翻转（旧内新外任一可见都要提示）→ 提示。
        await IdentityTestHelpers.ReadDataAsync<DocumentDetailDto>(await client.SendAsync(
            IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/documents/{internalDoc.Id}",
                admin,
                new UpdateDocumentRequest(null, DocumentConventions.VisibilityClient))));
        await WaitUntilAsync(() => !events.IsEmpty, "可见性翻转未触发 DocumentChanged");
        DequeueOne(events);

        // 删除 client 文档 → 提示。
        await IdentityTestHelpers.ReadDataAsync<DocumentSummaryDto>(await client.SendAsync(
            IdentityTestHelpers.Authorized(
                HttpMethod.Delete, $"/api/v1/admin/documents/{clientDoc.Id}", admin)));
        await WaitUntilAsync(() => !events.IsEmpty, "删除未触发 DocumentChanged");
        DequeueOne(events);

        await Task.Delay(500);
        Assert.True(events.IsEmpty);

        await target.Connection.DisposeAsync();
    }

    [Fact]
    public async Task 权限清单上传锁定与人工解锁各自触达且无翻转不重发()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var appKey = $"bc-perm-{Guid.NewGuid().ToString("N")[..10]}";
        var app = await IdentityTestHelpers.ReadDataAsync<AppDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/apps",
                admin,
                new CreateAppRequest(appKey, "广播权限测试"))));
        var target = await ConnectInstallationAsync(client, admin, appKey, "BC-P1-A");
        var events = new ConcurrentQueue<PermissionManifestChangedNotification>();
        target.Connection.On<PermissionManifestChangedNotification>(
            DeviceHubEvents.PermissionManifestChanged, x => events.Enqueue(x));

        await IdentityTestHelpers.ReadDataAsync<AppPermissionManifestDto>(await client.SendAsync(
            IdentityTestHelpers.Authorized(
                HttpMethod.Put,
                $"/api/v1/apps/{appKey}/permissions",
                admin,
                new ReplaceAppPermissionsRequest(
                    [new AppPermissionDto($"{appKey}.page.read", "查看")], null))));
        await WaitUntilAsync(() => !events.IsEmpty, "清单上传未触发 PermissionManifestChanged");
        var replaced = DequeueOne(events);
        Assert.Equal(appKey, replaced.AppKey);
        Assert.True(replaced.Locked);

        await IdentityTestHelpers.ReadDataAsync<AppPermissionManifestDto>(await client.SendAsync(
            IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/apps/{app.Id}/permissions/unlock", admin)));
        await WaitUntilAsync(() => !events.IsEmpty, "人工解锁未触发 PermissionManifestChanged");
        var unlocked = DequeueOne(events);
        Assert.False(unlocked.Locked);

        // 已是解锁态再解锁：无状态翻转，不重复提示。
        await IdentityTestHelpers.ReadDataAsync<AppPermissionManifestDto>(await client.SendAsync(
            IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/apps/{app.Id}/permissions/unlock", admin)));
        await Task.Delay(500);
        Assert.True(events.IsEmpty);

        await target.Connection.DisposeAsync();
    }

    [Fact]
    public async Task 摄取进度推送AdminHub且自动发布同步触达设备组()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var device = await ConnectInstallationAsync(client, admin, DemoAppKey, "BC-I1-A");
        var otherKey = $"bc-iso-{Guid.NewGuid().ToString("N")[..8]}";
        await EnsureAppAsync(client, admin, otherKey);
        var other = await ConnectInstallationAsync(client, admin, otherKey, "BC-I1-B");
        other.Connection.On<ReleasePublishedNotification>(DeviceHubEvents.ReleasePublished, _ => Interlocked.Increment(ref _leaked));

        var releaseNotification = new TaskCompletionSource<ReleasePublishedNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.Connection.On<ReleasePublishedNotification>(
            DeviceHubEvents.ReleasePublished, x => { releaseNotification.TrySetResult(x); });

        var progress = new ConcurrentQueue<AdminReleaseIngestionProgressNotification>();
        var adminHub = await ConnectAsync(AdminHubPaths.Root, admin);
        adminHub.On<AdminReleaseIngestionProgressNotification>(
            AdminHubEvents.ReleaseIngestionProgress, x => progress.Enqueue(x));
        await adminHub.StartAsync();

        var channelKey = $"bc-i1-{Guid.NewGuid().ToString("N")[..8]}";
        const string version = "7.0.0";
        await CreateChannelAsync(client, admin, channelKey);
        var payload = "incremental-broadcast"u8.ToArray();
        var packageBytes = BuildZip(
            ("af-package.json", IncrementalManifest(channelKey, version, ("App.exe", Sha256(payload), payload.Length))),
            ("App.exe", payload));
        await RegisterAsync(client, admin, new RegisterReleasePackageRequest(
            await UploadBytesAsync(client, admin, packageBytes),
            packageBytes.Length,
            $"Update-{channelKey}-{version}.zip",
            VersionConventions.PackageKindIncremental,
            Target: "win-x64",
            Format: "zip",
            ChannelKey: channelKey,
            Version: version,
            Publish: true));

        var processed = await ProcessNextAsync();
        Assert.NotNull(processed);
        Assert.Equal(VersionConventions.PackageStatusReady, processed!.Status);

        await WaitUntilAsync(
            () => progress.Any(x => x.Status == VersionConventions.PackageStatusProcessing)
                  && progress.Any(x => x.Status == VersionConventions.PackageStatusReady),
            "摄取状态迁移未推送 ReleaseIngestionProgress");
        Assert.All(progress, x => Assert.Equal(DemoAppKey, x.AppKey));
        Assert.Equal(processed.Id, progress.Single(x => x.Status == VersionConventions.PackageStatusReady).PackageId);
        Assert.Equal(1.0, progress.Single(x => x.Status == VersionConventions.PackageStatusReady).Progress);

        var released = await releaseNotification.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(channelKey, released.ChannelKey);
        Assert.Equal(version, released.Version);

        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref _leaked));

        await adminHub.DisposeAsync();
        await device.Connection.DisposeAsync();
        await other.Connection.DisposeAsync();
    }

    private int _leaked;

    private static async Task WaitUntilAsync(Func<bool> condition, string failMessage)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail(failMessage);
    }

    private static T DequeueOne<T>(ConcurrentQueue<T> queue)
    {
        Assert.True(queue.TryDequeue(out var item));
        return item;
    }

    private static async Task EnsureAppAsync(HttpClient client, string adminToken, string appKey)
    {
        _ = await IdentityTestHelpers.ReadDataAsync<AppDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/apps",
                adminToken,
                new CreateAppRequest(appKey, "广播隔离测试"))));
    }

    private async Task<HubConnection> ConnectAsync(string hubPath, string token) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, hubPath),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
            .Build();

    private async Task<TestInstallation> ConnectInstallationAsync(
        HttpClient client,
        string adminToken,
        string appKey,
        string displayName)
    {
        var connection = await ConnectAsync(DeviceHubPaths.Root, await DevTokenAsync(client, appKey));
        await connection.StartAsync();
        var installGuid = Guid.NewGuid();
        var registration = await connection.InvokeAsync<DeviceRegistrationResponse>(
            DeviceHubMethods.RegisterInstallation,
            new DeviceRegistrationRequest(
                new DeviceIdentity(
                    InstallGuid: installGuid,
                    SmbiosUuid: null,
                    SystemDiskSerial: $"bc-{installGuid:N}",
                    FrameworkVersion: "1.0.0"),
                displayName,
                IpAddress: "10.9.8.7"));
        return new TestInstallation(connection, registration);
    }

    private static async Task<string> DevTokenAsync(HttpClient client, string appKey)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/dev/token",
            new DevTokenRequest(
                Subject: $"broadcast-{Guid.NewGuid():N}",
                Name: appKey,
                Apps: [appKey],
                AppKey: appKey));
        return (await IdentityTestHelpers.ReadDataAsync<DevTokenResponse>(response)).AccessToken;
    }

    private static async Task<ReleaseChannelDto> CreateChannelAsync(
        HttpClient client,
        string token,
        string key) =>
        await IdentityTestHelpers.ReadDataAsync<ReleaseChannelDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/channels",
                token,
                new CreateReleaseChannelRequest(key, $"{key} 渠道", "广播集成测试"))));

    private static async Task<ReleaseDetailDto> CreateReleaseAsync(
        HttpClient client,
        string token,
        string channelKey,
        string version,
        bool publish,
        CreateReleaseArtifactRequest artifact) =>
        await IdentityTestHelpers.ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/releases",
                token,
                new CreateReleaseRequest(channelKey, version, "广播集成测试版本", publish, [artifact]))));

    private static CreateReleaseArtifactRequest Artifact(
        string runtimeIdentifier,
        params (string Path, byte[] Content, string Sha)[] files) =>
        new(
            runtimeIdentifier,
            [.. files.Select(static file =>
                new ReleaseFileInput(file.Path, file.Sha, file.Content.Length))]);

    private static async Task<string> UploadBytesAsync(HttpClient client, string token, byte[] content)
    {
        var sha = Sha256(content);
        var ticket = await IdentityTestHelpers.ReadDataAsync<UploadTicketDto>(
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
        var result = await IdentityTestHelpers.ReadDataAsync<UploadResultDto>(await client.SendAsync(request));
        Assert.Equal(sha, result.Sha256);
        return sha;
    }

    private static async Task<ReleasePackageDto> RegisterAsync(
        HttpClient client,
        string token,
        RegisterReleasePackageRequest request) =>
        await IdentityTestHelpers.ReadDataAsync<ReleasePackageDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/packages",
                token,
                request)));

    private static async Task<CheckUpdateResponse> CheckAsync(
        HttpClient client,
        string token,
        string channelKey,
        string? currentVersion) =>
        await IdentityTestHelpers.ReadDataAsync<CheckUpdateResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/client/versions/check",
                token,
                new CheckUpdateRequest(channelKey, currentVersion, "win-x64"))));

    private async Task<ReleasePackageDto?> ProcessNextAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<IReleasePackageIngestionService>();
        return await ingestion.ProcessNextAsync();
    }

    private static async Task<DocumentDetailDto> UploadDocumentAsync(
        HttpClient client,
        string token,
        string collectionKey,
        string fileName,
        byte[] content,
        string? visibility = null)
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

        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/documents")
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await IdentityTestHelpers.ReadDataAsync<DocumentDetailDto>(await client.SendAsync(request));
    }

    private static HttpRequestMessage DocumentRevisionRequest(string token, Guid documentId, byte[] content)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "manual.pdf");

        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/admin/documents/{documentId}/revisions")
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static string IncrementalManifest(
        string channelKey,
        string version,
        params (string Path, string Sha, long Size)[] files)
    {
        var entries = string.Join(
            ",",
            files.Select(static file =>
                $"{{ \"path\": \"{file.Path}\", \"sha256\": \"{file.Sha}\", \"sizeBytes\": {file.Size} }}"));
        return $$"""
        {
          "schema": "af-package/1",
          "kind": "incremental",
          "channelKey": "{{channelKey}}",
          "version": "{{version}}",
          "runtimeIdentifier": "win-x64",
          "files": [
              {{entries}}
          ]
        }
        """;
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

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed record TestInstallation(HubConnection Connection, DeviceRegistrationResponse Registration);
}
