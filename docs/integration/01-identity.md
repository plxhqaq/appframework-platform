# 身份与权限（登录链路 + RBAC + 单次密钥 + 扫码登录）

> 本文面向**客户端接入方**：桌面客户端走 `/api/v1/identity/*`，管理台走 `/api/v1/admin/auth/*`，两者的刷新令牌存放方式被刻意分开。
> 所有路由、字段、权限码、错误码与默认值都取自源码；端点前缀常量见 `src/Platform.Contracts/PlatformConventions.cs:20`（`PlatformRoutes`）。
> 30 秒跑通请回看 [`00-quickstart.md`](00-quickstart.md) 第 4 节，本文补的是逐端点语义与约束。

| 能力 | 管理端 | 客户端（桌面/服务） |
|---|---|---|
| 登录/刷新/登出/改密 | `/api/v1/admin/auth/*` | `/api/v1/identity/*` |
| 应用上下文 | `/api/v1/admin/auth/context` | 登录时传 `appKey` |
| 单次密钥 | `/api/v1/admin/identity/singlekeys` | `/api/v1/identity/singlekey/redeem` |
| 扫码登录 | `/api/v1/admin/identity/qr/sessions*` | `/api/v1/identity/qr/sessions*` |
| 用户/角色/会话 | `/api/v1/admin/identity/{users,roles,sessions}` | — |
| 主体自述 | `/api/v1/platform/whoami` | `/api/v1/platform/whoami` |

---

## 1. 前置：应用上下文只来自令牌

- 隔离轴是 JWT 的 `app_ctx` 声明（当前应用，单条）；`app` 声明只表示可访问应用（多条），`ICurrentPrincipal.AppKey` 读 `app_ctx`（`src/Platform.Api/Auth/JwtTokenIssuer.cs:82`）。平台上下文令牌没有 `app_ctx`，`AppKey` 为空。
- 服务端**不接受**请求体或请求头里的 `appKey` 作为隔离依据；版本、授权、备份、文档这类应用级模块直接从令牌取应用键。
- 两种拒绝要分清：应用级模块发现令牌没有应用上下文时返回 `4`（HTTP 403，「当前令牌没有应用上下文，无法访问版本资源」，`VersionsEndpoints.cs:484`）；登录或切换时指定了无权访问的应用返回 `118`（HTTP 403）。
- 进入应用的三条路：登录/换发时带 `appKey`、管理台 `POST /api/v1/admin/auth/context` 切换、M2M 令牌由 API Key 所属应用自动绑定。
- 平台管理员（`platform-admin`，权限 `*`）不指定应用登录时得到**平台上下文**令牌：`user.appKey` 为空，`app` 覆盖全部启用应用，但没有 `app_ctx`；此时应用级模块返回 `4`（403），签发单次密钥也必须显式给 `appKey`。管理台登录后会自动进入首个可访问应用，也可随时用顶部切换器换应用。
- **应用停用后**：登录/切换/刷新立即拒绝（`118`/403）；已签发的访问令牌由请求路径上的应用上下文守卫拦截（`602`/403），不必等访问令牌自然过期。守卫按 `AppContextGuard:CacheSeconds`（默认 30 秒）缓存状态，启用/停用在本副本立即失效，多副本最迟一个窗口后收敛。
- **用户级 epoch 守卫**：`users.token_epoch` 随每次「吊销收口」递增，访问令牌携带签发时的 `epoch` 声明；请求路径上的守卫发现令牌落后即 401（`114`），**所有续期路径同样被封死**——包括 `POST /api/v1/admin/auth/context` 自续期与 QR 审批，不等访问令牌自然过期。守卫按 `UserEpochGuard:CacheSeconds`（默认 30 秒）缓存库值，bump 在本副本经缓存失效立即生效；`pkind=service` 的 M2M/开发令牌豁免（M2M 轴由应用守卫与密钥吊销覆盖）。
- 平台管理员（通配权限 `*`）不受停用守卫影响——否则停用应用后就没人能从管理端把它重新启用。

---

## 2. 令牌链路

### 2.1 端点总表

| 方法 | 路径 | 认证 | 限流策略 | `Idempotency-Key` |
|---|---|---|---|---|
| POST | `/api/v1/apps/token` | 匿名，需 `X-App-Key` + `X-Api-Key` | `m2m-token` 30/分（按应用键，无键退回 IP） | 否 |
| POST | `/api/v1/identity/login` | 匿名 | `identity-login` 30/分（IP） | 否 |
| POST | `/api/v1/identity/refresh` | 匿名，body 带刷新令牌 | `identity-login` | 否 |
| POST | `/api/v1/identity/logout` | 匿名，body 可选 | — | 天然幂等 |
| POST | `/api/v1/identity/password/change` | Bearer，仅需已认证 | — | 否 |
| POST | `/api/v1/identity/singlekey/redeem` | 匿名 | `single-login-redeem` 10/分（IP） | 否 |
| POST | `/api/v1/identity/qr/sessions` | 匿名 | `qr-session` 60/分（IP） | 否 |
| GET | `/api/v1/identity/qr/sessions/{id}` | 匿名 | `qr-session`（与创建共用额度） | 否 |
| POST | `/api/v1/identity/qr/sessions/{id}/cancel` | 匿名 | `qr-session` | 是（仅取消待审批会话） |
| POST | `/api/v1/admin/auth/login` | 匿名 | `identity-login` | 否 |
| POST | `/api/v1/admin/auth/refresh` | Cookie `af_refresh_v2` | `identity-login` | 否 |
| POST | `/api/v1/admin/auth/context` | Bearer（服务主体被拒） | — | 否 |
| POST | `/api/v1/admin/auth/logout` | Cookie `af_refresh_v2` | — | 天然幂等 |

