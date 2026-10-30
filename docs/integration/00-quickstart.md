# 快速接入（B6：身份与权限 + 单次密钥/扫码 + 版本渠道 + 授权 + 备份/文档）

> 本文对应当前进度 **B6**：平台级端点、模块自述、应用接入、用户名口令登录与 RBAC、
> 6 位单次密钥兑换临时令牌、扫码登录审批、上传票据直传与版本下发、
> 授权产品/授权主体/离线码/签名密钥与 ECDSA 快照、命名空间备份与下发 Ack、
> 文档集合/修订/服务端强制可见性与保留清理。
> 备份与文档的完整说明见 [`04-backups.md`](04-backups.md) 与 [`05-documents.md`](05-documents.md)。
> 本文所有命令都已在本仓验证通过。

---

## 0. 前置

| 组件 | 版本 | 说明 |
|---|---|---|
| .NET SDK | 10.0.403+ | `global.json` 已锁定；`rollForward: latestFeature` |
| PostgreSQL | 16+ | 只有 `/ready` 与后续业务端点需要；`/healthz` 与模块自述不需要 |
| Node.js / pnpm | 22 / 9 | 仅管理台需要 |

---

## 1. 起服务端（两种方式二选一）

### A. 本地直跑（开发首选）

```bash
# 1) 起一个 PostgreSQL（或用自己的实例）
docker run -d --name platform-pg -p 5432:5432 \
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=appframework_platform postgres:17-alpine

# 2) 跑 API（Development 环境已内置开发连接串与开发签名密钥）
dotnet run --project src/Platform.Api
```

> **数据库迁移**：Development 下 `Database:AutoMigrate=true`，启动时自动 `MigrateAsync`。
> 生产环境必须关掉自动迁移（多副本同时迁移会互相踩），由部署脚本显式执行：
>
> ```bash
> dotnet tool restore    # 首次：安装仓库锁定的 dotnet-ef 10.0.12
> dotnet ef database update \
>   --project src/Platform.Infrastructure \
>   --startup-project src/Platform.Api
> ```
>
> 新增迁移必须从 `Platform.Api` 启动：设计时工厂在 Api 工程里，才能把各模块的
> `IPlatformModelContributor` 一并加载；否则生成出来的迁移会漏掉模块表。

### 管理台依赖（目录搬移后必读）

`web/node_modules` 里的 pnpm 符号链接记录的是**绝对路径**。仓库整体搬目录后必须重装一次，
否则 `vite`/`eslint` 会指向不存在的旧路径：

```bash
cd web
pnpm install --force     # 或删除 node_modules 后 pnpm install
pnpm verify              # tokens:check + lint + typecheck + test
```

### B. docker compose（想验证部署产物时用）

```bash
cp deploy/env.example deploy/.env     # 填 POSTGRES_PASSWORD 与 JWT_SIGNING_KEY（≥32 字符）
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build
```

---

## 2. 第一步验证：活着 + 就绪

```bash
curl -s http://localhost:5080/healthz     # {"isSuccess":true,...,"data":{"status":"ok"}}
curl -s http://localhost:5080/ready       # 含 postgres 检查项；库没起时这里会报 Unhealthy
```

`/healthz` **刻意不查库**：库抖动不应该让容器被反复重启。要判断能不能接流量，看 `/ready`。

---

## 3. 第二步：看模块清单

```bash
curl -s http://localhost:5080/api/v1/platform/modules
```

返回六个模块（`apps` / `identity` / `versions` / `licensing` / `backups` / `documents`），每个带：

- `key`：模块键，也是路由段的来源（`/api/v1/{key}`）
- `routes`：该模块挂载的路由前缀
- `requiredPermissions`：该模块声明的权限码（新增模块时 CI 会校验它们都已登记）

`apps` 永远排在首位 —— 其余五个模块的 `app_id` 隔离轴由它产生。

---

## 4. 第三步：登录拿令牌

管理端用**用户名口令**登录；刷新令牌由服务端写入 httpOnly + SameSite=Strict Cookie（`af_refresh`，路径 `/api/v1/admin/auth`），响应体只返回 access token：

```bash
curl -s -c admin.jar -X POST http://localhost:5080/api/v1/admin/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"userName":"admin","password":"Admin@12345"}'
```

> `Admin@12345` 只是开发环境的种子口令（`Seed__AdminPassword`）。生产必须通过环境变量注入强口令；种子账号首次登录会被要求改密（`mustChangePassword=true`）。

