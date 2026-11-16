# AppFramework Platform

AppFramework 通用服务提供平台：模块化单体（.NET 10 + PostgreSQL）+ React 管理台。
八个模块（`apps` / `identity` / `versions` / `licensing` / `devices` / `backups` / `documents` / `webhooks`）共用一套
统一返回、权限码、审计与幂等基础设施，任一模块都可以按清单摘除。

## 目录

```
src/
  Platform.Contracts/      跨端契约（DTO、错误码、权限码）→ 打成 nupkg 供客户端 pin 版本
  AppFramework.Platform.Client/  桌面客户端 SDK（令牌 + 自动续期 + 下载 + 授权快照验签）
  Platform.Kernel/         内核（模块契约、统一返回、权限、认证、中间件）
  Platform.Infrastructure/ 基础设施（EF Core、Blob、审计、幂等、迁移）
  Platform.Workers/        后台任务（幂等/审计/保留清理、Blob 孤儿扫描、增量包摄取）
  Platform.Modules.*/      八个业务模块
  Platform.Api/            宿主（端点装配、认证、限流、OpenAPI、托管静态资源）
tests/
  Platform.UnitTests/      纯逻辑单元测试
  Platform.ContractTests/  跨端契约测试（错误码/权限码唯一性与分段）
  Platform.IntegrationTests/ 数据库集成测试（EF InMemory）
  Platform.PostgresTests/  真实 PostgreSQL 测试（Testcontainers，需 Docker，CI 独立 job）
samples/
  ConsoleQuickstart/       一条命令跑通全链路的验收脚本（SDK 的消费示例）
web/                       React 18 + Ant Design 5 + Vite 管理台
design/                    设计令牌单一源（tokens.json）
deploy/                    Dockerfile（API + 管理台）/ compose（API、可选嵌入库、可选管理台）/
                           deploy.sh / nginx 站点模板 / systemd 定时器 / 环境变量样例
docs/integration/          接入与运维文档（00 quickstart ~ 11 设备与按设备下发）
tools/                     设计令牌生成与校验脚本
artifacts/packages/        本地 NuGet 输出（nuget.config 里 AppFramework.* 的源）
```

## 环境

| 组件 | 版本 |
|---|---|
| .NET SDK | 10.0.403+（`global.json` 锁定） |
| PostgreSQL | 16+（17 已在 compose 验证） |
| Node.js / pnpm | 22 / 9.15.9（`web/package.json` 锁定 `packageManager`） |

## 快速开始

```bash
# 1) 数据库
docker run -d --name platform-pg -p 5432:5432 \
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=appframework_platform postgres:17-alpine

# 2) API（Development 自动迁移；生产关闭 Database:AutoMigrate）
dotnet run --project src/Platform.Api

# 3) 管理台（开发用 vite dev；交付形态见下面「一键部署」，管理台会连 nginx 容器一起起）
cd web && pnpm install && pnpm dev
```

服务默认监听 `http://localhost:5080`，OpenAPI 文档与 Scalar 在线调试在 `/scalar` 下。详细的端点验证步骤见
[`docs/integration/00-quickstart.md`](docs/integration/00-quickstart.md)。

## 数据库迁移

```bash
# 新增迁移（必须从 Platform.Api 启动，才能装配全部模块的模型贡献者；统一 Release）
dotnet tool restore
dotnet ef migrations add <Name> \
  --project src/Platform.Infrastructure \
  --startup-project src/Platform.Api \
  --configuration Release

# 执行迁移：开发用 AutoMigrate，生产显式执行
dotnet run --project src/Platform.Api -- --migrate     # 跑完即退，不启 Web 服务
bash deploy/deploy.sh migrate                          # 容器里同一条路径
dotnet ef database update \
  --project src/Platform.Infrastructure \
  --startup-project src/Platform.Api
```

