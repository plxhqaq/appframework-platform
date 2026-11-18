# 快速接入（B7：全模块 + SDK + 示例 + Webhook/审计 + 可观测 + 一键部署）

> 本文对应当前进度 **B7（平台收口）**：平台级端点、模块自述、应用接入、用户名口令登录与 RBAC、
> 6 位单次密钥兑换临时令牌、扫码登录审批、上传票据直传与版本下发、
> 授权产品/授权主体/离线码/签名密钥与 ECDSA 快照、命名空间备份与下发 Ack、
> 文档集合/修订/服务端强制可见性与保留清理、Webhook 订阅与指数退避投递、审计查询、
> `/metrics` 与告警、`AppFramework.Platform.Client` SDK 与 `samples/ConsoleQuickstart`。
> 分模块的完整语义在 [`01-identity.md`](01-identity.md) ~ [`09-operations-drills.md`](09-operations-drills.md)。
> 本文所有命令都已在本仓验证通过。

---

## 0. 前置

| 组件 | 版本 | 说明 |
|---|---|---|
| .NET SDK | 10.0.403+ | `global.json` 已锁定；`rollForward: latestFeature` |
| PostgreSQL | 16+ | 只有 `/ready` 与后续业务端点需要；`/healthz` 与模块自述不需要 |
| Node.js / pnpm | 22 / 9 | 仅管理台需要 |
| Docker + Compose v2 | 最新 | 仅 `deploy/deploy.sh` 一键起栈需要 |
| curl / jq | — | 本文命令用它们解析响应 |

---

## 1. 起服务端（两种方式二选一）

### A. 本地直跑（开发首选）

```bash
# 1) 起一个 PostgreSQL（或用自己的实例）
docker run -d --name platform-pg -p 5432:5432 \
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=appframework_platform postgres:17-alpine

# 2) 跑 API（Development 已内置开发连接串、开发 JWT 签名密钥与种子口令）
dotnet run --project src/Platform.Api
```

> **Development 里没有内置的只有授权签名私钥**：不注入 `Licensing__SigningKeyPkcs8` 时
> 授权状态接口返回 `unmanaged`、离线码签发返回 `305`（见 §6.1）。这是刻意的 ——
> 私钥永远不进仓库，连开发环境也要自己生成一份。

> **数据库迁移**：Development 下 `Database:AutoMigrate=true`，启动时自动 `MigrateAsync`。
> 生产环境必须关掉自动迁移（多副本同时迁移会互相踩），由部署脚本显式执行。两种方式：
>
> ```bash
> # 方式一（推荐，容器/裸二进制同一入口）：跑完即退，不启 Web 服务
> dotnet run --project src/Platform.Api -- --migrate
> # 容器里：bash deploy/deploy.sh migrate（内部就是 compose run --rm api --migrate）
>
> # 方式二：dotnet-ef，需要在宿主机上装工具
> dotnet tool restore    # 首次：安装仓库锁定的 dotnet-ef 10.0.12
> dotnet ef database update \
>   --project src/Platform.Infrastructure \
>   --startup-project src/Platform.Api
> ```
>
> 新增迁移必须从 `Platform.Api` 启动：设计时工厂在 Api 工程里，才能把各模块的
> `IPlatformModelContributor` 一并加载；否则生成出来的迁移会漏掉模块表。

> **单副本约束（当前版本）**：启动时用 PostgreSQL 会话级 advisory lock 抢占唯一实例锁，
> 第二个共用同一数据库的进程会直接启动失败并打印锁号与原因，而不是带着重复投递跑起来。
> 原因：Webhook 投递没有行级认领，备份回执协调器与 SignalR 连接表都是进程内状态。
> 只有实现了投递行级认领 + SignalR 背板后才可设 `Deployment:RequireSingleInstance=false`。

### 管理台依赖（目录搬移后必读）

`web/node_modules` 里的 pnpm 符号链接记录的是**绝对路径**。仓库整体搬目录后必须重装一次，
否则 `vite`/`eslint` 会指向不存在的旧路径：

```bash
cd web
pnpm install --force     # 或删除 node_modules 后 pnpm install
pnpm verify              # tokens:check + lint + typecheck + test
```

### B. 一键部署（想验证部署产物时用）

```bash
bash deploy/deploy.sh up      # 首次生成 deploy/.env（数据库口令 + JWT 密钥 + 种子口令 + 授权签发密钥）：
                              # 预检端口 → 构建 → 起库 → 迁移 → 起 api → 等 /ready → 起管理台 → 打印现场信息汇总
```

脚本动作：`up` / `migrate` / `fix-pg-password` / `renew-cert` / `reset-admin-password` / `status` / `logs` / `health` / `down` / `uninstall` / `info`。
`up` 的顺序是「先探端口 → 起库并按 API 那条 TCP 路径验一次口令 → 再构建镜像 → 迁移 → 起 api → 等 `/ready`
→ 起管理台 → 等首页」：端口撞了（5432/8080 这类）和 `.env` 与数据卷口令对不上（28P01）这两类失败都跟镜像无关，
现在都在**构建之前**报出来，不用等一分多钟构建完才在「设置容器网络」时看到 `address already in use`，
也不用读一叠 Npgsql 栈才发现是 `.env` 被重生成过。报错里会直接给出对应选项与修法。
`SKIP_BUILD=1` 复用已有镜像。它**不会覆盖 `deploy/.env` 里已有的值**，只在留空时补写并告知。
结尾那份汇总（口令、keyId、指纹、脱敏后的密钥）事后随时可重取：`bash deploy/deploy.sh info [--show-secrets]`。

