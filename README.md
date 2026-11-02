# AppFramework Platform

AppFramework 通用服务提供平台：模块化单体（.NET 10 + PostgreSQL）+ React 管理台。
七个模块（`apps` / `identity` / `versions` / `licensing` / `backups` / `documents` / `webhooks`）共用一套
统一返回、权限码、审计与幂等基础设施，任一模块都可以按清单摘除。

## 目录

```
src/
  Platform.Contracts/      跨端契约（DTO、错误码、权限码）→ 打成 nupkg 供客户端 pin 版本
  AppFramework.Platform.Client/  桌面客户端 SDK（令牌 + 自动续期 + 下载 + 授权快照验签）
  Platform.Kernel/         内核（模块契约、统一返回、权限、认证、中间件）
  Platform.Infrastructure/ 基础设施（EF Core、Blob、审计、幂等、迁移）
  Platform.Workers/        后台任务（过期幂等记录/审计日志清理）
  Platform.Modules.*/      七个业务模块
  Platform.Api/            宿主（端点装配、认证、限流、OpenAPI、托管静态资源）
tests/
  Platform.UnitTests/      纯逻辑单元测试
  Platform.ContractTests/  跨端契约测试（错误码/权限码唯一性与分段）
  Platform.IntegrationTests/ 数据库集成测试（EF InMemory）
samples/
  ConsoleQuickstart/       一条命令跑通全链路的验收脚本（SDK 的消费示例）
web/                       React 18 + Ant Design 5 + Vite 管理台
design/                    设计令牌单一源（tokens.json）
deploy/                    Dockerfile / compose / deploy.sh / 环境变量样例
docs/integration/          接入与运维文档（00 quickstart ~ 09 演练）
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

# 3) 管理台
cd web && pnpm install && pnpm dev
```

服务默认监听 `http://localhost:5080`，OpenAPI 文档与 Scalar 在线调试在 `/scalar` 下。详细的端点验证步骤见
[`docs/integration/00-quickstart.md`](docs/integration/00-quickstart.md)。

## 数据库迁移

```bash
# 新增迁移（必须从 Platform.Api 启动，才能装配全部模块的模型贡献者）
dotnet tool restore
dotnet ef migrations add <Name> \
  --project src/Platform.Infrastructure \
  --startup-project src/Platform.Api

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
EF 迁移**只向前**：没有 down-migration 流程，含破坏性 DDL 的版本回滚前必须先做全量备份。

## 一键部署

```bash
bash deploy/deploy.sh up        # 首次生成 deploy/.env（随机强口令 + JWT 密钥 + 种子口令），起 pg → 迁移 → 构建 → 起 api → 等 /ready
bash deploy/deploy.sh health    # healthz / ready / metrics / scalar / openapi 一次打完
```

动作：`up` / `migrate` / `status` / `logs` / `health` / `down`（不删卷）。
`SKIP_BUILD=1` 复用镜像，`API_PORT` / `POSTGRES_PORT` 改端口。已存在的 `.env` 不会被覆盖。

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
- `Webhooks:DispatcherEnabled` **多副本时只能有一个副本为 true**（投递器没有行级抢占）。
- Blob 存储默认落在 `data/blobs`，容器里挂命名卷。

## 文档索引

| 文档 | 内容 |
|---|---|
| [`docs/integration/00-quickstart.md`](docs/integration/00-quickstart.md) | 15 分钟全链路验证、错误码/权限码分段、质量门 |
| [`01-identity.md`](docs/integration/01-identity.md) | 令牌链路、单次密钥、扫码审批、用户/角色/会话 |
| [`02-versions.md`](docs/integration/02-versions.md) | 渠道、上传票据、发布与归档、下载与缓存语义 |
| [`03-licensing.md`](docs/integration/03-licensing.md) | 产品/授权主体/试用与席位、ECDSA 快照、离线码、密钥轮换 |
| [`04-backups.md`](docs/integration/04-backups.md) | 命名空间备份、下发与 Ack、兼容矩阵 |
| [`05-documents.md`](docs/integration/05-documents.md) | 集合/修订/服务端强制可见性、保留清理 |
| [`06-webhooks-and-audit.md`](docs/integration/06-webhooks-and-audit.md) | 事件目录、HMAC 签名、退避与死信、审计查询 |
| [`07-openapi.md`](docs/integration/07-openapi.md) | OpenAPI/Scalar 覆盖范围与限制、两个 nupkg 与 SDK 入口 |
| [`08-observability.md`](docs/integration/08-observability.md) | 探针、`/metrics` 清单、Prometheus 告警规则、日志与 traceId |
| [`09-operations-drills.md`](docs/integration/09-operations-drills.md) | 三板斧演练：备份恢复 / 密钥轮换 / 版本回滚 |
| [`src/AppFramework.Platform.Client/README.md`](src/AppFramework.Platform.Client/README.md) | SDK 用法与边界 |

## 仓库搬移后

`web/node_modules` 的 pnpm 符号链接保存绝对路径，整仓搬目录后需重装：

```bash
cd web && pnpm install --force
```