设计时工厂位于 `src/Platform.Api/Persistence/PlatformApiDesignTimeFactory.cs`，它会装配全部模块的
`IPlatformModelContributor`。**不要**把工厂移回 Infrastructure：那样生成出来的迁移只包含横切表。

生产环境由部署流程显式执行迁移，`Database:AutoMigrate` 保持 `false`（多副本同时迁移会冲突）。
**默认仍按单副本部署**：启动时用 PostgreSQL advisory lock 抢占唯一实例锁，第二个共用同一数据库的
进程会直接启动失败并打印原因。增量包摄取已经改成行级认领（`FOR UPDATE SKIP LOCKED` + 租约，
多副本同时跑 Worker 不会重复摄取，见 `tests/Platform.PostgresTests`），但 Webhook 投递没有行级认领、
回执与 SignalR 推送仍是进程内状态，扩容前先补这两项再关闭 `Deployment:RequireSingleInstance`。
EF 迁移**只向前**：没有 down-migration 流程，含破坏性 DDL 的版本回滚前必须先做全量备份。

## 一键部署

```bash
bash deploy/deploy.sh up        # 首次生成 deploy/.env（数据库口令 + JWT 密钥 + 种子口令 + 授权签发密钥），
                                # 预检端口 → 构建 → 起库 → 迁移 → 起 api → 等 /ready → 起管理台 → 打印现场信息汇总
bash deploy/deploy.sh health    # healthz / ready / metrics / scalar / openapi 一次打完（启用了管理台会再多打一条）
bash deploy/deploy.sh info      # 只读重打上面那份汇总（凭据脱敏），不碰 docker
bash deploy/deploy.sh renew-cert  # 自签证书剩余有效期低于阈值才重签，签完只 reload nginx（不重启容器）
```

动作：`up` / `migrate` / `fix-pg-password` / `renew-cert` / `reset-admin-password` / `status` / `logs` / `health` / `down`（不删卷）/ `uninstall`（开发期推倒重来）/ `info`。
`logs` 可带服务名（`logs web` 只看管理台）。
`SKIP_BUILD=1` 复用镜像。已存在的 `.env` 不会被覆盖，只补写留空的密钥。
顺序是「探端口 → 起库并验一次库口令 → 构建 → 迁移 → 起 api → 等 `/ready` → 起管理台 → 等首页」：端口冲突与
`.env`／数据卷口令不一致（`28P01`）这两类跟镜像无关的失败，现在都在构建**之前**就报出来，
并直接给出修法。其中口令不一致用 `bash deploy/deploy.sh fix-pg-password` 修（只 `ALTER ROLE`
把卷里那个角色的口令对到 `.env`，数据不动、`.env` 不改；走容器内 unix socket，所以不需要旧口令）。

「汇总里印的引导口令进不去管理台」是同一类的另一种不一致：种子的口令**只在 admin 账号不存在时**写入
（改过口令的人不该被下一次重启盖回去），所以 `.env` 被删过/重新生成过而数据卷还留着、或者已经按提示
改过密之后，`info` 印出来的那份就对不上库里那把 Argon2 哈希。把它强行对回（同时作废该账号全部会话、
清掉登录节流；`.env` 里为空时先生成一个再写回）：

```bash
bash deploy/deploy.sh reset-admin-password
```

它跑一个一次性容器（`docker compose run --rm --no-deps api --reset-admin-password`），口令只走
`Seed__AdminPassword` 环境变量，不进命令行参数（argv 会留在 `ps` 与 `docker inspect` 里）。

开发期要把环境推倒重来（彻底清空数据库与部署产物，再全新部署一次）：

```bash
bash deploy/uninstall.sh          # 交互确认；脚本/CI 里加 --yes
bash deploy/deploy.sh up
```

