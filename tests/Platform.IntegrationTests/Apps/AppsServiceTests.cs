using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Permissions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Apps.Services;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Apps;

/// <summary>应用注册与 API Key 的业务不变式（明文只出现一次、吊销/过期即失效、scope 只减不增）。</summary>
public sealed class AppsServiceTests
{
    [Fact]
    public async Task 创建应用后可查询且重复键被拒绝()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var registry = new AppRegistryService(db);

        var created = await registry.CreateAsync(new CreateAppRequest("demo", "演示应用", "说明"));
        Assert.Equal("demo", created.Key);
        Assert.True(created.IsEnabled);

        var fetched = await registry.GetAsync(created.Id);
        Assert.Equal("演示应用", fetched.Name);
        Assert.Single(await registry.ListAsync());

        var conflict = await Assert.ThrowsAsync<BusinessException>(
            () => registry.CreateAsync(new CreateAppRequest("demo", "重复")));
        Assert.Equal(ApiErrorCode.AppsKeyExists, conflict.Code);
    }

    [Theory]
    [InlineData("Demo")]
    [InlineData("1demo")]
    [InlineData("d")]
    [InlineData("demo_1")]
    public async Task 非法应用键被拒绝(string key)
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var registry = new AppRegistryService(db);

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => registry.CreateAsync(new CreateAppRequest(key, "名称")));
        Assert.Equal(ApiErrorCode.ValidationFailed, exception.Code);
    }

    [Fact]
    public async Task 签发密钥返回一次明文且列表不含明文()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var registry = new AppRegistryService(db);
        var keys = new ApiKeyService(db, clock);
        var app = await registry.CreateAsync(new CreateAppRequest("demo", "演示应用"));

        var created = await keys.CreateAsync(app.Id, new CreateApiKeyRequest(
            "CI 密钥",
            [PermissionCatalog.VersionsReleaseRead],
            clock.UtcNow.AddDays(30)));

        Assert.StartsWith(AppConventions.ApiKeyPrefix, created.Plaintext, StringComparison.Ordinal);
        Assert.Contains($"_{created.Prefix}_", created.Plaintext, StringComparison.Ordinal);
        Assert.Equal(["versions.release.read"], created.Scopes);

        var summary = Assert.Single(await keys.ListAsync(app.Id));
        Assert.Equal(created.Prefix, summary.Prefix);
        Assert.True(summary.IsActive);
        Assert.DoesNotContain(created.Plaintext, string.Join('|', summary.Scopes), StringComparison.Ordinal);

        var authenticated = await keys.AuthenticateAsync("demo", created.Plaintext);
        Assert.Equal(app.Id, authenticated.App.Id);
        Assert.Equal(["versions.release.read"], authenticated.Scopes);
        Assert.Equal(clock.UtcNow, authenticated.Key.LastUsedAt);
    }

    [Fact]
    public async Task 错误明文校验失败()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var registry = new AppRegistryService(db);
        var keys = new ApiKeyService(db, clock);
        var app = await registry.CreateAsync(new CreateAppRequest("demo", "演示应用"));
        var created = await keys.CreateAsync(app.Id, new CreateApiKeyRequest());

        var tampered = created.Plaintext[..^1] + (created.Plaintext[^1] == 'A' ? 'B' : 'A');
        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => keys.AuthenticateAsync("demo", tampered));
        Assert.Equal(ApiErrorCode.AppsApiKeyInvalid, exception.Code);

        var wrongApp = await Assert.ThrowsAsync<BusinessException>(
            () => keys.AuthenticateAsync("other", created.Plaintext));
        Assert.Equal(ApiErrorCode.AppsApiKeyInvalid, wrongApp.Code);
    }

    [Fact]
    public async Task 吊销后校验失败()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var registry = new AppRegistryService(db);
        var keys = new ApiKeyService(db, clock);
        var app = await registry.CreateAsync(new CreateAppRequest("demo", "演示应用"));
        var created = await keys.CreateAsync(app.Id, new CreateApiKeyRequest());

        var revoked = await keys.RevokeAsync(app.Id, created.Id);
        Assert.False(revoked.IsActive);

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => keys.AuthenticateAsync("demo", created.Plaintext));
        Assert.Equal(ApiErrorCode.AppsApiKeyRevoked, exception.Code);
    }

    [Fact]
    public async Task 过期密钥校验失败()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var registry = new AppRegistryService(db);
        var keys = new ApiKeyService(db, clock);
        var app = await registry.CreateAsync(new CreateAppRequest("demo", "演示应用"));
        var created = await keys.CreateAsync(app.Id, new CreateApiKeyRequest(
            ExpiresAt: clock.UtcNow.AddHours(1)));

        clock.Advance(TimeSpan.FromHours(2));

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => keys.AuthenticateAsync("demo", created.Plaintext));
        Assert.Equal(ApiErrorCode.AppsApiKeyExpired, exception.Code);
    }

    [Fact]
    public async Task 禁用应用后校验失败()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var registry = new AppRegistryService(db);
        var keys = new ApiKeyService(db, clock);
        var app = await registry.CreateAsync(new CreateAppRequest("demo", "演示应用"));
        var created = await keys.CreateAsync(app.Id, new CreateApiKeyRequest());

        await registry.UpdateAsync(app.Id, new UpdateAppRequest(IsEnabled: false));

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => keys.AuthenticateAsync("demo", created.Plaintext));
        Assert.Equal(ApiErrorCode.AppsDisabled, exception.Code);
    }

    [Fact]
    public async Task 未知或通配符scope被拒绝()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var registry = new AppRegistryService(db);
        var keys = new ApiKeyService(db, clock);
        var app = await registry.CreateAsync(new CreateAppRequest("demo", "演示应用"));

        var unknown = await Assert.ThrowsAsync<BusinessException>(
            () => keys.CreateAsync(app.Id, new CreateApiKeyRequest(Scopes: ["no.such.code"])));
        Assert.Equal(ApiErrorCode.AppsApiKeyScopeUnknown, unknown.Code);

        var wildcard = await Assert.ThrowsAsync<BusinessException>(
            () => keys.CreateAsync(app.Id, new CreateApiKeyRequest(Scopes: ["*"])));
        Assert.Equal(ApiErrorCode.AppsApiKeyScopeDenied, wildcard.Code);
    }

    [Fact]
    public async Task M2M令牌写入服务主体与授权scope()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var registry = new AppRegistryService(db);
        var keys = new ApiKeyService(db, clock);
        var app = await registry.CreateAsync(new CreateAppRequest("demo", "演示应用"));
        var created = await keys.CreateAsync(app.Id, new CreateApiKeyRequest(
            Scopes: ["versions.release.read", "versions.artifact.download"]));
        var issuer = new RecordingIssuer();
        var tokens = new M2MTokenService(keys, issuer);

        var response = await tokens.IssueAsync("demo", created.Plaintext, ["versions.release.read"]);

        Assert.Equal("demo", response.AppKey);
        Assert.Equal(["versions.release.read"], response.Scopes);
        Assert.NotNull(issuer.LastSpec);
        Assert.Equal(PlatformPrincipalKinds.Service, issuer.LastSpec.PrincipalKind);
        Assert.Equal("demo", issuer.LastSpec.AppKey);
        Assert.Equal(["demo"], issuer.LastSpec.AppKeys);
        Assert.Equal(["versions.release.read"], issuer.LastSpec.Permissions);
        Assert.StartsWith("apikey:", issuer.LastSpec.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M2M不得请求超出密钥的scope()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var registry = new AppRegistryService(db);
        var keys = new ApiKeyService(db, clock);
        var app = await registry.CreateAsync(new CreateAppRequest("demo", "演示应用"));
        var created = await keys.CreateAsync(app.Id, new CreateApiKeyRequest(Scopes: ["versions.release.read"]));
        var tokens = new M2MTokenService(keys, new RecordingIssuer());

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => tokens.IssueAsync("demo", created.Plaintext, ["license.issue"]));

        Assert.Equal(ApiErrorCode.AppsApiKeyScopeDenied, exception.Code);
    }

    private sealed class RecordingIssuer : IAccessTokenIssuer
    {
        public AccessTokenSpec? LastSpec { get; private set; }

        public IssuedToken Issue(AccessTokenSpec spec)
        {
            LastSpec = spec;
            return new IssuedToken("test-token", new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero));
        }
    }
}
