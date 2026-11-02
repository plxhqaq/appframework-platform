using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AppFramework.Platform.Client;
using Platform.Contracts;
using Platform.Contracts.Licensing;
using Platform.Contracts.Permissions;

// 桌面客户端接入平台的最小链路。凭据只从环境变量读取：
//   PLATFORM_URL / PLATFORM_USERNAME / PLATFORM_PASSWORD / PLATFORM_APP_KEY
//   PLATFORM_CHANNEL / PLATFORM_VERSION / PLATFORM_PRODUCT_KEY / PLATFORM_MACHINE_CODE
// 除口令外都有默认值：干净服务端上版本与授权两步会打印「跳过 + 建数据命令」，其余步骤仍须全绿。
var baseUrl = Environment.GetEnvironmentVariable("PLATFORM_URL") ?? "http://localhost:5080";
var userName = Environment.GetEnvironmentVariable("PLATFORM_USERNAME") ?? "admin";
var password = Environment.GetEnvironmentVariable("PLATFORM_PASSWORD")
    ?? throw new InvalidOperationException(
        "缺少 PLATFORM_PASSWORD。开发环境请用种子口令：PLATFORM_PASSWORD=Admin@12345 dotnet run --project samples/ConsoleQuickstart");
var appKey = Environment.GetEnvironmentVariable("PLATFORM_APP_KEY") ?? "demo";
var channelKey = Environment.GetEnvironmentVariable("PLATFORM_CHANNEL") ?? "stable";
var currentVersion = Environment.GetEnvironmentVariable("PLATFORM_VERSION");
var productKey = Environment.GetEnvironmentVariable("PLATFORM_PRODUCT_KEY") ?? "erp-pro";

// 真实客户端的机器码来自本机硬件哈希（C 阶段客户端仓库负责）；这里用机器名派生一个稳定演示值。
var machineCode = Environment.GetEnvironmentVariable("PLATFORM_MACHINE_CODE")
    ?? DemoMachineCodeOf(Environment.MachineName);

using var client = new PlatformClient(new PlatformClientOptions
{
    BaseUrl = baseUrl,
    ClientVersion = currentVersion ?? "0.0.0",
    RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
});

Console.WriteLine($"平台地址：{baseUrl}    登录账号：{userName}    应用：{appKey}");

Step(1, "存活 / 就绪");
if (!await client.IsHealthyAsync())
{
    Console.Error.WriteLine("× /healthz 没有响应，先确认服务端已启动（dotnet run --project src/Platform.Api）。");
    return 1;
}

Console.WriteLine($"  就绪探针：{Shorten(await client.GetReadinessAsync(), 160)}");

Step(2, "模块清单");
foreach (var module in await client.GetModulesAsync())
{
    Console.WriteLine($"  {module.Key,-10} v{module.Version} 路由 {string.Join(", ", module.Routes)}");
}