现场参数一律走 `.env` 或下面的选项（都会写回 `.env`，重跑 `up` 沿用，不用每次带；
**不要直接改 compose 文件**，升级覆盖文件时改动会丢）：

| 选项 | `.env` 键 | 默认 | 什么时候用 |
|---|---|---|---|
| `--api-port <n>` | `API_PORT` | 8080 | 宿主机 8080 已被现场 nginx / 别的 Web 占用 |
| `--api-bind <地址>` | `API_BIND_HOST` | 0.0.0.0 | 前面已有反代，让 API 只听 127.0.0.1 |
| `--pg-port <n>` | `POSTGRES_PUBLISH_PORT` | 5433 | 连回环上的 5433 也被占；只影响运维直连，API 不看它 |
| `--blobs-dir <绝对路径>` | `BLOB_STORAGE_HOST_PATH` | 空 = 具名卷 | 安装包/附件要放数据盘。脚本建目录并 chown 到 uid 1001；换位置**不自动搬既有文件**（会打印搬法） |
| `--use-existing-pg "<conn>"` | `USE_EMBEDDED_POSTGRES=false`、`POSTGRES_*` | 建容器库 | 机器上已跑着 PostgreSQL |
| `--web` / `--no-web` | `WEB_ENABLED` | 起管理台容器 | 只做接口对接、或坚持把静态资源挂在宿主 nginx 上 |
| `--web-port <n>` / `--web-https-port <n>` | `WEB_HTTP_PORT` / `WEB_HTTPS_PORT` | 8081 / 8443 | 这两个端口也被占了（默认值刻意避开 80/443，见下） |
| `--web-bind <地址>` | `WEB_BIND_HOST` | 0.0.0.0 | 管理台只留给本机反代对外 |
| `--https` / `--no-https` | `WEB_HTTPS` | https | 纯 http 只适合内网临时验证：口令不加密，刷新 Cookie 也拿不到 `Secure` |
| `--cert-dir <绝对路径>` | `WEB_CERT_DIR` | `/var/lib/appframework-platform/certs` | 证书放别处（现场 CA 签的也放这里，脚本认出非自签就不再动它） |
| `--server-name <名字>` | `WEB_SERVER_NAME` | 主机名 | 自签证书的 CN / SAN 里的 DNS 项要与浏览器访问的名字对得上 |
| `--days <n>` | `WEB_SSL_RENEW_DAYS` | 30 | 低于这个剩余天数才重签（1–365；调太大等于每次部署都换证书） |
| `--no-license-key` | — | 生成一对 | 不需要授权签发，整体保持 unmanaged |
| `--skip-preflight` | — | 预检 | 明知端口能用、要跳过探测（谨慎） |

```bash
bash deploy/deploy.sh up --api-port 18080                      # 对外端口挪开
bash deploy/deploy.sh up --api-bind 127.0.0.1 --api-port 18080 # 只给本机反代，反代 upstream 指 127.0.0.1:18080
bash deploy/deploy.sh up --blobs-dir /data/appframework-blobs  # 附件放数据盘
bash deploy/deploy.sh up --web-port 8082 --web-https-port 8444 # 管理台端口挪开
bash deploy/deploy.sh up --no-web                              # 不起管理台容器（下次 up --web 恢复）
```

手写 compose 也支持，两种模式两份文件：

```bash
cp deploy/env.example deploy/.env     # 填 POSTGRES_PASSWORD 与 JWT_SIGNING_KEY（≥32 字符）
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.postgres.yml \
  --env-file deploy/.env up -d --build                       # 嵌入库
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build
                                                             # 已有库：USE_EMBEDDED_POSTGRES=false
                                                             # 并填 POSTGRES_HOST/PORT/DB/USER/PASSWORD
```

管理台是再往上的两层，与库的两个模式自由组合（`deploy.sh` 就是按 `.env` 里这两个键拼 `-f` 的）：

```bash
  -f deploy/docker-compose.web.yml                           # 起管理台（http 8081）
  -f deploy/docker-compose.web.yml -f deploy/docker-compose.web.https.yml
                                                             # 起管理台 + https（8443，证书见 WEB_CERT_DIR）
```

手工敲这两层之前先把证书准备好：https 那份挂载 `WEB_CERT_DIR/web.crt` 与 `web.key`，
`deploy.sh up --https` 会在缺的时候自签一张；纯手工路线就自己放一对进去（`bash deploy/deploy.sh renew-cert`
也能签）。

镜像构建上下文由仓库根的 `.dockerignore` 限定：`obj/` 与 `bin/` 一律不进上下文。
带着 Windows 上 `dotnet restore` 产物建树（尤其 zip 整包拷到 Linux）时，`COPY src/` 会把镜像内
刚 restore 出来的资产文件盖成记录本机路径的那份，`--no-restore` 的 publish 随即报
`MSB4018: Unable to find fallback package folder 'D:\Program Files (x86)\...'`。

---

## 2. 第一步验证：活着 + 就绪

```bash
curl -s http://localhost:5080/healthz     # {"isSuccess":true,...,"data":{"status":"ok"}}
curl -s http://localhost:5080/ready       # 含 postgres 检查项；库没起时这里会报 Unhealthy
curl -s http://localhost:5080/metrics | head -5   # Prometheus 文本（Metrics:Enabled 缺省 true）
```

`/healthz` **刻意不查库**：库抖动不应该让容器被反复重启。要判断能不能接流量，看 `/ready`。
`/metrics` 只在设了 `Metrics:ScrapeToken` 时才要令牌（`X-Metrics-Token` 或 `Bearer`），指标与告警见 [`08-observability.md`](08-observability.md)。

---

## 3. 第二步：看模块清单

```bash
curl -s http://localhost:5080/api/v1/platform/modules
```