`uninstall` 与 `down` 的分界是「环境状态留不留」：`down` 只停容器，卷、`.env`、证书、镜像都留着；
`uninstall` 删除本栈的容器/网络/卷（嵌入库 `pgdata` 整库、附件 `blobs`）、本栈镜像、`deploy/.env`、
自签证书与重签定时器，下一把 `up` 重新生成口令/密钥/证书。`--keep-images` / `--keep-env` / `--keep-certs`
分别保留镜像、`.env`、证书；`--purge-blobs` 才会连 `--blobs-dir` 指向的宿主机附件目录一起删
（默认只删具名卷，不会擅自清空数据盘上的目录）。外部库模式默认不 DROP 别人的库，只打印该在那套 PG 上
执行的 `DROP DATABASE` 语句；开发机确实要连库一起清时显式加 `--drop-external-db`（只 DROP/重建 `.env`
里那一个库）。

现场要改的参数一律走选项或 `.env`，**不用改 compose 文件**（那几份 yml 与仓库保持一致，
升级时直接覆盖文件才不出冲突）。选项都会写回 `deploy/.env`，下次重跑 `up` 沿用，不必重复带：

| 选项 | 写进 `.env` 的键 | 默认 | 说明 |
|---|---|---|---|
| `--api-port <n>` | `API_PORT` | 8080 | 宿主机对外端口；容器内恒为 8080，客户端配的服务地址要跟着变 |
| `--api-bind <地址>` | `API_BIND_HOST` | `0.0.0.0` | `127.0.0.1` = 只交给本机反代，公网端口由反代对外提供 |
| `--pg-port <n>` | `POSTGRES_PUBLISH_PORT` | 5433 | 嵌入库发布到宿主机 127.0.0.1 的运维直连端口，API 不看它 |
| `--blobs-dir <绝对路径>` | `BLOB_STORAGE_HOST_PATH` | 空（具名卷 `blobs`） | 附件与安装包改存宿主机目录（放数据盘时用），脚本会建目录并 chown 到 uid 1001 |
| `--use-existing-pg "<conn>"` | `USE_EMBEDDED_POSTGRES=false` + `POSTGRES_*` | 建容器库 | 复用现场 PostgreSQL，见下 |
| `--web` / `--no-web` | `WEB_ENABLED` | 起 | 管理台容器起不起；`--no-web` = 静态资源与反代自己安排 |
| `--web-port <n>` | `WEB_HTTP_PORT` | 8081 | 管理台 http 端口；开 https 时它只做 301 跳板与设备通路 |
| `--web-https-port <n>` | `WEB_HTTPS_PORT` | 8443 | 浏览器实际访问的 https 端口（301 也跳它） |
| `--web-bind <地址>` | `WEB_BIND_HOST` | `0.0.0.0` | `127.0.0.1` = 管理台只留给宿主反代对外 |
| `--https` / `--no-https` | `WEB_HTTPS` | https | 站点模板选哪份；切换只重建容器，不重新构建镜像 |
| `--cert-dir <绝对路径>` | `WEB_CERT_DIR` | `/var/lib/appframework-platform/certs` | `web.crt` / `web.key` 放这里，容器只读挂载；建不出来会自动退到 `deploy/certs/` |
| `--server-name <名字>` | `WEB_SERVER_NAME` | 主机名 | 自签证书的 CN 与 SAN 里的 DNS 项 |
| `--days <n>` | `WEB_SSL_RENEW_DAYS` | 30 | 剩余有效期低于这个天数才重签（1–365） |
| `--no-cert-timer` | — | 装 | root + systemd 的机器上默认装每天检查一次的定时器，用这个跳过 |
| `--force` | — | — | 只给 `renew-cert` 用：点名重签一张（非自签的证书会被拒绝覆盖） |
| `--no-license-key` | — | 生成 | 授权模块整体保持 unmanaged |
| `--skip-preflight` | — | 预检 | 跳过端口占用预探测（明知端口能用时） |
| `--show-secrets` | — | 脱敏 | `up`/`info` 的汇总里打印 JWT 密钥、库口令、授权私钥全文 |