返回 `data.accessToken` / `data.expiresAt` / `data.user`（角色、权限、可访问应用、是否待改密）。刷新与登出：

```bash
curl -s -b admin.jar -c admin.jar -X POST http://localhost:5080/api/v1/admin/auth/refresh
curl -s -b admin.jar -c admin.jar -X POST http://localhost:5080/api/v1/admin/auth/logout
```

桌面客户端走 `/api/v1/identity/login`，刷新令牌放在响应体里由客户端自行安全存储（D11）。

### 4.1 单次密钥（现场临时登录）

管理员先签发一个 6 位密钥（需要 `identity.singlekey.issue`；创建类端点支持 `Idempotency-Key`）：

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/identity/singlekeys \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: issue-20261030-001' \
  -d '{"durationMinutes":30,"deviceFingerprint":"line-1-pc"}'
```

响应 `data.key` 是明文密钥，**只出现这一次**；服务端只存过期时间、指纹与尝试数，审计也不记密钥。

客户端兑换（匿名，限流 `RateLimits:SingleLoginRedeemPerMinute`，默认 10 次/分）：

```bash
curl -s -X POST http://localhost:5080/api/v1/identity/singlekey/redeem \
  -H 'Content-Type: application/json' \
  -d '{"key":"123456","deviceFingerprint":"line-1-pc","machineCode":"PC-001"}'
```

成功后返回 `accessToken` / `expiresAt` / `user`：主体是内置 `engineer` 角色的 `temp-user`，
`whoami` 里 `isTemporary=true`，权限集不含任何 `identity.*` 管理码。

消费语义是**原子单次**的（条件 `ExecuteUpdateAsync`：未使用、未过期、尝试数未满、指纹匹配），
同一密钥并发兑换只有一个成功。失败码：`105` 无效、`106` 已过期、`107` 已使用/尝试超限、`108` 设备不符。

### 4.2 扫码登录

1. 客户端发起（匿名，限流 `RateLimits:QrSessionPerMinute`，默认 60 次/分）：

```bash
curl -s -X POST http://localhost:5080/api/v1/identity/qr/sessions \
  -H 'Content-Type: application/json' \
  -d '{"deviceFingerprint":"line-1-pc","clientName":"1 号线操作台"}'
```

返回 `requestId`、`expiresAt`（默认 10 分钟）与 `payload`（`appframework://qr-login?requestId=...`，供二维码渲染）。

2. 客户端轮询 `GET /api/v1/identity/qr/sessions/{requestId}`，状态为
   `pending` / `approved` / `rejected` / `expired`；`approved` 时响应带 `session.accessToken`（令牌过期后不再回传）。

3. 管理端审批（需要 `identity.qr.approve`）：

```bash
curl -s -b admin.jar 'http://localhost:5080/api/v1/admin/identity/qr/sessions?status=pending'
curl -s -b admin.jar -X POST http://localhost:5080/api/v1/admin/identity/qr/sessions/$REQUEST_ID/approve \
  -H 'Content-Type: application/json' -d '{"durationMinutes":30}'
# 或拒绝
curl -s -b admin.jar -X POST http://localhost:5080/api/v1/admin/identity/qr/sessions/$REQUEST_ID/reject
```

错误码：`109` 会话不存在、`110` 已过期、`111` 已处理（重复审批/拒绝）。

审批通过时服务端经 SignalR `/hubs/platform` 广播 `qr-login.approved`，但这只是**提示**：
客户端必须以轮询结果为准，Hub 断线或推送丢失都不会影响领取令牌。会话落 `qr_login_sessions` 表，
服务重启后待审批会话仍在。

### 4.3 切换应用上下文（管理台进入应用级模块）

应用隔离轴只来自令牌：平台管理员登录后默认落在首个启用应用（通常是 `demo`），进入版本、授权这类
应用级模块前需要换一个绑定到目标应用的访问令牌：

```bash
curl -s -b admin.jar -c admin.jar -X POST http://localhost:5080/api/v1/admin/auth/context \
  -H 'Content-Type: application/json' \
  -d '{"appKey":"demo"}'
```

返回与登录相同的 `accessToken` / `expiresAt` / `user`，`user.appKey` 为目标应用；服务端同时把应用键
写回当前刷新令牌记录，后续 Cookie 静默续期仍停在该应用。当前账号无权访问该应用时返回 `403`/`109`。

---

## 5. 第四步：版本与渠道（B4）

