using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Endpoints;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Devices;
using Platform.Contracts.Licensing;
using Platform.Contracts.Permissions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Paging;
using Platform.IntegrationTests.Support;
using Platform.Modules.Devices.Domain;
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
    public async Task 仅注销在线授权不影响离线码且可升级为完全吊销()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateProductAsync(client, token, "online-only-pro", seatLimit: null, trialDays: 0);
        var issued = await IssueAsync(client, token, "online-only-pro", MachineA, addDays: 30, licenseNo: "ONLINE-001");

        var offline = await ReadDataAsync<OfflineLicenseCodeDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/licensing/offline-codes",
                token,
                new IssueOfflineLicenseCodeRequest("online-only-pro", MachineA, null, 90, "OFF-ONLINE", "现场离线码"))));

        var revoked = await ReadDataAsync<LicenseEntitlementDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/licensing/entitlements/{issued.Id}/revoke",
                token,
                new RevokeLicenseRequest("只停在线", LicenseConventions.RevokeScopeOnline))));
        Assert.Equal(LicenseConventions.StatusRevoked, revoked.Status);
        Assert.Equal(LicenseConventions.RevokeScopeOnline, revoked.RevokeScope);

        // 在线状态按未授权下发：老客户端清掉在线快照，离线码接管
        var status = await StatusAsync(client, token, "online-only-pro", MachineA);
        Assert.Equal(LicenseConventions.StatusUnlicensed, status.Status);
        Assert.Null(status.Snapshot);
        Assert.Null(status.Kind);

        // 离线码本身不受影响
        var verified = await VerifyOfflineAsync(client, token, offline.Code, MachineA, "online-only-pro");
        Assert.True(verified.IsValid);

        // 再次提交 online 不降级/不重复推送；提交缺省 scope 升级为完全吊销
        var again = await ReadDataAsync<LicenseEntitlementDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/licensing/entitlements/{issued.Id}/revoke",
                token,
                new RevokeLicenseRequest(null, LicenseConventions.RevokeScopeOnline))));
        Assert.Equal(LicenseConventions.RevokeScopeOnline, again.RevokeScope);

        var escalated = await ReadDataAsync<LicenseEntitlementDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                $"/api/v1/admin/licensing/entitlements/{issued.Id}/revoke",
                token,
                new RevokeLicenseRequest("门店退租"))));
        Assert.Equal(LicenseConventions.RevokeScopeAll, escalated.RevokeScope);
        Assert.Equal("门店退租", escalated.RevokedReason);

        var locked = await StatusAsync(client, token, "online-only-pro", MachineA);
        Assert.Equal(LicenseConventions.StatusRevoked, locked.Status);
        Assert.NotNull(locked.Snapshot);
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

        // 同机器同产品的正式授权就地升级试用：沿用原行与席位，不需要先吊销
        var upgraded = await IssueAsync(client, token, "trial-seat", MachineA, addDays: 30, licenseNo: null);
        Assert.Equal(list.Items[0].Id, upgraded.Id);
        Assert.Equal(LicenseConventions.KindOfficial, upgraded.Kind);
        Assert.Equal(LicenseConventions.StatusActive, upgraded.Status);

        var afterUpgrade = await ReadDataAsync<PageResult<LicenseEntitlementDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/licensing/entitlements?productKey=trial-seat&status=active",
                token)));
        Assert.Single(afterUpgrade.Items);
    }

    [Fact]
    public async Task 离线码绑定机器与产品且跨机器误用被拒()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var product = await CreateProductAsync(client, token, "offline-pro", seatLimit: null, trialDays: 0);

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

        // 未过期（含宽限）的台账行不可删除，避免失去留痕
        var tooEarly = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete,
            $"/api/v1/admin/licensing/offline-codes/{ledger.Id}",
            token));
        Assert.Equal(
            (int)ApiErrorCode.Conflict,
            await IdentityTestHelpers.ReadErrorCodeAsync(tooEarly, HttpStatusCode.Conflict));

        // 过期后的台账行可删；码本身自包含，删除台账不影响码继续可用（离线码无法吊销）
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Set<OfflineLicenseCode>().Add(new OfflineLicenseCode
            {
                AppKey = "demo",
                ProductId = product.Id,
                MachineCode = MachineA,
                LicenseNo = "OFF-EXPIRED",
                Code = ledger.Code,
                Kind = LicenseConventions.KindOfficial,
                IssuedAt = DateTimeOffset.UtcNow.AddDays(-120),
                NotAfter = DateTimeOffset.UtcNow.AddDays(-30),
                GraceDays = 0,
            });
            await db.SaveChangesAsync();
        }

        var expiredRow = await ReadDataAsync<PageResult<OfflineLicenseCodeDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/licensing/offline-codes?keyword=OFF-EXPIRED",
                token)));
        var expiredLedger = expiredRow.Items.Single(x => x.LicenseNo == "OFF-EXPIRED");
        var deleted = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete,
            $"/api/v1/admin/licensing/offline-codes/{expiredLedger.Id}",
            token));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.True((await VerifyOfflineAsync(client, token, ledger.Code, MachineA, "offline-pro")).IsValid);
    }

    [Fact]
    public async Task 复用已吊销的授权行仍占用席位()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateProductAsync(client, token, "revoked-seat", seatLimit: 1, trialDays: 0);

        var first = await IssueAsync(client, token, "revoked-seat", MachineA, addDays: 30, licenseNo: null);
        await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/licensing/entitlements/{first.Id}/revoke",
            token,
            new RevokeLicenseRequest("退订")));

        // 吊销后席位释放，可以换一台机器签发
        var second = await IssueAsync(client, token, "revoked-seat", MachineB, addDays: 30, licenseNo: null);
        Assert.NotEqual(first.Id, second.Id);

        // B 已占满唯一席位：复活 A 的已吊销行同样是"新增 active 行"，必须按席位用尽拒绝
        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/licensing/entitlements",
            token,
            new IssueLicenseRequest("revoked-seat", MachineA, null, null, 30, null, null)));
        Assert.Equal(
            (int)ApiErrorCode.LicensingSeatExhausted,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task 产品停用后离线码校验被拒绝()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var product = await CreateProductAsync(client, token, "offline-disabled", seatLimit: null, trialDays: 0);

        var ledger = await ReadDataAsync<OfflineLicenseCodeDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/licensing/offline-codes",
                token,
                new IssueOfflineLicenseCodeRequest("offline-disabled", MachineA, null, 90, "OFF-DISABLED", null))));
        Assert.True((await VerifyOfflineAsync(client, token, ledger.Code, MachineA, "offline-disabled")).IsValid);

        var patched = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Patch,
            $"/api/v1/admin/licensing/products/{product.Id}",
            token,
            new UpdateLicenseProductRequest(null, null, false, null, null, null)));
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        var disabled = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/licensing/offline/verify",
            token,
            new VerifyOfflineLicenseCodeRequest(ledger.Code, MachineA, "offline-disabled")));
        Assert.Equal(
            (int)ApiErrorCode.ValidationFailed,
            await IdentityTestHelpers.ReadErrorCodeAsync(disabled, HttpStatusCode.BadRequest));
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
            "demo",
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

    [Fact]
    public async Task 授权可按节点签发并回填节点信息且校验机器一致()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        await CreateProductAsync(client, token, "node-pro", seatLimit: null, trialDays: 0);

        var node = new DeviceNode
        {
            MachineCode = MachineA,
            AutoName = "line-pc-01",
            DisplayName = "产线一号",
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Set<DeviceNode>().Add(node);
            await db.SaveChangesAsync();
        }

        var issued = await ReadDataAsync<LicenseEntitlementDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/licensing/entitlements",
                token,
                new IssueLicenseRequest("node-pro", null, "NODE-001", null, 30, null, null, node.Id))));
        Assert.Equal(MachineA, issued.MachineCode);
        Assert.Equal(node.Id, issued.NodeId);
        Assert.Equal("产线一号", issued.NodeDisplayName);

        var listed = await ReadDataAsync<PageResult<LicenseEntitlementDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get,
                "/api/v1/admin/licensing/entitlements?productKey=node-pro",
                token)));
        var row = Assert.Single(listed.Items);
        Assert.Equal(node.Id, row.NodeId);
        Assert.Equal("产线一号", row.NodeDisplayName);

        var status = await StatusAsync(client, token, "node-pro", MachineA);
        Assert.Equal(node.Id, status.NodeId);

        // 节点与机器码指向不同机器：拒绝
        var mismatch = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/licensing/entitlements",
            token,
            new IssueLicenseRequest("node-pro", MachineB, null, null, 30, null, null, node.Id)));
        Assert.Equal(
            (int)ApiErrorCode.ValidationFailed,
            await IdentityTestHelpers.ReadErrorCodeAsync(mismatch, HttpStatusCode.BadRequest));

        // 未知节点：809
        var unknownNode = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/licensing/entitlements",
            token,
            new IssueLicenseRequest("node-pro", null, null, null, 30, null, null, Guid.CreateVersion7())));
        Assert.Equal(
            (int)ApiErrorCode.DevicesNodeNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(unknownNode, HttpStatusCode.NotFound));

        // 两个都不给：拒绝（不再允许无节点的匿名授权）
        var missing = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/licensing/entitlements",
            token,
            new IssueLicenseRequest("node-pro", null, null, null, 30, null, null)));
        Assert.Equal(
            (int)ApiErrorCode.LicensingMachineCodeMissing,
            await IdentityTestHelpers.ReadErrorCodeAsync(missing, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task 离线码跨应用校验失败()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        await CreateProductAsync(client, token, "cross-pro", seatLimit: null, trialDays: 0);

        var issued = await ReadDataAsync<OfflineLicenseCodeDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/licensing/offline-codes",
                token,
                new IssueOfflineLicenseCodeRequest("cross-pro", MachineA, null, 30, null, null))));
        var own = await VerifyOfflineAsync(client, token, issued.Code, MachineA, "cross-pro");
        Assert.True(own.IsValid);
        Assert.Equal("demo", own.AppKey);

        var other = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/client/licensing/offline/verify",
            token,
            new VerifyOfflineLicenseCodeRequest(issued.Code, MachineA, "cross-pro", "other-app")));
        Assert.Equal(
            (int)ApiErrorCode.LicensingOfflineCodeInvalid,
            await IdentityTestHelpers.ReadErrorCodeAsync(other, HttpStatusCode.UnprocessableEntity));
    }

    [Fact]
    public async Task 签发与吊销经设备Hub定向推送到该机器安装实例()
    {
        using var client = _factory.CreateClient();
        var admin = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var deviceToken = await DeviceTokenAsync(client);

        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, DeviceHubPaths.Root),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(deviceToken);
                })
            .Build();
        await using var _ = connection;
        await connection.StartAsync();

        var installGuid = Guid.NewGuid();
        var registered = await connection.InvokeAsync<DeviceRegistrationResponse>(
            DeviceHubMethods.RegisterInstallation,
            new DeviceRegistrationRequest(
                new DeviceIdentity(
                    InstallGuid: installGuid,
                    SmbiosUuid: null,
                    SystemDiskSerial: $"lic-push-{installGuid:N}",
                    FrameworkVersion: "1.0.0"),
                "LIC-PUSH",
                IpAddress: "10.9.8.9"));

        var issuedNotice = new TaskCompletionSource<LicenseChangedNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var revokedNotice = new TaskCompletionSource<LicenseChangedNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<LicenseChangedNotification>(DeviceHubEvents.LicenseChanged, notice =>
        {
            if (notice.Reason == DeviceNotificationConventions.LicenseReasonIssued)
            {
                issuedNotice.TrySetResult(notice);
            }
            else if (notice.Reason == DeviceNotificationConventions.LicenseReasonRevoked)
            {
                revokedNotice.TrySetResult(notice);
            }
        });

        await CreateProductAsync(client, admin, "lic-push-pro", seatLimit: null, trialDays: 0);
        var issued = await IssueAsync(
            client, admin, "lic-push-pro", registered.MachineCode, addDays: 30, licenseNo: "LIC-PUSH-001");

        var issuedPayload = await issuedNotice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("demo", issuedPayload.AppKey);
        Assert.Equal(registered.MachineCode, issuedPayload.MachineCode);

        await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            $"/api/v1/admin/licensing/entitlements/{issued.Id}/revoke",
            admin,
            new RevokeLicenseRequest("集成测试吊销")));

        var revokedPayload = await revokedNotice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("demo", revokedPayload.AppKey);
        Assert.Equal(registered.MachineCode, revokedPayload.MachineCode);
    }

    [Fact]
    public async Task 产品删除按授权与离线码留痕受控()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var busy = await CreateProductAsync(client, token, "busy-pro", seatLimit: null, trialDays: 0);
        await IssueAsync(client, token, "busy-pro", MachineA, addDays: 30, licenseNo: null);

        var rejected = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/licensing/products/{busy.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.Conflict,
            await IdentityTestHelpers.ReadErrorCodeAsync(rejected, HttpStatusCode.Conflict));

        var empty = await CreateProductAsync(client, token, "deletable-pro", seatLimit: null, trialDays: 0);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/licensing/products/{empty.Id}", token))).StatusCode);

        var listed = await ReadDataAsync<List<LicenseProductDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/licensing/products", token)));
        Assert.DoesNotContain(listed, x => x.Id == empty.Id);

        var gone = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/licensing/products/{empty.Id}", token));
        Assert.Equal(
            (int)ApiErrorCode.LicensingProductNotFound,
            await IdentityTestHelpers.ReadErrorCodeAsync(gone, HttpStatusCode.NotFound));
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

    private static async Task<string> DeviceTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/dev/token",
            new DevTokenRequest(
                Subject: $"lic-push-{Guid.NewGuid():N}",
                Name: "demo",
                Permissions: [],
                Apps: ["demo"],
                AppKey: "demo"));
        return (await IdentityTestHelpers.ReadDataAsync<DevTokenResponse>(response)).AccessToken;
    }
}