try
{
    Step(3, "用户名口令登录（绑定应用上下文）");
    var session = await client.SignInAsync(userName, password, appKey);
    Console.WriteLine($"  主体：{session.User.UserName} 角色 {string.Join(",", session.User.Roles)} 权限 {session.User.Permissions.Count} 项 应用 {session.User.AppKey}");
    if (session.User.MustChangePassword)
    {
        Console.WriteLine("  注意：该账号需要改密（ChangePasswordAsync 会吊销全部会话，随后需重新登录）。");
    }

    Step(4, "令牌自述（声明是否落在预期位置）");
    var principal = await client.GetWhoAmIAsync();
    Console.WriteLine($"  userId={principal.UserId} name={principal.UserName} app={principal.AppKey} service={principal.IsService} temporary={principal.IsTemporary}");
    if (!string.Equals(principal.AppKey, appKey, StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"× 令牌应用键是 {principal.AppKey}，不是 {appKey}：应用级端点会返回 403。");
        return 1;
    }

    Step(5, "刷新令牌续期");
    var renewed = await client.RefreshAsync();
    Console.WriteLine($"  新令牌有效期到 {renewed.ExpiresAt.ToLocalTime():HH:mm:ss}；刷新令牌同样在响应体里，桌面端要自行安全存储。");

    if (client.HasPermission(PermissionCatalog.VersionsReleaseRead))
    {
        Step(6, "检查更新");
        try
        {
            var update = await client.CheckUpdateAsync(channelKey, currentVersion);
            if (update.Release is { } release)
            {
                Console.WriteLine($"  渠道 {update.ChannelKey} 最新版 {release.Version}（{release.RuntimeIdentifier}，{release.Files.Count} 个文件），hasUpdate={update.HasUpdate}");
            }
            else
            {
                Console.WriteLine($"  渠道 {update.ChannelKey} 的最新已发布版本没有 {RuntimeInformation.RuntimeIdentifier} 产物（不算错误：需要为该运行时补发布）。");
            }
        }
        catch (PlatformClientException failure) when (failure.Code == ApiErrorCode.VersionsChannelNotFound)
        {
            Console.WriteLine($"  跳过：渠道 {channelKey} 还不存在（干净服务端的正常形态）。");
            Console.WriteLine("       建渠道：POST /api/v1/admin/versions/channels，再上传并发布版本。");
        }
        catch (PlatformClientException failure) when (failure.Code == ApiErrorCode.VersionsNoPublishedRelease)
        {
            Console.WriteLine($"  跳过：渠道 {channelKey} 下没有已发布版本（212 的正常形态：先发布再拉取）。");
            Console.WriteLine("       发布：POST /api/v1/admin/versions/releases —— {\"channelKey\":\"...\",\"version\":\"1.0.0\",\"publish\":true,\"artifacts\":[…]}");
        }
    }
    else
    {
        Step(6, "检查更新（跳过：当前主体没有 versions.release.read）");
    }

    if (client.HasPermission(PermissionCatalog.LicenseRead))
    {
        Step(7, "在线授权状态 + 快照验签");
        try
        {
            var status = await client.GetLicenseStatusAsync(productKey, machineCode);

            // 只采信验签通过的快照：status.Status 字段本身不作数
            var verification = LicenseVerifier.Verify(status, appKey, productKey, machineCode);
            Console.WriteLine($"  状态 {status.Status}，放行 {verification.IsValid}{(verification.Reason is null ? string.Empty : $"，原因：{verification.Reason}")}");
            if (verification.Payload is { } payload)
            {
                Console.WriteLine($"  快照：{payload.Kind} 到期 {payload.NotAfter.UtcDateTime:yyyy-MM-dd} 宽限 {payload.GraceDays} 天 服务端时间 {payload.ServerTime.UtcDateTime:HH:mm:ss}");
            }
        }
        catch (PlatformClientException failure) when (failure.Code == ApiErrorCode.LicensingProductNotFound)
        {
            Console.WriteLine($"  跳过：产品 {productKey} 还不存在（干净服务端的正常形态）。");
            Console.WriteLine($"       建产品：POST /api/v1/admin/licensing/products —— {{\"key\":\"{productKey}\",\"name\":\"旗舰版\",\"trialDays\":14}}");
            Console.WriteLine($"       本机机器码（授权页展示形态）：{machineCode}");
        }
    }
    else
    {
        Step(7, "授权状态（跳过：当前主体没有 license.read）");
    }

    if (client.HasPermission(PermissionCatalog.DocumentRead))
    {
        Step(8, "客户端文档清单");
        var manifest = await client.GetDocumentManifestAsync();
        Console.WriteLine($"  可见文档 {manifest.Items.Count} 篇（internal 文档不会出现在这里），生成于 {manifest.GeneratedAt.ToLocalTime():HH:mm:ss}");
        foreach (var document in manifest.Items.Take(5))
        {
            Console.WriteLine($"    {document.CollectionKey}/{document.Title} r{document.Revision} {document.SizeBytes} B");
        }
    }
    else
    {
        Step(8, "客户端文档清单（跳过：当前主体没有 document.read）");
    }
}
catch (PlatformClientException failure)
{
    Console.Error.WriteLine($"× 平台返回错误：{failure.Message}");
    Console.Error.WriteLine("  提示：错误码段位即归属模块（100 身份 / 200 版本 / 300 授权 / 400 备份 / 500 文档 / 600 应用）。");
    return 1;
}

Console.WriteLine();
Console.WriteLine("✓ quickstart 通过：令牌、模块、版本、授权与文档链路都在真实服务端上跑通了。");
return 0;

static void Step(int index, string title) => Console.WriteLine($"[{index}] {title}");

static string DemoMachineCodeOf(string seed) =>
    Base32Crockford.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, LicenseConventions.MachineCodeBytes));

static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";
