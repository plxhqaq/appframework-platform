using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Identity;
using Platform.Contracts.Permissions;
using Platform.Contracts.Versions;
using Platform.Infrastructure.Persistence;
using Platform.IntegrationTests.Support;
using Platform.Modules.Versions.Domain;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 版本模块全链路：上传票据一次性、清单校验、检查更新、下载（Range/ETag/304）、
/// 归档回退与跨应用隔离。
/// </summary>
public sealed class VersionsEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private const string ChannelKey = "stable";
    private static readonly byte[] AppBytes = "hello-app-binary"u8.ToArray();
    private static readonly byte[] LibBytes = "library-payload"u8.ToArray();

    private readonly PlatformApiFactory _factory;

    public VersionsEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 发布后检查更新下载并归档回退()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);

        var channel = await CreateChannelAsync(client, token, ChannelKey);
        Assert.Equal(ChannelKey, channel.Key);

        var appSha = await UploadAsync(client, token, AppBytes);
        var libSha = await UploadAsync(client, token, LibBytes);

        var v100 = await CreateReleaseAsync(
            client,
            token,
            ChannelKey,
            "1.0.0",
            publish: true,
            Artifact("win-x64", ("App/App.exe", AppBytes, appSha), ("lib/core.dll", LibBytes, libSha)));

        Assert.Equal(VersionConventions.StatusPublished, v100.Status);
        Assert.Equal(2, v100.FileCount);
        Assert.Equal(AppBytes.Length + LibBytes.Length, v100.TotalSizeBytes);

        var check = await ReadDataAsync<CheckUpdateResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/client/versions/check",
                token,
                new CheckUpdateRequest(ChannelKey, "0.9.0", "win-x64"))));
        Assert.True(check.HasUpdate);
        Assert.Equal("1.0.0", check.Release!.Version);
        Assert.Equal(2, check.Release.Files.Count);
        Assert.Contains(check.Release.Files, x => x.Path == "App/App.exe" && x.Sha256 == appSha);

        var manifest = await ReadDataAsync<ReleaseManifestDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/client/versions/{ChannelKey}/1.0.0/manifest?runtimeIdentifier=win-x64",
                token)));
        Assert.Equal(check.Release.ManifestHash, manifest.ManifestHash);

        // 下载全文 + ETag + Range + If-None-Match
        var appResponse = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/download/{appSha}",
            token));
        Assert.Equal(HttpStatusCode.OK, appResponse.StatusCode);
        Assert.Equal($"\"{appSha}\"", appResponse.Headers.ETag!.Tag);
        Assert.Equal(AppBytes, await appResponse.Content.ReadAsByteArrayAsync());

        using var rangeRequest = IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/download/{appSha}",
            token);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);
        var rangeResponse = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal($"bytes 0-3/{AppBytes.Length}", rangeResponse.Content.Headers.ContentRange!.ToString());
        Assert.Equal("hell", await rangeResponse.Content.ReadAsStringAsync());

        using var cachedRequest = IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/download/{appSha}",
            token);
        cachedRequest.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{appSha}\""));
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(cachedRequest)).StatusCode);

        // 新版本发布后 check 指向新版本；把新版本归档即回退到上一版
        var v101 = await CreateReleaseAsync(
            client,
            token,
            ChannelKey,
            "1.0.1",
            publish: true,
            Artifact("win-x64", ("App/App.exe", AppBytes, appSha)));

        var newer = await CheckAsync(client, token, ChannelKey, "1.0.0");
        Assert.True(newer.HasUpdate);
        Assert.Equal("1.0.1", newer.Release!.Version);

        await ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/versions/releases/{v101.Id}/archive",
                token)));

        // 归档后默认不降级：客户端版本更高时只标记 isDowngrade
        var rollback = await CheckAsync(client, token, ChannelKey, "1.0.1");
        Assert.False(rollback.HasUpdate);
        Assert.True(rollback.IsDowngrade);
        Assert.Equal("1.0.0", rollback.Release!.Version);

        var forcedRollback = await ReadDataAsync<CheckUpdateResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/client/versions/check",
                token,
                new CheckUpdateRequest(ChannelKey, "1.0.1", "win-x64", AllowDowngrade: true))));
        Assert.True(forcedRollback.HasUpdate);
        Assert.True(forcedRollback.IsDowngrade);

        var sameVersion = await CheckAsync(client, token, ChannelKey, "1.0.0");
        Assert.False(sameVersion.HasUpdate);

        await ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/versions/releases/{v100.Id}/archive",
                token)));

        var empty = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/versions/check",
            token,
            new CheckUpdateRequest(ChannelKey, null, "win-x64")));
        Assert.Equal(
            (int)ApiErrorCode.VersionsNoPublishedRelease,
            await IdentityTestHelpers.ReadErrorCodeAsync(empty, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task 检查更新按语义版本选版且默认不降级()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);

        var channel = await CreateChannelAsync(client, token, "semver");
        var sha = await UploadAsync(client, token, AppBytes);

        // 先发 1.10.0 再发 1.9.0：按发布时间选版会错选 1.9.0。
        await CreateReleaseAsync(
            client, token, "semver", "1.10.0", publish: true,
            Artifact("win-x64", ("App.exe", AppBytes, sha)));
        await CreateReleaseAsync(
            client, token, "semver", "1.9.0", publish: true,
            Artifact("win-x64", ("App.exe", AppBytes, sha)));

        var check = await CheckAsync(client, token, "semver", "1.0.0");
        Assert.True(check.HasUpdate);
        Assert.Equal("1.10.0", check.LatestVersion);
        Assert.Equal("1.10.0", check.Release!.Version);
        Assert.False(check.IsDowngrade);

        var same = await CheckAsync(client, token, "semver", "1.10.0");
        Assert.False(same.HasUpdate);
        Assert.Equal("1.10.0", same.Release!.Version);

        // 四段与三段等价：1.10.0.0 == 1.10.0
        var fourSegment = await CheckAsync(client, token, "semver", "1.10.0.0");
        Assert.False(fourSegment.HasUpdate);

        var unknown = await CheckAsync(client, token, "semver", null);
        Assert.True(unknown.HasUpdate);

        // 当前版本更高：默认不降级，但如实标记 isDowngrade
        var newer = await CheckAsync(client, token, "semver", "2.0.0");
        Assert.False(newer.HasUpdate);
        Assert.True(newer.IsDowngrade);
        Assert.Equal("1.10.0", newer.Release!.Version);

        // 请求显式允许降级
        var requested = await ReadDataAsync<CheckUpdateResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/client/versions/check",
                token,
                new CheckUpdateRequest("semver", "2.0.0", "win-x64", AllowDowngrade: true))));
        Assert.True(requested.HasUpdate);
        Assert.True(requested.IsDowngrade);

        // 渠道级 allowDowngrade 一旦打开，客户端无需再传请求参数
        await ReadDataAsync<ReleaseChannelDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/versions/channels/{channel.Id}",
                token,
                new UpdateReleaseChannelRequest(null, null, null, AllowDowngrade: true))));

        var channelAllowed = await CheckAsync(client, token, "semver", "2.0.0");
        Assert.True(channelAllowed.HasUpdate);
        Assert.True(channelAllowed.IsDowngrade);
    }

    [Fact]
    public async Task 上传票据只能消费一次且过期与哈希不符分别拒绝()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        var content = "single-use-ticket"u8.ToArray();
        var sha = Sha256(content);

        var ticket = await CreateTicketAsync(client, token, sha, content.Length);
        var upload = await UploadWithTicketAsync(client, token, ticket, content);
        Assert.Equal(sha, upload.Sha256);
        Assert.False(upload.Reused);

        // 同一票据再次上传：第二段内容不会被写入
        using var replay = UploadRequest(token, ticket, content);
        Assert.Equal(
            (int)ApiErrorCode.VersionsUploadTicketConsumed,
            await IdentityTestHelpers.ReadErrorCodeAsync(
                await client.SendAsync(replay), HttpStatusCode.Conflict));

        // 声明哈希与实际内容不符：票据被消费，内容不落盘
        var wrongSha = Sha256("expected-content"u8.ToArray());
        var mismatchTicket = await CreateTicketAsync(client, token, wrongSha, content.Length);
        using var mismatch = UploadRequest(token, mismatchTicket, content);
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageHashMismatch,
            await IdentityTestHelpers.ReadErrorCodeAsync(
                await client.SendAsync(mismatch), HttpStatusCode.UnprocessableEntity));

        // 过期票据
        var expiredTicket = await CreateTicketAsync(client, token, sha, content.Length);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var entity = await db.Set<VersionUploadTicket>().SingleAsync(x => x.Id == expiredTicket.Id);
            entity.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using var expired = UploadRequest(token, expiredTicket, content);
        Assert.Equal(
            (int)ApiErrorCode.VersionsUploadTicketExpired,
            await IdentityTestHelpers.ReadErrorCodeAsync(
                await client.SendAsync(expired), HttpStatusCode.Gone));

        // 未知票据
        var unknownId = Guid.CreateVersion7();
        using var unknown = UploadRequest(
            token,
            new UploadTicketDto(unknownId, $"/api/v1/admin/versions/uploads/{unknownId}", default),
            content);
        Assert.Equal(
            (int)ApiErrorCode.VersionsUploadTicketInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(
                await client.SendAsync(unknown), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task 清单缺文件与重复版本分别返回206与204()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);

        await CreateChannelAsync(client, token, "manifest");
        var missingSha = Sha256("never-uploaded"u8.ToArray());

        var missing = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/releases",
            token,
            new CreateReleaseRequest(
                "manifest",
                "1.0.0",
                null,
                false,
                [new CreateReleaseArtifactRequest("win-x64", [new ReleaseFileInput("App.exe", missingSha, 42)])])));
        Assert.Equal(
            (int)ApiErrorCode.VersionsManifestMismatch,
            await IdentityTestHelpers.ReadErrorCodeAsync(missing, HttpStatusCode.UnprocessableEntity));

        var sha = await UploadAsync(client, token, AppBytes);
        await CreateReleaseAsync(
            client,
            token,
            "manifest",
            "1.0.0",
            publish: true,
            Artifact("win-x64", ("App.exe", AppBytes, sha)));

        var duplicate = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/releases",
            token,
            new CreateReleaseRequest(
                "manifest",
                "1.0.0",
                null,
                true,
                [new CreateReleaseArtifactRequest("win-x64", [new ReleaseFileInput("App.exe", sha, AppBytes.Length)])])));
        Assert.Equal(
            (int)ApiErrorCode.VersionsReleaseVersionExists,
            await IdentityTestHelpers.ReadErrorCodeAsync(duplicate, HttpStatusCode.Conflict));

        var unsupported = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/versions/check",
            token,
            new CheckUpdateRequest("manifest", "1.0.0", "plan9-x86")));
        Assert.Equal(
            (int)ApiErrorCode.VersionsRuntimeIdentifierUnsupported,
            await IdentityTestHelpers.ReadErrorCodeAsync(unsupported, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task 草稿版本对客户端不可见且可在发布后可见()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);

        await CreateChannelAsync(client, token, "draft");
        var sha = await UploadAsync(client, token, LibBytes);
        var draft = await CreateReleaseAsync(
            client,
            token,
            "draft",
            "2.0.0",
            publish: false,
            Artifact("win-x64", ("lib/core.dll", LibBytes, sha)));

        Assert.Equal(VersionConventions.StatusDraft, draft.Status);

        var check = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/versions/check",
            token,
            new CheckUpdateRequest("draft", null, "win-x64")));
        Assert.Equal(
            (int)ApiErrorCode.VersionsNoPublishedRelease,
            await IdentityTestHelpers.ReadErrorCodeAsync(check, HttpStatusCode.NotFound));

        var manifest = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            "/api/v1/client/versions/draft/2.0.0/manifest?runtimeIdentifier=win-x64",
            token));
        Assert.Equal(
            (int)ApiErrorCode.VersionsReleaseNotPublished,
            await IdentityTestHelpers.ReadErrorCodeAsync(manifest, HttpStatusCode.Conflict));

        await ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/versions/releases/{draft.Id}/publish",
                token)));

        var afterPublish = await CheckAsync(client, token, "draft", null);
        Assert.True(afterPublish.HasUpdate);
        Assert.Equal("2.0.0", afterPublish.Release!.Version);
    }

    [Fact]
    public async Task 草稿可改发布说明且发布后不可再改()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);

        await CreateChannelAsync(client, token, "draft-notes");
        var sha = await UploadAsync(client, token, AppBytes);
        var draft = await CreateReleaseAsync(
            client,
            token,
            "draft-notes",
            "3.0.0",
            publish: false,
            Artifact("win-x64", ("App.exe", AppBytes, sha)));
        Assert.Equal("集成测试版本", draft.Notes);

        var updated = await ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/versions/releases/{draft.Id}",
                token,
                new UpdateReleaseRequest("发布前核对后的说明"))));
        Assert.Equal("发布前核对后的说明", updated.Notes);

        // 空白视为清空
        var cleared = await ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/versions/releases/{draft.Id}",
                token,
                new UpdateReleaseRequest("  "))));
        Assert.Null(cleared.Notes);

        await ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/versions/releases/{draft.Id}/publish",
                token)));

        var rejected = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/versions/releases/{draft.Id}",
            token,
            new UpdateReleaseRequest("发布后再改")));
        Assert.Equal(
            (int)ApiErrorCode.VersionsReleaseImmutable,
            await IdentityTestHelpers.ReadErrorCodeAsync(rejected, HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task 停用渠道后不能再创建版本且渠道键幂等()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);

        var first = await CreateChannelAsync(client, token, "frozen", idempotencyKey: "channel-frozen");
        var replay = await CreateChannelAsync(client, token, "frozen", idempotencyKey: "channel-frozen");
        Assert.Equal(first.Id, replay.Id);

        await ReadDataAsync<ReleaseChannelDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Patch,
                $"/api/v1/admin/versions/channels/{first.Id}",
                token,
                new UpdateReleaseChannelRequest(null, null, false))));

        var sha = await UploadAsync(client, token, AppBytes);
        var response = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/releases",
            token,
            new CreateReleaseRequest(
                "frozen",
                "1.0.0",
                null,
                true,
                [new CreateReleaseArtifactRequest("win-x64", [new ReleaseFileInput("App.exe", sha, AppBytes.Length)])])));
        Assert.Equal(
            (int)ApiErrorCode.ValidationFailed,
            await IdentityTestHelpers.ReadErrorCodeAsync(response, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task 渠道删除按版本留痕受控()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);

        var busy = await CreateChannelAsync(client, token, "busy-channel");
        var sha = await UploadAsync(client, token, AppBytes);
        var release = await CreateReleaseAsync(
            client, token, "busy-channel", "1.0.0", publish: false,
            Artifact("win-x64", ("App.exe", AppBytes, sha)));

        // 仍登记版本（含草稿）时拒绝删除
        var rejected = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/versions/channels/{busy.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.VersionsReleaseImmutable,
            await IdentityTestHelpers.ReadErrorCodeAsync(rejected, HttpStatusCode.Conflict));

        // 先删版本，渠道即可删
        await ReadDataAsync<ReleaseDetailDto>(await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/versions/releases/{release.Id}", token)));
        Assert.True(await ReadDataAsync<bool>(await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/versions/channels/{busy.Id}", token))));

        var listed = await ReadDataAsync<List<ReleaseChannelDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/versions/channels", token)));
        Assert.DoesNotContain(listed, x => x.Id == busy.Id);

        // 空渠道直接可删；重复删除按不存在
        var empty = await CreateChannelAsync(client, token, "empty-channel");
        Assert.True(await ReadDataAsync<bool>(await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/versions/channels/{empty.Id}", token))));
        var gone = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/versions/channels/{empty.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.VersionsChannelNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(gone, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task 无权限与匿名请求被拒绝且跨应用看不到他人渠道()
    {
        using var client = _factory.CreateClient();
        var adminToken = await LoginAsync(client);

        await CreateChannelAsync(client, adminToken, "private");

        // 匿名下载：未认证
        using var anonymous = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/client/versions/download/{Sha256(AppBytes)}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(anonymous)).StatusCode);

        // viewer 只有读权限：不能建渠道
        const string viewerPassword = "Viewer@12345";
        var viewer = await IdentityTestHelpers.CreateUserAsync(
            client,
            adminToken,
            "versions-viewer",
            viewerPassword,
            "版本只读");
        Assert.NotEqual(Guid.Empty, viewer.Id);
        var viewerLogin = await IdentityTestHelpers.LoginAsync(client, "versions-viewer", viewerPassword);

        var forbidden = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/channels",
            viewerLogin.AccessToken,
            new CreateReleaseChannelRequest("forbidden", "越权渠道", null)));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // 另一个应用通过 M2M 令牌访问：看不到 demo 的渠道
        const string appKey = "versions-iso";
        var app = await ReadDataAsync<AppDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/apps",
                adminToken,
                new CreateAppRequest(appKey, "版本隔离测试"))));
        var key = await ReadDataAsync<CreatedApiKeyDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/apps/{app.Id}/keys",
                adminToken,
                new CreateApiKeyRequest("iso", [PermissionCatalog.VersionsReleaseRead]))));

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/apps/token");
        tokenRequest.Headers.TryAddWithoutValidation(PlatformHeaders.AppKey, appKey);
        tokenRequest.Headers.TryAddWithoutValidation(PlatformHeaders.ApiKey, key.Plaintext);
        var m2m = await ReadDataAsync<M2MTokenResponse>(await client.SendAsync(tokenRequest));
        Assert.Equal(appKey, m2m.AppKey);

        var crossApp = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/versions/check",
            m2m.AccessToken,
            new CheckUpdateRequest("private", null, "win-x64")));
        Assert.Equal(
            (int)ApiErrorCode.VersionsChannelNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(crossApp, HttpStatusCode.NotFound));

        // 只有读权限的 M2M 令牌不能申请上传票据
        var uploadDenied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/uploads",
            m2m.AccessToken,
            new CreateUploadTicketRequest(Sha256(AppBytes), AppBytes.Length)));
        Assert.Equal(HttpStatusCode.Forbidden, uploadDenied.StatusCode);
    }

    [Fact]
    public async Task 切换应用上下文后可访问版本接口且刷新后保持()
    {
        using var client = _factory.CreateClient();

        // 上下文切换依赖 httpOnly 刷新 Cookie，因此走管理端登录而不是桌面端登录
        var login = await ReadDataAsync<AdminSessionResponse>(
            await client.PostAsJsonAsync(
                "/api/v1/admin/auth/login",
                new LoginRequest(IdentityTestHelpers.AdminUserName, IdentityTestHelpers.AdminPassword)));
        var adminToken = login.AccessToken;

        // 平台管理员登录只拿到平台上下文（无 app_ctx）：先显式切到种子应用 demo 才能访问应用级资源
        var demoSession = await ReadDataAsync<AdminSessionResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/auth/context",
                adminToken,
                new SwitchAppContextRequest(IdentityConventions.DemoAppKey))));
        Assert.Equal(IdentityConventions.DemoAppKey, demoSession.User.AppKey);
        await CreateChannelAsync(client, demoSession.AccessToken, "context-demo");

        const string otherAppKey = "versions-context";
        await ReadDataAsync<AppDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/apps",
                adminToken,
                new CreateAppRequest(otherAppKey, "上下文切换测试"))));

        var switched = await ReadDataAsync<AdminSessionResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/auth/context",
                adminToken,
                new SwitchAppContextRequest(otherAppKey))));
        Assert.Equal(otherAppKey, switched.User.AppKey);

        // 切到另一个应用后看不到 demo 的渠道
        var channels = await ReadDataAsync<List<ReleaseChannelDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/versions/channels",
                switched.AccessToken)));
        Assert.DoesNotContain(channels, x => x.Key == "context-demo");

        // 静默续期后仍停留在该应用：刷新令牌记录了应用键
        var refreshed = await ReadDataAsync<AdminSessionResponse>(
            await client.PostAsync("/api/v1/admin/auth/refresh", content: null));
        Assert.Equal(otherAppKey, refreshed.User.AppKey);

        await CreateChannelAsync(client, refreshed.AccessToken, "context-switched");

        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/auth/context",
            refreshed.AccessToken,
            new SwitchAppContextRequest("not-granted")));
        Assert.Equal(
            (int)ApiErrorCode.IdentityPermissionDenied,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.Forbidden));
    }

    [Fact]
    public async Task 版本列表把已归档行排在最后()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        const string channelKey = "sort-archived";

        await CreateChannelAsync(client, token, channelKey);
        var sha = await UploadAsync(client, token, LibBytes);
        var artifact = Artifact("win-x64", ("lib/core.dll", LibBytes, sha));

        var older = await CreateReleaseAsync(client, token, channelKey, "1.0.0", publish: true, artifact);
        var middle = await CreateReleaseAsync(client, token, channelKey, "2.0.0", publish: true, artifact);
        var newest = await CreateReleaseAsync(client, token, channelKey, "3.0.0", publish: true, artifact);

        await ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/versions/releases/{newest.Id}/archive",
                token)));

        var listed = await ReadDataAsync<PageResult<ReleaseSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                $"/api/v1/admin/versions/releases?channelKey={channelKey}",
                token)));

        Assert.Equal(
            new[] { middle.Version, older.Version, newest.Version },
            listed.Items.Select(x => x.Version).ToArray());
        Assert.Equal(VersionConventions.StatusArchived, listed.Items[^1].Status);
    }

    private static async Task<string> LoginAsync(HttpClient client) =>
        await IdentityTestHelpers.LoginAsAdminAsync(client);

    private static async Task<ReleaseChannelDto> CreateChannelAsync(
        HttpClient client,
        string token,
        string key,
        string? idempotencyKey = null)
    {
        using var request = IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/channels",
            token,
            new CreateReleaseChannelRequest(key, $"{key} 渠道", "集成测试"));
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation(PlatformHeaders.IdempotencyKey, idempotencyKey);
        }

        return await ReadDataAsync<ReleaseChannelDto>(await client.SendAsync(request));
    }

    private static async Task<ReleaseDetailDto> CreateReleaseAsync(
        HttpClient client,
        string token,
        string channelKey,
        string version,
        bool publish,
        CreateReleaseArtifactRequest artifact)
    {
        return await ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/releases",
                token,
                new CreateReleaseRequest(channelKey, version, "集成测试版本", publish, [artifact]))));
    }

    private static CreateReleaseArtifactRequest Artifact(
        string runtimeIdentifier,
        params (string Path, byte[] Content, string Sha)[] files) =>
        new(
            runtimeIdentifier,
            [.. files.Select(static file =>
                new ReleaseFileInput(file.Path, file.Sha, file.Content.Length))]);

    private static async Task<string> UploadAsync(HttpClient client, string token, byte[] content)
    {
        var sha = Sha256(content);
        var ticket = await CreateTicketAsync(client, token, sha, content.Length);
        var result = await UploadWithTicketAsync(client, token, ticket, content);
        Assert.Equal(sha, result.Sha256);
        return sha;
    }

    private static async Task<UploadTicketDto> CreateTicketAsync(
        HttpClient client,
        string token,
        string sha,
        long sizeBytes) =>
        await ReadDataAsync<UploadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/uploads",
                token,
                new CreateUploadTicketRequest(sha, sizeBytes))));

    private static async Task<UploadResultDto> UploadWithTicketAsync(
        HttpClient client,
        string token,
        UploadTicketDto ticket,
        byte[] content)
    {
        using var request = UploadRequest(token, ticket, content);
        return await ReadDataAsync<UploadResultDto>(await client.SendAsync(request));
    }

    private static HttpRequestMessage UploadRequest(string token, UploadTicketDto ticket, byte[] content)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, ticket.UploadUrl)
        {
            Content = new ByteArrayContent(content),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
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