`info` 的汇总末尾会把这批键与对应选项一并列出来，现场照着改即可。

端口这类冲突现在都在构建之前探出来（原本要等镜像构建完才在「设置容器网络」时报
`address already in use`），报错会附上占用方那一行 `ss` 输出和可用的选项：

```bash
bash deploy/deploy.sh up --api-port 18080            # 8080 被现场 nginx / 别的 Web 占着
bash deploy/deploy.sh up --api-bind 127.0.0.1        # 前面已有反代，API 只听回环
bash deploy/deploy.sh up --pg-port 5434              # 5433 也被占（少见）
bash deploy/deploy.sh up --web-port 8082 --web-https-port 8444   # 管理台那两个端口也要挪（常见于本机已有别的容器占着 8081）
bash deploy/deploy.sh up --web-bind 127.0.0.1        # 管理台不直接对外，由宿主 nginx 挂出去
```

机器上已经跑着 PostgreSQL 时（撞 5432 是现场最常遇到的失败）：

```bash
bash deploy/deploy.sh up --use-existing-pg "Host=host.docker.internal;Port=5432;Database=platform;Username=platform;Password=***"
```

这会写 `.env`（`USE_EMBEDDED_POSTGRES=false`）并且不再创建数据库容器；容器里的 127.0.0.1 是容器自己，
所以连宿主机自装的库必须走 `host.docker.internal`（compose 里已配 `host-gateway`），脚本会自动改写。
仍想用容器库就只挪发布端口：`--pg-port 5434`（它只发布到 127.0.0.1，API 不看它）。

## 管理台（前端容器）与 HTTPS

`up` 默认在 API 之后再起一个 nginx 容器（`docker-compose.web.yml`，开 https 再叠
`docker-compose.web.https.yml`）。`Dockerfile.web` 两段式：`node:22-alpine` 里 `pnpm install --frozen-lockfile`
+ `vite build`，产物拷进 `nginx:1.27-alpine`。它对浏览器提供静态资源，并把 `/api`、`/hubs`、`/healthz`、
`/ready`、`/scalar`、`/openapi` 同源反代到 API 容器（站点模板在 `deploy/nginx/site.d/`，改完要重新构建镜像；
而开关 https 与换证书都只重建/重载容器，不重新构建）。

- **必须同源**：刷新令牌是 httpOnly Cookie（`SameSite=Strict` + 固定 Path），`Secure` 标记由 API 按
  `X-Forwarded-Proto` 决定。所以别在管理台里配 `VITE_API_BASE_URL` 指向另一个源——跨源要 CORS +
  `SameSite=None` + 真的 https，登录会静默拿不到刷新令牌。
- **默认端口是 8081/8443，不是 80/443**：现场机器上往往已经有一套 nginx 在代理别的应用。本栈不碰它，
  也不在 443 上抢 `default_server`（那会把别人的站点顶掉）。要并进去就抄 `site.d` 里对应那份的 location，
  或者 `--no-web` 自己挂静态资源；只想本机可达用 `--web-bind 127.0.0.1`。
- **默认开 https**：纯 http 等于口令与令牌在内网裸跑，而且刷新 Cookie 拿不到 `Secure`。证书是自签的
  （397 天，`SAN` 带主机名 + `localhost` + `WEB_SERVER_NAME` + 本机内网 IP；浏览器认 SAN，不认 CN）。
  设备端不受影响：`/api`、`/hubs` 在明文端口上照常通，只有浏览器路径 301 到 https 端口——
  客户端没有配置信任自签证书，硬把它们赶上 https 会直接连不上。