返回八个模块（`apps` / `identity` / `versions` / `licensing` / `devices` / `backups` / `documents` / `webhooks`），每个带：

- `key`：模块键，也是路由段的来源（`/api/v1/{key}`）
- `routes`：该模块挂载的路由前缀
- `requiredPermissions`：该模块声明的权限码（新增模块时 CI 会校验它们都已登记）

`apps` 永远排在首位 —— 其余七个模块的 `app_id` 隔离轴由它产生。
`version` 的四段式 `1.0.0.<提交数>` 里，最后一段是构建时 `git rev-list --count HEAD` 的提交总数
（`Platform.Api.csproj` 的 `ApplyGitCommitVersion` 目标写入 `Version` / `FileVersion` /
`InformationalVersion`），排查线上版本时直接读它。容器构建上下文不含 `.git`，
`deploy.sh` 会把提交数通过 `GIT_COMMIT_COUNT` 构建参数传给 `Dockerfile.api`；
直接跑 compose 时可用 `GIT_COMMIT_COUNT=$(git rev-list --count HEAD) docker compose ...` 指定，
未提供则退化为 `1.0.0.0`。

---

## 4. 第三步：登录拿令牌

管理端用**用户名口令**登录；刷新令牌由服务端写入 httpOnly + SameSite=Strict Cookie（`af_refresh_v2`，路径 `/api/v1/admin/auth`），响应体只返回 access token。
旧名 `af_refresh` 只做兼容读取与清理：HTTPS 部署期写入的 Secure 旧 Cookie 在 HTTP 下删不掉也覆盖不了，换新名才能让「HTTPS 切 HTTP」后的现场正常记住登录：

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

响应 `data.key` 是明文密钥，**只在响应里出现这一次**。库里 `single_login_keys` 以 6 位密钥本身作主键，
另存指纹、签发人、过期时间、兑换时间与尝试数；审计与日志都不记密钥。
（6 位数字 + 分钟级有效期 + 尝试上限 + 限流是这套短令牌能被接受的前提，长有效期请改用别的凭据。）

客户端兑换（匿名，限流 `RateLimits:SingleLoginRedeemPerMinute`，默认 10 次/分）：

```bash
curl -s -X POST http://localhost:5080/api/v1/identity/singlekey/redeem \
  -H 'Content-Type: application/json' \
  -d '{"key":"123456","deviceFingerprint":"line-1-pc","machineCode":"PC-001"}'
```

成功后返回 `accessToken` / `expiresAt` / `user`：主体是签发密钥的用户本人，
`user` 里的用户名、角色与权限与该用户口令登录一致；`whoami` 里 `isTemporary=true`，
管理端点仍按身份种类拒绝（临时令牌无法自我续期）。

消费语义是**原子单次**的（条件 `ExecuteUpdateAsync`：未使用、未过期、尝试数未满、指纹匹配），
同一密钥并发兑换只有一个成功。失败码：`105` 无效、`106` 已过期、`107` 已使用/尝试超限、`108` 设备不符。

### 4.2 扫码登录

1. 客户端发起（匿名，限流 `RateLimits:QrSessionPerMinute`，默认 60 次/分）：

```bash
curl -s -X POST http://localhost:5080/api/v1/identity/qr/sessions \
  -H 'Content-Type: application/json' \
  -d '{"deviceFingerprint":"line-1-pc","clientName":"1 号线操作台"}'
```

返回 `requestId`、`expiresAt`（默认 10 分钟）与 `payload`——`payload` 是管理台审批页的 https 链接
（`QrLogin:ApprovalUrlBase` + `/qr-login/` + 请求 id 的 `N` 形式），直接渲染成二维码，
现场工程师用手机相机扫开就能审批。基址要配成手机能访问到的地址，详见
[01-identity.md](01-identity.md) 第 5 节。

2. 客户端轮询 `GET /api/v1/identity/qr/sessions/{requestId}`，状态为
   `pending` / `approved` / `rejected` / `expired` / `cancelled`；`approved` 时响应带 `session.accessToken`（令牌过期后不再回传）。
   离开扫码页、刷新二维码或关窗时，客户端应调 `POST /api/v1/identity/qr/sessions/{requestId}/cancel`
   放弃本次扫码（匿名、幂等，只取消仍待审批的会话、不签发令牌），否则该单会在管理台一直可审批直到超时。

3. 管理端审批（需要 `identity.qr.approve`）：

```bash
curl -s -b admin.jar 'http://localhost:5080/api/v1/admin/identity/qr/sessions?status=pending'
curl -s -b admin.jar -X POST http://localhost:5080/api/v1/admin/identity/qr/sessions/$REQUEST_ID/approve \
  -H 'Content-Type: application/json' -d '{"durationMinutes":120}'
# 或拒绝
curl -s -b admin.jar -X POST http://localhost:5080/api/v1/admin/identity/qr/sessions/$REQUEST_ID/reject
```

错误码：`109` 会话不存在、`110` 已过期、`111` 已处理（重复审批/拒绝）。

审批通过时服务端经 SignalR `/hubs/platform` 广播 `QrLoginApproved`（事件名是 PascalCase 常量，
见 `PlatformHubEvents`），但这只是**提示**：
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
写回当前刷新令牌记录，后续 Cookie 静默续期仍停在该应用。当前账号无权进入该应用时返回 `403`/`118`
（`IdentityPermissionDenied`）。

> 别和另一个 403 混淆：如果拿**没有应用上下文的令牌**（平台管理员未进入任何应用、或 M2M 服务令牌）
> 去访问应用级端点，版本/授权模块的 `RequireAppKey` 直接抛 `403`/`4`（`Forbidden`），
> 与账号权限无关 —— 先按本节切上下文。

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

