# AppFramework.Platform.Client

平台桌面侧（Avalonia/WinForms/服务进程）的 HTTP 客户端封装：令牌链路、错误归一、
带哈希校验的下载，以及授权快照/离线码的本地验签。只依赖 `System.Net.Http.Json`
与 `Platform.Contracts`，不引入第三方 HTTP 或 JSON 库。

## 安装

```bash
dotnet add package AppFramework.Platform.Client --version 0.1.0-preview.1
```

客户端仓库必须**精确 pin 版本**（不要用 `*` 或范围）。它与 `AppFramework.Platform.Contracts`
同版本线发布，升级时两个包一起换。

## 五分钟上手

```csharp
using AppFramework.Platform.Client;

using var client = new PlatformClient(new PlatformClientOptions
{
    BaseUrl = "https://platform.example.com",
    ClientVersion = "3.2.0",                 // 写入 X-Client-Version
    RuntimeIdentifier = "win-x64",           // 写入 X-Runtime-Identifier，并作为下载/check 的默认 RID
});

var session = await client.SignInAsync("admin", password, appKey: "demo");
Console.WriteLine($"{session.User.UserName}：{client.CurrentUser!.Permissions.Count} 个权限码");

var update = await client.CheckUpdateAsync("stable", currentVersion: "1.2.2");
```

口令**只能**从交互输入或系统凭据库取得，不要写进配置文件或源码。

## 凭据存放与恢复

桌面端的刷新令牌要落盘（Windows 用凭据管理器，Linux 用 secret-service），下次启动回填：

```csharp
var client = new PlatformClient(new PlatformClientOptions { BaseUrl = baseUrl });
client.RefreshToken = await CredentialStore.LoadAsync();   // 可写属性，专为恢复设计

var principal = await client.GetWhoAmIAsync();             // 访问令牌为空/过期 → 自动用刷新令牌续期并重试一次
```

`UseAccessToken(token, expiresAt, user)` 用于只接管访问令牌的场景（例如 M2M 定时换发）。

## 自动续期的边界

带令牌的**业务**端点在 401 且持有刷新令牌时，自动 `RefreshAsync` 后重试**恰好一次**，
不会递归。凭据类端点（`SignInAsync` / `RefreshAsync` / `SignOutAsync` / `ChangePasswordAsync`）
不自愈 —— 它们失败就是失败，由调用方决定下一步。

`ChangePasswordAsync` 成功后服务端吊销全部会话，SDK 在 `finally` 里清空本地凭据，
调用方必须重新登录。

## 错误处理

任何非 2xx（含 401/403/429）与业务失败信封都会抛 `PlatformClientException`：

```csharp
try
{
    await client.DownloadVersionFileAsync(sha256, @"C:\app\new.zip");
}
catch (PlatformClientException failure)
{
    // Code: 契约里的分段枚举（100 身份 / 200 版本 / 300 授权 / 400 备份 / 500 文档 / 600 应用 / 700 Webhook）
    // StatusCode: 服务端 ApiErrorHttpStatus.Map 的唯一映射
    // TraceId: 与服务端日志 RequestId 对齐，报障先要它
    Console.Error.WriteLine(failure.Message);
}
```

响应体不是统一信封时（网关 HTML、纯文本），同样归一为 `PlatformClientException`，
错误码由 HTTP 状态推出。

## 下载

`DownloadVersionFileAsync` / `DownloadDocumentAsync` 是流式落盘 + 哈希核验：

- 先写同目录的 `*.tmp`，SHA-256 与期望值一致才 `File.Move(..., overwrite: true)`，
  失败或异常都不会留下半个文件；
- 返回 `false` 表示 `304 未变更`（此时不写盘）；
- `ifNoneMatch` **默认不填**。版本下载按内容哈希寻址，把期望哈希当 ETag 传进去必然 304，
  等于什么都没下 —— 只在「本地已有这个哈希、想确认服务端仍然一致」时传。

## 授权：必须验签才采信

`GetLicenseStatusAsync` 返回的 `Snapshot` 是服务端 ECDSA P-256 签名快照，
**不要**直接读 `status` 字段判权限，用 `LicenseVerifier`：

```csharp
var status = await client.GetLicenseStatusAsync("erp-pro", machineCode);

// 受信任公钥随响应一起返回（status.TrustedKeys），验签在本地完成
var verdict = LicenseVerifier.Verify(status,
    expectedAppKey: "demo",
    expectedProductKey: "erp-pro",
    expectedMachineCode: machineCode);

if (!verdict.IsValid)
{
    LockedOut(verdict.Reason);   // 验签失败 / appKey·产品·机器码不符 / 过期 / 未生效 / unmanaged
}
```

`LicenseVerifier` 的三条硬规则：

1. 只接受 `ECDSA-P256-SHA256` 且签名可验的快照，任一受信任公钥验过即算通过；
2. 快照里的 `appKey` / `productKey` / `machineCode` 必须与本次请求一致，机器码先规范化再比
   （手抄的 `O/I/L` 会被纠正）；
3. 到期判定用快照里的 `ServerTime`，不看本机时钟 —— 回拨系统时间不能延长授权。

离线码同理，纯本地验签，无需联网。公钥（SPKI Base64）内置于安装包，或从匿名的
`GetTrustedSigningKeysAsync` 预先取回：

```csharp
var keys = await client.GetTrustedSigningKeysAsync();
IReadOnlyCollection<string> publicKeys = [.. keys.Select(static k => k.PublicKey)];

var offline = LicenseVerifier.VerifyOfflineCode(code, machineCode, "erp-pro", publicKeys);
```

在线时也可以让服务端校验：`VerifyOfflineLicenseCodeAsync`。两者结论一致，离线时用本地那条。

## 配置下发

`GetBackupAsync` 读元数据，`ReadBackupContentAsync` 取 JSON 文本（≤64 KB 走内联，超过走流式）。
回执 Ack 走 SignalR `/hubs/platform`，本包只负责拉取内容，不含 Hub 客户端。

## 不在此包里的东西

- SignalR 实时通知（事件名取自 `PlatformHubEvents`：`QrLoginApproved`、`LicenseChanged`、
  `ConfigPush`/`ConfigPushAck`、`ReleasePublished`、`DocumentChanged`）—— 这些只是提示，
  客户端必须以轮询或状态接口为准，断线不影响正确性；
- 幂等键（`Idempotency-Key`）—— 只用于创建类端点，桌面端一般不需要，需要时自行拼 `HttpRequestMessage`；
- 管理端端点（`/api/v1/admin/*`）—— 那是管理台（Web）的职责。

## 快速验证

仓库里的 `samples/ConsoleQuickstart` 按本 README 的顺序把链路跑了一遍，
凭证全部来自环境变量，是「新人 15 分钟跑通」的验收载体。