> 限流注册见 `src/Platform.Api/Program.cs` 的 `AddRateLimiter`；全局还有兜底窗口（默认 600 次/分/IP，`RateLimits:GlobalPerMinute` 可调），被限流统一返回码 `7`（HTTP 429）。
> 账号维度的口令爆破由登录节流表负责，与 IP 限流互补。

### 2.2 M2M 服务令牌

```bash
curl -s -X POST http://localhost:5080/api/v1/apps/token \
  -H 'X-App-Key: demo' -H 'X-Api-Key: <API Key 明文>' -H 'Content-Type: application/json' \
  -d '{"scopes":["inspect.page.read","inspect.panel.operate"]}'
```

- `M2MTokenRequest.scopes` 可为 `null`（取该 Key 的全部 scope）；**只减不增**——请求了 Key 未被授予的 scope 返回 `606`（403），缺任一请求头返回 `603`（401）。
- Key 由管理台 `POST /api/v1/admin/apps/{id}/keys` 签发：每个 scope 必须是签发人**自己已持有**的权限（否则 `606`，403），不得使用通配符 `*`；未知权限码返回 `608`。
- Key 生命周期：`POST /api/v1/admin/apps/{id}/keys/{keyId}/revoke` 吊销后立即失效（换不到新令牌，不可恢复）；`DELETE /api/v1/admin/apps/{id}/keys/{keyId}` 只清理**已吊销**的行，仍在生效返回 `6`（409），不存在或不属于该应用返回 `609`（404）。删除只移走密钥行，签发与吊销留痕仍在审计日志里，已签发的访问令牌按自身有效期失效。
- 响应 `M2MTokenResponse`：`accessToken` / `tokenType`（固定 `Bearer`）/ `expiresAt` / `appKey` / `scopes` / `permissionsLocked`。`permissionsLocked=true` 表示该应用的权限清单已锁定，客户端连接成功后可直接跳过清单上传，不必重复调用。
- 令牌声明：`sub` = `apikey:{密钥id:N}`、`name` = `应用名 (密钥名)`、`role` = `service`、`perms` = 生效 scope、`app` = 该应用键、`pkind` = `service`。
- **没有刷新令牌**：到期只能再换一次；`/admin/auth/context` 明确拒绝服务主体（`4`，403）。

### 2.3 用户名口令登录

```bash
curl -s -X POST http://localhost:5080/api/v1/identity/login \
  -H 'Content-Type: application/json' \
  -d '{"userName":"operator01","password":"Oper@20260101","appKey":"demo",
       "deviceFingerprint":"line-1-pc","machineCode":"008J4CT4ANK7F24S"}'
```

- `userName` 大小写不敏感（服务端 `Trim().ToLowerInvariant()`）；空用户名或空口令统一返回 `101`。
- `appKey` 可选：期望进入的应用，不在可访问集合内返回 `118`（403）。`deviceFingerprint` 与 `machineCode` 可选，前者写入刷新令牌记录供按设备排查/吊销，后者同时写入访问令牌 `machine` 声明与刷新令牌记录，供授权绑定；`machineCode` 非空时必须是 16 位 Crockford Base32（`O/I/L` 按 `0/1/1` 容错），格式不符直接 400。
- 响应 `LoginResponse`：`accessToken` / `expiresAt` / `refreshToken` / `refreshExpiresAt` / `user`；`user` 即 `UserSessionDto`：`userId` / `userName` / `displayName` / `roles` / `permissions` / `appKeys` / `mustChangePassword` / `appKey` / `permissionsLocked`。`permissionsLocked` 仅在进入应用上下文时有意义，客户端据此决定是否还需要上传权限清单。
- 管理端 `/api/v1/admin/auth/login` 用同一个 `LoginRequest`，但返回 `AdminSessionResponse`（只有 `accessToken` / `expiresAt` / `user`）：刷新令牌改由 httpOnly + SameSite=Strict 的 Cookie `af_refresh_v2` 承载，`Path` 固定 `/api/v1/admin/auth`，`Secure` 跟随请求是否 HTTPS（`AdminAuthEndpoints.cs`）。旧名 `af_refresh` 仅在读不到新名时兼容续期一次，并在登录/刷新/登出时清除；重命名是为了绕开「HTTPS 部署留下的 Secure 旧 Cookie 无法被 HTTP 响应覆盖」的浏览器保护（表现为 HTTP 部署后 Edge/Chromium 每次刷新都要重登）。现象、定位与处置见仓库 README 的「HTTP ↔ HTTPS 切换与登录 Cookie（排障必读）」。

### 2.4 刷新（一次性 + 复用检测）

```bash
curl -s -X POST http://localhost:5080/api/v1/identity/refresh \
  -H 'Content-Type: application/json' -d '{"refreshToken":"afr_..."}'
```

