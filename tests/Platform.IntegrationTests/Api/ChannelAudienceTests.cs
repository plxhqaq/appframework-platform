using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>受众标签解析、最具体匹配与显式渠道优先级。</summary>
public sealed class ChannelAudienceTests : IClassFixture<PlatformApiFactory>
{
    private static readonly byte[] Payload = "audience-payload"u8.ToArray();
    private static readonly string[] ScannerTags = ["hardware:scanner", "win-x64"];

    private readonly PlatformApiFactory _factory;

    public ChannelAudienceTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 标签规范化且显式渠道键优先于标签()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        var sha = await EnsurePayloadAsync(client, token);

        var tagged = await CreateChannelAsync(client, token, "aud-tagged", "Win-x64", "hardware:scanner", "win-x64");
        Assert.NotNull(tagged.AudienceTags);
        Assert.Equal(ScannerTags, tagged.AudienceTags);
        Assert.False(tagged.AutoCreated);
        await CreateReleaseAsync(client, token, "aud-tagged", "1.0.0", sha);

        // 只给标签：请求标签包含渠道全部受众即命中。
        var byTags = await ReadDataAsync<CheckUpdateResponse>(
            await CheckRawAsync(client, token, new CheckUpdateRequest(
                null, "0.9.0", "win-x64", AudienceTags: ["win-x64", "hardware:scanner"])));
        Assert.True(byTags.HasUpdate);
        Assert.Equal("aud-tagged", byTags.ChannelKey);

        // 少一个渠道所需标签就不命中：渠道受众必须是请求标签的子集。
        var insufficient = await CheckRawAsync(
            client, token, new CheckUpdateRequest(null, "0.9.0", "win-x64", AudienceTags: ["win-x64"]));
        Assert.Equal(
            (int)ApiErrorCode.VersionsChannelNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(insufficient, HttpStatusCode.NotFound));

        // 显式渠道键即便没有标签，也优先于同时给出的标签。
        await CreateChannelAsync(client, token, "aud-explicit");
        await CreateReleaseAsync(client, token, "aud-explicit", "1.0.0", sha);
        var byKey = await ReadDataAsync<CheckUpdateResponse>(
            await CheckRawAsync(client, token, new CheckUpdateRequest(
                "aud-explicit", "0.9.0", "win-x64", AudienceTags: ["win-x64"])));
        Assert.Equal("aud-explicit", byKey.ChannelKey);
    }

    [Fact]
    public async Task 多个渠道命中时取标签最具体的()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        var sha = await EnsurePayloadAsync(client, token);

        await CreateChannelAsync(client, token, "aud-base", "win-x64", "feature:matching");
        await CreateChannelAsync(client, token, "aud-scanner", "win-x64", "feature:matching", "hardware:scanner");
        await CreateReleaseAsync(client, token, "aud-base", "1.0.0", sha);
        await CreateReleaseAsync(client, token, "aud-scanner", "1.0.0", sha);

        var narrow = await ReadDataAsync<CheckUpdateResponse>(
            await CheckRawAsync(client, token, new CheckUpdateRequest(
                null, "0.9.0", "win-x64", AudienceTags: ["win-x64", "feature:matching"])));
        Assert.Equal("aud-base", narrow.ChannelKey);

        var exact = await ReadDataAsync<CheckUpdateResponse>(
            await CheckRawAsync(client, token, new CheckUpdateRequest(
                null, "0.9.0", "win-x64", AudienceTags: ["win-x64", "feature:matching", "hardware:scanner"])));
        Assert.Equal("aud-scanner", exact.ChannelKey);
    }

    [Fact]
    public async Task 停用渠道不参与标签匹配()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);
        var sha = await EnsurePayloadAsync(client, token);

        var channel = await CreateChannelAsync(client, token, "aud-disabled", "feature:disabled");
        await CreateReleaseAsync(client, token, "aud-disabled", "1.0.0", sha);

        var disabled = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/versions/channels/{channel.Id}",
            token,
            new UpdateReleaseChannelRequest(null, null, false)));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        var response = await CheckRawAsync(
            client, token, new CheckUpdateRequest(null, "0.9.0", "win-x64", AudienceTags: ["feature:disabled"]));
        Assert.Equal(
            (int)ApiErrorCode.VersionsChannelNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(response, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task 缺渠道键与标签返回四百()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);

        var response = await CheckRawAsync(client, token, new CheckUpdateRequest(null, "0.9.0", "win-x64"));
        Assert.Equal(
            (int)ApiErrorCode.ValidationFailed,
            await IdentityTestHelpers.ReadErrorCodeAsync(response, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task 未开自动建渠道时无命中返回404()
    {
        using var client = _factory.CreateClient();
        var token = await LoginAsync(client);

        var response = await CheckRawAsync(
            client, token, new CheckUpdateRequest(null, "0.9.0", "win-x64", AudienceTags: ["feature:no-match"]));
        Assert.Equal(
            (int)ApiErrorCode.VersionsChannelNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(response, HttpStatusCode.NotFound));
    }

    private static async Task<string> LoginAsync(HttpClient client) =>
        await IdentityTestHelpers.LoginAsAdminAsync(client);

    private static async Task<ReleaseChannelDto> CreateChannelAsync(
        HttpClient client,
        string token,
        string key,
        params string[] audienceTags) =>
        await ReadDataAsync<ReleaseChannelDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/channels",
                token,
                new CreateReleaseChannelRequest(key, $"{key} 渠道", "受众标签测试", AudienceTags: audienceTags))));

    private static async Task CreateReleaseAsync(
        HttpClient client,
        string token,
        string channelKey,
        string version,
        string sha) =>
        await ReadDataAsync<ReleaseDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/releases",
                token,
                new CreateReleaseRequest(
                    channelKey,
                    version,
                    "受众标签测试",
                    true,
                    [new CreateReleaseArtifactRequest(
                        "win-x64",
                        [new ReleaseFileInput("App/App.exe", sha, Payload.Length)])]))));

    private static async Task<string> EnsurePayloadAsync(HttpClient client, string token)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(Payload));
        var ticket = await ReadDataAsync<UploadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/uploads",
                token,
                new CreateUploadTicketRequest(sha, Payload.Length))));

        using var request = new HttpRequestMessage(HttpMethod.Put, ticket.UploadUrl)
        {
            Content = new ByteArrayContent(Payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await ReadDataAsync<UploadResultDto>(await client.SendAsync(request));
        return sha;
    }

    private static Task<HttpResponseMessage> CheckRawAsync(
        HttpClient client,
        string token,
        CheckUpdateRequest request) =>
        client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/versions/check",
            token,
            request));

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response) =>
        await IdentityTestHelpers.ReadDataAsync<T>(response);
}

