using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Licensing;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.IntegrationTests.Support;
using Platform.Modules.Licensing.Domain;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 授权全链路：产品 → 签发/续期/吊销 → 客户端签名快照验签 → 离线码机器绑定 →
/// 密钥轮换（旧公钥继续可用）→ 未配置私钥时 Unmanaged → 跨应用隔离。
/// </summary>
public sealed class LicensingEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private static readonly string MachineA = MachineFromHex("00112233445566778899");
    private static readonly string MachineB = MachineFromHex("AABBCCDDEEFF00112233");

    private readonly PlatformApiFactory _factory;

    public LicensingEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 签发续期吊销后客户端快照可验签且吊销立即生效()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var product = await CreateProductAsync(client, token, "erp-pro", seatLimit: null, trialDays: 0);
        Assert.Equal("erp-pro", product.Key);

        var issued = await IssueAsync(client, token, "erp-pro", MachineA, addDays: 30, licenseNo: "LIC-IT-001");
        Assert.Equal(LicenseConventions.KindOfficial, issued.Kind);
        Assert.Equal(LicenseConventions.StatusActive, issued.Status);
        Assert.Equal("LIC-IT-001", issued.LicenseNo);

        var status = await StatusAsync(client, token, "erp-pro", MachineA);
        Assert.Equal(LicenseConventions.StatusActive, status.Status);
        Assert.Equal(TestLicenseKeys.KeyId, status.KeyId);
        Assert.Contains(status.TrustedKeys, x => x.KeyId == TestLicenseKeys.KeyId);
        Assert.NotNull(status.Snapshot);

        var trusted = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TestLicenseKeys.KeyId] = TestLicenseKeys.PublicKeySpkiBase64,
        };
        Assert.True(
            LicenseSnapshotCodec.TryVerify(status.Snapshot, trusted, out LicenseSnapshotPayload snapshot, out string? error),
            error);
        Assert.Equal("demo", snapshot.AppKey);
        Assert.Equal("erp-pro", snapshot.ProductKey);
        Assert.Equal(MachineA, snapshot.MachineCode);
        Assert.Equal(LicenseConventions.StatusActive, snapshot.Status);

        // 续期后快照的到期时间同步推进
        var renewed = await ReadDataAsync<LicenseEntitlementDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/licensing/entitlements/{issued.Id}/renew",
                token,
                new RenewLicenseRequest(null, 30, null, "续期一次"))));
        Assert.True(renewed.NotAfter > issued.NotAfter);

        var renewedStatus = await StatusAsync(client, token, "erp-pro", MachineA);
        Assert.Equal(renewed.NotAfter, renewedStatus.NotAfter);

        // 吊销后状态与快照都变成 revoked
        var revoked = await ReadDataAsync<LicenseEntitlementDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/licensing/entitlements/{issued.Id}/revoke",
                token,
                new RevokeLicenseRequest("客户退订"))));
        Assert.Equal(LicenseConventions.StatusRevoked, revoked.Status);
        Assert.Equal("客户退订", revoked.RevokedReason);

        var revokedStatus = await StatusAsync(client, token, "erp-pro", MachineA);
        Assert.Equal(LicenseConventions.StatusRevoked, revokedStatus.Status);
        Assert.True(
            LicenseSnapshotCodec.TryVerify(revokedStatus.Snapshot, trusted, out LicenseSnapshotPayload revokedSnapshot, out _));
        Assert.Equal(LicenseConventions.StatusRevoked, revokedSnapshot.Status);

        // 吊销后可重新签发（原行复活，授权编号可更新）
        var reissued = await IssueAsync(client, token, "erp-pro", MachineA, addDays: 10, licenseNo: "LIC-IT-002");
        Assert.Equal(issued.Id, reissued.Id);
        Assert.Equal(LicenseConventions.StatusActive, reissued.Status);
        Assert.Equal("LIC-IT-002", reissued.LicenseNo);

        // 续期一个已吊销的授权会被拒绝（先吊销新签发的授权）
        await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/licensing/entitlements/{reissued.Id}/revoke",
            token,
            new RevokeLicenseRequest("再次吊销")));
        var renewRevoked = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/licensing/entitlements/{reissued.Id}/renew",
            token,
            new RenewLicenseRequest(null, 30, null, null)));
        Assert.Equal(
            (int)ApiErrorCode.LicensingEntitlementRevoked,
            await IdentityTestHelpers.ReadErrorCodeAsync(renewRevoked, HttpStatusCode.Forbidden));
    }

    [Fact]
    public async Task 试用自动签发且席位用尽拒绝新授权()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateProductAsync(client, token, "trial-seat", seatLimit: 1, trialDays: 7);

        var first = await StatusAsync(client, token, "trial-seat", MachineA);
        Assert.Equal(LicenseConventions.StatusActive, first.Status);
        Assert.Equal(LicenseConventions.KindTrial, first.Kind);
        Assert.NotNull(first.LicenseNo);

        var second = await StatusAsync(client, token, "trial-seat", MachineA);
        Assert.Equal(first.LicenseNo, second.LicenseNo);

        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/licensing/entitlements",
            token,
            new IssueLicenseRequest("trial-seat", MachineB, null, null, 30, null, null)));
        Assert.Equal(
            (int)ApiErrorCode.LicensingSeatExhausted,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.Conflict));

        var list = await ReadDataAsync<PageResult<LicenseEntitlementDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/licensing/entitlements?productKey=trial-seat&status=active",
                token)));
        Assert.Single(list.Items);
    }

    [Fact]
    public async Task 离线码绑定机器与产品且跨机器误用被拒()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateProductAsync(client, token, "offline-pro", seatLimit: null, trialDays: 0);

        var ledger = await ReadDataAsync<OfflineLicenseCodeDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/licensing/offline-codes",
                token,
                new IssueOfflineLicenseCodeRequest("offline-pro", MachineA, null, 90, "OFF-001", "现场无网"))));
        Assert.StartsWith(OfflineLicenseCodec.Prefix, ledger.Code);
        Assert.Contains("-", ledger.FormattedCode);
        Assert.Equal(0, ledger.GraceDays);

        // 本机 + 正确产品：通过
        var verified = await VerifyOfflineAsync(client, token, ledger.Code, MachineA, "offline-pro");
        Assert.True(verified.IsValid);
        Assert.Equal(MachineA, verified.MachineCode);
        Assert.Equal("OFF-001", verified.LicenseNo);

        // 换一台机器：307
        var wrongMachine = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/licensing/offline/verify",
            token,
            new VerifyOfflineLicenseCodeRequest(ledger.Code, MachineB, "offline-pro")));
        Assert.Equal(
            (int)ApiErrorCode.LicensingOfflineCodeMachineMismatch,
            await IdentityTestHelpers.ReadErrorCodeAsync(wrongMachine, HttpStatusCode.UnprocessableEntity));

        // 产品不匹配：306
        var wrongProduct = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/licensing/offline/verify",
            token,
            new VerifyOfflineLicenseCodeRequest(ledger.Code, MachineA, "another-pro")));
        Assert.Equal(
            (int)ApiErrorCode.LicensingOfflineCodeInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(wrongProduct, HttpStatusCode.UnprocessableEntity));

        // 篡改一个字符：306
        int index = OfflineLicenseCodec.Prefix.Length + 8;
        char replacement = ledger.Code[index] == '0' ? '1' : '0';
        string tampered = ledger.Code[..index] + replacement + ledger.Code[(index + 1)..];
        var badCode = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/licensing/offline/verify",
            token,
            new VerifyOfflineLicenseCodeRequest(tampered, MachineA, "offline-pro")));
        Assert.Equal(
            (int)ApiErrorCode.LicensingOfflineCodeInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(badCode, HttpStatusCode.UnprocessableEntity));

        // 台账删除后码本身仍可用（离线码无法吊销）
        var deleted = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete,
            $"/api/v1/admin/licensing/offline-codes/{ledger.Id}",
            token));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.True((await VerifyOfflineAsync(client, token, ledger.Code, MachineA, "offline-pro")).IsValid);
    }

    [Fact]
    public async Task 密钥轮换后旧公钥仍参与验签且当前密钥不可退役()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var status = await ReadDataAsync<LicenseSigningKeyStatusDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/licensing/signing-keys",
                token)));
        Assert.True(status.IsManaged);
        Assert.Equal(TestLicenseKeys.KeyId, status.CurrentKeyId);
        Assert.Contains(status.Keys, x => x.KeyId == TestLicenseKeys.KeyId && x.IsCurrent);

        // 模拟上一代密钥：登记为 retired，旧公钥仍应被下发并用于验签
        using ECDsa oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string oldPublic = LicenseCryptography.ExportPublicKeySpkiBase64(oldKey);
        string oldKeyId = LicenseCryptography.ComputeKeyId(oldPublic);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Set<LicenseSigningKey>().Add(new LicenseSigningKey
            {
                KeyId = oldKeyId,
                Algorithm = LicenseConventions.SignatureAlgorithm,
                PublicKey = oldPublic,
                Fingerprint = LicenseCryptography.ComputeFingerprint(oldPublic),
                Status = LicenseConventions.SigningKeyRetired,
                ActivatedAt = DateTimeOffset.UtcNow.AddDays(-30),
                RetiredAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var publicKeys = await ReadDataAsync<List<LicenseKeyDescriptorDto>>(
            await client.GetAsync("/api/v1/client/licensing/keys"));
        Assert.Contains(publicKeys, x => x.KeyId == TestLicenseKeys.KeyId);
        Assert.Contains(publicKeys, x => x.KeyId == oldKeyId && x.Status == LicenseConventions.SigningKeyRetired);

        // 用旧私钥签发的离线码仍能通过校验（轮换不停机：旧码继续有效）
        await CreateProductAsync(client, token, "keys-pro", seatLimit: null, trialDays: 0);
        var now = DateTimeOffset.UtcNow;
        string oldCode = OfflineLicenseCodec.Encode(
            "keys-pro",
            MachineA,
            LicenseConventions.KindOfficial,
            now,
            now.AddDays(30),
            0,
            Convert.ToBase64String(oldKey.ExportPkcs8PrivateKey()),
            "OLD-001");
        Assert.True((await VerifyOfflineAsync(client, token, oldCode, MachineA, "keys-pro")).IsValid);

        // 当前密钥不可退役；拿非配置密钥去激活也不允许
        var current = status.Keys.Single(x => x.IsCurrent);
        var retireCurrent = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/licensing/signing-keys/{current.Id}/retire",
            token));
        Assert.Equal(
            (int)ApiErrorCode.LicensingSigningKeyUnavailable,
            await IdentityTestHelpers.ReadErrorCodeAsync(retireCurrent, HttpStatusCode.ServiceUnavailable));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var old = await db.Set<LicenseSigningKey>().SingleAsync(x => x.KeyId == oldKeyId);
            var activateOld = await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/licensing/signing-keys/{old.Id}/activate",
                token));
            Assert.Equal(
                (int)ApiErrorCode.LicensingSigningKeyUnavailable,
                await IdentityTestHelpers.ReadErrorCodeAsync(activateOld, HttpStatusCode.ServiceUnavailable));
        }

        // 旧密钥可以退役（幂等），退役后仍保留在受信任集合里
        var oldEntity = await ReadDataAsync<LicenseSigningKeyStatusDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/licensing/signing-keys",
                token)));
        var oldDto = oldEntity.Keys.Single(x => x.KeyId == oldKeyId);
        var retired = await ReadDataAsync<LicenseSigningKeyStatusDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/licensing/signing-keys/{oldDto.Id}/retire",
                token)));
        Assert.Equal(LicenseConventions.SigningKeyRetired, retired.Keys.Single(x => x.KeyId == oldKeyId).Status);
        Assert.Equal(TestLicenseKeys.KeyId, retired.CurrentKeyId);
    }

    [Fact]
    public async Task 未配置签名私钥时返回unmanaged且拒绝签发离线码()
    {
        using var unmanaged = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Licensing:SigningKeyPkcs8"] = string.Empty,
                }));
        });
        using var client = unmanaged.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateProductAsync(client, token, "unmanaged-pro", seatLimit: null, trialDays: 0);

        var status = await StatusAsync(client, token, "unmanaged-pro", MachineA);
        Assert.Equal(LicenseConventions.StatusUnmanaged, status.Status);
        Assert.Null(status.Snapshot);
        Assert.Empty(status.TrustedKeys);

        var issue = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/licensing/offline-codes",
            token,
            new IssueOfflineLicenseCodeRequest("unmanaged-pro", MachineA, null, 30, null, null)));
        Assert.Equal(
            (int)ApiErrorCode.LicensingSigningKeyUnavailable,
            await IdentityTestHelpers.ReadErrorCodeAsync(issue, HttpStatusCode.ServiceUnavailable));
    }

    [Fact]
    public async Task 无权限匿名与跨应用隔离()
    {
        using var client = _factory.CreateClient();
        var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(client);
        await CreateProductAsync(client, adminToken, "isolated-pro", seatLimit: null, trialDays: 0);

        // 匿名：公钥发现开放，状态校验需要认证
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/client/licensing/keys")).StatusCode);
        var anonymousStatus = await client.PostAsJsonAsync(
            "/api/v1/client/licensing/status",
            new LicenseStatusRequest("isolated-pro", MachineA));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousStatus.StatusCode);

        // 无授权权限的用户：读接口 403
        const string viewerPassword = "Viewer@12345";
        await IdentityTestHelpers.CreateUserAsync(
            client, adminToken, "license-viewer", viewerPassword, "授权只读");
        var viewer = await IdentityTestHelpers.LoginAsync(client, "license-viewer", viewerPassword);
        var forbidden = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            "/api/v1/admin/licensing/products",
            viewer.AccessToken));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // 另一个应用通过 M2M 令牌访问：看不到 demo 的产品
        const string appKey = "licensing-iso";
        var app = await ReadDataAsync<AppDetailDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/apps",
                adminToken,
                new CreateAppRequest(appKey, "授权隔离测试"))));
        var key = await ReadDataAsync<CreatedApiKeyDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/apps/{app.Id}/keys",
                adminToken,
                new CreateApiKeyRequest("license", [PermissionCatalog.LicenseRead]))));

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/apps/token");
        tokenRequest.Headers.TryAddWithoutValidation(PlatformHeaders.AppKey, appKey);
        tokenRequest.Headers.TryAddWithoutValidation(PlatformHeaders.ApiKey, key.Plaintext);
        var m2m = await ReadDataAsync<M2MTokenResponse>(await client.SendAsync(tokenRequest));

        var crossApp = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/licensing/status",
            m2m.AccessToken,
            new LicenseStatusRequest("isolated-pro", MachineA)));
        Assert.Equal(
            (int)ApiErrorCode.LicensingProductNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(crossApp, HttpStatusCode.NotFound));
    }

    private static async Task<LicenseProductDto> CreateProductAsync(
        HttpClient client,
        string token,
        string key,
        int? seatLimit,
        int trialDays) =>
        await ReadDataAsync<LicenseProductDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/licensing/products",
                token,
                new CreateLicenseProductRequest(key, $"{key} 产品", "集成测试", seatLimit, 7, trialDays))));

    private static async Task<LicenseEntitlementDto> IssueAsync(
        HttpClient client,
        string token,
        string productKey,
        string machineCode,
        int addDays,
        string? licenseNo) =>
        await ReadDataAsync<LicenseEntitlementDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/licensing/entitlements",
                token,
                new IssueLicenseRequest(productKey, machineCode, licenseNo, null, addDays, null, "集成测试"))));

    private static async Task<LicenseStatusResponse> StatusAsync(
        HttpClient client,
        string token,
        string productKey,
        string machineCode) =>
        await ReadDataAsync<LicenseStatusResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/client/licensing/status",
                token,
                new LicenseStatusRequest(productKey, machineCode))));

    private static async Task<OfflineLicenseVerificationDto> VerifyOfflineAsync(
        HttpClient client,
        string token,
        string code,
        string machineCode,
        string productKey) =>
        await ReadDataAsync<OfflineLicenseVerificationDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/client/licensing/offline/verify",
                token,
                new VerifyOfflineLicenseCodeRequest(code, machineCode, productKey))));

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response) =>
        await IdentityTestHelpers.ReadDataAsync<T>(response);

    private static string MachineFromHex(string hex) =>
        Base32Crockford.Encode(Convert.FromHexString(hex));
}
