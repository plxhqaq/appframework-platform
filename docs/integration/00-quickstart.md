# 快速接入（B2：身份与权限）

> 本文对应当前进度 **B2**：平台级端点、模块自述、应用接入、用户名口令登录与 RBAC。
> 版本、授权、备份、文档的服务端点在 B4–B6 落地后增补 `02-versions.md` ~ `05-documents.md`。
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

---

## 5. 第四步：验证令牌声明

```bash
TOKEN=... # 上一步的 accessToken
curl -s http://localhost:5080/api/v1/platform/whoami -H "Authorization: Bearer $TOKEN"
```

对着返回逐项看：`userId`（`sub`）、`userName`（`name`）、`roles`（`role`）、`permissions`（`perms`）、`appKey`/`appKeys`（`app`）、`isTemporary`、`isService`。

这一步是 JWT 声明链路的第一站：如果 `userId` 是 `null`，几乎一定是 `MapInboundClaims` 被打开了（会把 `sub` 改写成 `ClaimTypes.NameIdentifier` 的 URI），本仓已在 `AuthenticationSetup` 里显式关掉。

---

## 6. 权限码与错误码

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

## 7. 统一返回形状

所有端点（含 401/403/429/异常）都是同一个形状：

```json
{ "isSuccess": false, "code": 1001, "message": "用户名或密码错误", "data": null, "traceId": "0HN..." }
```

`traceId` 与服务端日志的 `RequestId` 对齐 —— 报障时先要这个值。

---

## 8. 契约包（客户端仓库消费）

```bash
dotnet pack src/Platform.Contracts/Platform.Contracts.csproj -c Release \
  -p:PackageId=AppFramework.Platform.Contracts -o artifacts/packages
```

产物 `AppFramework.Platform.Contracts.0.1.0-preview.1.nupkg` 内含 DTO、错误码、权限码目录、Hub 契约与分页类型。
客户端仓库应**精确 pin 版本**（不用 `*` 或范围），破坏性变更必须主版本 +1。

---

## 9. 管理台

```bash
cd web
pnpm install
pnpm dev            # http://localhost:5173，/api 自动代理到 http://localhost:5080
```

用种子管理员 `admin` / `Admin@12345` 登录；首次登录会跳转强制改密，改密后全部会话被吊销，需要重新登录。
登录后总览页展示当前主体与平台模块；「身份与权限」页按 `identity.*.read` 权限展示用户、角色、会话三个标签页。

改主题：右上角色系下拉 + 模式切换（8 族 × 3 模式 = 24 套）。
**不要手改** `web/src/styles/antd-theme.ts`：它由 `design/tokens.json` 生成，改色板请改 JSON 后执行：

```bash
node tools/gen-tokens.mjs          # 生成
node tools/gen-tokens.mjs --check  # 校验（CI 用这个）
```

---

## 10. 本地质量门（与 CI 一致）

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
| S1 | `POST /identity/login`、`POST /identity/refresh`、`POST /identity/logout`、`/apps*`、`/admin/users*`、`/admin/roles*` |
| S2 | `POST /identity/single-key`、`POST /identity/qr/*`（发起 / 状态 / 审批） |
| S3 | `POST /versions/check`、`GET /versions/{channel}/{version}/manifest`、`GET /download/*` |
| S4 | `POST /licenses/status`、`POST /licenses/offline`、`/admin/licenses*` |
| S5 | `/backups*`、`/documents*` |

约定不变：路由 `/api/v1/{module}/...`，管理端与客户端入口分离（`/admin/*` 与 `/client/*`），服务端强制可见性。