- 响应 `RefreshTokenResponse`：`accessToken` / `expiresAt` / `refreshToken` / `refreshExpiresAt` / `user`。刷新令牌格式为 `afr_` + 32 随机字节的 Base64Url，库里只存 SHA-256 哈希，明文不回查。
- 每次刷新**换发新刷新令牌**，旧令牌立即标记 `rotated`；已轮换的令牌再次出现按泄露处理：同一 `familyId` 下全部活跃令牌标记 `reuse_detected` 并被吊销，本次返回 `114`（401）——合法持有者只能重新登录。缺失/非法/已过期返回 `113`，账号被禁用返回 `102`（403）。
- 刷新时**重新读库构建权限**，角色改权限后下一次刷新即生效；应用上下文随家族延续，新令牌继承记录里的 `appKey`，不会刷新后跨应用。当前应用已停用时刷新返回 `118`（403），应用重新启用后同一刷新令牌仍可使用。
- 吊销原因只有四种：`rotated` / `logout` / `revoked_by_admin` / `password_changed`。

### 2.5 登出与强制改密

```bash
curl -s -X POST http://localhost:5080/api/v1/identity/logout \
  -H 'Content-Type: application/json' -d '{"refreshToken":"afr_..."}'

curl -s -X POST http://localhost:5080/api/v1/identity/password/change \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"currentPassword":"Oper@20260101","newPassword":"Oper@20260202"}'
```

- 登出吊销该令牌所在的**整个家族**；令牌不存在也返回成功，客户端可安全重复调用。
- 登出同样走「吊销收口」并 bump 用户 `token_epoch`：这是**用户级**语义——该用户名下全部访问令牌（含其它设备/浏览器会话）在守卫窗口内一并 401，必须重新登录。多设备共存依赖的是各会话独立的刷新令牌，不是访问令牌豁免。
- 改密成功返回 `PasswordChangedResponse{changedAt, revokedSessions}`，并吊销本人**全部**活跃刷新令牌（原因 `password_changed`）+ bump epoch：其它设备旧访问令牌很快 401、下一次刷新即 `114`，必须重新登录。
- 口令策略：长度 8–128 且同时含字母与数字，不强制特殊字符；违规返回 `115`（400），当前口令不正确返回 `101`，新旧口令相同返回 `115`。
- `mustChangePassword` 只是提示位：服务端**不会**据此拒绝其它端点，客户端与管理台必须自己拦住（种子管理员该位为 `true`，改密后置 `false`）。

---

## 3. JWT 声明与 whoami

| 声明 | 短名 | 多条 | 来源 |
|---|---|---|---|
| 主体 id | `sub` | 否 | 用户 id / `apikey:{id}` |
| 名称 | `name` | 否 | 用户名或 `应用名 (密钥名)` |
| 角色 | `role` | 是 | 角色名（M2M 固定 `service`） |
| 权限码 | `perms` | 是 | 角色权限并集，`*` 表示通配 |
| 应用键 | `app` | 是 | 可访问应用（平台管理员为全部启用应用） |
| 当前应用 | `app_ctx` | 否 | 当前令牌固定的应用；平台上下文令牌没有此声明 |
| 主体类型 | `pkind` | 否 | `user` / `service` |
| 临时标记 | `is_temporary` | 否 | 单次密钥与扫码为 `true` |
| 令牌 id | `jti` | 否 | v7 guid |
| 机器码 | `machine` | 否 | 仅当登录/兑换时提供 |
| 令牌代号 | `epoch` | 否 | 用户级 `token_epoch` 快照；缺失按 `0`；吊销收口 bump 后旧令牌落后即 401（仅 `pkind=user` 参与对账） |

```bash
curl -s http://localhost:5080/api/v1/platform/whoami -H "Authorization: Bearer $TOKEN"
```

返回 `WhoAmIResponse`：`isAuthenticated` / `userId` / `userName` / `isService` / `isTemporary` / `appKey` / `roles` / `permissions` / `appKeys`。这是联调的第一站。

> **`MapInboundClaims` 坑**：宿主在 `src/Platform.Api/Auth/AuthenticationSetup.cs:44` 显式设为 `false`。一旦打开，`sub`/`perms` 会被改写成 `ClaimTypes.*` 的 URI，`HttpCurrentPrincipal` 全读不到，表现为「令牌明明有效却处处 403、`userId` 为 null」。自研宿主接入时同样必须关掉。校验侧还固定 `NameClaimType = name`、`RoleClaimType = role`、`ClockSkew` 30 秒，并校验签发者、受众与签名密钥。

---

## 4. 单次密钥（现场临时登录）

签发（权限 `identity.singlekey.issue`，支持 `Idempotency-Key`，作用域同名）：

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/identity/singlekeys \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: issue-20261030-001' \
  -d '{"durationMinutes":30,"deviceFingerprint":"line-1-pc"}'