> **归档会让文件立即不可下载**（下载定位只认 `status == published`，返回 `404`/`205`）。
> 因此回滚顺序是「先发布正确版本，再归档坏版本」，别只归档 ——
> 半更新到坏版本的机器就再也拉不到它的文件了。演练见 [`09-operations-drills.md`](09-operations-drills.md) §三。

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
配合 `If-None-Match` 可让客户端重复检查零流量。

`check` 的三个语义容易踩：

- 渠道里**没有已发布版本**是错误（`404`/`212`），不是空清单；
- **最新已发布版本没有你请求的那个 `runtimeIdentifier` 产物**时返回 `hasUpdate=false` 且 `release=null`（不是错误）
  —— 这是「本运行时暂无发布物」，需要人工补产物而不是等更新；
- `hasUpdate` 只比较 `currentVersion` 与最新版本字符串是否**不相等**（不做语义化大小比较），
  所以传高了也算「有更新」，客户端自己决定装不装。

版本错误码：`200` 渠道不存在、`201` 渠道键已存在、`202` 版本不存在、`203` 未发布、`204` 同渠道版本号重复、
`205` 产物/文件不存在（含归档后下载）、`206` 清单与 blob 不符、`207` 票据无效、`208` 票据过期、
`209` 票据已消费、`210` 直传字节哈希不符、`211` `runtimeIdentifier` 不支持、`212` 渠道无已发布版本。
完整链路与发布校验规则见 [`02-versions.md`](02-versions.md)。

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
# 生成一份开发用私钥（生产请走 KMS/密码库，同样的两步）
openssl ecparam -name prime256v1 -genkey -noout -out license-key.pem
openssl pkcs8 -topk8 -nocrypt -in license-key.pem -outform DER -out license-key.pkcs8.der

export Licensing__SigningKeyPkcs8="$(base64 -w0 license-key.pkcs8.der)"
dotnet run --project src/Platform.Api
```

compose 栈对应 `deploy/.env` 里的 `LICENSING_SIGNING_KEY_PKCS8`（`env.example` 注了解释与生成命令）。
私钥只在启动时读取，换钥必须重启。

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

# 吊销：不可恢复；需要恢复只能重新签发。scope 缺省 all（在线 + 离线码），online 仅注销在线授权
curl -s -X POST http://localhost:5080/api/v1/admin/licensing/entitlements/$ID/revoke \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"reason":"客户退订","scope":"all"}'
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
避免本机时钟被回拨。吊销时服务端经 SignalR 广播 `LicenseChanged`（PascalCase 常量），这只是提示：
完全吊销时客户端回拉状态接口拿到新的 `revoked` 快照后锁定；仅在线注销（`scope=online`）返回
`unlicensed` + 空快照，客户端清除在线快照并回退本地离线码。

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

## 7. 第六步：备份与文档（B6）

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

# 下发：必须显式指定目标设备（可多选）；留空不再广播全部在线连接
# 同渠道硬门禁；force 只放宽主版本；currentHash 仅单设备时可用
curl -s -X POST http://localhost:5080/api/v1/admin/backups/$ID/push \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"targetDeviceIds":["<DEVICE_ID>"],"targetChannel":"default",
       "targetFrameworkVersion":"3.2.0","force":false}'

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

### 7.1 设备连接与按设备下发（S1–S4）

设备用带 `app` 声明的令牌连 SignalR `/hubs/devices`（WebSocket 走 `?access_token=<JWT>`），
首帧 `RegisterDevice`，随后按周期 `Heartbeat` / `ReportStationState`，收到 `ConfigPush` 后回
`AcknowledgeConfigPush`。设备身份由平台按 `(primaryIdentifier, installGuid)` 计算稳定键，
同一应用内重复注册复用同一条记录；顶号时旧连接收到 `SessionReplaced`。

```bash
# 管理端：看设备与在线状态（device.read）
curl -s "http://localhost:5080/api/v1/admin/devices?onlineStatus=online&keyword=line3" \
  -H "Authorization: Bearer $TOKEN"

# 按设备下发：targetDeviceIds 可多选；留空返回 400，应用组广播需显式 broadcastToApp=true
# 门禁：留空 400、停用 802/403、离线 803/409、运行中 804/409、跨设备未确认 407
curl -s -X POST http://localhost:5080/api/v1/admin/backups/$ID/push \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"targetDeviceIds":["<DEVICE_ID>"],"targetChannel":"default",
       "targetFrameworkVersion":"3.2.0","confirmCrossDevice":false}'
```

**设备通路走明文 HTTP**：现场设备连 API 的明文端口（部署默认 `--api-port 8080`；老端默认 8082
就按现场实际配），不要连管理台的自签 https（8443/8444）——客户端不信任自签证书，会直接连不上。
客户端备份上传/列表用 `/api/v1/client/backups`（`deviceId` 必填，默认 `device-backups` 命名空间）。
完整语义、错误码与迁移说明见 [`11-devices.md`](11-devices.md)。

---

## 8. 第七步：Webhook 与审计（B7）

Webhook 是**旁路**能力：订阅由各模块发布的事件（`version.released`、`license.revoked`、
`backup.pushed`、`document.uploaded`、`document.deleted`、`webhook.test`），
投递失败只进死信，绝不影响业务事务。审计查询按应用与动作回看谁在什么时候做了什么。

```bash
# 订阅（需要 webhook.manage；密钥只在创建/轮换时明文返回一次）
curl -s -X POST http://localhost:5080/api/v1/admin/webhooks \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: webhook-hook-001' \
  -d '{"url":"https://ops.example.com/hooks/platform","eventTypes":["version.released","license.revoked"],"description":"运维通知"}' \
  > webhook.json