版本数据按应用隔离（渠道键不是全局唯一的），因此下面的管理端命令都要求令牌已带应用上下文
（4.3 或登录时 `appKey`）。上传是「先声明哈希与长度、再直传字节」的两步：内容按 SHA-256 寻址，
同内容只存一份，票据一次性消费。

### 5.1 建渠道

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/versions/channels \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: channel-stable-001' \
  -d '{"key":"stable","name":"稳定版","description":"生产默认渠道"}'
```

渠道键规则 `^[a-z][a-z0-9-]{1,31}$`，创建后不可修改（客户端按它引用）；停用渠道不能发布新版本，
但已发布版本的下载不受影响。

### 5.2 上传文件（票据 → 直传）

先算哈希，再申请票据，最后把字节 PUT 到票据地址：

```bash
SHA=$(sha256sum app.zip | cut -d' ' -f1)
SIZE=$(stat -c%s app.zip)

TICKET=$(curl -s -X POST http://localhost:5080/api/v1/admin/versions/uploads \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: ticket-app-zip-001' \
  -d "{\"sha256\":\"$SHA\",\"sizeBytes\":$SIZE}" | jq -r '.data.uploadUrl')

curl -s -X PUT "http://localhost:5080$TICKET" \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/octet-stream' \
  --data-binary @app.zip
```

票据默认 30 分钟有效且一次性；内容已存在时返回 `reused=true`。失败码：
`207` 票据无效、`208` 已过期、`209` 已消费、`210` 字节哈希不符、`206` 发布时声明与 blob 不符。

### 5.3 创建版本（可创建即发布）

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/versions/releases \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: release-1.2.3-win-x64' \
  -d '{
    "channelKey":"stable",
    "version":"1.2.3",
    "notes":"首个稳定版",
    "publish":true,
    "artifacts":[{
      "runtimeIdentifier":"win-x64",
      "files":[
        {"path":"app/App.exe","sha256":"<sha256>","sizeBytes":123456},
        {"path":"app/appsettings.json","sha256":"<sha256>","sizeBytes":789}
      ]
    }]
  }'
```

服务端为每个产物计算清单哈希（按路径排序后对 `path\nsha256\nsize\n` 增量 SHA-256），
客户端可据此判断是否需要修复。版本号规则 `\d{1,5}\.\d{1,5}\.\d{1,5}(-后缀)?`；
`runtimeIdentifier` 支持 `win-x64/win-arm64/linux-x64/linux-arm64/osx-x64/osx-arm64`。
草稿对客户端不可见；`POST /api/v1/admin/versions/releases/{id}/publish` 发布草稿，
`.../archive` 归档（最新版归档后 check 自动回退到上一版）。

### 5.4 客户端检查更新 / 清单 / 下载

```bash
# 检查更新：无更新时仍返回最新清单，供客户端做完整性修复
curl -s -X POST http://localhost:5080/api/v1/client/versions/check \
  -H "Authorization: Bearer $CLIENT_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"channelKey":"stable","currentVersion":"1.2.2","runtimeIdentifier":"win-x64"}'

# 读取某版本某运行时的清单
curl -s "http://localhost:5080/api/v1/client/versions/stable/1.2.3/manifest?runtimeIdentifier=win-x64" \
  -H "Authorization: Bearer $CLIENT_TOKEN"

# 按内容哈希下载：ETag=sha256，支持 Range/206 与 If-None-Match/304
curl -s -OJ "http://localhost:5080/api/v1/client/versions/download/$SHA" \
  -H "Authorization: Bearer $CLIENT_TOKEN"
```

下载是纯流式（`Results.Stream` + Range），服务端不缓冲整文件；`ETag` 就是内容哈希，
配合 `If-None-Match` 可让客户端重复检查零流量。版本错误码：`200` 渠道不存在、`202` 版本不存在、
`203` 未发布、`204` 版本重复、`205` 产物不存在、`206` 清单与 blob 不符、`212` 渠道无已发布版本。

---

## 6. 第五步：授权（B5）

授权数据按应用隔离（产品键在同一应用内唯一），令牌同样需要应用上下文（4.3）。
签名密钥是**全局资源**：公钥客户端可匿名发现，私钥只从部署环境变量读取，管理台只能看到指纹与状态。

### 6.1 确认服务端已启用授权

```bash
curl -s http://localhost:5080/api/v1/admin/licensing/signing-keys \
  -H "Authorization: Bearer $TOKEN"
```