```

响应 `SingleLoginKeyDto{key, expiresAt, durationMinutes}`：密钥是 6 位十进制（含前导 0），随机生成并查重、冲突最多重试 5 次，连续失败返回 `6`（409）。`durationMinutes` 为空取 `SingleLogin:DefaultDurationMinutes`（30），超出 `SingleLogin:MaxDurationMinutes`（120，即 2 小时）按上限钳制，最小 1 分钟；`deviceFingerprint` 为空表示**不限设备**，非空时只有同一指纹（区分大小写、逐字比较）能兑换。节点/安装实例绑定用 `machineCode`（16 位 Crockford Base32，格式非法 400）与 `installGuid`（不得为全零 Guid）：管理台「选择设备」即走这两列，因为服务端不存原始主标识、无法反推指纹，兑换请求本就携带它们。绑定列与指纹**逐列取与**——同时填写时都匹配才可兑换，留空即该列不约束。明文只在这条响应里出现一次；`single_login_keys` 表以密钥本身为主键，另存指纹、绑定机器码、绑定实例、签发人、`expires_at`、`used_at`、`attempt_count`，审计与日志不记密钥。

兑换（匿名）：

```bash
curl -s -X POST http://localhost:5080/api/v1/identity/singlekey/redeem \
  -H 'Content-Type: application/json' \
  -d '{"key":"123456","deviceFingerprint":"line-1-pc","machineCode":"008J4CT4ANK7F24S"}'
```

- **原子单次**：消费靠一条带条件的 UPDATE（`used_at IS NULL AND expires_at > now AND attempt_count < maxAttempts AND 指纹/绑定机器码/绑定实例逐列匹配`），受影响行数为 1 才算赢，多副本并发只有一个成功（`SingleLoginKeyService.cs:136`）。尝试上限 `SingleLogin:MaxAttempts`（默认 5）；任何绑定不匹配同样累计一次尝试，累计到上限后密钥作废。绑定不要求实例在线——比对的是兑换请求携带的设备身份。
- `machineCode` 非空时必须是 16 位 Crockford Base32（`O/I/L` 按 `0/1/1` 容错），格式不符直接 400 且**不消耗密钥**；`installGuid` 可选，除用于会话的设备绑定外，也是密钥的实例绑定列（密钥绑定时必须逐字相等）。
- 临时令牌有效期**独立取默认值**（`SingleLogin:DefaultDurationMinutes` 默认 30 分钟，上限 `MaxDurationMinutes` 120）：签发密钥时选的有效期只约束密钥本身，不再压缩兑换后的会话。响应 `TemporarySessionResponse{accessToken, expiresAt, user}`，**没有刷新令牌**，过期即失去访问。错误码 `105`/`107`/`108` 见第 7 节。
- 临时主体绑定密钥签发人：`sub`/`name`/`role`/`perms` 全部来自该用户当前会话（与口令登录共用 `UserSessionFactory`），`displayName` 为用户的显示名，`is_temporary` = `true`，`app` = 该用户可进入的首个应用，`machine` = 规范化后的 `machineCode`（只写入声明，不与密钥记录比对）。签发人账号被禁用/删除时兑换返回对应的身份错误。
- 客户端因此拿到签发人的真实角色与权限（管理员签发就是 `*`）。边界靠身份种类而不是权限集合：四个管理端点都挂了 `RequireNonTemporaryPrincipal()`，临时令牌即便权限齐全也返回 `102`（403），无法自我续期。

---

## 5. 扫码登录

会话状态机：`pending` →（审批）`approved` /（拒绝）`rejected` /（客户端放弃）`cancelled`；`pending` 且已过 `expiresAt` 时在读取时算作 `expired`（**不写库**，`expired` 不落表）。

```bash
# 1) 客户端发起（匿名）
curl -s -X POST http://localhost:5080/api/v1/identity/qr/sessions \
  -H 'Content-Type: application/json' \
  -d '{"deviceFingerprint":"line-1-pc","clientName":"1 号线操作台"}'

# 2) 客户端轮询（匿名；id 为标准带连字符 GUID）
curl -s http://localhost:5080/api/v1/identity/qr/sessions/$REQUEST_ID

# 3) 客户端放弃本次扫码（匿名、幂等；只取消仍 pending 且未过期的会话）
curl -s -X POST http://localhost:5080/api/v1/identity/qr/sessions/$REQUEST_ID/cancel

# 4) 管理端（identity.qr.approve）
curl -s -b admin.jar 'http://localhost:5080/api/v1/admin/identity/qr/sessions?status=pending&page=1&pageSize=20'
curl -s -b admin.jar http://localhost:5080/api/v1/admin/identity/qr/sessions/$REQUEST_ID
curl -s -b admin.jar -X POST http://localhost:5080/api/v1/admin/identity/qr/sessions/$REQUEST_ID/approve \
  -H 'Content-Type: application/json' -d '{"durationMinutes":120}'