jq -r '.data.secret' webhook.json     # 形如 whsec_...，只在创建/轮换响应里出现这一次
SUB_ID=$(jq -r '.data.subscription.id' webhook.json)

# 事件目录与签名细节（本地可先用 test 端点验签通路）
curl -s -X POST "http://localhost:5080/api/v1/admin/webhooks/$SUB_ID/test" \
  -H "Authorization: Bearer $TOKEN" | jq .      # {"data":{"deliveryCount":1}}

# 投递台账（只可按订阅过滤；状态在返回条目里，dead 的可用 redeliver 手动重放）
curl -s -H "Authorization: Bearer $TOKEN" "http://localhost:5080/api/v1/admin/webhooks/deliveries?pageSize=20" \
  | jq '.data.items[] | {eventType,status,attempts,lastStatusCode,lastError}'
curl -s -X POST "http://localhost:5080/api/v1/admin/webhooks/deliveries/$DELIVERY_ID/redeliver" -H "Authorization: Bearer $TOKEN"

# 审计查询（需要 audit.read，且该权限标了 platformAdminOnly —— 只有平台管理员角色能查）
curl -s -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5080/api/v1/admin/audit-logs?page=1&pageSize=20&action=versions.release.archive&result=failure" | jq '.data.items[]'
```

签名头三件套 `X-Platform-Signature` / `X-Platform-Timestamp` / `X-Platform-Event`
（外加 `X-Platform-Delivery` 作为投递唯一号，接收端**必须**按它去重）：

```
X-Platform-Signature: sha256=hex(Lower)(HMAC-SHA256(secret, "{timestamp}.{rawBody}"))
```

退避 `30s → 1m → 2m → 4m → 8m → 16m → 32m`（上限 1 小时，含首次共 8 次尝试，超时 10 秒，2xx 记成功）。
**当前版本只支持单副本**：启动守卫会用 PostgreSQL advisory lock 把第二个实例挡在启动阶段
（见 §0 数据库迁移后的说明）。单副本内投递器可保持 `Webhooks:DispatcherEnabled=true`；
若关闭了守卫（`Deployment:RequireSingleInstance=false`），必须自行保证多副本下只有一个投递器
且回执/推送可跨副本路由。
完整语义、验签示例与错误码（`700–704`）见 [`06-webhooks-and-audit.md`](06-webhooks-and-audit.md)。
上面这些步骤（建订阅、拿密钥、轮换、测试、查台账与重投）在管理台「Webhook」页都有对应入口，见 §15。

---

## 9. 第八步：SDK 与示例（一条命令跑通全链路）

`samples/ConsoleQuickstart` 用真实服务端把「存活 → 模块 → 登录 → 自述 → 刷新 → 检查更新 →
授权快照验签 → 文档清单」八步走完，是 15 分钟接入的验收脚本：

```bash
# 1) 打包本地 NuGet 源（SDK 与契约包都进 artifacts/packages，nuget.config 已映射 AppFramework.*）
dotnet pack src/Platform.Contracts/Platform.Contracts.csproj -c Release -p:PackageId=AppFramework.Platform.Contracts -o artifacts/packages
dotnet pack src/AppFramework.Platform.Client/AppFramework.Platform.Client.csproj -c Release -o artifacts/packages

