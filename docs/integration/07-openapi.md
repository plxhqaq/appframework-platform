# OpenAPI、契约包与 SDK（B7）

> 本文说明**平台对外契约的三份来源**：运行期 OpenAPI 文档、`AppFramework.Platform.Contracts`
> 契约包、`AppFramework.Platform.Client` SDK 包，以及各自能用来做什么、不能用来做什么。
> 结论先行：**OpenAPI 用来看，契约包用来编译，SDK 用来跑。**

---

## 0. OpenAPI 文档与在线调试

| 地址 | 内容 |
|---|---|
| `GET /openapi/v1.json` | OpenAPI **3.1.1** 文档，`info.title = Platform.Api`，`info.version = v1` |
| `GET /scalar/v1` | Scalar 在线调试页（直接读上面那份文档） |

装配在 `src/Platform.Api/Program.cs:78`（`AddOpenApi()`）与 `:219-220`（`MapOpenApi()` / `MapScalarApiReference()`），
**没有环境门**：Development 与 Production 都暴露。公网部署时二选一：

- 在反向代理只放内网/办公网访问这两个路径；
- 或者接受「路由结构对外可见」——它们不含任何凭据，但会泄露内部端点命名。

`info.version` 是文档版本，**不等于包版本**（包版本见 §2）。

当前这份文档的规模可以自查：

```bash
curl -s http://localhost:5080/openapi/v1.json | jq '{paths: (.paths|length), schemas: (.components.schemas|length)}'
```

---

## 1. 文档里有什么、缺什么

有：

| 项 | 说明 |
|---|---|
| `paths` | 全部业务端点（约 90 条），含 `tags`、`summary`、`operationId`、`requestBody` 的 schema 引用 |
| `components.schemas` | 请求/响应 DTO 记录（约 40 个） |
| `tags` | `platform` / `apps` / `identity` / `versions` / `licensing` / `backups` / `documents` / `webhooks` |
| `servers` | 由**当前请求的基址**推导，从反向代理后面拿到的地址可能与外网地址不一致 |

没有（刻意的，也是已知缺口）：

| 缺什么 | 后果 | 真源在哪 |
|---|---|---|
| `securitySchemes`（无 `AddSecurityDefinition`，也没有 operation transformer） | 生成器产出的客户端不会自动带 `Authorization` | [`01-identity.md`](01-identity.md) 的令牌链路 |
| 错误响应模型（每个操作只声明 `200`，没有 401/403/429/业务失败） | 生成器不会为失败分支建模 | `src/Platform.Contracts/ApiErrorCode.cs` + `ApiErrorHttpStatus.Map` |
| 统一信封 `ApiResult<T>`（`isSuccess`/`code`/`message`/`data`/`traceId`） | 生成的客户端把 200 的 body 直接当 `T`，拿不到 `code`/`traceId` | `src/Platform.Contracts/ApiResult.cs` |
| 请求头 `X-App-Key` / `X-Api-Key` / `Idempotency-Key` / `X-Client-Version` / `X-Runtime-Identifier` / `X-Machine-Code` | 文档里看不到，必须按约定自加 | `PlatformConventions` / `PlatformHeaders` |
| `/metrics` | `ExcludeFromDescription()`，永远不在文档里（`src/Platform.Api/Endpoints/MetricsEndpoints.cs:77`） | [`08-observability.md`](08-observability.md) |
| `/openapi/v1.json`、`/scalar/v1` 自身 | 不在文档里 | 本文 |

> **不要用 OpenAPI 生成器给桌面客户端产代码**：缺了信封与错误码的客户端，
> 在 401/429 上会直接抛反序列化异常，而不是拿到可判定的错误码。用 §3 的契约包。

`dev` 标签下的端点（`/api/v1/dev-token*`）只在 Development **且** `Jwt:AllowDevTokenEndpoint=true` 时存在，
生产文档里不会出现；反过来说，在生产文档里看到 `dev` 标签就说明这个开关被误开了。

---

## 2. 契约包 `AppFramework.Platform.Contracts`

```bash
dotnet pack src/Platform.Contracts/Platform.Contracts.csproj -c Release \
  -p:PackageId=AppFramework.Platform.Contracts -o artifacts/packages
```

版本由 `Directory.Build.props` 的 `PlatformContractsVersion` 给出（当前 `0.1.0-preview.1`），
零第三方依赖，包内含：