curl -s -b admin.jar -X POST http://localhost:5080/api/v1/admin/identity/qr/sessions/$REQUEST_ID/reject
```

- 创建响应 `QrSessionCreatedDto{requestId, expiresAt, payload}`：`expiresAt` 取 `QrLogin:ExpireMinutes`（默认 10 分钟）；`payload` 是**管理台审批页的 https 链接** = `QrLogin:ApprovalUrlBase` + `/qr-login/` + 请求 id 的 **32 位无连字符（`N`）形式**，手机相机扫开即直达审批页（未登录会先跳登录再回来）。轮询 URL 用的是带连字符的标准 GUID。
- `QrLogin:ApprovalUrlBase` 必须显式配置成**手机浏览器能打开**的基址（部署脚本的 `QR_APPROVAL_URL_BASE`，留空时按证书 CN 或本机内网 IP + 浏览器访问端口推导后写回 `.env`）。不能靠请求推导：nginx 转发的 `Host` 不带端口，推出来的地址手机扫开连不上；配置留空才退回按当前请求的 `scheme://host` 兜底，那只够本机开发用。
- 轮询响应 `QrSessionStatusDto{requestId, status, createdAt, expiresAt, approvedAt, session}`：仅当 `status=approved` 且已签发的令牌尚未过期时 `session` 非空（`TemporarySessionResponse`，同上一节）。令牌过期后 `status` 仍是 `approved`，但 `session` 为 `null`——只能重新扫码。
- 审批 `durationMinutes` 为空取 `QrLogin:DefaultDurationMinutes`（30），上限 `QrLogin:MaxDurationMinutes`（120，即 2 小时）。临时令牌在**审批瞬间**按审批人的真实身份签发并落库，同一请求 id 之后每次轮询领取到的是同一枚。
- 管理端按 id 取单个会话 `GET /api/v1/admin/identity/qr/sessions/{id}`（权限同上）：手机扫码直达审批页时用它，不必先翻列表。
- 客户端取消 `POST /api/v1/identity/qr/sessions/{id}/cancel`（匿名、幂等）：仅当会话仍为 `pending` 且未过期时迁到 `cancelled`，之后管理端待审批列表不再出现、审批返回 `111`；已批准/已拒绝/已过期的会话原样返回、不改状态，也不会吊销已签发令牌。客户端离开扫码页、刷新二维码、关窗时都应调用，否则待审批单会一直留在管理台直到自然超时（10 分钟）。
- 管理端列表 `status` 只接受 `pending`/`approved`/`rejected`/`expired`/`cancelled`（其它值返回 `2`，400），`expired` 筛的是「仍为 pending 但已超时」的行；响应 `QrSessionSummaryDto` 含 `clientName`/`deviceFingerprint`/`createdByIp`/`approvedAt`/`approvedByName`。
- 并发审批由带 `status = 'pending' AND expires_at > now` 条件的状态迁移语句裁决（`QrLoginService.cs:200`）：两个审批人同时点击只有一个生效。会话落 `qr_login_sessions` 表，服务重启后待审批会话仍在。
- 审批/拒绝后服务端经 SignalR `/hubs/devices` 向发起会话的安装实例定向发送事件 `QrLoginApproved`（负载 `QrLoginApprovedNotification{requestId, approved}`；`installGuid` 优先，退而机器码，两者皆无则只靠轮询），同时向管理台 `/hubs/admin` 的 identity 域组广播 `QrSessionChanged`（负载 `AdminQrSessionChangedNotification{appKey, sessionId, status, occurredAt}`）：创建 `pending`、取消 `cancelled`、审批 `approved`、拒绝 `rejected` 各推一条；转 `expired` 仍由列表/详情的惰性判定呈现、**不发事件**。广播**只是提示**，推送失败只记日志、不影响已落库结果，管理台以失效回拉为准、客户端以轮询为准。错误码 `109`（404）/`110`（410）/`111`（409）见第 7 节。

---

## 6. 用户 / 角色 / 会话管理端点

| 方法 | 路径 | 权限码 | `Idempotency-Key` |
|---|---|---|---|
| GET | `/api/v1/admin/identity/users?page&pageSize&keyword&enabled` | `identity.user.read` | — |
| GET | `/api/v1/admin/identity/users/{id}` | `identity.user.read` | — |
| POST | `/api/v1/admin/identity/users` | `identity.user.manage` | 是（`identity.user.create`） |
| PATCH / DELETE | `/api/v1/admin/identity/users/{id}` | `identity.user.manage` | PATCH 否 |
| POST | `/api/v1/admin/identity/users/{id}/password/reset` | `identity.user.password.reset` | 否 |
| GET | `/api/v1/admin/identity/roles`（不分页）与 `roles/{id}` | `identity.role.read` | — |
| POST | `/api/v1/admin/identity/roles` | `identity.role.manage` | 是（`identity.role.create`） |
| PATCH / DELETE | `/api/v1/admin/identity/roles/{id}` | `identity.role.manage` | PATCH 否 |
| GET | `/api/v1/admin/identity/sessions?page&pageSize&userId` | `identity.session.read` | — |
| GET | `/api/v1/admin/identity/users/{id}/sessions` | `identity.session.read` | — |
| POST | `/api/v1/admin/identity/sessions/{id}/revoke` | `identity.session.revoke` | — |
| POST | `/api/v1/admin/identity/users/{id}/sessions/revoke` | `identity.session.revoke` | — |

单次密钥与扫码端点见第 4、5 节。身份域权限码取自 `src/Platform.Contracts/Permissions/PermissionCatalog.cs:22`（模块键 `identity`）：
`identity.user.read`(200)、`identity.user.manage`(210)、`identity.user.password.reset`(220)、`identity.role.read`(230)、
`identity.role.manage`(240)、`identity.session.read`(250)、`identity.session.revoke`(260)、`identity.singlekey.issue`(270)、`identity.qr.approve`(280)。

约束要点（全部在服务端强制，界面隐藏不算判据）：