# 2) 起服务端（§1），然后跑示例
export PLATFORM_URL=http://localhost:5080
export PLATFORM_USERNAME=admin
export PLATFORM_PASSWORD=Admin@12345
export PLATFORM_APP_KEY=demo
dotnet run --project samples/ConsoleQuickstart
```

`PLATFORM_PRODUCT_KEY` / `PLATFORM_MACHINE_CODE` / `PLATFORM_CHANNEL` / `PLATFORM_VERSION` 都有缺省：
产品键缺省 `erp-pro`，渠道缺省 `stable`，机器码缺省用本机名派生（形态和管理台授权页一致），
干净服务端下第 6/7 步会打印「建渠道 / 建产品」的请求体并跳过，而不是报错退出。
把样例打印的机器码拿去 §6.2–6.3 建产品并签发（或给产品设 `trialDays` 让首次状态查询自动发试用），
再跑一次就能看到第 7 步「状态 active，放行 True」。

最后一行输出 `✓ quickstart 通过` 即链路可用。最小客户端代码只有五行：

```csharp
using var client = new PlatformClient(new PlatformClientOptions { BaseUrl = "http://localhost:5080" });
await client.SignInAsync("admin", "Admin@12345", "demo");
var update = await client.CheckUpdateAsync("stable", "1.2.2", "win-x64");
var ok = LicenseVerifier.Verify(await client.GetLicenseStatusAsync("erp-pro", machineCode), "demo", "erp-pro", machineCode);
```

要点（展开在 [`src/AppFramework.Platform.Client/README.md`](../../src/AppFramework.Platform.Client/README.md) 与 [`07-openapi.md`](07-openapi.md)）：

- 业务端点 `401` 时自动用刷新令牌续期并**恰好重试一次**（不递归）；
- 桌面端重启后把安全存储里的刷新令牌赋回 `client.RefreshToken` 即可静默续命；
- 下载走流式 + 增量哈希校验 + `*.tmp` 原子改名，`304` 返回 `false`；
- `LicenseVerifier` 只信 ECDSA-P256-SHA256 且必须绑定 `appKey`/`productKey`/`machineCode`，`unmanaged` 一律拒；
- `PlatformClientException` 带 `Code` / `StatusCode` / `TraceId`，`HasPermission` 与平台管理员的 `*` 通配同语义。

---

## 10. 第九步：验证令牌声明

```bash
TOKEN=... # 上一步的 accessToken
curl -s http://localhost:5080/api/v1/platform/whoami -H "Authorization: Bearer $TOKEN"
```

对着返回逐项看：`userId`（`sub`）、`userName`（`name`）、`roles`（`role`）、`permissions`（`perms`）、`appKey`/`appKeys`（`app`）、`isTemporary`、`isService`。

这一步是 JWT 声明链路的第一站：如果 `userId` 是 `null`，几乎一定是 `MapInboundClaims` 被打开了（会把 `sub` 改写成 `ClaimTypes.NameIdentifier` 的 URI），本仓已在 `AuthenticationSetup` 里显式关掉。

---

## 11. 权限码与错误码

```bash
curl -s http://localhost:5080/api/v1/platform/permissions
```

返回平台权限码描述符（`code` / `moduleKey` / `displayNameKey` / `sort` / `platformAdminOnly` / `description` / `moduleLabel`）。
带 `?appKey=xxx` 时并入该应用通过权限清单声明的权限；客户端专属权限不再由平台硬编码，由各应用自行声明。
管理台权限树、角色编辑与 API Key 选择器都消费这份合并目录。
应用权限清单的锁定/解锁需要 `apps.permissions.manage`；内置 `platform-admin`、`app-admin`
（应用管理员）与 `engineer`（现场工程师）角色持有该码。

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
| 700–799 | Webhook |
| 800–899 | 设备 |

HTTP 状态码由 `ApiErrorHttpStatus.Map` 唯一映射，**不要**在业务代码里手写 `StatusCode`。

权限码里的 `*` 是通配：**平台管理员角色带 `*`**，`HasPermission` 与 SDK 同语义（未知码也判为已授权）。
`audit.read`、应用与角色的增删改这类标了 `platformAdminOnly` 的码只发给平台管理员，
给业务角色逐条显式授权才是正常形态。

---

## 12. 统一返回形状

所有端点（含 401/403/429/异常）都是同一个形状：

```json
{ "isSuccess": false, "code": 101, "message": "用户名或密码错误", "data": null, "traceId": "0HN..." }
```

`traceId` 与服务端日志的 `RequestId` 对齐 —— 报障时先要这个值。

---

## 13. 契约包与 SDK 包（客户端仓库消费）

```bash
# 本地源目录：nuget.config 已把 AppFramework.* 指向 artifacts/packages
dotnet pack src/Platform.Contracts/Platform.Contracts.csproj -c Release \
  -p:PackageId=AppFramework.Platform.Contracts -o artifacts/packages
dotnet pack src/AppFramework.Platform.Client/AppFramework.Platform.Client.csproj -c Release \
  -o artifacts/packages