`isManaged=false` 表示没有配置私钥，此时：

- 客户端状态接口返回 `unmanaged`，客户端不采信任何授权；
- 签发离线码返回 `305`（HTTP 503）；
- 管理台仍可建产品与签发授权，但快照为空。

启用方式（启动前注入，私钥是 PKCS#8 DER 的 Base64，P-256）：

```bash
export Licensing__SigningKeyPkcs8="$(base64 -w0 license-key.pkcs8.der)"
dotnet run --project src/Platform.Api
```

`keyId` 固定为 `lk-` + 公钥 SPKI SHA-256 指纹前 16 个十六进制字符；换钥后指纹随之变化。

### 6.2 建授权产品

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/licensing/products \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: license-product-erp-pro' \
  -d '{
    "key":"erp-pro",
    "name":"旗舰版",
    "description":"按机器授权",
    "seatLimit":50,
    "defaultGraceDays":7,
    "trialDays":14
  }'
```

- 产品键规则 `^[a-z][a-z0-9-]{1,31}$`，创建后不可修改（离线码与客户端都按它引用）。
- `seatLimit` 为 `null`/0 表示不限；有效试用也占席位。
- `trialDays > 0` 时，未签发的机器首次调用状态接口会自动获得试用授权（同一机器只发一次）。

### 6.3 签发 / 续期 / 吊销授权

机器码是客户端「授权」页展示的 16 位 Crockford Base32（10 字节哈希）。手抄时 `O/I/L` 会被纠正为 `0/1/1`，
但长度必须完整。

```bash
# 签发（addDays 与 notAfter 二选一；licenseNo 留空自动生成）
curl -s -X POST http://localhost:5080/api/v1/admin/licensing/entitlements \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: license-issue-001' \
  -d '{"productKey":"erp-pro","machineCode":"008J4CT4ANK7F24S","addDays":365,"licenseNo":"LIC-20261030-001"}'

# 续期：addDays 从「当前到期时间与现在取较晚者」追加
curl -s -X POST http://localhost:5080/api/v1/admin/licensing/entitlements/$ID/renew \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"addDays":180}'

# 吊销：不可恢复；需要恢复只能重新签发
curl -s -X POST http://localhost:5080/api/v1/admin/licensing/entitlements/$ID/revoke \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"reason":"客户退订"}'
```

客户端校验在线状态（需要 `license.read`）：

```bash
curl -s -X POST http://localhost:5080/api/v1/client/licensing/status \
  -H "Authorization: Bearer $CLIENT_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"productKey":"erp-pro","machineCode":"008J4CT4ANK7F24S"}'
```

返回 `status`（`active` / `expired` / `revoked` / `unlicensed` / `unmanaged`）、时间字段
`snapshot`（Base64 签名快照）与 `trustedKeys`（可下发的公钥）。

**客户端必须验签后才采信快照**：用 `trustedKeys` 中任一公钥验证 ECDSA P-256（IEEE-P1363 64 字节签名，
SHA-256），并核对快照里的 `appKey` / `productKey` / `machineCode`。过期判定用服务端时间，
避免本机时钟被回拨。吊销时服务端经 SignalR 广播 `license.changed`，这只是提示：
客户端回拉状态接口拿到新的 `revoked` 快照后才会锁定。

### 6.4 离线授权码

无网现场用离线码：服务端签发 → 现场录入客户端 → 客户端本地验签（公钥内置/随安装包下发）。

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/licensing/offline-codes \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: license-offline-001' \
  -d '{"productKey":"erp-pro","machineCode":"008J4CT4ANK7F24S","addDays":365,"licenseNo":"LIC-OFF-001"}'
```

返回 `code`（原始串）与 `formattedCode`（每 8 字符插 `-`，仅人读）。格式为
`AF3 | payload 长度(2) | payload | 签名(103) | 校验位(1)`，绑定**产品键 + 机器码**，
复制到另一台机器或另一个产品均会被拒。服务端校验端点（客户端离线时可不用）：

```bash
curl -s -X POST http://localhost:5080/api/v1/client/licensing/offline/verify \
  -H "Authorization: Bearer $CLIENT_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"code":"AF3...","machineCode":"008J4CT4ANK7F24S","productKey":"erp-pro"}'
```

失败码：`306` 码非法/验签失败、`307` 机器码不匹配、`303` 已过期。
**离线码没有吊销通道**：台账删除不影响已发出的码，只能等它到期；请保留台账用于对账。