- **续期**：`bash deploy/deploy.sh renew-cert` 只在剩余有效期低于 `--days`（默认 30 天）时才重签，
  重签后只 `nginx -s reload`，不重启容器、不惊动已开着的页面。root + systemd 的机器上 `up` 会顺手装一个
  每天 06:17 检查一次的定时器（`deploy/systemd/` 那两份模板渲染而来，`--no-cert-timer` 跳过）。
  没有 systemd 或非 root 时脚本会打印该自己安排的命令。重签会让所有浏览器记住的「继续前往」例外作废，
  每台机器各点一次——不想吃这一步就用现场 CA 签一张放进 `WEB_CERT_DIR`，脚本认出非自签后只报到期时间、
  不再覆盖，也不装定时器。
- **内网机器装不了 npm 包时**：在有外网的机器上把镜像构建好，再 `SKIP_BUILD=1 bash deploy/deploy.sh up`
  复用现有镜像，或 `docker save appframework-platform-web | docker load` 把镜像搬过去。
  改前端代码要重新构建（`pnpm install` 那一层只在 lockfile 变化时才重装依赖）；只换证书不需要构建。

## 构建与测试

```bash
dotnet build Platform.slnx -c Release --disable-build-servers -warnaserror
dotnet test  Platform.slnx -c Release --no-build --disable-build-servers
dotnet format Platform.slnx --verify-no-changes --no-restore

node tools/gen-tokens.mjs --check
cd web && pnpm install --frozen-lockfile && pnpm verify && pnpm build
```

CI（`.github/workflows/ci.yml`）执行同一组命令，并额外产出 `AppFramework.Platform.Contracts`
与 `AppFramework.Platform.Client` 两个 nupkg。

`tests/Platform.PostgresTests` 需要本机 Docker（Testcontainers 起 `postgres:17-alpine`）：
没有 Docker 时该项目的用例在发现阶段就带 `Skip`，不影响 `dotnet test Platform.slnx`；
想显式关掉（例如 CI 自检）设 `POSTGRES_TESTS=0`。CI 里它们被拆成独立的 `postgres` job
（主 job 用 `--filter "FullyQualifiedName!~Platform.PostgresTests"` 排除）。

拉不到 Docker Hub 的内网机器可以直接指到现有实例（要求账号有 CREATEDB 权限）：

```bash
POSTGRES_TEST_CONNECTION='Host=127.0.0.1;Port=5432;Username=postgres;Password=<pwd>;Database=postgres' \
  dotnet test tests/Platform.PostgresTests/Platform.PostgresTests.csproj -c Release
```

测试会新建 `platform_tests_<随机后缀>` 库并跑完全部迁移，进程退出时 `DROP ... WITH (FORCE)`；
不会碰连接串里那个库。这组用例覆盖 jsonb 查询、并发唯一约束、`SKIP LOCKED` 认领与迁移落地，
是 InMemory 测不到的部分。

## 客户端接入

```bash
dotnet pack src/Platform.Contracts/Platform.Contracts.csproj -c Release \
  -p:PackageId=AppFramework.Platform.Contracts -o artifacts/packages
dotnet pack src/AppFramework.Platform.Client/AppFramework.Platform.Client.csproj -c Release \
  -p:PackageId=AppFramework.Platform.Client -o artifacts/packages

PLATFORM_PASSWORD=Admin@12345 dotnet run --project samples/ConsoleQuickstart   # 全链路验收
```

版本由 `Directory.Build.props` 的 `PlatformContractsVersion` / `PlatformClientVersion` 单点控制；
客户端仓库精确 pin，不用 `*` 或范围。

## 密钥与配置

- JWT 签名密钥、数据库连接串、授权签名私钥一律通过环境变量 / Secret 注入，
  仓库内 `appsettings.Development.json` 只放**开发专用**占位值。
- 生产启动会校验 JWT 签名密钥长度（≥32 字符）；不满足直接启动失败，而不是降级运行。
- `Licensing:SigningKeyPkcs8`（P-256 PKCS#8 DER 的 Base64）缺失时授权状态接口返回 `unmanaged`、
  离线码签发返回 `305`；私钥只在启动时读取，换钥必须重启。
