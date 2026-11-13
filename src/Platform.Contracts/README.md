# AppFramework.Platform.Contracts

AppFramework 通用服务平台的**跨端契约包**。服务端各模块与客户端都只依赖这一份类型定义，避免两端各自维护 DTO 造成漂移。

## 内容边界

| 允许 | 禁止 |
|---|---|
| DTO / record / enum / 常量 | EF Core 实体与 DbContext |
| 错误码与权限码目录 | 控制器、服务实现 |
| Hub 路径与事件名常量 | ASP.NET Core 实现类型（`IEndpointRouteBuilder` 等） |
| 分页与统一返回包装 | 平台内部抽象（`IClock`、`IBlobStorage`） |

## 版本策略

- 包版本独立于产品版本（`PlatformContractsVersion`）。
- **破坏性变更**（删字段/改语义/改路径）必须升主版本，且服务端保留 `/api/v1` 旧路由至下一个主版本。
- 客户端必须 pin 精确版本，不用浮动范围。

## 使用

```xml
<PackageReference Include="AppFramework.Platform.Contracts" Version="0.2.0-preview.1" />
```

## 发布

```bash
dotnet pack src/Platform.Contracts/Platform.Contracts.csproj -c Release -o artifacts/packages
```