| 内容 | 类型 |
|---|---|
| 各模块 DTO / 请求记录 | `Platform.Contracts.{Apps,Identity,Versions,Licensing,Backups,Documents,Webhooks,Audit}` |
| 错误码分段枚举 | `ApiErrorCode`（0–99 通用 / 100 身份 / 200 版本 / 300 授权 / 400 备份 / 500 文档 / 600 应用 / 700 Webhook） |
| 统一返回信封 | `ApiResult` / `ApiResult<T>` |
| 权限码目录 | `PermissionCatalog`（含 `Wildcard`）、`ClientPermissionCatalog`、`PermissionDescriptor` |
| SignalR 契约 | `PlatformHubPaths.Root = "/hubs/platform"`、分组 `app:{appKey}`、事件名 `LicenseChanged` / `ConfigPush` / `ConfigPushAck` / `QrLoginApproved` / `ReleasePublished` / `DocumentChanged` 及通知记录类型 |
| 分页 | `PageQuery` / `PageResult<T>` |
| 约定与常量 | `PlatformConventions`（路由前缀、头名、声明名、认证方案名）、`PlatformModuleKeys`、`PlatformRoutes` |
| 授权编解码 | `LicenseSnapshotCodec` / `OfflineLicenseCodec` / `Base32Crockford` / `LicenseConventions` |
| 平台自述类型 | `ModuleInfoDto`、`WhoAmIResponse` |

引用规则（客户端仓库）：

- **精确 pin 版本**，不要用 `*` 或范围；
- 新增字段不升主版本；破坏性变更主版本 +1，两端一起换；
- 本地开发把产物指向 `artifacts/packages`（仓内 `nuget.config` 已把 `AppFramework.*` 映射到该目录）。

CI 会 `dotnet pack` 契约包与客户端 SDK 两个包，一起作为 `contracts-nupkg` 构建产物
（`.github/workflows/ci.yml`）；
**OpenAPI 文档本身没有 CI 校验步骤**，改动端点时不会有任何自动信号提醒你同步文档 —— 
端点变更请同时更新 `docs/integration/` 下对应模块文档。

---

## 3. SDK 包 `AppFramework.Platform.Client`

```bash
dotnet pack src/AppFramework.Platform.Client/AppFramework.Platform.Client.csproj -c Release -o artifacts/packages

# 客户端仓库
dotnet add package AppFramework.Platform.Client --version 0.1.0-preview.1
```

版本线跟契约包一致（`Directory.Build.props` 的 `PlatformClientVersion`），
唯一依赖就是契约包，只依赖 `System.Net.Http.Json` + `System.Text.Json`。

入口类型：

| 类型 | 作用 |
|---|---|
| `PlatformClient` | 令牌链路 + 各模块调用 + 带哈希校验的流式下载（`partial`，模块方法在 `PlatformClient.Modules.cs`） |
| `PlatformClientOptions` | `BaseUrl`（必须带 http/https 的绝对地址）、`ClientVersion`、`RuntimeIdentifier`、`Timeout` |
| `PlatformClientException` | 非 2xx 与业务失败信封统一归一，暴露 `Code`（`ApiErrorCode`）、`StatusCode`、`TraceId` |
| `LicenseVerifier` | 授权快照与离线码的本地验签（见 [`03-licensing.md`](03-licensing.md)） |

包内自带 `README.md`（`PackageReadmeFile`），讲凭据落盘、自动续期边界、下载语义与验签规则。

跑一遍真实链路：

```bash
PLATFORM_URL=http://localhost:5080 \
PLATFORM_PASSWORD='<管理台种子口令，只走环境变量>' \
  dotnet run --project samples/ConsoleQuickstart -c Release
```

示例按「存活 → 模块 → 登录 → 自述 → 续期 → 检查更新 → 授权验签 → 文档清单」八步执行，
干净服务端上第 6/7 步会打印「跳过 + 建数据命令」而不是失败。

---

## 4. 权限码与端点清单的在线来源

这两个不需要读文档，直接问服务端：

```bash
curl -s http://localhost:5080/api/v1/platform/modules     # 模块键、路由前缀、所需权限码
curl -s http://localhost:5080/api/v1/platform/permissions # 全部权限码描述符（code/moduleKey/displayNameKey/sort/platformAdminOnly）
curl -s http://localhost:5080/api/v1/webhooks/info        # 单模块自述（匿名）
```

`/api/v1/platform/modules` 是「新增模块时 CI 校验权限码都已登记」的同一份数据；
管理台的权限树和客户端的 `PermissionType` 映射都从 `/permissions` 出发，**不要在前端硬编码权限码清单**。

---

## 5. 常见坑

1. **拿 Scalar 调试通过就以为契约完整** —— Scalar 不会提醒你错误分支没有模型；业务失败仍然是 HTTP 200 + `isSuccess=false` 的情况在文档里完全看不出来。
2. **用生成器代码接下载端点** —— 版本/备份/文档下载是流式 + Range + ETag，`Results.Stream` 不是 JSON 信封，生成器会按 200 schema 去反序列化。
3. **忽略 `X-App-Key`** —— M2M 换发令牌靠 `X-App-Key` + `X-Api-Key` 两个头，OpenAPI 里没有它们。
4. **把 `info.version` 当包版本** —— 它是 `v1`，与 `0.1.0-preview.1` 无关；pin 版本看 `Directory.Build.props`。
5. **改了端点没同步文档** —— CI 不校验 OpenAPI，`docs/integration/*` 才是人工同步点。

---

相关文档：
[`00-quickstart.md`](00-quickstart.md) ·
[`06-webhooks-and-audit.md`](06-webhooks-and-audit.md) ·
[`08-observability.md`](08-observability.md)