- 用户名 `^[a-z0-9][a-z0-9._-]{2,63}$`；显示名 ≤256 字符；口令走同一套 `PasswordPolicy`；创建用户默认 `mustChangePassword=true`。
- 用户名重复 → `6`（409）；角色不存在 → `103`（404）；未知权限码 → `104`（400）。
- 不能禁用或删除当前登录账号、不能删除内置 `admin`：`118` / `6`。
- 角色名 `^[a-z][a-z0-9-]{1,63}$`；`scope` 只有 `platform` / `app`，应用级角色必须指定存在的应用键。
- 应用级角色的权限只在该角色绑定的 `appKey` 与当前令牌应用上下文一致时并入；平台级角色权限始终生效。
  登录/切换指定已停用应用返回 `118`（403），且新令牌不再携带其他应用的 app 角色权限。
- 内置角色（`isSystem`）不可删除（`6`）；仍有用户的角色不可删除（`117`，409）。
- 通配符 `*` 只允许内置 `platform-admin` 角色持有，其它角色申请返回 `118`。
- 两个 revoke 端点都会 bump 用户 `token_epoch`（吊销收口统一路径）并定向推送 `UserSessionRevoked`：
  单会话吊销带 `sessionId`，全量吊销按用户级发；管理台连接靠 `/hubs/admin` 的 `Touch` 上行
  携带 `exp`+`epoch` 被守卫踢线。重置口令（`password/reset`）同样 bump。
- 分页响应统一为 `PageResult<T>{items, page, pageSize, total, nextCursor}`。

---

## 7. 错误码（身份段 100–199）

| 码 | 成员 | HTTP | 触发点 |
|---|---|---|---|
| `100` | `IdentityUserNotFound` | 404 | 用户详情/改密/重置时用户不存在 |
| `101` | `IdentityInvalidCredentials` | 401 | 登录用户名或口令错、改密当前口令错 |
| `102` | `IdentityUserDisabled` | 403 | 登录/刷新时账号已禁用 |
| `103` | `IdentityRoleNotFound` | 404 | 角色详情/给用户授角色时角色不存在 |
| `104` | `IdentityPermissionUnknown` | 400 | 角色权限里出现未登记权限码 |
| `105` | `IdentitySingleKeyInvalid` | 401 | 密钥格式非法或库中不存在 |
| `106` | `IdentitySingleKeyExpired` | 401 | 密钥已过期 |
| `107` | `IdentitySingleKeyExhausted` | 401 | 密钥已使用或尝试数达上限 |
| `108` | `IdentitySingleKeyDeviceMismatch` | 401 | 设备指纹 / 机器码 / 安装实例与密钥绑定不符 |
| `109` | `IdentityQrSessionNotFound` | 404 | 扫码会话 id 不存在 |
| `110` | `IdentityQrSessionExpired` | 410 | 扫码会话待审批超时 |
| `111` | `IdentityQrSessionAlreadyUsed` | 409 | 重复审批/拒绝，或审批瞬间状态被改 |
| `112` | `IdentityTokenExpired` | 401 | 预留码：访问令牌过期实际由 JWT 挑战返回 `3` |
| `113` | `IdentityRefreshTokenInvalid` | 401 | 刷新令牌缺失/无效/已过期 |
| `114` | `IdentityRefreshTokenRevoked` | 401 | 刷新令牌已吊销（含复用检测触发） |
| `115` | `IdentityPasswordPolicyViolation` | 400 | 口令长度/字符构成不合规、新旧口令相同 |
| `116` | `IdentityAccountThrottled` | 429 | 登录失败达阈值，账号或 IP 被锁定 |
| `117` | `IdentityRoleInUse` | 409 | 删除仍有用户的角色 |
| `118` | `IdentityPermissionDenied` | 403 | 无权进入指定应用、自我禁用/删除、通配符越权 |

HTTP 状态由 `ApiErrorHttpStatus.Map` 唯一映射（`src/Platform.Kernel/Errors/ApiErrorHttpStatus.cs:37`），业务代码里不要手写状态码；客户端只认信封里的 `code`。

---

## 8. SDK 用法（`AppFramework.Platform.Client`）

```csharp
using AppFramework.Platform.Client;
using Platform.Contracts.Permissions;

var client = new PlatformClient(new PlatformClientOptions
{
    BaseUrl = "http://localhost:5080",
    ClientVersion = "3.2.0",          // X-Client-Version
    RuntimeIdentifier = "win-x64",    // X-Runtime-Identifier
});

// 1) 用户名口令登录（桌面端刷新令牌在响应体）
await client.SignInAsync(userName: "operator01", password: "Oper@20260101", appKey: "demo",
    deviceFingerprint: "line-1-pc", machineCode: "008J4CT4ANK7F24S");

// 2) 业务调用：401 且有刷新令牌时自动续期并**恰好重试一次**
var who = await client.GetWhoAmIAsync();

// 3) 凭据落盘 / 重启恢复：RefreshToken 是可写属性
SaveToCredentialStore(client.AccessToken, client.RefreshToken!);
client.RefreshToken = ReadFromCredentialStore();          // 回填后即可继续续期
client.UseAccessToken(stillValidAccessToken, expiresAt);  // 或接管外部令牌

// 4) 权限判定（* 视为全部；M2M 主体没有 user 上下文，恒为 false）
bool canReset = client.HasPermission(PermissionCatalog.IdentityUserPasswordReset);

// 5) 临时登录与服务令牌
var temp = await client.RedeemSingleKeyAsync("123456", "line-1-pc", machineCode: "008J4CT4ANK7F24S");
var service = await client.RequestServiceTokenAsync("demo", apiKey, scopes: null);

// 6) 扫码：推送只是提示，以轮询为准
var qr = await client.CreateQrLoginSessionAsync("line-1-pc", "1 号线操作台");
var session = await client.WaitForQrLoginApprovalAsync(qr.RequestId, TimeSpan.FromMinutes(9));
```