### 6.5 密钥轮换

1. 生成新 P-256 私钥，更新部署环境变量 `Licensing__SigningKeyPkcs8`；
2. 重启服务（私钥只在启动时读取）；
3. 调用 `POST /api/v1/admin/licensing/signing-keys/sync`（或等待任一授权请求触发自动同步）。

新密钥变为 `active`，旧密钥自动转为 `retired`：不再签新快照，但继续出现在 `trustedKeys` 中，
客户端手里的旧快照仍能验签。当前私钥对应的密钥不能退役，否则会签出无人可验的快照。

---

## 7. 第七步：备份与文档（B6）

备份按「应用 + 命名空间」隔离：≤64 KB 内联、超过转存 Blob；下发走 SignalR 并等待 Ack。
文档按集合分组、每次上传生成修订，`internal`（默认）文档不会出现在客户端清单，下载同样被服务端拒绝。

```bash
# 命名空间（channel 缺省 default；maxVersions 缺省 50）
curl -s -X POST http://localhost:5080/api/v1/admin/backups/namespaces \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: backup-ns-global-001' \
  -d '{"key":"global","name":"全局配置","channel":"default","maxVersions":50}'

# 创建备份（payloadJson 为 JSON 文本；sha256 可省略，提供则必须一致）
curl -s -X POST http://localhost:5080/api/v1/admin/backups \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: backup-global-001' \
  -d '{"namespaceKey":"global","kind":"config","name":"dark-theme","schemaVersion":1,
       "frameworkVersion":"3.2.0","channel":"default","payloadJson":"{\"theme\":\"dark\"}"}'

# 下载（内联 JSON 或流式文件；ETag / Range / 304）
curl -s "http://localhost:5080/api/v1/admin/backups/$ID/download" \
  -H "Authorization: Bearer $TOKEN" -o backup.json

# 下发：同渠道硬门禁；force 只放宽主版本；currentHash 非空会先落 pre-push 快照
curl -s -X POST http://localhost:5080/api/v1/admin/backups/$ID/push \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"targetChannel":"default","targetFrameworkVersion":"3.2.0","force":false}'

# 文档集合 + 上传（multipart；visibility 缺省 internal）
curl -s -X POST http://localhost:5080/api/v1/admin/documents/collections \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"key":"manual","name":"用户手册"}'

curl -s -X POST http://localhost:5080/api/v1/admin/documents \
  -H "Authorization: Bearer $TOKEN" \
  -F 'collectionKey=manual' -F 'title=安装手册' -F 'visibility=client' \
  -F 'file=@install.pdf;type=application/pdf'

# 客户端清单与下载：清单只含 client 可见且集合启用的文档
curl -s http://localhost:5080/api/v1/client/documents/manifest \
  -H "Authorization: Bearer $CLIENT_TOKEN"
```

备份错误码 `400–408`、文档错误码 `500–505`；软删除后由 `RetentionWorker` 在宽限期后
“先删文件、再删行”。完整语义、兼容矩阵与配置项见 [`04-backups.md`](04-backups.md)、
[`05-documents.md`](05-documents.md)。

---

## 8. 第八步：验证令牌声明

```bash
TOKEN=... # 上一步的 accessToken
curl -s http://localhost:5080/api/v1/platform/whoami -H "Authorization: Bearer $TOKEN"
```

对着返回逐项看：`userId`（`sub`）、`userName`（`name`）、`roles`（`role`）、`permissions`（`perms`）、`appKey`/`appKeys`（`app`）、`isTemporary`、`isService`。

这一步是 JWT 声明链路的第一站：如果 `userId` 是 `null`，几乎一定是 `MapInboundClaims` 被打开了（会把 `sub` 改写成 `ClaimTypes.NameIdentifier` 的 URI），本仓已在 `AuthenticationSetup` 里显式关掉。

---

## 8. 权限码与错误码

```bash
curl -s http://localhost:5080/api/v1/platform/permissions
```

返回全部权限码描述符（`code` / `moduleKey` / `displayNameKey` / `sort` / `platformAdminOnly`）。
两端共用同一份：管理台用它渲染权限树，Avalonia 客户端用 `client.*` 段的码对应 `PermissionType` 枚举成员。

错误码是**分段枚举**（见 `src/Platform.Contracts/ApiErrorCode.cs`）：

