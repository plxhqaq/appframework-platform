# AppFramework Platform

AppFramework 通用服务提供平台：模块化单体（.NET 10 + PostgreSQL）+ React 管理台。
六个业务模块（`apps` / `identity` / `versions` / `licensing` / `backups` / `documents`）共用一套
统一返回、权限码、审计与幂等基础设施，任一模块都可以按清单摘除。

## 目录

```
src/
  Platform.Contracts/      跨端契约（DTO、错误码、权限码）→ 打成 nupkg 供客户端 pin 版本
  Platform.Kernel/         内核（模块契约、统一返回、权限、认证、中间件）
  Platform.Infrastructure/ 基础设施（EF Core、Blob、审计、幂等、迁移）
  Platform.Workers/        后台任务（过期幂等记录/审计日志清理）
  Platform.Modules.*/      六个业务模块
  Platform.Api/            宿主（端点装配、认证、限流、OpenAPI、托管静态资源）
tests/
  Platform.UnitTests/      纯逻辑单元测试
  Platform.ContractTests/  跨端契约测试（错误码/权限码唯一性与分段）
  Platform.IntegrationTests/ 数据库集成测试（EF InMemory）
web/                       React 18 + Ant Design 5 + Vite 管理台
design/                    设计令牌单一源（tokens.json）
deploy/                    Dockerfile / compose / 环境变量样例
docs/                      接入与运维文档
tools/                     设计令牌生成与校验脚本
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
dotnet tool restore
dotnet ef migrations add <Name> \
  --project src/Platform.Infrastructure \
  --startup-project src/Platform.Api
dotnet ef database update \
  --project src/Platform.Infrastructure \
  --startup-project src/Platform.Api
```

设计时工厂位于 `src/Platform.Api/Persistence/PlatformApiDesignTimeFactory.cs`，它会装配全部模块的
`IPlatformModelContributor`。**不要**把工厂移回 Infrastructure：那样生成出来的迁移只包含横切表。

生产环境由部署流程显式执行迁移，`Database:AutoMigrate` 保持 `false`（多副本同时迁移会冲突）。

## 构建与测试

```bash
dotnet build Platform.slnx -c Release --disable-build-servers -warnaserror
dotnet test  Platform.slnx -c Release --no-build --disable-build-servers

node tools/gen-tokens.mjs --check
cd web && pnpm verify && pnpm build
```

CI（`.github/workflows/ci.yml`）执行同一组命令，并额外产出 `AppFramework.Platform.Contracts` 契约包。

## 密钥与配置

- JWT 签名密钥、数据库连接串一律通过环境变量 / Secret 注入，仓库内 `appsettings.Development.json`
  只放**开发专用**占位值。
- 生产启动会校验签名密钥长度（≥32 字符）；不满足直接启动失败，而不是降级运行。
- Blob 存储默认落在 `data/blobs`，容器里挂命名卷。

## 仓库搬移后

`web/node_modules` 的 pnpm 符号链接保存绝对路径，整仓搬目录后需重装：

```bash
cd web && pnpm install --force
```