```

`AppFramework.Platform.Contracts` 内含 DTO、错误码、权限码目录、Hub 契约与分页类型；
`AppFramework.Platform.Client` 是薄 SDK（令牌 + 自动续期 + 下载 + `LicenseVerifier`）。
两者版本号都由 `Directory.Build.props` 的 `PlatformContractsVersion` / `PlatformClientVersion` 单点控制，
当前 `0.5.0-preview.1`。该版本起新增匿名扫码会话取消（`CancelQrLoginSessionAsync` / `cancelled` 状态）；
`0.3.0-preview.1` 起 M2M 令牌响应与会话携带 `permissionsLocked`，且不再导出
`ClientPermissionCatalog`（客户端权限改由应用权限清单声明，见 §11）。客户端仓库应**精确 pin 版本**（不用 `*` 或范围），破坏性变更必须主版本 +1。
在线文档（OpenAPI / Scalar）与包的完整清单见 [`07-openapi.md`](07-openapi.md)。

---

## 14. 可观测与部署

```bash
bash deploy/deploy.sh up        # 生成 .env（随机密钥 + 授权签发密钥）→ 预检端口与库口令 → 构建 → 迁移 → 起 api → 等 /ready → 起管理台 → 等首页
bash deploy/deploy.sh health    # healthz / ready / metrics / scalar / openapi 一次打完（启用了管理台会再多打一条）
bash deploy/deploy.sh status    # compose ps
bash deploy/deploy.sh logs      # 最近 api 日志（可带服务名：logs web / logs postgres）
bash deploy/deploy.sh migrate   # 只跑迁移（compose run --rm api --migrate）
bash deploy/deploy.sh down      # 停栈（不删卷）
bash deploy/deploy.sh uninstall # 开发期推倒重来：删容器/网络/卷（含 pgdata 整库与 blobs）、镜像、.env、自签证书（--yes 免确认）
bash deploy/deploy.sh info      # 只读重打现场信息汇总（脱敏）；--show-secrets 打全文
bash deploy/deploy.sh fix-pg-password   # 只 ALTER ROLE 把 pgdata 卷里的角色口令对到 .env，数据不动
bash deploy/deploy.sh renew-cert [--force]  # 自签证书到期前重签（--force 点名换一张）
bash deploy/deploy.sh reset-admin-password  # 把 admin 的口令对回 .env 里那份引导口令（旧会话全部作废）
```

- 现场要改的参数（端口、绑定地址、附件目录、库）都做成了 `up` 的选项并写回 `.env`，见上面 B 节的表；
  **不要直接改 compose 文件**，升级覆盖文件时改动会丢。
- 撞过 `28P01: password authentication failed for user "platform"` 的话原因基本只有一个：
  `.env` 被删过或重新生成过，而 `pgdata` 卷还在——`initdb` 只在空卷上跑一次，之后改 `.env` 不会跟着改
  库里的角色口令。现在 `up` 会在**构建之前**用 API 那条 TCP 路径先验一次并把这件事说清楚；
  修法是 `bash deploy/deploy.sh fix-pg-password`（只改角色口令，不动数据、不改 `.env`，
  走容器内 unix socket 所以不需要旧口令）。这个库真不要了、想连部署产物一起清掉，用
  `bash deploy/uninstall.sh`（同 `deploy.sh uninstall`：卷、镜像、`.env`、自签证书一并清，
  下一把 `up` 是全新现场；外部库模式默认不 DROP 别人的库，只打印该执行的 SQL，
  确需连库一起清时显式加 `--drop-external-db`）。

- 登录报「用户名或口令错误」而 `info` 里明明印着引导口令：种子的口令只在 admin 账号**不存在**时写入，
  所以 `.env` 重新生成过而数据卷还留着（或已经按提示改过密）就会这样，而且它不会自己好。
  `bash deploy/deploy.sh reset-admin-password` 把库里那份强行对回 `.env`（`.env` 为空则先生成再写回），
  同时作废该账号全部会话并清掉登录节流——连续试错 10 次会锁 15 分钟，这件事它也一并解决。
  排查用的原始证据在两边：`logs api` 里搜「身份种子」（有没有跳过创建），库里 `users` 表有没有那行 `admin`。
- `deploy/.env` 首次生成后**不会被覆盖已有值**，但留空的密钥会就地补写并告知：`POSTGRES_PASSWORD` /
  `JWT_SIGNING_KEY` / `SEED_ADMIN_PASSWORD` / `LICENSING_SIGNING_KEY_PKCS8`（ECDSA P-256，同一次生成，
  公钥与 keyId 由它派生后写回，用 `info` 查）。不想启用授权：`up --no-license-key`，
  或 `.env` 里 `LICENSING_GENERATE_KEY=false`——授权模块整体按 unmanaged 运行，客户端不受限。
  私钥只要非空，重跑部署绝不重新生成（换钥会让已签发的快照与离线码全部失效）。
- 机器上已有 PostgreSQL 时不用改 yml，也不用等镜像拉完才发现 5432 被占：
  `bash deploy/deploy.sh up --use-existing-pg "Host=...;Port=...;Database=...;Username=...;Password=***"`
  会切到外部库模式（`USE_EMBEDDED_POSTGRES=false`，不建容器、不动那套库）。宿主机自装的库要写
  `host.docker.internal`——容器里的 127.0.0.1 是容器自己，脚本会代填这个别名。仍用容器库就只挪
  发布端口 `POSTGRES_PUBLISH_PORT`（默认 5433，且只发布到 127.0.0.1）。
- `SKIP_BUILD=1 bash deploy/deploy.sh up` 复用现有镜像；`API_PORT` 改对外端口。
- 管理台（前端）默认一起部署成容器：`WEB_ENABLED`（`--web` / `--no-web`）与 `WEB_HTTPS`（`--https` /
  `--no-https`）两个开关各对应一份附加 yml，`WEB_HTTP_PORT` / `WEB_HTTPS_PORT` 默认 **8081 / 8443**。
  刻意避开 80/443：现场机器上往往已经有一套 nginx 在代理别的应用的站点，本栈不碰它，也不在 443 上抢
  `default_server`（那会把别人的站点顶掉）。三种走法：换一对空闲端口、`--web-bind 127.0.0.1` 由宿主反代
  对外、或 `--no-web` 后把 `deploy/nginx/site.d/` 里对应那份的 location 抄进现有站点。
- https 用的是自签证书（397 天，SAN 带主机名 + `WEB_SERVER_NAME` + 本机内网 IP），装在
  `WEB_CERT_DIR`（默认 `/var/lib/appframework-platform/certs`，非 root 建不出来会自动退到
  `deploy/certs/` 并写回 `.env`）。root + systemd 的机器上 `up` 会顺手装一个每天 06:17 检查一次的定时器，
  剩余有效期低于 `WEB_SSL_RENEW_DAYS`（默认 30 天）才重签，重签后只 `nginx -s reload`（不重启容器，
  已开着的页面不受影响）。`--no-cert-timer` 跳过安装，脚本会打印该自己安排的命令。
  换证书会让所有浏览器记住的「继续前往」例外作废，每台机器各点一次；不想吃这一步就把现场 CA 签的
  `web.crt` / `web.key` 放进同一个目录——脚本认出非自签后只报到期时间，不再覆盖、也不装定时器。
- 设备端（扫码、更新下载）继续走明文：客户端没有配置信任自签证书，硬赶上 https 会直接连不上。
  https 站点上 `/api`、`/hubs` 在 80 端口照常通，只有浏览器路径才 301 到 https 端口。
- 内网机器访问不到 npm registry 时：在有外网的机器上构建好管理台镜像，再
  `docker save appframework-platform-web | docker load` 搬过去，或 `SKIP_BUILD=1 bash deploy/deploy.sh up`。
- 指标清单、Prometheus 抓取与告警规则：[`08-observability.md`](08-observability.md)。
- 备份恢复 / 密钥轮换 / 版本回滚的演练脚本：[`09-operations-drills.md`](09-operations-drills.md)。

---

## 15. 管理台

开发形态（vite dev，改代码即时生效）：

```bash
cd web
pnpm install
pnpm dev            # http://localhost:5173，/api 自动代理到 http://localhost:5080
```

交付形态不用手工挂静态资源：`bash deploy/deploy.sh up` 会把构建好的管理台连同 nginx 反代一起起来
（默认 `https://<内网 IP>:8443`，纯 http 是 8081），详见 14 节。此时登录地址就是那个端口，
`pnpm dev` 的 5173 只在本机开发时用。