- `RequestServiceTokenAsync` / `RedeemSingleKeyAsync` / 扫码领取都**不会**设置刷新令牌（`ApplyCredentials(refreshToken: null)`）：临时与服务主体的令牌过期后只能重新走一次登录流程。
- `ChangePasswordAsync` 在 `finally` 里 `ClearCredentials()`：服务端已吊销全部会话，本地凭据同步作废，即使改密请求本身失败也会清空。`SignOutAsync` 无论服务端结果如何都清本地凭据。
- `WaitForQrLoginApprovalAsync` 默认 2 秒轮询；被拒绝抛码 `111`、过期抛码 `110`、超时抛 `TimeoutException`。`GetQrLoginSessionAsync` 用带连字符的 GUID 形式拼 URL。
- 失败抛 `PlatformClientException`，携带 `Code` / `StatusCode` / `TraceId`；非信封响应（反向代理的 HTML 错误页等）会被翻译成按 HTTP 状态推导的码（401→`3`、403→`4`、404→`5`、409→`6`、429→`7`，其余 `1`）。

---

## 9. 常见坑

- **口令节流**：`LoginThrottle:MaxFailuresBeforeLockout`（10）在 `TrackingWindowMinutes`（15）窗口内累计，触发即锁 `LockoutMinutes`（15）分钟并返回 `116`（429），提示语带剩余分钟数。账号与 IP 两个维度**同时**计数且落库（`login_throttles`），多副本有效；登录成功只清账号维度，IP 维度保留；锁定期间即使口令正确也被拒。
- **刷新令牌一次性**：每次刷新换发新令牌，旧令牌再被使用即按泄露处理并吊销整族（`114`）。不要把刷新令牌写日志、崩溃转储或明文落盘；`afr_` 前缀只为脱敏识别，不承载秘密。
- **临时主体不能签发凭据**：临时令牌继承真实用户（密钥签发人 / 扫码审批人）的角色与权限，甚至可能是 `*`，但**没有刷新令牌**；四个管理端点以身份种类拦截，仍返回 `102`（403），改角色也续不了期。
- **应用上下文不匹配**：令牌没有当前应用 → 应用级模块返回 `4`（403）；登录/切换时指定无权访问的应用 → `118`（403）。切换成功后服务端把应用键写回当前刷新令牌记录，Cookie 静默续期仍停在同一应用；刷新令牌缺失或不属于该用户时只换发访问令牌，不报错。
- **`MapInboundClaims`** 一旦被宿主打开，`sub`/`perms` 全部读不到（第 3 节）。
- **`Idempotency-Key` 是可选的主动权**：未携带就是普通请求；同键默认保留 24 小时并回放首次响应，并发在途返回 `6`（409），业务失败会释放占位允许同键重试，长度上限 256 字符。
- **改密即全场吊销**：自助改密与管理员重置都会吊销该用户全部活跃刷新令牌，客户端要预期下一次 401。
- **权限只在签发/刷新时快照**：访问令牌里的 `perms` 不随角色变化实时更新，但下一次刷新即重新读库生效。

---

## 10. 与管理台页面的对应关系

管理台左侧菜单与路由守卫共用一份事实源（`web/src/app/navigation.tsx`）：命中权限码才出现菜单项，
直接敲地址也会被守卫拦成「无访问权限」页。隐藏只是不打扰用户，越权请求仍由服务端 `RequirePermission` 拒绝。

| 页面 | 路径 | 需要的权限 | 写操作额外需要 |
|---|---|---|---|
| 总览 | `/overview` | 登录即可 | — |
| 密钥登录 | `/single-key` | `identity.singlekey.issue` | 同左 |
| 扫码登录 | `/qr-approval` | `identity.qr.approve` | 同左 |
| 扫码详情（手机直达） | `/qr-login/{requestId}` | `identity.qr.approve` | 同左 |
| 身份与权限 · 用户 | `/identity` | `identity.user.read` | `identity.user.manage` / `identity.user.password.reset` |
| 身份与权限 · 角色 | `/identity` | `identity.role.read` | `identity.role.manage` |
| 身份与权限 · 会话 | `/identity` | `identity.session.read` | `identity.session.revoke` |
| 修改口令 | `/password` | 登录即可 | — |

- **密钥登录与扫码登录是独立页面，不再是「身份与权限」的标签页**：内置 `engineer` 角色只拿到这两个入口码，
  拆开后现场工程师登录就能直接发密钥、批扫码，不必再被授予任何 `identity.*.read` 管理码。
  「身份与权限」页只剩用户 / 角色 / 会话三个标签页，三个读码都没有时显示引导提示。
- 两个页面的有效期都可选 15 / 30 分钟、1 / 2 小时，也可手填 1–120 分钟（与服务端上限一致，见第 11 节）。
- **手机端**：`/qr-login/{requestId}` 是二维码里带的地址。未登录会跳 `/login?returnTo=…`，登录后回到原请求；
  窄屏（<768px）下扫码列表渲染成卡片 + 整宽按钮而不是七列表格，导航收进抽屉。
  `QrLogin:ApprovalUrlBase` 必须配成手机能打开的内网地址，否则扫码会指向 localhost。