- `Metrics:ScrapeToken` 一旦设置，`/metrics` 就要求 `X-Metrics-Token`（或 `Bearer`）。
- `Deployment:RequireSingleInstance`（默认 `true`）**把平台锁死在单副本**：启动时用 PostgreSQL
  会话级 advisory lock 互斥，第二个实例启动即失败。增量包摄取已是行级认领、可多副本并行；
  但关闭守卫之前仍必须先实现 Webhook 投递的行级认领与 SignalR 背板，否则多副本会重复外呼、丢备份回执。
- `Webhooks:DispatcherEnabled` 在单副本里保持 `true`；关闭守卫的多副本部署只能有一个副本为 `true`。
- Webhook 出站地址默认拒绝环回/私网/链路本地/保留地址（创建时与每次投递前各校验一次）；
 内网回调必须显式列入 `Webhooks:AllowedHosts`（主机名、`*.` 通配子域、IP 或 CIDR，逗号分隔，
  容器里用 `WEBHOOKS_ALLOWED_HOSTS`）。
- Blob 存储默认落在 `data/blobs`，容器里挂命名卷。
- **磁盘规划**：包字节按 `versions/{appKey}/{sha256}` 内容寻址长期保留（`RetainRawPackage` 默认保留原包），
  上传票据预留取 `max(MaxFileSizeBytes, MaxPackageSizeBytes)`；同一块盘上还要留
  `Versions:MinFreeDiskBytes`（默认 5 GiB）的余量，低于它上传返回 `227`/507、摄取直接失败。
  孤儿 Blob 由保留任务扫描，`Versions:BlobSweepDryRun` 默认 `true`，先看报告再关掉。
- 后台任务都在 `Platform.Workers`：保留清理每 600 s，增量包摄取按 `Versions:IngestionConcurrency`
  并行，Blob 扫描受 advisory lock 约束单实例执行。

## 文档索引

| 文档 | 内容 |
|---|---|
| [`docs/integration/00-quickstart.md`](docs/integration/00-quickstart.md) | 15 分钟全链路验证、错误码/权限码分段、质量门 |
| [`01-identity.md`](docs/integration/01-identity.md) | 令牌链路、单次密钥、扫码审批、用户/角色/会话 |
| [`02-versions.md`](docs/integration/02-versions.md) | 渠道、上传票据、发布与归档、增量摄取、保留与孤儿回收、下载与缓存语义 |
| [`03-licensing.md`](docs/integration/03-licensing.md) | 产品/授权主体/试用与席位、ECDSA 快照、离线码、密钥轮换 |
| [`04-backups.md`](docs/integration/04-backups.md) | 命名空间备份、下发与 Ack、兼容矩阵 |
| [`05-documents.md`](docs/integration/05-documents.md) | 集合/修订/服务端强制可见性、保留清理 |
| [`06-webhooks-and-audit.md`](docs/integration/06-webhooks-and-audit.md) | 事件目录、HMAC 签名、退避与死信、审计查询 |
| [`07-openapi.md`](docs/integration/07-openapi.md) | OpenAPI/Scalar 覆盖范围与限制、两个 nupkg 与 SDK 入口 |
| [`08-observability.md`](docs/integration/08-observability.md) | 探针、`/metrics` 清单（含 `versions_*`）、Prometheus 告警规则、日志与 traceId |
| [`09-operations-drills.md`](docs/integration/09-operations-drills.md) | 演练手册：备份恢复 / 密钥轮换 / 版本回滚 / 容量与故障 |
| [`src/AppFramework.Platform.Client/README.md`](src/AppFramework.Platform.Client/README.md) | SDK 用法与边界 |

## 仓库搬移后

`web/node_modules` 的 pnpm 符号链接保存绝对路径，整仓搬目录后需重装：

```bash
cd web && pnpm install --force
```