> 前端始终与 API **同源**部署：刷新令牌是 httpOnly Cookie（`SameSite=Strict` + 固定 Path），
> `Secure` 标记由 API 按 `X-Forwarded-Proto` 决定。所以不要给管理台配 `VITE_API_BASE_URL` 指向另一个源——
> 跨源要 CORS + `SameSite=None` + 真的 https，登录会拿不到刷新令牌（表现为过一阵子被踢回登录页）。

用种子管理员 `admin` / `Admin@12345` 登录；首次登录会跳转强制改密，改密后全部会话被吊销，需要重新登录。
登录后总览页是仪表盘：系统状态、运行平台、运行时间、CPU/内存占用（含最近采样趋势）、
已连接设备、平台信息与当前主体，数据来自 `GET /api/v1/platform/dashboard`（10 秒轮询）。
左侧菜单按权限裁剪，直接敲无权地址会被路由守卫拦住：
「密钥登录」（`/single-key`，`identity.singlekey.issue`）与「扫码登录」（`/qr-approval`，`identity.qr.approve`）
是两个独立页面，内置 `engineer` 角色只有这两个码也能正常干活，有效期最长可设 2 小时；
手机扫二维码直达 `/qr-login/{requestId}`，窄屏下审批列表是卡片 + 整宽按钮。
「身份与权限」页只剩用户 / 角色 / 会话三个标签页
（`identity.user.read`、`identity.role.read`、`identity.session.read`）。
「版本与渠道」页（`versions.channel.*` / `versions.release.*` / `versions.artifact.upload`）
支持新建/编辑渠道、选文件后浏览器端算 SHA-256 并直传、创建并发布、查看产物清单与归档；
「设备」页（`device.read` / `device.manage` / `device.restart`）按关键字/在线/分支筛选，详情里看机器码、
心跳、报警与工位，可改名/启停与重启在线设备；
「配置备份」页的下发表单用「选择在线设备」打开设备表格（关键字/分支筛选、多选、跨页保留，
默认勾选备份来源设备；留空会被拒绝，跨设备仍需勾选确认），下发后按设备展示逐台回执，
「文档」页支持填写/修改「适用版本」并在列表与详情中展示；
「授权」页（`license.product.*` / `license.issue` / `license.revoke` / `license.offlinecode.issue` /
`license.signingkey.manage`）按标签页提供授权产品、授权主体（签发/续期/吊销）、离线码台账
（复制/删除）与签名密钥（当前指纹、轮换同步、退役密钥）；
「审计」页（`audit.read`）按应用 / 动作 / 主体 / 结果 / 关键字与时间区间分页查询，详情里给 `traceId`；
「Webhook」页（`webhook.read` / `webhook.manage`）在当前应用下新建与编辑订阅、轮换签名密钥（明文只出现一次）、
发起测试投递，并在投递台账里按订阅过滤、展开看负载与错误、对失败与死信手动重投；
页面顶部的「当前应用」切换走 4.3 的上下文端点。

改主题：右上角色系下拉 + 模式切换（8 族 × 3 模式 = 24 套）。
**不要手改** `web/src/styles/antd-theme.ts`：它由 `design/tokens.json` 生成，改色板请改 JSON 后执行：

```bash
node tools/gen-tokens.mjs          # 生成
node tools/gen-tokens.mjs --check  # 校验（CI 用这个）
```

---

## 16. 本地质量门（与 CI 一致）

```bash
dotnet build Platform.slnx -c Release --disable-build-servers -warnaserror
dotnet test  Platform.slnx -c Release --no-build --disable-build-servers
dotnet format Platform.slnx --verify-no-changes --no-restore
node tools/gen-tokens.mjs --check

cd web
pnpm install --frozen-lockfile
pnpm verify          # tokens:check + lint + typecheck + test
pnpm build
```

---

## 附录：端点全景（按落地批次）

| 阶段 | 端点（前缀省略 `/api/v1`） |
|---|---|
| B0–B1 | `/platform/*`、`/apps/token`、`/admin/apps*` |
| B2–B3 | `/identity/login`、`/identity/refresh`、`/identity/logout`、`/identity/password/change`、`/admin/auth/*`（含 `/context`）、`/admin/identity/users*`、`/admin/identity/roles*`、`/admin/identity/sessions*`、`/identity/singlekey/redeem`、`/identity/qr/sessions*`、`/admin/identity/singlekeys`、`/admin/identity/qr/sessions*` |
| B4 | `/admin/versions/channels*`、`/admin/versions/releases*`、`/admin/versions/uploads*`、`/client/versions/check`、`/client/versions/{channel}/{version}/manifest`、`/client/versions/download/{sha256}` |
| B5 | `/admin/licensing/products*`、`/admin/licensing/entitlements*`、`/admin/licensing/offline-codes*`、`/admin/licensing/signing-keys*`、`/client/licensing/status`、`/client/licensing/offline/verify`、`/client/licensing/keys` |
| B6 | `/admin/backups*`（含 `/{id}/download`、`/{id}/push`）、`/client/backups*`（含按设备上传/列表）、`/admin/documents*`、`/client/documents/manifest`、`/client/documents/{id}*` |
| B7 | `/admin/webhooks`（含 `/{id}`、`/{id}/secret/rotate`、`/{id}/test`、`/deliveries`、`/deliveries/{id}/redeliver`）、`/webhooks/info`、`/admin/audit-logs`、`/metrics`、`/openapi/v1.json`、`/scalar/v1` |
| S1–S4 | `/hubs/devices`（设备注册/心跳/工位/回执）、`/admin/devices*`（含 `/{id}`、`/{id}/restart`） |

约定不变：路由 `/api/v1/{module}/...`，管理端与客户端入口分离（`/admin/*` 与 `/client/*`），服务端强制可见性。
每个模块还有一个 `/{module}/info` 自述端点（匿名，返回版本、权限码与路由前缀）。