/// <summary>开启自动建渠道后的标签解析。</summary>
public sealed class ChannelAudienceAutoCreateTests : IClassFixture<ChannelAudienceAutoCreateTests.AutoCreateFactory>
{
    private static readonly string[] ExpectedTags = ["auto-test:unique", "win-x64"];

    private readonly AutoCreateFactory _factory;

    public ChannelAudienceAutoCreateTests(AutoCreateFactory factory)
    {
        _factory = factory;
    }

    /// <summary>只在本用例族打开自动建渠道。</summary>
    public sealed class AutoCreateFactory : PlatformApiFactory
    {
        protected override void ConfigureTestSettings(IDictionary<string, string?> settings) =>
            settings["Versions:AudienceAutoCreateChannels"] = "true";
    }

    [Fact]
    public async Task 无命中时按标签摘要建渠道并复用()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var request = new CheckUpdateRequest(
            null, "0.9.0", "win-x64", AudienceTags: ["auto-test:unique", "win-x64"]);

        // 刚建出来的渠道还没有版本：check 返回 212，但渠道应已落库。
        var first = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post, "/api/v1/client/versions/check", token, request));
        Assert.Equal(
            (int)ApiErrorCode.VersionsNoPublishedRelease,
            await IdentityTestHelpers.ReadErrorCodeAsync(first, HttpStatusCode.NotFound));

        var second = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post, "/api/v1/client/versions/check", token, request));
        Assert.Equal(
            (int)ApiErrorCode.VersionsNoPublishedRelease,
            await IdentityTestHelpers.ReadErrorCodeAsync(second, HttpStatusCode.NotFound));

        var channels = await IdentityTestHelpers.ReadDataAsync<ReleaseChannelDto[]>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/versions/channels", token)));

        var autoCreated = Assert.Single(
            channels,
            channel => channel.AudienceTags is not null && channel.AudienceTags.Contains("auto-test:unique"));
        Assert.True(autoCreated.AutoCreated);
        Assert.StartsWith(VersionConventions.AutoChannelKeyPrefix, autoCreated.Key);
        Assert.NotNull(autoCreated.AudienceTags);
        Assert.Equal(ExpectedTags, autoCreated.AudienceTags);
    }
}