- 页面顶部「当前应用」切换走 `/api/v1/admin/auth/context`。

权限码目录也可用 `GET /api/v1/platform/permissions` 直接核对（匿名可读，字段 `code`/`moduleKey`/`displayNameKey`/`sort`/`platformAdminOnly`）。

---

## 11. 过期数据清理

`IdentityCleanupTask`（名称 `identity`）挂在 `RetentionWorker` 上，每个周期按 `Identity:Cleanup` 的
保留期分批删除：

| 表 | 删除条件 |
|---|---|
| `refresh_tokens` | `expires_at` 早于「现在 − `RefreshTokenRetentionDays`」 |
| `single_login_keys` | `expires_at` 早于「现在 − `SingleKeyRetentionHours`」 |
| `qr_login_sessions` | `expires_at` 早于「现在 − `QrSessionRetentionHours`」 |
| `login_throttles` | `window_start` 早于「现在 −（`TrackingWindowMinutes` + `LoginThrottleRetentionHours`）」，且未处于锁定期 |

只删已失效的行：未到期的令牌/密钥/会话、以及 `locked_until` 仍在未来的节流记录一律保留。
删除数量累加到 `platform_retention_deleted_rows_total{task="identity"}`。

## 12. 配置项

| 键 | 默认 | 说明 |
|---|---|---|
| `Jwt:Issuer` / `Jwt:Audience` | `appframework-platform` / `appframework` | 校验签发者与受众 |
| `Jwt:SigningKey` | 空（启动失败） | HMAC-SHA256，**至少 32 字符** |
| `Jwt:AccessTokenMinutes` / `Jwt:RefreshTokenDays` | 30 / 30 | 刷新令牌有效期最小按 1 天 |
| `Jwt:AllowDevTokenEndpoint` | `false` | 开发令牌端点开关，仅 Development 生效 |
| `AppContextGuard:CacheSeconds` | 30 | 应用停用状态的请求路径缓存窗口；`0` 表示每请求查库（集成测试用） |
| `UserEpochGuard:CacheSeconds` | 30 | 用户 `token_epoch` 的请求路径缓存窗口；`0` 表示每请求查库（集成测试用）。bump 在本副本经缓存失效即时生效 |
| `LoginThrottle:MaxFailuresBeforeLockout` | 10 | 窗口内失败上限 |
| `LoginThrottle:LockoutMinutes` / `TrackingWindowMinutes` | 15 / 15 | 锁定时长与失败计数窗口 |
| `SingleLogin:DefaultDurationMinutes` / `MaxDurationMinutes` / `MaxAttempts` | 30 / 120 / 5 | 单次密钥默认与上限时长（2 小时）、尝试上限 |
| `QrLogin:ExpireMinutes` / `DefaultDurationMinutes` / `MaxDurationMinutes` | 10 / 30 / 120 | 待审批存活与审批后时长（上限 2 小时） |
| `QrLogin:ApprovalUrlBase` | 空（退回按请求推导） | 二维码里的管理台基址，必须是手机能打开的地址；部署键 `QR_APPROVAL_URL_BASE` |
| `RateLimits:SingleLoginRedeemPerMinute` / `QrSessionPerMinute` | 10 / 60 | 兑换限流（IP）；创建与轮询共用额度 |
| `RateLimits:GlobalPerMinute` | 600 | 全局兜底窗口（IP）；设备集中走同一 NAT 出口时按需放大 |
| `RateLimits:M2MTokenPerMinute` | 30 | M2M 令牌续期限流（应用键分区，无键时退回 IP）；令牌 30min 无刷新，可支撑的设备数≈额度×30 |
| `RateLimits:IdentityLoginPerMinute` | 30 | 管理台登录/刷新限流（IP）；账号爆破由登录节流表兜住 |
| `Identity:Cleanup:BatchSize` | 500 | 每批删除行数（四类过期数据共用） |
| `Identity:Cleanup:RefreshTokenRetentionDays` | 7 | 过期刷新令牌保留天数（按 `expires_at` 计） |
| `Identity:Cleanup:SingleKeyRetentionHours` / `QrSessionRetentionHours` | 24 / 24 | 过期单次密钥、过期扫码会话保留小时数 |
| `Identity:Cleanup:LoginThrottleRetentionHours` | 24 | 节流记录冷却后再保留的小时数（最后失败超过「计数窗口 + 该值」才删） |
| `Argon2:MemorySizeKiB` / `Iterations` / `DegreeOfParallelism` | 65536 / 4 / 8 | 参数随每条哈希入库；调参只影响新哈希 |
| `Seed:Enabled` / `AdminUserName` / `AdminPassword` / `DemoAppKey` | `true` / `admin` / 空 / `demo` | 口令只从环境变量 `Seed__AdminPassword` 注入；为空跳过管理员创建 |

compose 部署的环境变量映射见 `deploy/env.example`：`JWT_SIGNING_KEY` → `Jwt__SigningKey`、`JWT_ACCESS_TOKEN_MINUTES` → `Jwt__AccessTokenMinutes`、`SEED_ADMIN_PASSWORD` → `Seed__AdminPassword`。

---

返回 [00-quickstart.md](00-quickstart.md) ｜ 下一篇 [02-versions.md](02-versions.md)