| 段位 | 归属 |
|---|---|
| 0–99 | 通用（校验失败、未认证、限流、服务不可用…） |
| 100–199 | 身份 |
| 200–299 | 版本 |
| 300–399 | 授权 |
| 400–499 | 备份 |
| 500–599 | 文档 |
| 600–699 | 应用接入 |

HTTP 状态码由 `ApiErrorHttpStatus.Map` 唯一映射，**不要**在业务代码里手写 `StatusCode`。

---

## 9. 统一返回形状

所有端点（含 401/403/429/异常）都是同一个形状：

```json
{ "isSuccess": false, "code": 1001, "message": "用户名或密码错误", "data": null, "traceId": "0HN..." }
```

`traceId` 与服务端日志的 `RequestId` 对齐 —— 报障时先要这个值。

---

## 10. 契约包（客户端仓库消费）

```bash
dotnet pack src/Platform.Contracts/Platform.Contracts.csproj -c Release \
  -p:PackageId=AppFramework.Platform.Contracts -o artifacts/packages
```

产物 `AppFramework.Platform.Contracts.0.1.0-preview.1.nupkg` 内含 DTO、错误码、权限码目录、Hub 契约与分页类型。
客户端仓库应**精确 pin 版本**（不用 `*` 或范围），破坏性变更必须主版本 +1。

---

## 11. 管理台

```bash
cd web
pnpm install
pnpm dev            # http://localhost:5173，/api 自动代理到 http://localhost:5080
```

用种子管理员 `admin` / `Admin@12345` 登录；首次登录会跳转强制改密，改密后全部会话被吊销，需要重新登录。
登录后总览页展示当前主体与平台模块；「身份与权限」页按权限展示标签页：用户 / 角色 / 会话
（`identity.user.read`、`identity.role.read`、`identity.session.read`），以及「单次密钥」
（`identity.singlekey.issue`）和「扫码审批」（`identity.qr.approve`）。
「版本与渠道」页（`versions.channel.*` / `versions.release.*` / `versions.artifact.upload`）
支持新建/编辑渠道、选文件后浏览器端算 SHA-256 并直传、创建并发布、查看产物清单与归档；
「授权」页（`license.product.*` / `license.issue` / `license.revoke` / `license.offlinecode.issue` /
`license.signingkey.manage`）按标签页提供授权产品、授权主体（签发/续期/吊销）、离线码台账
（复制/删除）与签名密钥（当前指纹、轮换同步、退役密钥）；
页面顶部的「当前应用」切换走 4.3 的上下文端点。

改主题：右上角色系下拉 + 模式切换（8 族 × 3 模式 = 24 套）。
**不要手改** `web/src/styles/antd-theme.ts`：它由 `design/tokens.json` 生成，改色板请改 JSON 后执行：

```bash
node tools/gen-tokens.mjs          # 生成
node tools/gen-tokens.mjs --check  # 校验（CI 用这个）
```

---

## 12. 本地质量门（与 CI 一致）

```bash
dotnet build Platform.slnx -c Release --disable-build-servers -warnaserror
dotnet test  Platform.slnx -c Release --disable-build-servers

cd web
pnpm run tokens:check && pnpm lint && pnpm typecheck && pnpm test && pnpm build
```

---

## 附录：后续阶段会出现在这里的端点

| 阶段 | 端点（前缀省略 `/api/v1`） |
|---|---|
| 已落地 B0–B5 | `/platform/*`、`/apps/token`、`/admin/apps*`、`/identity/login`、`/identity/refresh`、`/identity/logout`、`/identity/password/change`、`/admin/auth/*`（含 `/context`）、`/admin/identity/users*`、`/admin/identity/roles*`、`/admin/identity/sessions*`、`/identity/singlekey/redeem`、`/identity/qr/sessions*`、`/admin/identity/singlekeys`、`/admin/identity/qr/sessions*`、`/admin/versions/channels*`、`/admin/versions/releases*`、`/admin/versions/uploads*`、`/client/versions/check`、`/client/versions/{channel}/{version}/manifest`、`/client/versions/download/{sha256}`、`/admin/licensing/products*`、`/admin/licensing/entitlements*`、`/admin/licensing/offline-codes*`、`/admin/licensing/signing-keys*`、`/client/licensing/status`、`/client/licensing/offline/verify`、`/client/licensing/keys` |
| B6 | `/backups*`、`/documents*` |

约定不变：路由 `/api/v1/{module}/...`，管理端与客户端入口分离（`/admin/*` 与 `/client/*`），服务端强制可见性。
