# 备份/文档回收站 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让软删除的备份/文档在宽限期内可见、可恢复，宽限期可在管理台编辑，并提供单条/全部立即清理。

**Architecture:** 新增横切表 `retention_settings`（Infrastructure）+ Kernel 抽象 `IRetentionSettingsStore`；把两个清理任务的批循环抽成 `BackupPurgeExecutor` / `DocumentPurgeExecutor`，定时任务与「立即清理」共用；备份/文档各加「回收站」服务与 admin 端点；管理台在既有卡片内加 Tabs + 回收站面板，宽限期走共享弹窗。

**Tech Stack:** .NET 10 / ASP.NET Core Minimal API / EF Core 10 (Npgsql) / xUnit；React 18 + antd 5 + TanStack Query + vitest。

**Spec:** `docs/superpowers/specs/2026-11-20-retention-recycle-bin-design.md`（提交 `019a25a`）

## Global Constraints

- 仓库根：`D:\SVNCheckOut\WorkSpace\AppFrameworkCrossPlatform\appframework-platform`；解决方案 `Platform.slnx`。
- 构建/测试一律带 `--disable-build-servers`；`dotnet build` 与 `dotnet test` 分成两条独立 Bash 调用；收尾只清理本次命令行拉起的 .NET Host / MSBuild 残留。
- EF 迁移必须显式 `--configuration Release` 生成。
- 手写 `.cs` 无 BOM；`docs/`、`web/src`、`.md` 为 LF 无 BOM。脚本改写后用 `grep` + 字节核验。
- D11 零兼容：不留兼容分支、不写迁移回填逻辑。
- 权限码命名 `{域}.{资源}.{动作}` 小写点分；新增只插空 sort，不重排既有编号。
- 审计动作串沿用端点内联复数写法（`"backups.restore"` 等），不统一成单数。
- 前端文案一律 `tr("中文原文")`；新增英文翻译只补 `web/src/i18n/locales/en-US/ui.ts`（中文资源自动回填原文）。
- 不新增 SignalR 事件类型；文档恢复复用 `DeviceHubEvents.DocumentChanged`。
- 默认只本地提交；只有用户明确要求时才 `git push`。
- 迁移/测试使用的 `PlatformApiFactory` 用 InMemory 库；直接构造服务的测试用 `TestDbContext.Create(clock)`。

## 文件结构

| 文件 | 责任 |
|---|---|
| `src/Platform.Contracts/ApiErrorCode.cs` | 新增 `BackupsBlobReclaimed=409`、`DocumentsFileReclaimed=506` |
| `src/Platform.Contracts/Permissions/PermissionCatalog.cs` | 5 个新权限码 + 描述符 |
| `src/Platform.Contracts/Retention/RetentionContracts.cs`（新） | 宽限期设置 DTO、`PurgeResultDto` |
| `src/Platform.Contracts/Backups/BackupContracts.cs` | `DeletedBackupDto` |
| `src/Platform.Contracts/Documents/DocumentContracts.cs` | `DeletedDocumentDto` |
| `src/Platform.Kernel/Abstractions/IRetentionSettingsStore.cs`（新） | 宽限期读写抽象 |
| `src/Platform.Kernel/Errors/ApiErrorHttpStatus.cs` | 两个新错误码 → 409 |
| `src/Platform.Infrastructure/Persistence/Entities/RetentionSettingsRow.cs`（新） | 表实体 |
| `src/Platform.Infrastructure/Persistence/Configurations/RetentionSettingsConfiguration.cs`（新） | 表映射 |
| `src/Platform.Infrastructure/Persistence/RetentionSettingsStore.cs`（新） | 读写实现 |
| `src/Platform.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs` | 注册 store |
| `src/Platform.Infrastructure/Migrations/*_AddRetentionSettings.cs`（新，生成） | 建表迁移 + snapshot |
| `src/Platform.Api/Endpoints/RetentionEndpoints.cs`（新） | `GET|PUT /api/v1/admin/retention/settings` |
| `src/Platform.Api/Program.cs` | 挂载 retention 端点 |
| `src/Platform.Modules.Backups/Services/BackupPurgeExecutor.cs`（新） | 备份批清理循环（定时/手动共用） |
| `src/Platform.Modules.Backups/Services/BackupRecycleService.cs`（新） | 回收站列表/恢复/单条清理/全部清理 |
| `src/Platform.Modules.Backups/Services/BackupCleanupTask.cs` | 改读库配置 + 委托执行器 |
| `src/Platform.Modules.Backups/Services/BackupService.cs` | `ToDto` 改 `internal static` 供复用 |
| `src/Platform.Modules.Backups/Services/BackupNamespaceService.cs` | 修正「可被恢复」注释与文案 |
| `src/Platform.Modules.Backups/Endpoints/BackupEndpoints.cs` | 4 个回收站端点 |
| `src/Platform.Modules.Backups/BackupsModule.cs` | 权限清单 + 注册回收站服务 |
| `src/Platform.Modules.Documents/Services/DocumentPurgeExecutor.cs`（新） | 文档批清理循环 |
| `src/Platform.Modules.Documents/Services/DocumentRecycleService.cs`（新） | 回收站列表/恢复/清理 |
| `src/Platform.Modules.Documents/Services/DocumentChangeNotifier.cs`（新） | 抽出客户端变更推送 |
| `src/Platform.Modules.Documents/Services/DocumentService.cs` | 改用 notifier；`ToSummary` 改 `internal static` |
| `src/Platform.Modules.Documents/Services/DocumentsCleanupTask.cs` | 改读库配置 + 委托执行器 |
| `src/Platform.Modules.Documents/Endpoints/DocumentEndpoints.cs` | 4 个回收站端点 |
| `src/Platform.Modules.Documents/DocumentsModule.cs` | 权限清单 + 注册两个服务 |
| `src/Platform.Modules.Identity/Services/IdentitySeeder.cs` | 应用管理员追加 4 码 |
| `tests/Platform.ContractTests/Contracts/ApiErrorCodeTests.cs` | 新错误码分段/HTTP 断言 |
| `tests/Platform.ContractTests/Contracts/PermissionCatalogTests.cs` | 新权限码归属/排序断言 |
| `tests/Platform.IntegrationTests/Support/TestRetentionSettingsStore.cs`（新） | 内存版 store |
| `tests/Platform.IntegrationTests/Infrastructure/RetentionSettingsStoreTests.cs`（新） | store upsert/读取 |
| `tests/Platform.IntegrationTests/Infrastructure/RetentionCleanupTests.cs` | 构造改传 store + 两条库配置用例 |
| `tests/Platform.IntegrationTests/Infrastructure/BackupRecycleServiceTests.cs`（新） | 恢复/缺失拒绝/清理作用域 |
| `tests/Platform.IntegrationTests/Infrastructure/DocumentRecycleServiceTests.cs`（新） | 修订一起恢复/缺失拒绝 |
| `tests/Platform.IntegrationTests/Api/BackupRecycleEndpointsTests.cs`（新） | 端点 + 403 |
| `tests/Platform.IntegrationTests/Api/DocumentRecycleEndpointsTests.cs`（新） | 端点 + 403 |
| `tests/Platform.IntegrationTests/Api/RetentionSettingsEndpointsTests.cs`（新） | 读写 + 校验 + 403 |
| `web/src/shared/api/types.ts` | 新 DTO 类型 |
| `web/src/shared/api/backups.ts` / `documents.ts` | 4 个方法各 |
| `web/src/shared/api/retention.ts`（新） | 设置读写 |
| `web/src/shared/permissions.ts` | 5 个键 |
| `web/src/i18n/locales/en-US/permissions.ts` | 5 条权限说明 |
| `web/src/i18n/locales/en-US/ui.ts` | 新界面文案英文 |
| `web/src/shared/recycleUi.ts` + `.test.ts`（新） | 剩余宽限/告警/文件状态纯函数 |
| `web/src/shared/ui/RetentionSettingsModal.tsx`（新） | 两域宽限期弹窗 |
| `web/src/features/backups/BackupRecyclePanel.tsx`（新） | 备份回收站 |
| `web/src/features/backups/BackupsPage.tsx` | 卡片套 Tabs |
| `web/src/features/documents/DocumentRecyclePanel.tsx`（新） | 文档回收站 |
| `web/src/features/documents/DocumentsPage.tsx` | 卡片套 Tabs |
| `docs/integration/04-backups.md`、`docs/integration/05-documents.md`、`docs/平台删除能力_分批执行计划.md` | 文档更新 |

---

### Task 1: 契约层（错误码、权限码、DTO）与角色种子

**Files:**
- Modify: `tests/Platform.ContractTests/Contracts/ApiErrorCodeTests.cs`
- Modify: `tests/Platform.ContractTests/Contracts/PermissionCatalogTests.cs`
- Modify: `src/Platform.Contracts/ApiErrorCode.cs`
- Modify: `src/Platform.Kernel/Errors/ApiErrorHttpStatus.cs`
- Modify: `src/Platform.Contracts/Permissions/PermissionCatalog.cs`
- Create: `src/Platform.Contracts/Retention/RetentionContracts.cs`
- Modify: `src/Platform.Contracts/Backups/BackupContracts.cs`
- Modify: `src/Platform.Contracts/Documents/DocumentContracts.cs`
- Modify: `src/Platform.Modules.Identity/Services/IdentitySeeder.cs`

**Interfaces:**
- Consumes: 现有 `ApiErrorCode` / `PermissionDescriptor` / `PageResult<T>`。
- Produces（后续任务依赖的精确形状）：
  - `ApiErrorCode.BackupsBlobReclaimed = 409`、`ApiErrorCode.DocumentsFileReclaimed = 506`
  - `PermissionCatalog.BackupRestore/BackupPurge/DocumentRestore/DocumentPurge/RetentionCleanupManage`
  - `Platform.Contracts.Retention.RetentionDomainSettingsDto(string ModuleKey, int GraceMinutes, bool IsDatabaseValue)`
  - `Platform.Contracts.Retention.RetentionSettingsDto(RetentionDomainSettingsDto Backups, RetentionDomainSettingsDto Documents)`
  - `Platform.Contracts.Retention.UpdateRetentionSettingsRequest(int BackupsGraceMinutes, int DocumentsGraceMinutes)`
  - `Platform.Contracts.Retention.PurgeResultDto(int RowsDeleted, int FilesDeleted)`
  - `Platform.Contracts.Backups.DeletedBackupDto(Guid Id, string NamespaceKey, string Kind, string Name, long SizeBytes, bool HasFile, bool FileIntact, DateTimeOffset DeletedAt, int RemainingGraceMinutes)`
  - `Platform.Contracts.Documents.DeletedDocumentDto(Guid Id, string CollectionKey, string Title, string Visibility, int Revision, string FileName, long SizeBytes, bool FileIntact, DateTimeOffset DeletedAt, int RemainingGraceMinutes)`

- [ ] **Step 1: 先写失败断言**

在 `tests/Platform.ContractTests/Contracts/ApiErrorCodeTests.cs` 的 `各模块错误码必须落在自己的分段` 中追加两行：

```csharp
        Assert.Equal(ApiErrorSegments.Backups, ApiErrorSegments.Of(ApiErrorCode.BackupsBlobReclaimed));
        Assert.Equal(ApiErrorSegments.Documents, ApiErrorSegments.Of(ApiErrorCode.DocumentsFileReclaimed));
```

在同文件 `关键错误码的HTTP映射必须稳定` 中追加：

```csharp
        Assert.Equal(409, ApiErrorHttpStatus.Map(ApiErrorCode.BackupsBlobReclaimed));
        Assert.Equal(409, ApiErrorHttpStatus.Map(ApiErrorCode.DocumentsFileReclaimed));
```

在 `tests/Platform.ContractTests/Contracts/PermissionCatalogTests.cs` 末尾（类内）追加：

```csharp
    [Fact]
    public void 回收站权限码归属与平台专属标记正确()
    {
        var backups = PermissionCatalog.ForModule(PlatformModuleKeys.Backups);
        Assert.Contains(backups, static p => p.Code == "backup.restore" && p.Sort == 560);
        Assert.Contains(backups, static p => p.Code == "backup.purge" && p.Sort == 570);

        var documents = PermissionCatalog.ForModule(PlatformModuleKeys.Documents);
        Assert.Contains(documents, static p => p.Code == "document.restore" && p.Sort == 650);
        Assert.Contains(documents, static p => p.Code == "document.purge" && p.Sort == 660);

        var retention = PermissionCatalog.All.Single(static p => p.Code == "retention.cleanup.manage");
        Assert.Equal("platform", retention.ModuleKey);
        Assert.Equal(915, retention.Sort);
        Assert.True(retention.PlatformAdminOnly);
    }
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd "D:/SVNCheckOut/WorkSpace/AppFrameworkCrossPlatform/appframework-platform"
dotnet test tests/Platform.ContractTests/Platform.ContractTests.csproj -c Release --disable-build-servers -v:q
```

Expected: 编译失败，报 `ApiErrorCode` 不含 `BackupsBlobReclaimed` / `DocumentsFileReclaimed`、`PermissionCatalog` 不含 `RetentionCleanupManage`。

- [ ] **Step 3: 加错误码与 HTTP 映射**

`src/Platform.Contracts/ApiErrorCode.cs` 备份段：

```csharp
    BackupsPushRejected = 407,
    BackupsAckTimeout = 408,
    BackupsBlobReclaimed = 409,
```

文档段：

```csharp
    DocumentsFileMissing = 505,
    DocumentsFileReclaimed = 506,
```

`src/Platform.Kernel/Errors/ApiErrorHttpStatus.cs` 在 `BackupsAckTimeout` 映射下一行加：

```csharp
        ApiErrorCode.BackupsBlobReclaimed => StatusCodes.Status409Conflict,
```

在 `DocumentsFileMissing` 映射下一行加：

```csharp
        ApiErrorCode.DocumentsFileReclaimed => StatusCodes.Status409Conflict,
```

- [ ] **Step 4: 加权限码**

`src/Platform.Contracts/Permissions/PermissionCatalog.cs` 常量区：

```csharp
    public const string BackupPush = "backup.push";
    public const string BackupDelete = "backup.delete";
    public const string BackupRestore = "backup.restore";
    public const string BackupPurge = "backup.purge";
```

```csharp
    public const string DocumentManage = "document.manage";
    public const string DocumentDelete = "document.delete";
    public const string DocumentRestore = "document.restore";
    public const string DocumentPurge = "document.purge";
```

```csharp
    public const string AuditRead = "audit.read";
    public const string PlatformApiDocsRead = "platform.apidocs.read";
    public const string PlatformAdmin = "platform.admin";
    public const string RetentionCleanupManage = "retention.cleanup.manage";
```

描述符区（`PlatformPermissions` 内）：

```csharp
        new(BackupPush, PlatformModuleKeys.Backups, "perm.backup.push", 540),
        new(BackupDelete, PlatformModuleKeys.Backups, "perm.backup.delete", 550),
        new(BackupRestore, PlatformModuleKeys.Backups, "perm.backup.restore", 560),
        new(BackupPurge, PlatformModuleKeys.Backups, "perm.backup.purge", 570),
```

```csharp
        new(DocumentDelete, PlatformModuleKeys.Documents, "perm.document.delete", 640),
        new(DocumentRestore, PlatformModuleKeys.Documents, "perm.document.restore", 650),
        new(DocumentPurge, PlatformModuleKeys.Documents, "perm.document.purge", 660),
```

```csharp
        new(PlatformAdmin, PlatformOnlyModuleKey, "perm.platform.admin", 910, PlatformAdminOnly: true),
        new(RetentionCleanupManage, PlatformOnlyModuleKey, "perm.retention.cleanup.manage", 915, PlatformAdminOnly: true),
```

- [ ] **Step 5: 加契约 DTO**

新建 `src/Platform.Contracts/Retention/RetentionContracts.cs`：

```csharp
namespace Platform.Contracts.Retention;

/// <summary>单个域的清理宽限期；<see cref="IsDatabaseValue"/> 为 false 表示回落出厂默认。</summary>
public sealed record RetentionDomainSettingsDto(
    string ModuleKey,
    int GraceMinutes,
    bool IsDatabaseValue);

/// <summary>备份/文档两域的清理宽限期（平台全部应用生效）。</summary>
public sealed record RetentionSettingsDto(
    RetentionDomainSettingsDto Backups,
    RetentionDomainSettingsDto Documents);

/// <summary>整体提交两域宽限期（分钟，最小 1）。</summary>
public sealed record UpdateRetentionSettingsRequest(
    int BackupsGraceMinutes,
    int DocumentsGraceMinutes);

/// <summary>立即清理结果：删除的数据库行数与回收的物理文件数。</summary>
public sealed record PurgeResultDto(int RowsDeleted, int FilesDeleted);
```

在 `src/Platform.Contracts/Backups/BackupContracts.cs` 的 `BackupDto` 之后追加：

```csharp
/// <summary>回收站中的备份（软删除，宽限期内可恢复）。</summary>
public sealed record DeletedBackupDto(
    Guid Id,
    string NamespaceKey,
    string Kind,
    string Name,
    long SizeBytes,
    bool HasFile,
    bool FileIntact,
    DateTimeOffset DeletedAt,
    int RemainingGraceMinutes);
```

在 `src/Platform.Contracts/Documents/DocumentContracts.cs` 的 `DocumentSummaryDto` 之后追加：

```csharp
/// <summary>回收站中的文档（软删除，宽限期内可恢复）。</summary>
public sealed record DeletedDocumentDto(
    Guid Id,
    string CollectionKey,
    string Title,
    string Visibility,
    int Revision,
    string FileName,
    long SizeBytes,
    bool FileIntact,
    DateTimeOffset DeletedAt,
    int RemainingGraceMinutes);
```

- [ ] **Step 6: 应用管理员追加四码**

`src/Platform.Modules.Identity/Services/IdentitySeeder.cs` 的应用管理员权限列表，在 `PermissionCatalog.BackupPush,` 后加：

```csharp
                PermissionCatalog.BackupRestore,
                PermissionCatalog.BackupPurge,
```

在 `PermissionCatalog.DocumentManage,` 后加：

```csharp
                PermissionCatalog.DocumentRestore,
                PermissionCatalog.DocumentPurge,
```

不动 `backup.delete` / `document.delete`，不加 `retention.cleanup.manage`。

- [ ] **Step 7: 跑契约测试确认通过**

```bash
dotnet test tests/Platform.ContractTests/Platform.ContractTests.csproj -c Release --disable-build-servers -v:q
```

Expected: 全部通过（含既有「权限码不得重复」「显示名键不得重复」）。

- [ ] **Step 8: 提交**

```bash
git add src/Platform.Contracts src/Platform.Kernel/Errors/ApiErrorHttpStatus.cs src/Platform.Modules.Identity/Services/IdentitySeeder.cs tests/Platform.ContractTests
git commit -m "feat: 回收站契约（错误码/权限码/DTO）与应用管理员授权"
```

---

### Task 2: retention_settings 表 + Kernel 抽象 + 设置端点

**Files:**
- Create: `src/Platform.Kernel/Abstractions/IRetentionSettingsStore.cs`
- Create: `src/Platform.Infrastructure/Persistence/Entities/RetentionSettingsRow.cs`
- Create: `src/Platform.Infrastructure/Persistence/Configurations/RetentionSettingsConfiguration.cs`
- Create: `src/Platform.Infrastructure/Persistence/RetentionSettingsStore.cs`
- Modify: `src/Platform.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`
- Modify: `src/Platform.Infrastructure/Persistence/PlatformDbContext.cs`
- Create: `src/Platform.Api/Endpoints/RetentionEndpoints.cs`
- Modify: `src/Platform.Api/Program.cs`
- Generate: `src/Platform.Infrastructure/Migrations/<timestamp>_AddRetentionSettings.cs`
- Create: `tests/Platform.IntegrationTests/Support/TestRetentionSettingsStore.cs`
- Create: `tests/Platform.IntegrationTests/Infrastructure/RetentionSettingsStoreTests.cs`
- Create: `tests/Platform.IntegrationTests/Api/RetentionSettingsEndpointsTests.cs`

**Interfaces:**
- Consumes: `PlatformDbContext`、`IClock`、`Platform.Contracts.Retention.*`、Task 1 的权限码。
- Produces:
  - `Platform.Kernel.Abstractions.IRetentionSettingsStore`
    - `Task<int?> GetGraceMinutesAsync(string moduleKey, CancellationToken cancellationToken = default)`
    - `Task SetGraceMinutesAsync(IReadOnlyDictionary<string,int> minutesByModule, string updatedBy, CancellationToken cancellationToken = default)`
  - `PlatformApiFactory` 可解析到该服务（用于集成测试）。
  - **与 spec 的差异（已刻意收敛）**：spec 示例是单域 `SetGraceMinutesAsync(moduleKey, minutes, ...)`；实现改为字典批量，使 `PUT` 两域在一次 `SaveChanges` 内原子落库，避免只改一半。读取语义与 spec 一致。

- [ ] **Step 1: 先写 store 测试**

新建 `tests/Platform.IntegrationTests/Support/TestRetentionSettingsStore.cs`：

```csharp
using Platform.Kernel.Abstractions;

namespace Platform.IntegrationTests.Support;

/// <summary>内存版宽限期配置：按模块键存分钟数，未配置返回 null。</summary>
internal sealed class TestRetentionSettingsStore : IRetentionSettingsStore
{
    private readonly Dictionary<string, int> _values = new(StringComparer.Ordinal);

    public void Set(string moduleKey, int minutes) => _values[moduleKey] = minutes;

    public Task<int?> GetGraceMinutesAsync(string moduleKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.TryGetValue(moduleKey, out var minutes) ? (int?)minutes : null);

    public Task SetGraceMinutesAsync(
        IReadOnlyDictionary<string, int> minutesByModule,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        foreach (var pair in minutesByModule)
        {
            _values[pair.Key] = pair.Value;
        }

        return Task.CompletedTask;
    }
}
```

新建 `tests/Platform.IntegrationTests/Infrastructure/RetentionSettingsStoreTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Infrastructure.Persistence;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>宽限期配置：未配置返回 null；批量写入在一次 SaveChanges 内落两域。</summary>
public sealed class RetentionSettingsStoreTests
{
    [Fact]
    public async Task 未配置返回空且批量写入后按模块读取()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var store = new RetentionSettingsStore(db, clock);

        Assert.Null(await store.GetGraceMinutesAsync(PlatformModuleKeys.Backups));

        await store.SetGraceMinutesAsync(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [PlatformModuleKeys.Backups] = 60,
                [PlatformModuleKeys.Documents] = 120,
            },
            "admin");

        Assert.Equal(60, await store.GetGraceMinutesAsync(PlatformModuleKeys.Backups));
        Assert.Equal(120, await store.GetGraceMinutesAsync(PlatformModuleKeys.Documents));

        await store.SetGraceMinutesAsync(
            new Dictionary<string, int>(StringComparer.Ordinal) { [PlatformModuleKeys.Documents] = 30 },
            "operator");
        Assert.Equal(60, await store.GetGraceMinutesAsync(PlatformModuleKeys.Backups));
        Assert.Equal(30, await store.GetGraceMinutesAsync(PlatformModuleKeys.Documents));

        var row = await db.Set<RetentionSettingsRow>().SingleAsync(x => x.ModuleKey == PlatformModuleKeys.Documents);
        Assert.Equal("operator", row.UpdatedBy);
        Assert.Equal(clock.UtcNow, row.UpdatedAt);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --disable-build-servers -v:q --filter "FullyQualifiedName~RetentionSettingsStoreTests"
```

Expected: 编译失败，`IRetentionSettingsStore` / `RetentionSettingsStore` / `RetentionSettingsRow` 不存在。

- [ ] **Step 3: 加 Kernel 抽象与 Infrastructure 实现**

新建 `src/Platform.Kernel/Abstractions/IRetentionSettingsStore.cs`：

```csharp
namespace Platform.Kernel.Abstractions;

/// <summary>
/// 清理宽限期配置（库内覆盖出厂默认）。读取返回 <c>null</c> 时调用方回落各自 <c>*Options</c>；
/// 不做缓存——定时清理每 600s 才读一次，编辑时即时生效更重要。
/// </summary>
public interface IRetentionSettingsStore
{
    /// <summary>读取某个模块的宽限期（分钟）；未配置返回 <c>null</c>。</summary>
    Task<int?> GetGraceMinutesAsync(string moduleKey, CancellationToken cancellationToken = default);

    /// <summary>批量写入宽限期；单次 <c>SaveChanges</c>，保证两域不会只改一半。</summary>
    Task SetGraceMinutesAsync(
        IReadOnlyDictionary<string, int> minutesByModule,
        string updatedBy,
        CancellationToken cancellationToken = default);
}
```

新建 `src/Platform.Infrastructure/Persistence/Entities/RetentionSettingsRow.cs`：

```csharp
namespace Platform.Infrastructure.Persistence.Entities;

/// <summary>模块清理宽限期配置行（横切表，不属于任何业务模块）。</summary>
public sealed class RetentionSettingsRow
{
    /// <summary>模块键（<c>backups</c> / <c>documents</c>）。</summary>
    public string ModuleKey { get; set; } = string.Empty;

    /// <summary>软删除到物理清理的宽限期（分钟，最小 1）。</summary>
    public int CleanupGraceMinutes { get; set; }

    /// <summary>最近一次修改时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>最近一次修改人（登录名）。</summary>
    public string? UpdatedBy { get; set; }
}
```

新建 `src/Platform.Infrastructure/Persistence/Configurations/RetentionSettingsConfiguration.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Infrastructure.Persistence.Entities;

namespace Platform.Infrastructure.Persistence.Configurations;

/// <summary><c>retention_settings</c> 映射。</summary>
public sealed class RetentionSettingsConfiguration : IEntityTypeConfiguration<RetentionSettingsRow>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RetentionSettingsRow> builder)
    {
        builder.ToTable("retention_settings");
        builder.HasKey(static x => x.ModuleKey);

        builder.Property(static x => x.ModuleKey).HasColumnName("module_key").HasMaxLength(64);
        builder.Property(static x => x.CleanupGraceMinutes).HasColumnName("cleanup_grace_minutes").IsRequired();
        builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(static x => x.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);
    }
}
```

新建 `src/Platform.Infrastructure/Persistence/RetentionSettingsStore.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Platform.Kernel.Abstractions;
using Platform.Infrastructure.Persistence.Entities;

namespace Platform.Infrastructure.Persistence;

/// <summary>宽限期配置读写：upsert 单次 SaveChanges，读取直查不缓存。</summary>
public sealed class RetentionSettingsStore : IRetentionSettingsStore
{
    private readonly PlatformDbContext _db;
    private readonly IClock _clock;

    /// <summary>构造。</summary>
    public RetentionSettingsStore(PlatformDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <inheritdoc />
    public Task<int?> GetGraceMinutesAsync(string moduleKey, CancellationToken cancellationToken = default) =>
        _db.Set<RetentionSettingsRow>()
            .AsNoTracking()
            .Where(x => x.ModuleKey == moduleKey)
            .Select(static x => (int?)x.CleanupGraceMinutes)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task SetGraceMinutesAsync(
        IReadOnlyDictionary<string, int> minutesByModule,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(minutesByModule);

        var keys = minutesByModule.Keys.ToList();
        var existing = await _db.Set<RetentionSettingsRow>()
            .Where(x => keys.Contains(x.ModuleKey))
            .ToDictionaryAsync(static x => x.ModuleKey, StringComparer.Ordinal, cancellationToken);

        var now = _clock.UtcNow;
        foreach (var pair in minutesByModule)
        {
            if (existing.TryGetValue(pair.Key, out var row))
            {
                row.CleanupGraceMinutes = pair.Value;
                row.UpdatedAt = now;
                row.UpdatedBy = updatedBy;
            }
            else
            {
                _db.Set<RetentionSettingsRow>().Add(new RetentionSettingsRow
                {
                    ModuleKey = pair.Key,
                    CleanupGraceMinutes = pair.Value,
                    UpdatedAt = now,
                    UpdatedBy = updatedBy,
                });
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
```

`PlatformDbContext.cs` 在审计/幂等 DbSet 后加一行：

```csharp
    /// <summary>模块清理宽限期配置（横切表）。</summary>
    public DbSet<RetentionSettingsRow> RetentionSettings => Set<RetentionSettingsRow>();
```

`InfrastructureServiceCollectionExtensions.cs` 在 `IAuditWriter` 注册旁加：

```csharp
        services.AddScoped<IRetentionSettingsStore, RetentionSettingsStore>();
```

- [ ] **Step 4: 跑 store 测试确认通过**

```bash
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --disable-build-servers -v:q --filter "FullyQualifiedName~RetentionSettingsStoreTests"
```

Expected: PASS。

- [ ] **Step 5: 生成迁移**

```bash
cd "D:/SVNCheckOut/WorkSpace/AppFrameworkCrossPlatform/appframework-platform"
dotnet ef migrations add AddRetentionSettings --project src/Platform.Infrastructure --startup-project src/Platform.Api --configuration Release
ls src/Platform.Infrastructure/Migrations | tail -4
```

Expected: 出现新的 `*_AddRetentionSettings.cs`、`.Designer.cs`，且 `PlatformDbContextModelSnapshot.cs` 已更新；迁移内容只有 `CreateTable("retention_settings")`。

- [ ] **Step 6: 先写端点测试**

新建 `tests/Platform.IntegrationTests/Api/RetentionSettingsEndpointsTests.cs`：

```csharp
using System.Net;
using Platform.Contracts;
using Platform.Contracts.Identity;
using Platform.Contracts.Retention;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>宽限期设置端点：读写、默认来源标记、校验与权限门禁。</summary>
public sealed class RetentionSettingsEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public RetentionSettingsEndpointsTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 初始回落出厂默认且写入后标记为库内配置()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var initial = await IdentityTestHelpers.ReadDataAsync<RetentionSettingsDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/retention/settings", token)));
        Assert.False(initial.Backups.IsDatabaseValue);
        Assert.Equal(1, initial.Backups.GraceMinutes);

        var updated = await IdentityTestHelpers.ReadDataAsync<RetentionSettingsDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Put,
                "/api/v1/admin/retention/settings",
                token,
                new UpdateRetentionSettingsRequest(45, 90))));
        Assert.True(updated.Backups.IsDatabaseValue);
        Assert.Equal(45, updated.Backups.GraceMinutes);
        Assert.Equal(90, updated.Documents.GraceMinutes);

        var reread = await IdentityTestHelpers.ReadDataAsync<RetentionSettingsDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/retention/settings", token)));
        Assert.Equal(45, reread.Backups.GraceMinutes);
        Assert.Equal(90, reread.Documents.GraceMinutes);
    }

    [Fact]
    public async Task 小于一分钟被拒绝()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Put,
            "/api/v1/admin/retention/settings",
            token,
            new UpdateRetentionSettingsRequest(0, 30)));
        Assert.Equal(
            (int)ApiErrorCode.ValidationFailed,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task 无权限的用户读不到也改不了()
    {
        using var client = _factory.CreateClient();
        var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var viewerRole = await IdentityTestHelpers.GetRoleAsync(
            client, adminToken, IdentityConventions.ViewerRole);
        await IdentityTestHelpers.CreateUserAsync(
            client, adminToken, "recycle-viewer", "Viewer@12345", roleIds: [viewerRole.Id]);

        var viewerToken = (await IdentityTestHelpers.LoginAsync(
            client, "recycle-viewer", "Viewer@12345", IdentityConventions.DemoAppKey)).AccessToken;

        var read = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/admin/retention/settings", viewerToken));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(read, HttpStatusCode.Forbidden));

        var write = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Put,
            "/api/v1/admin/retention/settings",
            viewerToken,
            new UpdateRetentionSettingsRequest(30, 30)));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(write, HttpStatusCode.Forbidden));
    }
}
```

> 说明：`PlatformApiFactory` 把 `Backups:CleanupGraceMinutes` / `Documents:CleanupGraceMinutes` 都配成 `1`，所以「初始回落出厂默认」断言值是 1。

- [ ] **Step 7: 跑测试确认失败**

```bash
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --disable-build-servers -v:q --filter "FullyQualifiedName~RetentionSettingsEndpointsTests"
```

Expected: 404（端点未挂载）。

- [ ] **Step 8: 写端点并挂载**

新建 `src/Platform.Api/Endpoints/RetentionEndpoints.cs`：

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Contracts.Retention;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Http;
using Platform.Kernel.Security;
using Platform.Modules.Backups.Options;
using Platform.Modules.Documents.Options;

namespace Platform.Api.Endpoints;

/// <summary>保留策略配置端点：两域清理宽限期（平台级，跨应用生效）。</summary>
public static class RetentionEndpoints
{
    /// <summary>挂载保留策略配置。</summary>
    public static IEndpointRouteBuilder MapRetentionEndpoints(this IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var retention = api
            .MapGroup($"{PlatformRoutes.AdminPrefix}/retention")
            .WithTags("platform");

        retention.MapGet(
                "/settings",
                async (
                    HttpContext context,
                    IRetentionSettingsStore store,
                    IOptions<BackupsOptions> backupsOptions,
                    IOptions<DocumentsOptions> documentsOptions,
                    CancellationToken cancellationToken) =>
                    ApiResults.Ok(
                        context,
                        await ReadAsync(
                            store,
                            backupsOptions.Value,
                            documentsOptions.Value,
                            cancellationToken)))
            .WithName("retention.settings.get")
            .WithSummary("清理宽限期（库内覆盖出厂默认）")
            .RequirePermission(PermissionCatalog.RetentionCleanupManage);

        retention.MapPut(
                "/settings",
                async (
                    HttpContext context,
                    UpdateRetentionSettingsRequest request,
                    ICurrentPrincipal principal,
                    IRetentionSettingsStore store,
                    IOptions<BackupsOptions> backupsOptions,
                    IOptions<DocumentsOptions> documentsOptions,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        if (request.BackupsGraceMinutes < 1 || request.DocumentsGraceMinutes < 1)
                        {
                            throw BusinessException.Invalid("清理宽限期最小 1 分钟。");
                        }

                        await store.SetGraceMinutesAsync(
                            new Dictionary<string, int>(StringComparer.Ordinal)
                            {
                                [PlatformModuleKeys.Backups] = request.BackupsGraceMinutes,
                                [PlatformModuleKeys.Documents] = request.DocumentsGraceMinutes,
                            },
                            principal.UserName ?? principal.UserId ?? "unknown",
                            cancellationToken);

                        var settings = await ReadAsync(
                            store,
                            backupsOptions.Value,
                            documentsOptions.Value,
                            cancellationToken);
                        await WriteAuditAsync(
                            context,
                            audit,
                            principal,
                            action: "retention.settings.update",
                            result: AuditResults.Success,
                            message: $"backups={settings.Backups.GraceMinutes},documents={settings.Documents.GraceMinutes}");
                        return ApiResults.Ok(context, settings);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context,
                            audit,
                            principal,
                            action: "retention.settings.update",
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("retention.settings.update")
            .WithSummary("整体提交备份/文档清理宽限期（平台全部应用生效）")
            .RequirePermission(PermissionCatalog.RetentionCleanupManage);

        return api;
    }

    private static async Task<RetentionSettingsDto> ReadAsync(
        IRetentionSettingsStore store,
        BackupsOptions backups,
        DocumentsOptions documents,
        CancellationToken cancellationToken) =>
        new(
            await ResolveAsync(store, PlatformModuleKeys.Backups, backups.CleanupGraceMinutes, cancellationToken),
            await ResolveAsync(store, PlatformModuleKeys.Documents, documents.CleanupGraceMinutes, cancellationToken));

    private static async Task<RetentionDomainSettingsDto> ResolveAsync(
        IRetentionSettingsStore store,
        string moduleKey,
        int fallbackMinutes,
        CancellationToken cancellationToken)
    {
        var stored = await store.GetGraceMinutesAsync(moduleKey, cancellationToken);
        return new RetentionDomainSettingsDto(
            moduleKey,
            stored ?? Math.Max(1, fallbackMinutes),
            stored is not null);
    }

    private static Task WriteAuditAsync(
        HttpContext context,
        IAuditWriter audit,
        ICurrentPrincipal principal,
        string action,
        string result,
        string? message) =>
        audit.WriteAsync(new AuditEntry
        {
            AppKey = null,
            ActorId = principal.UserId,
            ActorName = principal.UserName,
            Action = action,
            TargetType = "retention_settings",
            TargetId = null,
            Result = result,
            Message = message,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            TraceId = context.TraceIdentifier,
        }, context.RequestAborted);
}
```

`src/Platform.Api/Program.cs` 在 `app.MapAuditEndpoints();` 后加：

```csharp
    app.MapRetentionEndpoints();
```

- [ ] **Step 9: 跑测试确认通过**

```bash
dotnet build Platform.slnx -c Release --disable-build-servers -v:q
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --no-build --disable-build-servers -v:q --filter "FullyQualifiedName~RetentionSettingsEndpointsTests|FullyQualifiedName~RetentionSettingsStoreTests"
```

Expected: 全部 PASS。

- [ ] **Step 10: 提交**

```bash
git add src/Platform.Kernel/Abstractions/IRetentionSettingsStore.cs src/Platform.Infrastructure src/Platform.Api tests/Platform.IntegrationTests/Support/TestRetentionSettingsStore.cs tests/Platform.IntegrationTests/Infrastructure/RetentionSettingsStoreTests.cs tests/Platform.IntegrationTests/Api/RetentionSettingsEndpointsTests.cs
git commit -m "feat: retention_settings 表与宽限期配置端点"
```

---

### Task 3: 清理执行器抽取与任务改读库配置

**Files:**
- Create: `src/Platform.Modules.Backups/Services/BackupPurgeExecutor.cs`
- Create: `src/Platform.Modules.Documents/Services/DocumentPurgeExecutor.cs`
- Modify: `src/Platform.Modules.Backups/Services/BackupCleanupTask.cs`
- Modify: `src/Platform.Modules.Documents/Services/DocumentsCleanupTask.cs`
- Modify: `tests/Platform.IntegrationTests/Infrastructure/RetentionCleanupTests.cs`

**Interfaces:**
- Consumes: `IRetentionSettingsStore`、`PlatformModuleKeys`、`IBlobStorage`、`PlatformDbContext`。
- Produces:
  - `BackupPurgeExecutor.PurgeAsync(PlatformDbContext db, IBlobStorage blobs, DateTimeOffset cutoff, string? appKey, CancellationToken ct) -> Task<RetentionCleanupResult>`
  - `DocumentPurgeExecutor.PurgeAsync(PlatformDbContext db, IBlobStorage blobs, DateTimeOffset cutoff, string? appKey, CancellationToken ct) -> Task<RetentionCleanupResult>`
  - 两个清理任务构造函数新增 `IRetentionSettingsStore settings` 参数（既有行为：库无配置时回落 Options）。

- [ ] **Step 1: 更新既有测试并加库配置用例**

`tests/Platform.IntegrationTests/Infrastructure/RetentionCleanupTests.cs`：

把备份用例的构造改为：

```csharp
        var settings = new TestRetentionSettingsStore();
        var task = new BackupCleanupTask(
            db, blobs, clock, Options.Create(new BackupsOptions { CleanupGraceMinutes = 30 }), settings);
```

把文档用例的构造改为：

```csharp
        var settings = new TestRetentionSettingsStore();
        var task = new DocumentsCleanupTask(
            db, blobs, clock, Options.Create(new DocumentsOptions { CleanupGraceMinutes = 30 }), settings);
```

在类内追加两个用例：

```csharp
    [Fact]
    public async Task 库内宽限期覆盖Options默认()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();

        var space = new BackupNamespace { AppKey = "demo", Key = "grace-cover", Name = "覆盖" };
        db.Add(space);
        var backup = new Backup
        {
            AppKey = "demo",
            NamespaceId = space.Id,
            Name = "kept",
            FrameworkVersion = "1.0.0",
            BlobPath = "backups/demo/grace-cover/kept.json",
            DeletedAt = clock.UtcNow.AddMinutes(-90),
        };
        db.Add(backup);
        await db.SaveChangesAsync();
        await blobs.WriteAsync(backup.BlobPath, new MemoryStream("payload"u8.ToArray()));

        var settings = new TestRetentionSettingsStore();
        settings.Set(PlatformModuleKeys.Backups, 120);
        var task = new BackupCleanupTask(
            db, blobs, clock, Options.Create(new BackupsOptions { CleanupGraceMinutes = 30 }), settings);

        var result = await task.RunAsync();

        Assert.Equal(0, result.RowsDeleted);
        Assert.NotNull(await db.Set<Backup>().SingleOrDefaultAsync(x => x.Id == backup.Id));
        Assert.True(await blobs.ExistsAsync(backup.BlobPath));
    }

    [Fact]
    public async Task 库内无配置回落Options默认()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();

        var collection = new DocumentCollection { AppKey = "demo", Key = "grace-fallback", Name = "回落" };
        db.Add(collection);
        var document = new Document
        {
            AppKey = "demo",
            CollectionId = collection.Id,
            Title = "旧文档",
            Revision = 1,
            FileName = "old.txt",
            BlobPath = "documents/demo/grace-fallback/doc/r1.bin",
            Sha256 = new string('a', 64),
            DeletedAt = clock.UtcNow.AddMinutes(-60),
        };
        db.Add(document);
        db.Add(new DocumentRevision
        {
            DocumentId = document.Id,
            Revision = 1,
            FileName = "old.txt",
            BlobPath = document.BlobPath,
            Sha256 = new string('a', 64),
            DeletedAt = document.DeletedAt,
        });
        await db.SaveChangesAsync();
        await blobs.WriteAsync(document.BlobPath, new MemoryStream("payload"u8.ToArray()));

        var settings = new TestRetentionSettingsStore();
        var task = new DocumentsCleanupTask(
            db, blobs, clock, Options.Create(new DocumentsOptions { CleanupGraceMinutes = 30 }), settings);

        var result = await task.RunAsync();

        Assert.Equal(2, result.RowsDeleted);
        Assert.Null(await db.Set<Document>().SingleOrDefaultAsync(x => x.Id == document.Id));
    }
```

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --disable-build-servers -v:q --filter "FullyQualifiedName~RetentionCleanupTests"
```

Expected: 编译失败（构造函数参数不匹配）。

- [ ] **Step 3: 写执行器**

新建 `src/Platform.Modules.Backups/Services/BackupPurgeExecutor.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Backups.Domain;

namespace Platform.Modules.Backups.Services;

/// <summary>
/// 备份物理清理批循环：每批 200、先删文件后删行；文件删不掉就保留行等下一轮。
/// 定时任务与「立即清理」共用，区别只在 cutoff 与 appKey 作用域。
/// </summary>
internal static class BackupPurgeExecutor
{
    private const int BatchSize = 200;

    public static async Task<RetentionCleanupResult> PurgeAsync(
        PlatformDbContext db,
        IBlobStorage blobs,
        DateTimeOffset cutoff,
        string? appKey,
        CancellationToken cancellationToken)
    {
        var rows = 0;
        var files = 0;

        while (true)
        {
            var query = db.Set<Backup>()
                .Where(x => x.DeletedAt != null && x.DeletedAt <= cutoff);
            if (appKey is not null)
            {
                query = query.Where(x => x.AppKey == appKey);
            }

            var batch = await query
                .OrderBy(static x => x.DeletedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                return new RetentionCleanupResult(rows, files);
            }

            foreach (var backup in batch)
            {
                if (!string.IsNullOrEmpty(backup.BlobPath))
                {
                    await blobs.DeleteAsync(backup.BlobPath, cancellationToken);
                    files++;
                }
            }

            db.Set<Backup>().RemoveRange(batch);
            await db.SaveChangesAsync(cancellationToken);
            rows += batch.Count;
        }
    }
}
```

新建 `src/Platform.Modules.Documents/Services/DocumentPurgeExecutor.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Documents.Domain;

namespace Platform.Modules.Documents.Services;

/// <summary>
/// 文档物理清理批循环：先修订后文档、每批 200、先删文件后删行。
/// 定时任务与「立即清理」共用，区别只在 cutoff 与 appKey 作用域。
/// </summary>
internal static class DocumentPurgeExecutor
{
    private const int BatchSize = 200;

    public static async Task<RetentionCleanupResult> PurgeAsync(
        PlatformDbContext db,
        IBlobStorage blobs,
        DateTimeOffset cutoff,
        string? appKey,
        CancellationToken cancellationToken)
    {
        var rows = 0;
        var files = 0;

        while (true)
        {
            var revisionQuery = db.Set<DocumentRevision>()
                .Where(x => x.DeletedAt != null && x.DeletedAt <= cutoff);
            if (appKey is not null)
            {
                revisionQuery = revisionQuery.Where(x => x.Document.AppKey == appKey);
            }

            var revisions = await revisionQuery
                .OrderBy(static x => x.DeletedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (revisions.Count == 0)
            {
                break;
            }

            foreach (var revision in revisions)
            {
                await blobs.DeleteAsync(revision.BlobPath, cancellationToken);
                files++;
            }

            db.Set<DocumentRevision>().RemoveRange(revisions);
            await db.SaveChangesAsync(cancellationToken);
            rows += revisions.Count;
        }

        while (true)
        {
            var documentQuery = db.Set<Document>()
                .Where(x => x.DeletedAt != null && x.DeletedAt <= cutoff);
            if (appKey is not null)
            {
                documentQuery = documentQuery.Where(x => x.AppKey == appKey);
            }

            var documents = await documentQuery
                .OrderBy(static x => x.DeletedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (documents.Count == 0)
            {
                return new RetentionCleanupResult(rows, files);
            }

            foreach (var document in documents)
            {
                if (!string.IsNullOrEmpty(document.BlobPath))
                {
                    await blobs.DeleteAsync(document.BlobPath, cancellationToken);
                    files++;
                }
            }

            db.Set<Document>().RemoveRange(documents);
            await db.SaveChangesAsync(cancellationToken);
            rows += documents.Count;
        }
    }
}
```

- [ ] **Step 4: 两个清理任务改读库配置并委托执行器**

`BackupCleanupTask.cs` 整体替换为：

```csharp
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Backups.Options;

namespace Platform.Modules.Backups.Services;

/// <summary>
/// 备份软删除清理：宽限期读库内配置（无配置回落 Options），超期后先删 Blob、再删行。
/// </summary>
public sealed class BackupCleanupTask : IRetentionCleanupTask
{
    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly BackupsOptions _options;
    private readonly IRetentionSettingsStore _settings;

    /// <summary>构造。</summary>
    public BackupCleanupTask(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IOptions<BackupsOptions> options,
        IRetentionSettingsStore settings)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _options = options.Value;
        _settings = settings;
    }

    /// <inheritdoc />
    public string Name => "backups";

    /// <inheritdoc />
    public async Task<RetentionCleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var grace = await _settings.GetGraceMinutesAsync(PlatformModuleKeys.Backups, cancellationToken)
                    ?? _options.CleanupGraceMinutes;
        var cutoff = _clock.UtcNow.AddMinutes(-Math.Max(1, grace));
        return await BackupPurgeExecutor.PurgeAsync(_db, _blobs, cutoff, appKey: null, cancellationToken);
    }
}
```

`DocumentsCleanupTask.cs` 整体替换为：

```csharp
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Documents.Options;

namespace Platform.Modules.Documents.Services;

/// <summary>
/// 文档软删除清理：宽限期读库内配置（无配置回落 Options），超期后先删修订文件、再删修订行与文档行。
/// </summary>
public sealed class DocumentsCleanupTask : IRetentionCleanupTask
{
    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly DocumentsOptions _options;
    private readonly IRetentionSettingsStore _settings;

    /// <summary>构造。</summary>
    public DocumentsCleanupTask(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IOptions<DocumentsOptions> options,
        IRetentionSettingsStore settings)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _options = options.Value;
        _settings = settings;
    }

    /// <inheritdoc />
    public string Name => "documents";

    /// <inheritdoc />
    public async Task<RetentionCleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var grace = await _settings.GetGraceMinutesAsync(PlatformModuleKeys.Documents, cancellationToken)
                    ?? _options.CleanupGraceMinutes;
        var cutoff = _clock.UtcNow.AddMinutes(-Math.Max(1, grace));
        return await DocumentPurgeExecutor.PurgeAsync(_db, _blobs, cutoff, appKey: null, cancellationToken);
    }
}
```

- [ ] **Step 5: 跑清理测试与本模块全量**

```bash
dotnet build Platform.slnx -c Release --disable-build-servers -v:q
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --no-build --disable-build-servers -v:q --filter "FullyQualifiedName~RetentionCleanupTests"
```

Expected: 4 条用例全 PASS。

- [ ] **Step 6: 提交**

```bash
git add src/Platform.Modules.Backups/Services/BackupCleanupTask.cs src/Platform.Modules.Backups/Services/BackupPurgeExecutor.cs src/Platform.Modules.Documents/Services/DocumentsCleanupTask.cs src/Platform.Modules.Documents/Services/DocumentPurgeExecutor.cs tests/Platform.IntegrationTests/Infrastructure/RetentionCleanupTests.cs
git commit -m "refactor: 清理任务抽出执行器并改读库内宽限期"
```

---

### Task 4: 备份回收站（服务 + 端点 + 测试）

**Files:**
- Modify: `src/Platform.Modules.Backups/Services/BackupService.cs`
- Create: `src/Platform.Modules.Backups/Services/BackupRecycleService.cs`
- Modify: `src/Platform.Modules.Backups/Services/BackupNamespaceService.cs`
- Modify: `src/Platform.Modules.Backups/BackupsModule.cs`
- Modify: `src/Platform.Modules.Backups/Endpoints/BackupEndpoints.cs`
- Create: `tests/Platform.IntegrationTests/Infrastructure/BackupRecycleServiceTests.cs`
- Create: `tests/Platform.IntegrationTests/Api/BackupRecycleEndpointsTests.cs`

**Interfaces:**
- Consumes: Task 1 的 DTO/错误码/权限码；Task 3 的 `BackupPurgeExecutor`；`BackupService.ToDto`。
- Produces:
  - `IBackupRecycleService`
    - `Task<PageResult<DeletedBackupDto>> ListDeletedAsync(string appKey, PageQuery query, string? keyword, CancellationToken ct = default)`
    - `Task<BackupDto> RestoreAsync(string appKey, Guid id, CancellationToken ct = default)`
    - `Task<PurgeResultDto> PurgeAsync(string appKey, Guid id, CancellationToken ct = default)`
    - `Task<PurgeResultDto> PurgeAllAsync(string appKey, CancellationToken ct = default)`
  - 端点：`GET /api/v1/admin/backups/deleted`、`POST /{id}/restore`、`POST /{id}/purge`、`POST /purge-all`

- [ ] **Step 1: 先写服务测试**

新建 `tests/Platform.IntegrationTests/Infrastructure/BackupRecycleServiceTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Infrastructure.Storage;
using Platform.IntegrationTests.Support;
using Platform.Kernel.Errors;
using Platform.Modules.Backups.Domain;
using Platform.Modules.Backups.Options;
using Platform.Modules.Backups.Services;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>备份回收站：恢复先验文件、缺失拒绝；清理先文件后行；全部清理只作用于当前应用。</summary>
public sealed class BackupRecycleServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "platform-recycle-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 恢复清删除标记且文件缺失拒绝恢复()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = new BackupRecycleService(
            db, blobs, clock, new TestRetentionSettingsStore(), Options.Create(new BackupsOptions()));

        var backup = await SeedAsync(db, blobs, clock, "demo", "restore", "backups/demo/restore/old.json");

        var restored = await service.RestoreAsync("demo", backup.Id);
        Assert.Equal("restore", restored.NamespaceKey);
        Assert.Null((await db.Set<Backup>().SingleAsync(x => x.Id == backup.Id)).DeletedAt);

        var row = await db.Set<Backup>().SingleAsync(x => x.Id == backup.Id);
        row.DeletedAt = clock.UtcNow;
        await db.SaveChangesAsync();
        await blobs.DeleteAsync(backup.BlobPath!);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => service.RestoreAsync("demo", backup.Id));
        Assert.Equal(ApiErrorCode.BackupsBlobReclaimed, error.Code);
    }

    [Fact]
    public async Task 列表标记文件完好并给出剩余宽限()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = new BackupRecycleService(
            db, blobs, clock, new TestRetentionSettingsStore(), Options.Create(new BackupsOptions { CleanupGraceMinutes = 30 }));

        var intact = await SeedAsync(db, blobs, clock, "demo", "list", "backups/demo/list/intact.json");
        var missing = await SeedAsync(db, blobs, clock, "demo", "list", "backups/demo/list/gone.json");
        await blobs.DeleteAsync(missing.BlobPath!);

        var page = await service.ListDeletedAsync(
            "demo", new PageQuery { Page = 1, PageSize = 20 }, keyword: null);

        Assert.Equal(2, page.Total);
        var intactRow = Assert.Single(page.Items, x => x.Id == intact.Id);
        Assert.True(intactRow.FileIntact);
        Assert.InRange(intactRow.RemainingGraceMinutes, 29, 30);
        Assert.False(Assert.Single(page.Items, x => x.Id == missing.Id).FileIntact);
    }

    [Fact]
    public async Task 全部清理只作用于当前应用且无视宽限期()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = new BackupRecycleService(
            db, blobs, clock, new TestRetentionSettingsStore(), Options.Create(new BackupsOptions { CleanupGraceMinutes = 600 }));

        var mine = await SeedAsync(db, blobs, clock, "demo", "purge", "backups/demo/purge/a.json");
        var other = await SeedAsync(db, blobs, clock, "other", "purge", "backups/other/purge/b.json");

        var result = await service.PurgeAllAsync("demo");

        Assert.Equal(1, result.RowsDeleted);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Null(await db.Set<Backup>().SingleOrDefaultAsync(x => x.Id == mine.Id));
        Assert.NotNull(await db.Set<Backup>().SingleOrDefaultAsync(x => x.Id == other.Id));
        Assert.True(await blobs.ExistsAsync(other.BlobPath!));
    }

    [Fact]
    public async Task 单条清理先删文件后删行()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = new BackupRecycleService(
            db, blobs, clock, new TestRetentionSettingsStore(), Options.Create(new BackupsOptions()));

        var backup = await SeedAsync(db, blobs, clock, "demo", "single", "backups/demo/single/a.json");

        var result = await service.PurgeAsync("demo", backup.Id);

        Assert.Equal(1, result.RowsDeleted);
        Assert.Equal(1, result.FilesDeleted);
        Assert.False(await blobs.ExistsAsync(backup.BlobPath!));
        Assert.Null(await db.Set<Backup>().SingleOrDefaultAsync(x => x.Id == backup.Id));
    }

    private static async Task<Backup> SeedAsync(
        Platform.Infrastructure.Persistence.PlatformDbContext db,
        LocalBlobStorage blobs,
        TestClock clock,
        string appKey,
        string spaceKey,
        string blobPath)
    {
        var space = new BackupNamespace { AppKey = appKey, Key = spaceKey, Name = spaceKey };
        db.Add(space);
        var backup = new Backup
        {
            AppKey = appKey,
            NamespaceId = space.Id,
            Name = "deleted",
            FrameworkVersion = "1.0.0",
            BlobPath = blobPath,
            Sha256 = new string('a', 64),
            SizeBytes = 7,
            DeletedAt = clock.UtcNow.AddMinutes(-5),
        };
        db.Add(backup);
        await db.SaveChangesAsync();
        await blobs.WriteAsync(blobPath, new MemoryStream("payload"u8.ToArray()));
        return backup;
    }

    private LocalBlobStorage CreateBlobs() =>
        new(Options.Create(new BlobStorageOptions { RootPath = _root }));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --disable-build-servers -v:q --filter "FullyQualifiedName~BackupRecycleServiceTests"
```

Expected: 编译失败，`BackupRecycleService` 不存在。

- [ ] **Step 3: 写回收站服务**

`BackupService.cs` 把 `private static BackupDto ToDto(` 改为 `internal static BackupDto ToDto(`。

新建 `src/Platform.Modules.Backups/Services/BackupRecycleService.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Backups;
using Platform.Contracts.Retention;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Backups.Domain;
using Platform.Modules.Backups.Options;

namespace Platform.Modules.Backups.Services;

/// <summary>备份回收站：软删除行的列表、恢复与立即清理。</summary>
public interface IBackupRecycleService
{
    /// <summary>回收站分页列表；文件完好标记按当前页逐行 stat。</summary>
    Task<PageResult<DeletedBackupDto>> ListDeletedAsync(
        string appKey,
        PageQuery query,
        string? keyword,
        CancellationToken cancellationToken = default);

    /// <summary>恢复：先验文件存在；缺失抛 <see cref="ApiErrorCode.BackupsBlobReclaimed"/>。</summary>
    Task<BackupDto> RestoreAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>单条立即清理：仅接受已软删除的行，先删文件后删行。</summary>
    Task<PurgeResultDto> PurgeAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>全部清理：cutoff=now、限定当前 appKey，无视宽限期。</summary>
    Task<PurgeResultDto> PurgeAllAsync(string appKey, CancellationToken cancellationToken = default);
}

/// <summary>备份回收站实现。</summary>
public sealed class BackupRecycleService : IBackupRecycleService
{
    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly IRetentionSettingsStore _settings;
    private readonly BackupsOptions _options;

    /// <summary>构造。</summary>
    public BackupRecycleService(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IRetentionSettingsStore settings,
        IOptions<BackupsOptions> options)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _settings = settings;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<PageResult<DeletedBackupDto>> ListDeletedAsync(
        string appKey,
        PageQuery query,
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var grace = await ResolveGraceMinutesAsync(cancellationToken);
        var source = _db.Set<Backup>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey && x.DeletedAt != null);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            source = source.Where(x => EF.Functions.Like(x.Name, pattern)
                                       || (x.NodeName != null && EF.Functions.Like(x.NodeName, pattern)));
        }

        var rows = await source
            .OrderByDescending(static x => x.DeletedAt)
            .Select(static x => new { Backup = x, NamespaceKey = x.Namespace.Key })
            .ToPageResultAsync(query, cancellationToken);

        var now = _clock.UtcNow;
        var items = new List<DeletedBackupDto>(rows.Items.Count);
        foreach (var row in rows.Items)
        {
            var deletedAt = row.Backup.DeletedAt!.Value;
            var hasFile = !string.IsNullOrEmpty(row.Backup.BlobPath);
            var fileIntact = !hasFile || await _blobs.ExistsAsync(row.Backup.BlobPath!, cancellationToken);
            var remaining = (int)Math.Ceiling((deletedAt.AddMinutes(grace) - now).TotalMinutes);
            items.Add(new DeletedBackupDto(
                row.Backup.Id,
                row.NamespaceKey,
                row.Backup.Kind,
                row.Backup.Name,
                row.Backup.SizeBytes,
                hasFile,
                fileIntact,
                deletedAt,
                Math.Max(0, remaining)));
        }

        return PageResult<DeletedBackupDto>.Create(items, rows.Total, query);
    }

    /// <inheritdoc />
    public async Task<BackupDto> RestoreAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<Backup>()
                      .Include(static x => x.Namespace)
                      .FirstOrDefaultAsync(
                          x => x.Id == id && x.AppKey == appKey && x.DeletedAt != null,
                          cancellationToken)
                  ?? throw new BusinessException(
                      ApiErrorCode.BackupsNotFound,
                      $"备份 {id} 不在回收站中。");

        if (!string.IsNullOrEmpty(row.BlobPath)
            && !await _blobs.ExistsAsync(row.BlobPath, cancellationToken))
        {
            throw new BusinessException(
                ApiErrorCode.BackupsBlobReclaimed,
                "物理文件已被回收，无法恢复；请改用『立即清理』删除该记录。");
        }

        row.DeletedAt = null;
        await _db.SaveChangesAsync(cancellationToken);
        return BackupService.ToDto(row, row.Namespace.Key);
    }

    /// <inheritdoc />
    public async Task<PurgeResultDto> PurgeAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<Backup>()
                      .FirstOrDefaultAsync(
                          x => x.Id == id && x.AppKey == appKey && x.DeletedAt != null,
                          cancellationToken)
                  ?? throw new BusinessException(
                      ApiErrorCode.BackupsNotFound,
                      $"备份 {id} 不在回收站中，只有已软删除的备份可以立即清理。");

        var files = 0;
        if (!string.IsNullOrEmpty(row.BlobPath))
        {
            await _blobs.DeleteAsync(row.BlobPath, cancellationToken);
            files = 1;
        }

        _db.Set<Backup>().Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return new PurgeResultDto(1, files);
    }

    /// <inheritdoc />
    public async Task<PurgeResultDto> PurgeAllAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var result = await BackupPurgeExecutor.PurgeAsync(
            _db, _blobs, _clock.UtcNow, appKey, cancellationToken);
        return new PurgeResultDto(result.RowsDeleted, result.FilesDeleted);
    }

    private async Task<int> ResolveGraceMinutesAsync(CancellationToken cancellationToken) =>
        await _settings.GetGraceMinutesAsync(PlatformModuleKeys.Backups, cancellationToken)
        ?? _options.CleanupGraceMinutes;
}
```

`BackupNamespaceService.cs` 修正注释与文案：

```csharp
        // 软删除的备份仍占配额；彻底清理后才可删命名空间。
```

```csharp
                $"命名空间 {entity.Key} 仍有 {remaining} 条备份（含待清理的已删除备份），请先清空再删除命名空间。");
```

- [ ] **Step 4: 跑服务测试确认通过**

```bash
dotnet build Platform.slnx -c Release --disable-build-servers -v:q
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --no-build --disable-build-servers -v:q --filter "FullyQualifiedName~BackupRecycleServiceTests"
```

Expected: 4 条用例全 PASS。

- [ ] **Step 5: 先写端点测试**

新建 `tests/Platform.IntegrationTests/Api/BackupRecycleEndpointsTests.cs`：

```csharp
using System.Net;
using Platform.Contracts;
using Platform.Contracts.Backups;
using Platform.Contracts.Identity;
using Platform.Contracts.Retention;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>备份回收站端点：删除→列表→恢复→回到列表；文件缺失拒绝恢复；清理与权限门禁。</summary>
public sealed class BackupRecycleEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public BackupRecycleEndpointsTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 删除后在回收站可见且可恢复到列表()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateNamespaceAsync(client, token, "recycle-inline");
        const string payload = """{"theme":"dark"}""";
        var backup = await CreateBackupAsync(client, token, "recycle-inline", "recycle-1", payload);
        await DeleteBackupAsync(client, token, backup.Id);

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<DeletedBackupDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/backups/deleted?page=1&pageSize=20", token)));
        var row = Assert.Single(page.Items, x => x.Id == backup.Id);
        Assert.True(row.FileIntact);
        Assert.InRange(row.RemainingGraceMinutes, 0, 1);

        var restored = await IdentityTestHelpers.ReadDataAsync<BackupDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/backups/{backup.Id}/restore", token)));
        Assert.Equal(backup.Name, restored.Name);

        var list = await IdentityTestHelpers.ReadDataAsync<PageResult<BackupDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/backups?page=1&pageSize=20", token)));
        Assert.Contains(list.Items, x => x.Id == backup.Id);
    }

    [Fact]
    public async Task 文件被回收后拒绝恢复且可立即清理()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateNamespaceAsync(client, token, "recycle-blob");
        var payload = "\"" + new string('x', (64 * 1024) + 1024) + "\"";
        var backup = await CreateBackupAsync(client, token, "recycle-blob", "recycle-big", payload);
        await DeleteBackupAsync(client, token, backup.Id);

        foreach (var file in Directory.GetFiles(_factory.BlobRoot, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post, $"/api/v1/admin/backups/{backup.Id}/restore", token));
        Assert.Equal(
            (int)ApiErrorCode.BackupsBlobReclaimed,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.Conflict));

        var purged = await IdentityTestHelpers.ReadDataAsync<PurgeResultDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/backups/{backup.Id}/purge", token)));
        Assert.Equal(1, purged.RowsDeleted);

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<DeletedBackupDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/backups/deleted?page=1&pageSize=20", token)));
        Assert.DoesNotContain(page.Items, x => x.Id == backup.Id);
    }

    [Fact]
    public async Task 全部清理删除当前应用回收站行()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateNamespaceAsync(client, token, "recycle-all");
        var first = await CreateBackupAsync(client, token, "recycle-all", "all-1", """{"a":1}""");
        var second = await CreateBackupAsync(client, token, "recycle-all", "all-2", """{"a":2}""");
        await DeleteBackupAsync(client, token, first.Id);
        await DeleteBackupAsync(client, token, second.Id);

        var purged = await IdentityTestHelpers.ReadDataAsync<PurgeResultDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, "/api/v1/admin/backups/purge-all", token)));
        Assert.Equal(2, purged.RowsDeleted);

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<DeletedBackupDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/backups/deleted?page=1&pageSize=20", token)));
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task 只读角色看不到回收站也删不掉()
    {
        using var client = _factory.CreateClient();
        var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var viewerRole = await IdentityTestHelpers.GetRoleAsync(
            client, adminToken, IdentityConventions.ViewerRole);
        await IdentityTestHelpers.CreateUserAsync(
            client, adminToken, "recycle-reader", "Viewer@12345", roleIds: [viewerRole.Id]);
        var viewerToken = (await IdentityTestHelpers.LoginAsync(
            client, "recycle-reader", "Viewer@12345", IdentityConventions.DemoAppKey)).AccessToken;

        var list = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/admin/backups/deleted?page=1&pageSize=20", viewerToken));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(list, HttpStatusCode.Forbidden));

        var purge = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post, $"/api/v1/admin/backups/{Guid.NewGuid()}/purge", viewerToken));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(purge, HttpStatusCode.Forbidden));
    }

    private static async Task CreateNamespaceAsync(HttpClient client, string token, string key)
    {
        await IdentityTestHelpers.ReadDataAsync<BackupNamespace>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/backups/namespaces",
                token,
                new CreateBackupNamespaceRequest(key, key, null, null, null))));
    }

    private static async Task<BackupDto> CreateBackupAsync(
        HttpClient client, string token, string namespaceKey, string name, string payload) =>
        await IdentityTestHelpers.ReadDataAsync<BackupDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/backups",
                token,
                new CreateBackupRequest(
                    namespaceKey, BackupConventions.KindConfig, name, null, null, 1, "1.0.0", null, payload, null))));

    private static async Task DeleteBackupAsync(HttpClient client, string token, Guid id)
    {
        var response = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/backups/{id}", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

- [ ] **Step 6: 跑测试确认失败**

```bash
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --disable-build-servers -v:q --filter "FullyQualifiedName~BackupRecycleEndpointsTests"
```

Expected: 404（端点不存在）。

- [ ] **Step 7: 加端点并注册服务**

`BackupsModule.cs`：`RequiredPermissions` 追加：

```csharp
        PermissionCatalog.BackupDelete,
        PermissionCatalog.BackupRestore,
        PermissionCatalog.BackupPurge,
```

`RegisterServices` 追加：

```csharp
        services.AddScoped<IBackupRecycleService, BackupRecycleService>();
```

`BackupEndpoints.cs` 在 `MapBackupEndpointsInternal(admin, createRequestLimit);` 后加一行调用：

```csharp
        MapRecycleEndpoints(admin);
```

同文件新增方法（放在 `MapClientEndpoints` 之前）：

```csharp
    private static void MapRecycleEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/deleted",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IBackupRecycleService recycle,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? keyword = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize, Keyword = keyword };
                    return ApiResults.Ok(
                        context,
                        await recycle.ListDeletedAsync(
                            RequireAppKey(principal), query, keyword, cancellationToken));
                })
            .WithName("backups.deleted.list")
            .WithSummary("回收站列表（软删除、宽限期内可恢复）")
            .RequirePermission(PermissionCatalog.BackupRestore);

        admin.MapPost(
                "/{id:guid}/restore",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IBackupRecycleService recycle,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var restored = await recycle.RestoreAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.restore",
                            targetType: "backup",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: restored.Name);
                        return ApiResults.Ok(context, restored);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.restore",
                            targetType: "backup",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("backups.restore")
            .WithSummary("恢复回收站中的备份（物理文件缺失时拒绝）")
            .RequirePermission(PermissionCatalog.BackupRestore);

        admin.MapPost(
                "/{id:guid}/purge",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IBackupRecycleService recycle,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var result = await recycle.PurgeAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.purge",
                            targetType: "backup",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: $"行 {result.RowsDeleted} / 文件 {result.FilesDeleted}");
                        return ApiResults.Ok(context, result);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.purge",
                            targetType: "backup",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("backups.purge")
            .WithSummary("立即物理清理单条已删除备份")
            .RequirePermission(PermissionCatalog.BackupPurge);

        admin.MapPost(
                "/purge-all",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IBackupRecycleService recycle,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var result = await recycle.PurgeAllAsync(appKey, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.purge",
                            targetType: "backup",
                            targetId: null,
                            result: AuditResults.Success,
                            message: $"全部清理：行 {result.RowsDeleted} / 文件 {result.FilesDeleted}");
                        return ApiResults.Ok(context, result);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "backups.purge",
                            targetType: "backup",
                            targetId: null,
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("backups.purge-all")
            .WithSummary("立即物理清理当前应用全部已删除备份")
            .RequirePermission(PermissionCatalog.BackupPurge);
    }
```

- [ ] **Step 8: 跑测试确认通过**

```bash
dotnet build Platform.slnx -c Release --disable-build-servers -v:q
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --no-build --disable-build-servers -v:q --filter "FullyQualifiedName~BackupRecycleEndpointsTests|FullyQualifiedName~BackupsEndpointsTests"
```

Expected: 新旧备份端点用例全 PASS。

- [ ] **Step 9: 提交**

```bash
git add src/Platform.Modules.Backups tests/Platform.IntegrationTests/Infrastructure/BackupRecycleServiceTests.cs tests/Platform.IntegrationTests/Api/BackupRecycleEndpointsTests.cs
git commit -m "feat: 备份回收站（列表/恢复/立即清理/全部清理）"
```

---

### Task 5: 文档回收站（服务 + 端点 + 推送复用 + 测试）

**Files:**
- Create: `src/Platform.Modules.Documents/Services/DocumentChangeNotifier.cs`
- Modify: `src/Platform.Modules.Documents/Services/DocumentService.cs`
- Modify: `src/Platform.Modules.Documents/Services/DocumentService.cs`（`ToSummary` 改 `internal static`）
- Create: `src/Platform.Modules.Documents/Services/DocumentRecycleService.cs`
- Modify: `src/Platform.Modules.Documents/DocumentsModule.cs`
- Modify: `src/Platform.Modules.Documents/Endpoints/DocumentEndpoints.cs`
- Create: `tests/Platform.IntegrationTests/Infrastructure/DocumentRecycleServiceTests.cs`
- Create: `tests/Platform.IntegrationTests/Api/DocumentRecycleEndpointsTests.cs`

**Interfaces:**
- Consumes: Task 1 DTO/错误码；Task 3 `DocumentPurgeExecutor`；`DocumentService.ToSummary`；`DeviceHubEvents.DocumentChanged` + `DocumentChangedNotification`。
- Produces:
  - `Platform.Modules.Documents.Services.DocumentChangeNotifier`（`public sealed`，`Task NotifyClientChangedAsync(string appKey, string collectionKey, CancellationToken ct)`）
  - `IDocumentRecycleService`
    - `Task<PageResult<DeletedDocumentDto>> ListDeletedAsync(string appKey, PageQuery query, string? keyword, CancellationToken ct = default)`
    - `Task<DocumentSummaryDto> RestoreAsync(string appKey, Guid id, CancellationToken ct = default)`
    - `Task<PurgeResultDto> PurgeAsync(string appKey, Guid id, CancellationToken ct = default)`
    - `Task<PurgeResultDto> PurgeAllAsync(string appKey, CancellationToken ct = default)`
  - 端点：`GET /api/v1/admin/documents/deleted`、`POST /{id}/restore`、`POST /{id}/purge`、`POST /purge-all`

- [ ] **Step 1: 先抽出推送器（保持行为不变）**

新建 `src/Platform.Modules.Documents/Services/DocumentChangeNotifier.cs`：

```csharp
using Microsoft.Extensions.Logging;
using Platform.Contracts.Devices;
using Platform.Kernel.Abstractions;

namespace Platform.Modules.Documents.Services;

/// <summary>客户端可见文档变化提示：app 组广播，客户端按需回拉 manifest；推送失败不影响落库结果。</summary>
public sealed class DocumentChangeNotifier
{
    private static readonly Action<ILogger, string, Exception?> PushFailedLog =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, "DocumentChangedPushFailed"),
            "文档变更提示推送失败（落库结果不受影响）：应用 {AppKey}。");

    private readonly IInstallationEventRouter? _router;
    private readonly ILogger<DocumentChangeNotifier> _logger;

    /// <summary>构造。</summary>
    public DocumentChangeNotifier(
        IEnumerable<IInstallationEventRouter> routers,
        ILogger<DocumentChangeNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(routers);
        _router = routers.FirstOrDefault();
        _logger = logger;
    }

    /// <summary>发布 <see cref="DeviceHubEvents.DocumentChanged"/>；无路由器时静默跳过。</summary>
    public async Task NotifyClientChangedAsync(
        string appKey,
        string collectionKey,
        CancellationToken cancellationToken)
    {
        if (_router is null)
        {
            return;
        }

        try
        {
            await _router.ToAppAsync(
                appKey,
                DeviceHubEvents.DocumentChanged,
                new DocumentChangedNotification(appKey, collectionKey),
                cancellationToken);
        }
        catch (Exception exception)
        {
            PushFailedLog(_logger, appKey, exception);
        }
    }
}
```

`DocumentService.cs` 改动：

1. 删除字段 `_router`、`_logger` 与 `ChangedPushFailedLog` 委托；
2. 新增字段 `private readonly DocumentChangeNotifier _notifier;`；
3. 构造函数把 `IEnumerable<IInstallationEventRouter> routers, ILogger<DocumentService> logger` 换成 `DocumentChangeNotifier notifier`，赋值 `_notifier = notifier;`；
4. 删除私有方法 `NotifyClientChangedAsync`；
5. 4 处调用 `await NotifyClientChangedAsync(...)` 改为 `await _notifier.NotifyClientChangedAsync(...)`；
6. 删除不再使用的 `using Microsoft.Extensions.Logging;` 与 `using Platform.Contracts.Devices;`（若编译警告提示未使用）。

`DocumentsModule.RegisterServices` 加：

```csharp
        services.AddScoped<DocumentChangeNotifier>();
```

- [ ] **Step 2: 跑既有文档测试确认重构无行为变化**

```bash
dotnet build Platform.slnx -c Release --disable-build-servers -v:q
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --no-build --disable-build-servers -v:q --filter "FullyQualifiedName~DocumentsEndpointsTests|FullyQualifiedName~BroadcastPushEventsTests"
```

Expected: 全 PASS。

- [ ] **Step 3: 先写服务测试**

新建 `tests/Platform.IntegrationTests/Infrastructure/DocumentRecycleServiceTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Infrastructure.Storage;
using Platform.IntegrationTests.Support;
using Platform.Kernel.Errors;
using Platform.Modules.Documents.Domain;
using Platform.Modules.Documents.Options;
using Platform.Modules.Documents.Services;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>文档回收站：文档与修订一起恢复；任一文件缺失整体拒绝；清理按应用隔离。</summary>
public sealed class DocumentRecycleServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "platform-doc-recycle-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 恢复同时清文档与全部修订的删除标记()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = CreateService(db, blobs, clock);

        var (document, revisions) = await SeedAsync(db, blobs, clock, "demo", "manual", 2);

        var restored = await service.RestoreAsync("demo", document.Id);
        Assert.Equal("manual", restored.CollectionKey);

        var row = await db.Set<Document>().Include(x => x.Revisions).SingleAsync(x => x.Id == document.Id);
        Assert.Null(row.DeletedAt);
        Assert.All(row.Revisions, revision => Assert.Null(revision.DeletedAt));
        Assert.Equal(revisions.Count, row.Revisions.Count);
    }

    [Fact]
    public async Task 任一修订文件缺失即拒绝恢复()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = CreateService(db, blobs, clock);

        var (document, revisions) = await SeedAsync(db, blobs, clock, "demo", "manual", 2);
        await blobs.DeleteAsync(revisions.Single(x => x.Revision == 1).BlobPath);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => service.RestoreAsync("demo", document.Id));
        Assert.Equal(ApiErrorCode.DocumentsFileReclaimed, error.Code);
    }

    [Fact]
    public async Task 全部清理只作用于当前应用()
    {
        var clock = new TestClock();
        await using var db = TestDbContext.Create(clock);
        var blobs = CreateBlobs();
        var service = CreateService(db, blobs, clock);

        var (mine, _) = await SeedAsync(db, blobs, clock, "demo", "mine", 1);
        var (other, _) = await SeedAsync(db, blobs, clock, "other", "other", 1);

        var result = await service.PurgeAllAsync("demo");

        Assert.Equal(2, result.RowsDeleted);
        Assert.Null(await db.Set<Document>().SingleOrDefaultAsync(x => x.Id == mine.Id));
        Assert.NotNull(await db.Set<Document>().SingleOrDefaultAsync(x => x.Id == other.Id));
    }

    private static DocumentRecycleService CreateService(
        Platform.Infrastructure.Persistence.PlatformDbContext db,
        LocalBlobStorage blobs,
        TestClock clock) =>
        new(
            db,
            blobs,
            clock,
            new TestRetentionSettingsStore(),
            Options.Create(new DocumentsOptions()),
            new DocumentChangeNotifier([], NullLogger<DocumentChangeNotifier>.Instance));

    private static async Task<(Document Document, List<DocumentRevision> Revisions)> SeedAsync(
        Platform.Infrastructure.Persistence.PlatformDbContext db,
        LocalBlobStorage blobs,
        TestClock clock,
        string appKey,
        string collectionKey,
        int revisionCount)
    {
        var collection = new DocumentCollection { AppKey = appKey, Key = collectionKey, Name = collectionKey };
        db.Add(collection);

        var revisions = new List<DocumentRevision>();
        for (var revision = 1; revision <= revisionCount; revision++)
        {
            revisions.Add(new DocumentRevision
            {
                Revision = revision,
                FileName = $"{revision}.txt",
                BlobPath = $"documents/{appKey}/{collectionKey}/r{revision}.bin",
                Sha256 = new string((char)('a' + revision), 64),
                DeletedAt = clock.UtcNow.AddMinutes(-5),
            });
        }

        var latest = revisions[^1];
        var document = new Document
        {
            AppKey = appKey,
            CollectionId = collection.Id,
            Title = "回收文档",
            Revision = revisionCount,
            FileName = latest.FileName,
            BlobPath = latest.BlobPath,
            Sha256 = latest.Sha256,
            SizeBytes = 7,
            DeletedAt = clock.UtcNow.AddMinutes(-5),
            Revisions = revisions,
        };
        db.Add(document);
        await db.SaveChangesAsync();

        foreach (var revision in revisions)
        {
            await blobs.WriteAsync(revision.BlobPath, new MemoryStream("payload"u8.ToArray()));
        }

        return (document, revisions);
    }

    private LocalBlobStorage CreateBlobs() =>
        new(Options.Create(new BlobStorageOptions { RootPath = _root }));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
```

> 注意：清理 2 条修订 + 1 条文档 = 3 行（文档与最新修订共用路径，文件计数会去重后为 3 次删除调用）。本用例只断言 `RowsDeleted`，不绑定文件计数。

- [ ] **Step 4: 跑测试确认失败**

```bash
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --disable-build-servers -v:q --filter "FullyQualifiedName~DocumentRecycleServiceTests"
```

Expected: 编译失败，`DocumentRecycleService` 不存在。

- [ ] **Step 5: 写文档回收站服务**

`DocumentService.cs` 把 `private static DocumentSummaryDto ToSummary(` 改为 `internal static DocumentSummaryDto ToSummary(`。

新建 `src/Platform.Modules.Documents/Services/DocumentRecycleService.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Documents;
using Platform.Contracts.Retention;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Documents.Domain;
using Platform.Modules.Documents.Options;

namespace Platform.Modules.Documents.Services;

/// <summary>文档回收站：软删除文档与全部修订的列表、恢复与立即清理。</summary>
public interface IDocumentRecycleService
{
    /// <summary>回收站分页列表；文件完好 = 文档当前文件与全部修订文件都存在。</summary>
    Task<PageResult<DeletedDocumentDto>> ListDeletedAsync(
        string appKey,
        PageQuery query,
        string? keyword,
        CancellationToken cancellationToken = default);

    /// <summary>恢复文档与全部修订；任一文件缺失抛 <see cref="ApiErrorCode.DocumentsFileReclaimed"/>。</summary>
    Task<DocumentSummaryDto> RestoreAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>单条立即清理：先删全部修订文件与文档文件，再删修订行与文档行。</summary>
    Task<PurgeResultDto> PurgeAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>全部清理：cutoff=now、限定当前 appKey，无视宽限期。</summary>
    Task<PurgeResultDto> PurgeAllAsync(string appKey, CancellationToken cancellationToken = default);
}

/// <summary>文档回收站实现。</summary>
public sealed class DocumentRecycleService : IDocumentRecycleService
{
    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly IRetentionSettingsStore _settings;
    private readonly DocumentsOptions _options;
    private readonly DocumentChangeNotifier _notifier;

    /// <summary>构造。</summary>
    public DocumentRecycleService(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IRetentionSettingsStore settings,
        IOptions<DocumentsOptions> options,
        DocumentChangeNotifier notifier)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _settings = settings;
        _options = options.Value;
        _notifier = notifier;
    }

    /// <inheritdoc />
    public async Task<PageResult<DeletedDocumentDto>> ListDeletedAsync(
        string appKey,
        PageQuery query,
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var grace = await ResolveGraceMinutesAsync(cancellationToken);
        var source = _db.Set<Document>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey && x.DeletedAt != null);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            source = source.Where(x => EF.Functions.Like(x.Title, pattern)
                                       || EF.Functions.Like(x.FileName, pattern));
        }

        var rows = await source
            .OrderByDescending(static x => x.DeletedAt)
            .Select(static x => new { Document = x, CollectionKey = x.Collection.Key })
            .ToPageResultAsync(query, cancellationToken);

        var ids = rows.Items.Select(static x => x.Document.Id).ToList();
        var revisionPaths = ids.Count == 0
            ? []
            : await _db.Set<DocumentRevision>()
                .AsNoTracking()
                .Where(x => ids.Contains(x.DocumentId))
                .Select(static x => new { x.DocumentId, x.BlobPath })
                .ToListAsync(cancellationToken);

        var now = _clock.UtcNow;
        var items = new List<DeletedDocumentDto>(rows.Items.Count);
        foreach (var row in rows.Items)
        {
            var deletedAt = row.Document.DeletedAt!.Value;
            var paths = revisionPaths
                .Where(x => x.DocumentId == row.Document.Id)
                .Select(static x => x.BlobPath)
                .Append(row.Document.BlobPath)
                .Where(static p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.Ordinal);

            var fileIntact = true;
            foreach (var path in paths)
            {
                if (!await _blobs.ExistsAsync(path, cancellationToken))
                {
                    fileIntact = false;
                    break;
                }
            }

            var remaining = (int)Math.Ceiling((deletedAt.AddMinutes(grace) - now).TotalMinutes);
            items.Add(new DeletedDocumentDto(
                row.Document.Id,
                row.CollectionKey,
                row.Document.Title,
                row.Document.Visibility,
                row.Document.Revision,
                row.Document.FileName,
                row.Document.SizeBytes,
                fileIntact,
                deletedAt,
                Math.Max(0, remaining)));
        }

        return PageResult<DeletedDocumentDto>.Create(items, rows.Total, query);
    }

    /// <inheritdoc />
    public async Task<DocumentSummaryDto> RestoreAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<Document>()
                      .Include(static x => x.Collection)
                      .Include(static x => x.Revisions)
                      .FirstOrDefaultAsync(
                          x => x.Id == id && x.AppKey == appKey && x.DeletedAt != null,
                          cancellationToken)
                  ?? throw new BusinessException(
                      ApiErrorCode.DocumentsNotFound,
                      $"文档 {id} 不在回收站中。");

        var paths = new List<string>();
        if (!string.IsNullOrEmpty(row.BlobPath))
        {
            paths.Add(row.BlobPath);
        }

        paths.AddRange(row.Revisions
            .Select(static x => x.BlobPath)
            .Where(static p => !string.IsNullOrEmpty(p)));

        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (!await _blobs.ExistsAsync(path, cancellationToken))
            {
                throw new BusinessException(
                    ApiErrorCode.DocumentsFileReclaimed,
                    "物理文件已被回收，无法恢复；请改用『立即清理』删除该记录。");
            }
        }

        row.DeletedAt = null;
        foreach (var revision in row.Revisions)
        {
            revision.DeletedAt = null;
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (string.Equals(row.Visibility, DocumentConventions.VisibilityClient, StringComparison.Ordinal))
        {
            await _notifier.NotifyClientChangedAsync(appKey, row.Collection.Key, cancellationToken);
        }

        return DocumentService.ToSummary(row, row.Collection.Key);
    }

    /// <inheritdoc />
    public async Task<PurgeResultDto> PurgeAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<Document>()
                      .Include(static x => x.Revisions)
                      .FirstOrDefaultAsync(
                          x => x.Id == id && x.AppKey == appKey && x.DeletedAt != null,
                          cancellationToken)
                  ?? throw new BusinessException(
                      ApiErrorCode.DocumentsNotFound,
                      $"文档 {id} 不在回收站中，只有已软删除的文档可以立即清理。");

        var paths = row.Revisions
            .Select(static x => x.BlobPath)
            .Append(row.BlobPath)
            .Where(static p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var path in paths)
        {
            await _blobs.DeleteAsync(path, cancellationToken);
        }

        _db.Set<DocumentRevision>().RemoveRange(row.Revisions);
        _db.Set<Document>().Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return new PurgeResultDto(row.Revisions.Count + 1, paths.Count);
    }

    /// <inheritdoc />
    public async Task<PurgeResultDto> PurgeAllAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var result = await DocumentPurgeExecutor.PurgeAsync(
            _db, _blobs, _clock.UtcNow, appKey, cancellationToken);
        return new PurgeResultDto(result.RowsDeleted, result.FilesDeleted);
    }

    private async Task<int> ResolveGraceMinutesAsync(CancellationToken cancellationToken) =>
        await _settings.GetGraceMinutesAsync(PlatformModuleKeys.Documents, cancellationToken)
        ?? _options.CleanupGraceMinutes;
}
```

- [ ] **Step 6: 跑服务测试确认通过**

```bash
dotnet build Platform.slnx -c Release --disable-build-servers -v:q
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --no-build --disable-build-servers -v:q --filter "FullyQualifiedName~DocumentRecycleServiceTests"
```

Expected: 3 条用例全 PASS。

- [ ] **Step 7: 先写端点测试**

新建 `tests/Platform.IntegrationTests/Api/DocumentRecycleEndpointsTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Headers;
using Platform.Contracts;
using Platform.Contracts.Documents;
using Platform.Contracts.Identity;
using Platform.Contracts.Retention;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>文档回收站端点：删除→列表→恢复；文件缺失拒绝恢复；清理与权限门禁。</summary>
public sealed class DocumentRecycleEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public DocumentRecycleEndpointsTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 删除后可见且恢复回列表()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateCollectionAsync(client, token, "recycle-docs");
        var document = await UploadAsync(client, token, "recycle-docs", "手册.txt");
        await DeleteAsync(client, token, document.Id);

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<DeletedDocumentDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/documents/deleted?page=1&pageSize=20", token)));
        var row = Assert.Single(page.Items, x => x.Id == document.Id);
        Assert.True(row.FileIntact);
        Assert.Equal("手册.txt", row.FileName);

        var restored = await IdentityTestHelpers.ReadDataAsync<DocumentSummaryDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/documents/{document.Id}/restore", token)));
        Assert.Equal(document.Title, restored.Title);

        var list = await IdentityTestHelpers.ReadDataAsync<PageResult<DocumentSummaryDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/documents?page=1&pageSize=20", token)));
        Assert.Contains(list.Items, x => x.Id == document.Id);
    }

    [Fact]
    public async Task 文件缺失拒绝恢复且可立即清理()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateCollectionAsync(client, token, "recycle-docs-gone");
        var document = await UploadAsync(client, token, "recycle-docs-gone", "gone.txt");
        await DeleteAsync(client, token, document.Id);

        foreach (var file in Directory.GetFiles(_factory.BlobRoot, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        var denied = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post, $"/api/v1/admin/documents/{document.Id}/restore", token));
        Assert.Equal(
            (int)ApiErrorCode.DocumentsFileReclaimed,
            await IdentityTestHelpers.ReadErrorCodeAsync(denied, HttpStatusCode.Conflict));

        var purged = await IdentityTestHelpers.ReadDataAsync<PurgeResultDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, $"/api/v1/admin/documents/{document.Id}/purge", token)));
        Assert.Equal(2, purged.RowsDeleted);
    }

    [Fact]
    public async Task 全部清理删除当前应用回收站行()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);

        await CreateCollectionAsync(client, token, "recycle-docs-all");
        var first = await UploadAsync(client, token, "recycle-docs-all", "a.txt");
        var second = await UploadAsync(client, token, "recycle-docs-all", "b.txt");
        await DeleteAsync(client, token, first.Id);
        await DeleteAsync(client, token, second.Id);

        var purged = await IdentityTestHelpers.ReadDataAsync<PurgeResultDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post, "/api/v1/admin/documents/purge-all", token)));
        Assert.Equal(4, purged.RowsDeleted);

        var page = await IdentityTestHelpers.ReadDataAsync<PageResult<DeletedDocumentDto>>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Get, "/api/v1/admin/documents/deleted?page=1&pageSize=20", token)));
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task 只读角色看不到回收站也删不掉()
    {
        using var client = _factory.CreateClient();
        var adminToken = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var viewerRole = await IdentityTestHelpers.GetRoleAsync(
            client, adminToken, IdentityConventions.ViewerRole);
        await IdentityTestHelpers.CreateUserAsync(
            client, adminToken, "doc-recycle-reader", "Viewer@12345", roleIds: [viewerRole.Id]);
        var viewerToken = (await IdentityTestHelpers.LoginAsync(
            client, "doc-recycle-reader", "Viewer@12345", IdentityConventions.DemoAppKey)).AccessToken;

        var list = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get, "/api/v1/admin/documents/deleted?page=1&pageSize=20", viewerToken));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(list, HttpStatusCode.Forbidden));

        var purge = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post, $"/api/v1/admin/documents/{Guid.NewGuid()}/purge", viewerToken));
        Assert.Equal(
            (int)ApiErrorCode.Forbidden,
            await IdentityTestHelpers.ReadErrorCodeAsync(purge, HttpStatusCode.Forbidden));
    }

    private static async Task CreateCollectionAsync(HttpClient client, string token, string key) =>
        await IdentityTestHelpers.ReadDataAsync<DocumentCollectionDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/documents/collections",
                token,
                new CreateDocumentCollectionRequest(key, key, null))));

    private static async Task<DocumentDetailDto> UploadAsync(
        HttpClient client,
        string token,
        string collectionKey,
        string fileName)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(collectionKey), "collectionKey");
        form.Add(new StringContent(Path.GetFileNameWithoutExtension(fileName)), "title");
        form.Add(new StringContent(DocumentConventions.VisibilityClient), "visibility");
        var file = new ByteArrayContent("document"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/documents")
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await IdentityTestHelpers.ReadDataAsync<DocumentDetailDto>(await client.SendAsync(request));
    }

    private static async Task DeleteAsync(HttpClient client, string token, Guid id)
    {
        var response = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Delete, $"/api/v1/admin/documents/{id}", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

> 说明：`PlatformApiFactory` 关闭了 `Documents:CleanupGraceMinutes = 1`，但清理只由 `RetentionWorker` 触发，测试宿主不会自动跑，所以删除后行仍在。

- [ ] **Step 8: 跑测试确认失败**

```bash
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --disable-build-servers -v:q --filter "FullyQualifiedName~DocumentRecycleEndpointsTests"
```

Expected: 404（端点不存在）。

- [ ] **Step 9: 加端点并注册服务**

`DocumentsModule.cs`：`RequiredPermissions` 追加：

```csharp
        PermissionCatalog.DocumentDelete,
        PermissionCatalog.DocumentRestore,
        PermissionCatalog.DocumentPurge,
```

`RegisterServices` 追加：

```csharp
        services.AddScoped<IDocumentRecycleService, DocumentRecycleService>();
```

`DocumentEndpoints.cs` 在 `MapDocumentAdminEndpoints(admin, uploadRequestLimit);` 后加：

```csharp
        MapRecycleEndpoints(admin);
```

同文件新增方法（放在 `MapCollectionEndpoints` 之前）：

```csharp
    private static void MapRecycleEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet(
                "/deleted",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IDocumentRecycleService recycle,
                    int page = 1,
                    int pageSize = PageQuery.DefaultPageSize,
                    string? keyword = null,
                    CancellationToken cancellationToken = default) =>
                {
                    var query = new PageQuery { Page = page, PageSize = pageSize, Keyword = keyword };
                    return ApiResults.Ok(
                        context,
                        await recycle.ListDeletedAsync(
                            RequireAppKey(principal), query, keyword, cancellationToken));
                })
            .WithName("documents.deleted.list")
            .WithSummary("回收站列表（软删除、宽限期内可恢复）")
            .RequirePermission(PermissionCatalog.DocumentRestore);

        admin.MapPost(
                "/{id:guid}/restore",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IDocumentRecycleService recycle,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var restored = await recycle.RestoreAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.restore",
                            targetType: "document",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: restored.Title);
                        return ApiResults.Ok(context, restored);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.restore",
                            targetType: "document",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("documents.restore")
            .WithSummary("恢复回收站中的文档（含全部修订；任一文件缺失时拒绝）")
            .RequirePermission(PermissionCatalog.DocumentRestore);

        admin.MapPost(
                "/{id:guid}/purge",
                async (
                    HttpContext context,
                    Guid id,
                    ICurrentPrincipal principal,
                    IDocumentRecycleService recycle,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var result = await recycle.PurgeAsync(appKey, id, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.purge",
                            targetType: "document",
                            targetId: id.ToString(),
                            result: AuditResults.Success,
                            message: $"行 {result.RowsDeleted} / 文件 {result.FilesDeleted}");
                        return ApiResults.Ok(context, result);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.purge",
                            targetType: "document",
                            targetId: id.ToString(),
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("documents.purge")
            .WithSummary("立即物理清理单条已删除文档（含修订）")
            .RequirePermission(PermissionCatalog.DocumentPurge);

        admin.MapPost(
                "/purge-all",
                async (
                    HttpContext context,
                    ICurrentPrincipal principal,
                    IDocumentRecycleService recycle,
                    IAuditWriter audit,
                    CancellationToken cancellationToken) =>
                {
                    var appKey = RequireAppKey(principal);
                    try
                    {
                        var result = await recycle.PurgeAllAsync(appKey, cancellationToken);
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.purge",
                            targetType: "document",
                            targetId: null,
                            result: AuditResults.Success,
                            message: $"全部清理：行 {result.RowsDeleted} / 文件 {result.FilesDeleted}");
                        return ApiResults.Ok(context, result);
                    }
                    catch (BusinessException exception)
                    {
                        await WriteAuditAsync(
                            context, audit, principal,
                            action: "documents.purge",
                            targetType: "document",
                            targetId: null,
                            result: AuditResults.Failure,
                            message: exception.Message);
                        throw;
                    }
                })
            .WithName("documents.purge-all")
            .WithSummary("立即物理清理当前应用全部已删除文档")
            .RequirePermission(PermissionCatalog.DocumentPurge);
    }
```

- [ ] **Step 10: 跑测试确认通过**

```bash
dotnet build Platform.slnx -c Release --disable-build-servers -v:q
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --no-build --disable-build-servers -v:q --filter "FullyQualifiedName~DocumentRecycleEndpointsTests|FullyQualifiedName~DocumentsEndpointsTests"
```

Expected: 全 PASS。

- [ ] **Step 11: 提交**

```bash
git add src/Platform.Modules.Documents tests/Platform.IntegrationTests/Infrastructure/DocumentRecycleServiceTests.cs tests/Platform.IntegrationTests/Api/DocumentRecycleEndpointsTests.cs
git commit -m "feat: 文档回收站（修订一起恢复/清理，复用 DocumentChanged 推送）"
```

---

### Task 6: 前端 API 层、权限键、i18n 与纯函数

**Files:**
- Modify: `web/src/shared/api/types.ts`
- Modify: `web/src/shared/api/backups.ts`
- Modify: `web/src/shared/api/documents.ts`
- Create: `web/src/shared/api/retention.ts`
- Modify: `web/src/shared/permissions.ts`
- Modify: `web/src/i18n/locales/en-US/permissions.ts`
- Modify: `web/src/i18n/locales/en-US/ui.ts`
- Create: `web/src/shared/recycleUi.ts`
- Create: `web/src/shared/recycleUi.test.ts`

**Interfaces:**
- Consumes: 后端 DTO 字段（camelCase 由 ASP.NET 默认序列化）。
- Produces:
  - `backupsApi.listDeletedBackups(query, signal)` / `restoreBackup(id)` / `purgeBackup(id)` / `purgeAllBackups()`
  - `documentsApi.listDeletedDocuments(query, signal)` / `restoreDocument(id)` / `purgeDocument(id)` / `purgeAllDocuments()`
  - `retentionApi.getSettings(signal?)` / `retentionApi.updateSettings(body)`
  - `recycleUi.isGraceCritical / remainingGraceLabel / fileStateMeta / GRACE_WARNING_MINUTES`

- [ ] **Step 1: 先写纯函数测试**

新建 `web/src/shared/recycleUi.test.ts`：

```ts
import { describe, expect, it } from 'vitest';
import {
  GRACE_WARNING_MINUTES,
  fileStateMeta,
  isGraceCritical,
  remainingGraceLabel,
} from './recycleUi';

describe('回收站展示口径', () => {
  it('剩余 5 分钟及以内告警', () => {
    expect(isGraceCritical(GRACE_WARNING_MINUTES)).toBe(true);
    expect(isGraceCritical(GRACE_WARNING_MINUTES - 1)).toBe(true);
    expect(isGraceCritical(GRACE_WARNING_MINUTES + 1)).toBe(false);
  });

  it('剩余宽限文案覆盖分钟/小时/到期', () => {
    expect(remainingGraceLabel(0)).toBe('等待下次清理');
    expect(remainingGraceLabel(12)).toBe('剩余 12 分钟');
    expect(remainingGraceLabel(60)).toBe('剩余 1 小时');
    expect(remainingGraceLabel(95)).toBe('剩余 1 小时 35 分钟');
  });

  it('文件缺失行不可恢复', () => {
    expect(fileStateMeta(true)).toEqual({ label: '文件完好', color: 'green', restorable: true });
    expect(fileStateMeta(false)).toEqual({ label: '文件已缺失', color: 'red', restorable: false });
  });
});
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd "D:/SVNCheckOut/WorkSpace/AppFrameworkCrossPlatform/appframework-platform/web"
npx vitest run src/shared/recycleUi.test.ts
```

Expected: 模块不存在，测试失败。

- [ ] **Step 3: 写纯函数**

新建 `web/src/shared/recycleUi.ts`：

```ts
import { tr } from '../i18n/translate';

/** 剩余宽限 ≤5 分钟时标红，提示即将物理删除。 */
export const GRACE_WARNING_MINUTES = 5;

export function isGraceCritical(remainingMinutes: number): boolean {
  return remainingMinutes <= GRACE_WARNING_MINUTES;
}

/** 剩余宽限文案：0 表示已到期，等待下一轮清理。 */
export function remainingGraceLabel(remainingMinutes: number): string {
  if (remainingMinutes <= 0) {
    return tr("等待下次清理");
  }
  if (remainingMinutes < 60) {
    return tr("剩余 {{arg0}} 分钟", { arg0: remainingMinutes });
  }
  const hours = Math.floor(remainingMinutes / 60);
  const minutes = remainingMinutes % 60;
  return minutes > 0
    ? tr("剩余 {{arg0}} 小时 {{arg1}} 分钟", { arg0: hours, arg1: minutes })
    : tr("剩余 {{arg0}} 小时", { arg0: hours });
}

/** 文件状态：缺失行在回收站可见但不可恢复，只能立即清理。 */
export function fileStateMeta(fileIntact: boolean): {
  label: string;
  color?: string;
  restorable: boolean;
} {
  return fileIntact
    ? { label: tr("文件完好"), color: 'green', restorable: true }
    : { label: tr("文件已缺失"), color: 'red', restorable: false };
}
```

- [ ] **Step 4: 加类型与 API 方法**

`web/src/shared/api/types.ts` 在 `BackupSummary` 后追加：

```ts
/** 回收站中的备份（软删除，宽限期内可恢复）。 */
export interface DeletedBackup {
  id: string;
  namespaceKey: string;
  kind: BackupKind;
  name: string;
  sizeBytes: number;
  hasFile: boolean;
  fileIntact: boolean;
  deletedAt: string;
  remainingGraceMinutes: number;
}

/** 立即清理结果。 */
export interface PurgeResult {
  rowsDeleted: number;
  filesDeleted: number;
}

/** 单个域的清理宽限期；isDatabaseValue=false 表示回落出厂默认。 */
export interface RetentionDomainSettings {
  moduleKey: string;
  graceMinutes: number;
  isDatabaseValue: boolean;
}

/** 备份/文档两域宽限期（平台全部应用生效）。 */
export interface RetentionSettings {
  backups: RetentionDomainSettings;
  documents: RetentionDomainSettings;
}

/** 整体提交两域宽限期（分钟，最小 1）。 */
export interface UpdateRetentionSettingsRequest {
  backupsGraceMinutes: number;
  documentsGraceMinutes: number;
}
```

在 `DocumentSummary` 后追加：

```ts
/** 回收站中的文档（软删除，宽限期内可恢复）。 */
export interface DeletedDocument {
  id: string;
  collectionKey: string;
  title: string;
  visibility: DocumentVisibility;
  revision: number;
  fileName: string;
  sizeBytes: number;
  fileIntact: boolean;
  deletedAt: string;
  remainingGraceMinutes: number;
}
```

`web/src/shared/api/backups.ts`：import 增加 `DeletedBackup, PurgeResult`；`backupsApi` 对象末尾追加：

```ts
  /** 回收站：软删除的备份（宽限期内可恢复）。 */
  listDeletedBackups: (query: BackupListQuery, signal?: AbortSignal) =>
    request<PageResult<DeletedBackup>>(
      `/api/v1/admin/backups/deleted${toQueryString({ ...query })}`,
      { signal },
    ),

  restoreBackup: (id: string) =>
    request<BackupSummary>(`/api/v1/admin/backups/${id}/restore`, { method: 'POST' }),

  purgeBackup: (id: string) =>
    request<PurgeResult>(`/api/v1/admin/backups/${id}/purge`, { method: 'POST' }),

  purgeAllBackups: () =>
    request<PurgeResult>('/api/v1/admin/backups/purge-all', { method: 'POST' }),
```

`web/src/shared/api/documents.ts`：import 增加 `DeletedDocument, PurgeResult`；`documentsApi` 对象末尾追加：

```ts
  /** 回收站：软删除的文档（宽限期内可恢复）。 */
  listDeletedDocuments: (query: DocumentListQuery, signal?: AbortSignal) =>
    request<PageResult<DeletedDocument>>(
      `/api/v1/admin/documents/deleted${toQueryString({ ...query })}`,
      { signal },
    ),

  restoreDocument: (id: string) =>
    request<DocumentSummary>(`/api/v1/admin/documents/${id}/restore`, { method: 'POST' }),

  purgeDocument: (id: string) =>
    request<PurgeResult>(`/api/v1/admin/documents/${id}/purge`, { method: 'POST' }),

  purgeAllDocuments: () =>
    request<PurgeResult>('/api/v1/admin/documents/purge-all', { method: 'POST' }),
```

新建 `web/src/shared/api/retention.ts`：

```ts
import { request } from './client';
import type {
  RetentionSettings,
  UpdateRetentionSettingsRequest,
} from './types';

/** 清理宽限期（`/api/v1/admin/retention/settings`，平台级）。 */
export const retentionApi = {
  getSettings: (signal?: AbortSignal) =>
    request<RetentionSettings>('/api/v1/admin/retention/settings', { signal }),

  updateSettings: (body: UpdateRetentionSettingsRequest) =>
    request<RetentionSettings>('/api/v1/admin/retention/settings', { method: 'PUT', body }),
};
```

- [ ] **Step 5: 权限键与文案**

`web/src/shared/permissions.ts` 在 `backupDelete` 后加：

```ts
  backupRestore: 'backup.restore',
  backupPurge: 'backup.purge',
```

在 `documentDelete` 后加：

```ts
  documentRestore: 'document.restore',
  documentPurge: 'document.purge',
```

在 `platformApiDocsRead` 后加：

```ts
  retentionCleanupManage: 'retention.cleanup.manage',
```

`web/src/i18n/locales/en-US/permissions.ts`：

- `codes.backup` 内 `delete` 后加：

```ts
      restore: 'Restore a backed-up item from the recycle bin (rejected when its file is already reclaimed)',
      purge: 'Permanently purge a deleted backup now, without waiting for the grace period',
```

- `codes.document` 内 `delete` 后加：

```ts
      restore: 'Restore a document and all of its revisions from the recycle bin',
      purge: 'Permanently purge a deleted document and all of its revisions now',
```

- 在 `codes.platform` 之前加：

```ts
    retention: {
      cleanup: {
        manage: 'Edit the cleanup grace period for backups and documents (applies to every app)',
      },
    },
```

`web/src/i18n/locales/zh-CN/permissions.ts` 对应位置加：

```ts
      restore: '从回收站恢复备份（物理文件已回收时拒绝）',
      purge: '立即物理清理已删除备份，跳过宽限期',
```

```ts
      restore: '从回收站恢复文档及其全部修订',
      purge: '立即物理清理已删除文档及其全部修订',
```

```ts
    retention: {
      cleanup: { manage: '编辑备份/文档的清理宽限期（作用于平台全部应用）' },
    },
```

`web/src/i18n/locales/en-US/ui.ts` 追加（按中文键排序插入均可）：

```ts
  "等待下次清理": "Awaiting the next cleanup",
  "剩余 {{arg0}} 分钟": "{{arg0}} min left",
  "剩余 {{arg0}} 小时": "{{arg0}} h left",
  "剩余 {{arg0}} 小时 {{arg1}} 分钟": "{{arg0}} h {{arg1}} min left",
  "文件完好": "File intact",
  "文件已缺失": "File missing",
  "回收站": "Recycle bin",
  "备份记录": "Backups",
  "文档列表": "Documents",
  "恢复": "Restore",
  "立即清理": "Purge now",
  "全部清理": "Purge all",
  "清理宽限期": "Cleanup grace period",
  "保存": "Save",
  "取消": "Cancel",
  "按名称或节点名过滤回收站": "Filter recycle bin by name or node",
  "按标题或文件名过滤回收站": "Filter recycle bin by title or file name",
  "回收站为空": "Recycle bin is empty",
  "共 {{arg0}} 条待清理记录": "{{arg0}} items pending cleanup",
  "物理文件已被回收，只能清理": "File already reclaimed; only cleanup is possible",
  "文件已缺失，只能清理": "File missing; only cleanup is possible",
  "恢复备份「{{arg0}}」？": "Restore backup \"{{arg0}}\"?",
  "恢复后可能超过命名空间保留上限，后续新建备份会被拒绝，直到删除旧备份。": "Restoring may exceed the namespace retention limit; new backups will be rejected until older ones are deleted.",
  "恢复文档「{{arg0}}」？": "Restore document \"{{arg0}}\"?",
  "恢复后文档与全部修订重新出现在列表与客户端清单中。": "After restoring, the document and all revisions reappear in the list and the client manifest.",
  "立即清理「{{arg0}}」？此操作不可恢复。": "Purge \"{{arg0}}\" now? This cannot be undone.",
  "清理回收站全部 {{arg0}} 条记录？此操作不可恢复。": "Purge all {{arg0}} items in the recycle bin? This cannot be undone.",
  "已清理 {{arg0}} 条记录、回收 {{arg1}} 个文件": "Purged {{arg0}} records and reclaimed {{arg1}} files",
  "备份已恢复": "Backup restored",
  "文档已恢复": "Document restored",
  "清理完成": "Cleanup completed",
  "清理宽限期已保存": "Cleanup grace period saved",
  "软删除到物理清理之间的等待时间；宽限期内可在回收站恢复。修改作用于平台全部应用。": "Wait time between soft delete and physical cleanup; items can be restored from the recycle bin during this window. Applies to every app.",
  "库内配置": "Database value",
  "出厂默认": "Default",
  "最小 1 分钟": "Minimum 1 minute",
  "分钟": "min",
  "备份": "Backups",
  "文档": "Documents",
  "剩余宽限": "Grace left",
  "文件状态": "File status",
  "删除时间": "Deleted at",
  "命名空间": "Namespace",
  "集合": "Collection",
```

> `备份`、`文档`、`命名空间`、`集合` 等键若已存在会被重复键覆盖；追加前先在文件里 `grep -n '"备份":'` 确认，已存在则跳过。

- [ ] **Step 6: 跑前端校验**

```bash
cd "D:/SVNCheckOut/WorkSpace/AppFrameworkCrossPlatform/appframework-platform/web"
npx vitest run src/shared/recycleUi.test.ts src/shared/permissionDescriptions.test.ts
npm run typecheck
npm run lint
```

Expected: vitest 全绿、tsc 无错误、eslint 0 警告。

- [ ] **Step 7: 提交**

```bash
git add web/src/shared web/src/i18n
git commit -m "feat: 管理台回收站 API 层/权限键/纯函数"
```

---

### Task 7: 宽限期弹窗与备份回收站面板

**Files:**
- Create: `web/src/shared/ui/RetentionSettingsModal.tsx`
- Create: `web/src/features/backups/BackupRecyclePanel.tsx`
- Modify: `web/src/features/backups/BackupsPage.tsx`

**Interfaces:**
- Consumes: Task 6 的 `backupsApi.*`、`retentionApi`、`recycleUi`、权限键。
- Produces: `<RetentionSettingsModal open onClose />`、`<BackupRecyclePanel appKey canRestore canPurge canManageGrace />`。

- [ ] **Step 1: 写共享弹窗**

新建 `web/src/shared/ui/RetentionSettingsModal.tsx`：

```tsx
import { tr } from '../../i18n/translate';
import { App, Form, InputNumber, Modal, Space, Tag, Typography } from 'antd';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { retentionApi } from '../api/retention';
import type { UpdateRetentionSettingsRequest } from '../api/types';

/** 两域清理宽限期：整体提交，避免只改一半。 */
export function RetentionSettingsModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<UpdateRetentionSettingsRequest>();

  const settings = useQuery({
    queryKey: ['retention', 'settings'],
    queryFn: ({ signal }) => retentionApi.getSettings(signal),
    enabled: open,
  });

  useEffect(() => {
    if (!open || !settings.data) {
      return;
    }
    form.setFieldsValue({
      backupsGraceMinutes: settings.data.backups.graceMinutes,
      documentsGraceMinutes: settings.data.documents.graceMinutes,
    });
  }, [open, settings.data, form]);

  const submit = async () => {
    const values = await form.validateFields().catch(() => null);
    if (!values) {
      return;
    }

    try {
      await retentionApi.updateSettings(values);
      message.success(tr("清理宽限期已保存"));
      onClose();
      await queryClient.invalidateQueries({ queryKey: ['retention', 'settings'] });
      await queryClient.invalidateQueries({ queryKey: ['backups'] });
      await queryClient.invalidateQueries({ queryKey: ['documents'] });
    } catch (error) {
      message.error(error instanceof Error ? error.message : tr("保存失败"));
    }
  };

  const sourceTag = (isDatabaseValue: boolean) =>
    isDatabaseValue ? <Tag color="blue">{tr("库内配置")}</Tag> : <Tag>{tr("出厂默认")}</Tag>;

  return (
    <Modal
      open={open}
      title={tr("清理宽限期")}
      okText={tr("保存")}
      cancelText={tr("取消")}
      confirmLoading={settings.isFetching}
      onOk={() => void submit()}
      onCancel={onClose}
    >
      <Typography.Paragraph type="secondary">
        {tr("软删除到物理清理之间的等待时间；宽限期内可在回收站恢复。修改作用于平台全部应用。")}
      </Typography.Paragraph>
      <Form form={form} layout="vertical">
        <Form.Item
          name="backupsGraceMinutes"
          label={
            <Space size={6}>
              {tr("备份")}
              {sourceTag(settings.data?.backups.isDatabaseValue ?? false)}
            </Space>
          }
          rules={[{ required: true, type: 'number', min: 1, message: tr("最小 1 分钟") }]}
        >
          <InputNumber min={1} max={10080} addonAfter={tr("分钟")} style={{ width: '100%' }} />
        </Form.Item>
        <Form.Item
          name="documentsGraceMinutes"
          label={
            <Space size={6}>
              {tr("文档")}
              {sourceTag(settings.data?.documents.isDatabaseValue ?? false)}
            </Space>
          }
          rules={[{ required: true, type: 'number', min: 1, message: tr("最小 1 分钟") }]}
        >
          <InputNumber min={1} max={10080} addonAfter={tr("分钟")} style={{ width: '100%' }} />
        </Form.Item>
      </Form>
    </Modal>
  );
}
```

- [ ] **Step 2: 写备份回收站面板**

新建 `web/src/features/backups/BackupRecyclePanel.tsx`：

```tsx
import { tr } from '../../i18n/translate';
import {
  DeleteOutlined,
  ReloadOutlined,
  SettingOutlined,
  UndoOutlined,
} from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Flex,
  Input,
  Space,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { TableColumnsType } from 'antd';
import { useState } from 'react';
import { backupsApi } from '../../shared/api/backups';
import type { DeletedBackup } from '../../shared/api/types';
import { formatBytes, formatDateTime } from '../../shared/format';
import { fileStateMeta, isGraceCritical, remainingGraceLabel } from '../../shared/recycleUi';
import { responsivePagination } from '../../shared/responsive';
import { RetentionSettingsModal } from '../../shared/ui/RetentionSettingsModal';
import { useResponsive } from '../../shared/useResponsive';

interface BackupRecyclePanelProps {
  appKey: string;
  canRestore: boolean;
  canPurge: boolean;
  canManageGrace: boolean;
}

/** 备份回收站：软删除行、剩余宽限、文件完好标记与恢复/清理动作。 */
export function BackupRecyclePanel({
  appKey,
  canRestore,
  canPurge,
  canManageGrace,
}: BackupRecyclePanelProps) {
  const { message, modal } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const [keyword, setKeyword] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [settingsOpen, setSettingsOpen] = useState(false);

  const deleted = useQuery({
    queryKey: ['backups', 'recycle', appKey, keyword, page, pageSize],
    queryFn: ({ signal }) =>
      backupsApi.listDeletedBackups(
        { keyword: keyword.trim() || undefined, page, pageSize },
        signal,
      ),
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['backups'] });
  };

  const handleRestore = (row: DeletedBackup) => {
    modal.confirm({
      title: tr("恢复备份「{{arg0}}」？", { arg0: row.name }),
      content: tr(
        "恢复后可能超过命名空间保留上限，后续新建备份会被拒绝，直到删除旧备份。",
      ),
      okText: tr("恢复"),
      cancelText: tr("取消"),
      onOk: async () => {
        try {
          await backupsApi.restoreBackup(row.id);
          message.success(tr("备份已恢复"));
          await refresh();
        } catch (error) {
          message.error(error instanceof Error ? error.message : tr("恢复失败"));
        }
      },
    });
  };

  const handlePurge = (row: DeletedBackup) => {
    modal.confirm({
      title: tr("立即清理「{{arg0}}」？此操作不可恢复。", { arg0: row.name }),
      okText: tr("立即清理"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: async () => {
        try {
          const result = await backupsApi.purgeBackup(row.id);
          message.success(
            tr("已清理 {{arg0}} 条记录、回收 {{arg1}} 个文件", {
              arg0: result.rowsDeleted,
              arg1: result.filesDeleted,
            }),
          );
          await refresh();
        } catch (error) {
          message.error(error instanceof Error ? error.message : tr("清理失败"));
        }
      },
    });
  };

  const handlePurgeAll = () => {
    modal.confirm({
      title: tr("清理回收站全部 {{arg0}} 条记录？此操作不可恢复。", {
        arg0: deleted.data?.total ?? 0,
      }),
      okText: tr("全部清理"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: async () => {
        try {
          const result = await backupsApi.purgeAllBackups();
          message.success(
            tr("已清理 {{arg0}} 条记录、回收 {{arg1}} 个文件", {
              arg0: result.rowsDeleted,
              arg1: result.filesDeleted,
            }),
          );
          await refresh();
        } catch (error) {
          message.error(error instanceof Error ? error.message : tr("清理失败"));
        }
      },
    });
  };

  const columns: TableColumnsType<DeletedBackup> = [
    { title: tr("名称"), dataIndex: 'name', key: 'name' },
    { title: tr("命名空间"), dataIndex: 'namespaceKey', key: 'namespaceKey', width: 130 },
    {
      title: tr("类型"),
      dataIndex: 'kind',
      key: 'kind',
      width: 90,
      render: (kind: string) => <Tag>{kind}</Tag>,
    },
    {
      title: tr("大小"),
      dataIndex: 'sizeBytes',
      key: 'sizeBytes',
      width: 100,
      responsive: ['md'],
      render: (value: number) => formatBytes(value),
    },
    {
      title: tr("删除时间"),
      dataIndex: 'deletedAt',
      key: 'deletedAt',
      width: 170,
      render: (value: string) => formatDateTime(value),
    },
    {
      title: tr("剩余宽限"),
      dataIndex: 'remainingGraceMinutes',
      key: 'remainingGraceMinutes',
      width: 140,
      render: (value: number) => (
        <Typography.Text type={isGraceCritical(value) ? 'danger' : undefined}>
          {remainingGraceLabel(value)}
        </Typography.Text>
      ),
    },
    {
      title: tr("文件状态"),
      dataIndex: 'fileIntact',
      key: 'fileIntact',
      width: 110,
      render: (intact: boolean) => {
        const meta = fileStateMeta(intact);
        return <Tag color={meta.color}>{meta.label}</Tag>;
      },
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 180,
      fixed: 'right',
      render: (_: unknown, row: DeletedBackup) => (
        <Space size="small">
          {canRestore ? (
            row.fileIntact ? (
              <Button
                type="link"
                size="small"
                icon={<UndoOutlined />}
                onClick={() => handleRestore(row)}
              >
                {tr("恢复")}
              </Button>
            ) : (
              <Tooltip title={tr("文件已缺失，只能清理")}>
                <Button type="link" size="small" icon={<UndoOutlined />} disabled>
                  {tr("恢复")}
                </Button>
              </Tooltip>
            )
          ) : null}
          {canPurge ? (
            <Button
              type="link"
              size="small"
              danger
              icon={<DeleteOutlined />}
              onClick={() => handlePurge(row)}
            >
              {tr("立即清理")}
            </Button>
          ) : null}
        </Space>
      ),
    },
  ];

  return (
    <Space direction="vertical" size={12} style={{ display: 'flex' }}>
      <Flex wrap gap={8} align="center">
        <Input
          allowClear
          placeholder={tr("按名称或节点名过滤回收站")}
          style={{ width: 240, maxWidth: '100%' }}
          value={keyword}
          onChange={(event) => {
            setKeyword(event.target.value);
            setPage(1);
          }}
        />
        <Button icon={<ReloadOutlined />} onClick={() => void deleted.refetch()}>
          {tr("刷新")}
        </Button>
        <span style={{ flex: 1 }} />
        {canManageGrace ? (
          <Button icon={<SettingOutlined />} onClick={() => setSettingsOpen(true)}>
            {tr("清理宽限期")}
          </Button>
        ) : null}
        {canPurge ? (
          <Button danger icon={<DeleteOutlined />} onClick={handlePurgeAll}>
            {tr("全部清理")}
          </Button>
        ) : null}
      </Flex>

      {deleted.isError ? (
        <Alert
          type="error"
          showIcon
          message={(deleted.error as Error).message}
          action={
            <Button size="small" onClick={() => void deleted.refetch()}>
              {tr("重试")}
            </Button>
          }
        />
      ) : null}

      <Table<DeletedBackup>
        scroll={{ x: 'max-content' }}
        rowKey="id"
        size="small"
        loading={deleted.isPending}
        dataSource={deleted.data?.items ?? []}
        columns={columns}
        pagination={responsivePagination(isMobile, {
          current: deleted.data?.page ?? page,
          pageSize: deleted.data?.pageSize ?? pageSize,
          total: deleted.data?.total ?? 0,
          showTotal: (total: number) =>
            tr("共 {{arg0}} 条待清理记录", { arg0: total }),
          onChange: (nextPage: number, nextPageSize: number) => {
            setPage(nextPageSize === pageSize ? nextPage : 1);
            setPageSize(nextPageSize);
          },
        })}
        locale={{ emptyText: tr("回收站为空") }}
      />

      <RetentionSettingsModal open={settingsOpen} onClose={() => setSettingsOpen(false)} />
    </Space>
  );
}
```

- [ ] **Step 3: 备份页卡片套 Tabs**

`web/src/features/backups/BackupsPage.tsx`：

1. import 增加 `Tabs`（antd import 列表）与：

```tsx
import { BackupRecyclePanel } from './BackupRecyclePanel';
```

2. 权限布尔区（`const canDelete = ...` 之后）加：

```tsx
  const canRestore = useAuthStore((state) => state.hasPermission(PERMISSIONS.backupRestore));
  const canPurge = useAuthStore((state) => state.hasPermission(PERMISSIONS.backupPurge));
  const canManageGrace = useAuthStore((state) =>
    state.hasPermission(PERMISSIONS.retentionCleanupManage),
  );
  const [activeTab, setActiveTab] = useState<'list' | 'recycle'>('list');
```

3. 把「备份」卡片体改成 Tabs：保留 `<Card size="small" title={tr("备份")} ...>` 外壳，`extra` 改为仅列表页显示新建按钮：

```tsx
        extra={
          activeTab === 'list' && canCreate ? (
            <Button type="primary" icon={<PlusOutlined />} onClick={openBackupForm}>
              {tr("新建备份")}</Button>
          ) : null
        }
```

4. 卡片 children 由「原 `<Space>` 工具栏 + `Alert` + `<Table<BackupSummary>>`」整段包成列表页签；结构如下（把原三段原样缩进到 `children` 内，不要改写内容）：

```tsx
        <Tabs
          activeKey={activeTab}
          onChange={(key) => setActiveTab(key as 'list' | 'recycle')}
          items={[
            {
              key: 'list',
              label: tr("备份记录"),
              children: (
                <>
                  {/* 原有「筛选/刷新」<Space> 工具栏 */}
                  {/* 原有 backups.isError 的 <Alert> */}
                  {/* 原有 <Table<BackupSummary>> */}
                </>
              ),
            },
            ...(canRestore
              ? [
                  {
                    key: 'recycle',
                    label: tr('回收站'),
                    children: (
                      <BackupRecyclePanel
                        appKey={appKey}
                        canRestore={canRestore}
                        canPurge={canPurge}
                        canManageGrace={canManageGrace}
                      />
                    ),
                  },
                ]
              : []),
          ]}
        />
```

- [ ] **Step 4: 前端校验**

```bash
cd "D:/SVNCheckOut/WorkSpace/AppFrameworkCrossPlatform/appframework-platform/web"
npm run typecheck
npm run lint
```

Expected: 0 错误、0 警告。

- [ ] **Step 5: 提交**

```bash
git add web/src/shared/ui/RetentionSettingsModal.tsx web/src/features/backups
git commit -m "feat: 备份页回收站页签与宽限期弹窗"
```

---

### Task 8: 文档回收站面板与页签

**Files:**
- Create: `web/src/features/documents/DocumentRecyclePanel.tsx`
- Modify: `web/src/features/documents/DocumentsPage.tsx`

**Interfaces:**
- Consumes: Task 6 的 `documentsApi.*`、`recycleUi`、`RetentionSettingsModal`。
- Produces: `<DocumentRecyclePanel appKey canRestore canPurge canManageGrace />`。

- [ ] **Step 1: 写文档回收站面板**

新建 `web/src/features/documents/DocumentRecyclePanel.tsx`：

```tsx
import { tr } from '../../i18n/translate';
import {
  DeleteOutlined,
  ReloadOutlined,
  SettingOutlined,
  UndoOutlined,
} from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  App,
  Button,
  Flex,
  Input,
  Space,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { TableColumnsType } from 'antd';
import { useState } from 'react';
import { documentsApi } from '../../shared/api/documents';
import type { DeletedDocument } from '../../shared/api/types';
import { formatBytes, formatDateTime } from '../../shared/format';
import { fileStateMeta, isGraceCritical, remainingGraceLabel } from '../../shared/recycleUi';
import { responsivePagination } from '../../shared/responsive';
import { RetentionSettingsModal } from '../../shared/ui/RetentionSettingsModal';
import { useResponsive } from '../../shared/useResponsive';

interface DocumentRecyclePanelProps {
  appKey: string;
  canRestore: boolean;
  canPurge: boolean;
  canManageGrace: boolean;
}

/** 文档回收站：与备份同构，文件完好按文档当前文件 + 全部修订判断。 */
export function DocumentRecyclePanel({
  appKey,
  canRestore,
  canPurge,
  canManageGrace,
}: DocumentRecyclePanelProps) {
  const { message, modal } = App.useApp();
  const { isMobile } = useResponsive();
  const queryClient = useQueryClient();
  const [keyword, setKeyword] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [settingsOpen, setSettingsOpen] = useState(false);

  const deleted = useQuery({
    queryKey: ['documents', 'recycle', appKey, keyword, page, pageSize],
    queryFn: ({ signal }) =>
      documentsApi.listDeletedDocuments(
        { keyword: keyword.trim() || undefined, page, pageSize },
        signal,
      ),
  });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['documents'] });
  };

  const handleRestore = (row: DeletedDocument) => {
    modal.confirm({
      title: tr("恢复文档「{{arg0}}」？", { arg0: row.title }),
      content: tr("恢复后文档与全部修订重新出现在列表与客户端清单中。"),
      okText: tr("恢复"),
      cancelText: tr("取消"),
      onOk: async () => {
        try {
          await documentsApi.restoreDocument(row.id);
          message.success(tr("文档已恢复"));
          await refresh();
        } catch (error) {
          message.error(error instanceof Error ? error.message : tr("恢复失败"));
        }
      },
    });
  };

  const handlePurge = (row: DeletedDocument) => {
    modal.confirm({
      title: tr("立即清理「{{arg0}}」？此操作不可恢复。", { arg0: row.title }),
      okText: tr("立即清理"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: async () => {
        try {
          const result = await documentsApi.purgeDocument(row.id);
          message.success(
            tr("已清理 {{arg0}} 条记录、回收 {{arg1}} 个文件", {
              arg0: result.rowsDeleted,
              arg1: result.filesDeleted,
            }),
          );
          await refresh();
        } catch (error) {
          message.error(error instanceof Error ? error.message : tr("清理失败"));
        }
      },
    });
  };

  const handlePurgeAll = () => {
    modal.confirm({
      title: tr("清理回收站全部 {{arg0}} 条记录？此操作不可恢复。", {
        arg0: deleted.data?.total ?? 0,
      }),
      okText: tr("全部清理"),
      okButtonProps: { danger: true },
      cancelText: tr("取消"),
      onOk: async () => {
        try {
          const result = await documentsApi.purgeAllDocuments();
          message.success(
            tr("已清理 {{arg0}} 条记录、回收 {{arg1}} 个文件", {
              arg0: result.rowsDeleted,
              arg1: result.filesDeleted,
            }),
          );
          await refresh();
        } catch (error) {
          message.error(error instanceof Error ? error.message : tr("清理失败"));
        }
      },
    });
  };

  const columns: TableColumnsType<DeletedDocument> = [
    { title: tr("名称"), dataIndex: 'title', key: 'title' },
    { title: tr("集合"), dataIndex: 'collectionKey', key: 'collectionKey', width: 130 },
    {
      title: tr("大小"),
      dataIndex: 'sizeBytes',
      key: 'sizeBytes',
      width: 100,
      responsive: ['md'],
      render: (value: number) => formatBytes(value),
    },
    {
      title: tr("删除时间"),
      dataIndex: 'deletedAt',
      key: 'deletedAt',
      width: 170,
      render: (value: string) => formatDateTime(value),
    },
    {
      title: tr("剩余宽限"),
      dataIndex: 'remainingGraceMinutes',
      key: 'remainingGraceMinutes',
      width: 140,
      render: (value: number) => (
        <Typography.Text type={isGraceCritical(value) ? 'danger' : undefined}>
          {remainingGraceLabel(value)}
        </Typography.Text>
      ),
    },
    {
      title: tr("文件状态"),
      dataIndex: 'fileIntact',
      key: 'fileIntact',
      width: 110,
      render: (intact: boolean) => {
        const meta = fileStateMeta(intact);
        return <Tag color={meta.color}>{meta.label}</Tag>;
      },
    },
    {
      title: tr("操作"),
      key: 'actions',
      width: 180,
      fixed: 'right',
      render: (_: unknown, row: DeletedDocument) => (
        <Space size="small">
          {canRestore ? (
            row.fileIntact ? (
              <Button
                type="link"
                size="small"
                icon={<UndoOutlined />}
                onClick={() => handleRestore(row)}
              >
                {tr("恢复")}
              </Button>
            ) : (
              <Tooltip title={tr("文件已缺失，只能清理")}>
                <Button type="link" size="small" icon={<UndoOutlined />} disabled>
                  {tr("恢复")}
                </Button>
              </Tooltip>
            )
          ) : null}
          {canPurge ? (
            <Button
              type="link"
              size="small"
              danger
              icon={<DeleteOutlined />}
              onClick={() => handlePurge(row)}
            >
              {tr("立即清理")}
            </Button>
          ) : null}
        </Space>
      ),
    },
  ];

  return (
    <Space direction="vertical" size={12} style={{ display: 'flex' }}>
      <Flex wrap gap={8} align="center">
        <Input
          allowClear
          placeholder={tr("按标题或文件名过滤回收站")}
          style={{ width: 240, maxWidth: '100%' }}
          value={keyword}
          onChange={(event) => {
            setKeyword(event.target.value);
            setPage(1);
          }}
        />
        <Button icon={<ReloadOutlined />} onClick={() => void deleted.refetch()}>
          {tr("刷新")}
        </Button>
        <span style={{ flex: 1 }} />
        {canManageGrace ? (
          <Button icon={<SettingOutlined />} onClick={() => setSettingsOpen(true)}>
            {tr("清理宽限期")}
          </Button>
        ) : null}
        {canPurge ? (
          <Button danger icon={<DeleteOutlined />} onClick={handlePurgeAll}>
            {tr("全部清理")}
          </Button>
        ) : null}
      </Flex>

      {deleted.isError ? (
        <Alert
          type="error"
          showIcon
          message={(deleted.error as Error).message}
          action={
            <Button size="small" onClick={() => void deleted.refetch()}>
              {tr("重试")}
            </Button>
          }
        />
      ) : null}

      <Table<DeletedDocument>
        scroll={{ x: 'max-content' }}
        rowKey="id"
        size="small"
        loading={deleted.isPending}
        dataSource={deleted.data?.items ?? []}
        columns={columns}
        pagination={responsivePagination(isMobile, {
          current: deleted.data?.page ?? page,
          pageSize: deleted.data?.pageSize ?? pageSize,
          total: deleted.data?.total ?? 0,
          showTotal: (total: number) =>
            tr("共 {{arg0}} 条待清理记录", { arg0: total }),
          onChange: (nextPage: number, nextPageSize: number) => {
            setPage(nextPageSize === pageSize ? nextPage : 1);
            setPageSize(nextPageSize);
          },
        })}
        locale={{ emptyText: tr("回收站为空") }}
      />

      <RetentionSettingsModal open={settingsOpen} onClose={() => setSettingsOpen(false)} />
    </Space>
  );
}
```

- [ ] **Step 2: 文档页卡片套 Tabs**

`web/src/features/documents/DocumentsPage.tsx`：

1. import 增加 `Tabs` 与：

```tsx
import { DocumentRecyclePanel } from './DocumentRecyclePanel';
```

2. 权限区（`const canDelete = ...` 之后）加：

```tsx
  const canRestore = useAuthStore((state) => state.hasPermission(PERMISSIONS.documentRestore));
  const canPurge = useAuthStore((state) => state.hasPermission(PERMISSIONS.documentPurge));
  const canManageGrace = useAuthStore((state) =>
    state.hasPermission(PERMISSIONS.retentionCleanupManage),
  );
  const [activeTab, setActiveTab] = useState<'list' | 'recycle'>('list');
```

3. 「文档」卡片的 `extra` 改为仅列表页显示上传按钮：

```tsx
        extra={
          activeTab === 'list' && canManage ? (
            <Button type="primary" icon={<PlusOutlined />} onClick={openUploadForm}>
              {tr("上传文档")}</Button>
          ) : null
        }
```

4. 卡片 children 换成 Tabs（原工具栏 + 告警 + 表格整体缩进进 `list` 页签）：

```tsx
        <Tabs
          activeKey={activeTab}
          onChange={(key) => setActiveTab(key as 'list' | 'recycle')}
          items={[
            {
              key: 'list',
              label: tr("文档列表"),
              children: (
                <>
                  {/* 原有「集合筛选/关键词/刷新」<Space> 工具栏 */}
                  {/* 原有 documents.isError 的 <Alert> */}
                  {/* 原有 <Table<DocumentSummary>> */}
                </>
              ),
            },
            ...(canRestore
              ? [
                  {
                    key: 'recycle',
                    label: tr('回收站'),
                    children: (
                      <DocumentRecyclePanel
                        appKey={appKey}
                        canRestore={canRestore}
                        canPurge={canPurge}
                        canManageGrace={canManageGrace}
                      />
                    ),
                  },
                ]
              : []),
          ]}
        />
```

- [ ] **Step 3: 前端校验**

```bash
cd "D:/SVNCheckOut/WorkSpace/AppFrameworkCrossPlatform/appframework-platform/web"
npm run typecheck
npm run lint
npm run test
```

Expected: tsc 0 错误、eslint 0 警告、vitest 全绿（含 `permissionDescriptions.test.ts`）。

- [ ] **Step 4: 提交**

```bash
git add web/src/features/documents
git commit -m "feat: 文档页回收站页签"
```

---

### Task 9: 文档更新与全量绿灯

**Files:**
- Modify: `docs/integration/04-backups.md`
- Modify: `docs/integration/05-documents.md`
- Modify: `docs/平台删除能力_分批执行计划.md`

**Interfaces:**
- Consumes: 前 8 个任务的全部实现。
- Produces: 接口文档与执行计划与代码一致。

- [ ] **Step 1: 更新备份接口文档**

`docs/integration/04-backups.md` 的「删除与清理」章节补：

```markdown
### 回收站（软删除与恢复）

删除备份只打 `deleted_at`，宽限期（默认 30 分钟，可在管理台「清理宽限期」编辑，平台全部应用生效）内可从回收站恢复：

| 端点 | 权限 | 说明 |
|---|---|---|
| `GET /api/v1/admin/backups/deleted` | `backup.restore` | 回收站分页；返回删除时间、剩余宽限（服务端计算）与文件完好标记 |
| `POST /api/v1/admin/backups/{id}/restore` | `backup.restore` | 先验证 Blob 存在；缺失返回 `BackupsBlobReclaimed`(409) |
| `POST /api/v1/admin/backups/{id}/purge` | `backup.purge` | 立即物理清理单条（先删文件后删行） |
| `POST /api/v1/admin/backups/purge-all` | `backup.purge` | 立即清理当前应用全部回收站行（cutoff=now） |

宽限期到期后仍由后台清理任务自动执行；「立即清理」只是跳过等待。恢复不发布事件；恢复可能超过命名空间 `max_versions`，后续新建备份会被拒绝直到删除旧备份。
```

错误码表补：

```markdown
| `409` | `BackupsBlobReclaimed` | 备份物理文件已被清理，无法恢复 |
```

- [ ] **Step 2: 更新文档接口文档**

`docs/integration/05-documents.md` 的「更新、删除与清理」章节补：

```markdown
### 回收站（软删除与恢复）

删除文档时文档行与全部修订行盖同一个 `deleted_at`；宽限期内可整体恢复：

| 端点 | 权限 | 说明 |
|---|---|---|
| `GET /api/v1/admin/documents/deleted` | `document.restore` | 回收站分页；文件完好 = 文档当前文件与全部修订文件都存在 |
| `POST /api/v1/admin/documents/{id}/restore` | `document.restore` | 逐修订验文件，任一缺失返回 `DocumentsFileReclaimed`(506) |
| `POST /api/v1/admin/documents/{id}/purge` | `document.purge` | 立即清理文档与全部修订 |
| `POST /api/v1/admin/documents/purge-all` | `document.purge` | 立即清理当前应用全部回收站行 |

恢复后若 `visibility=client`，服务端广播 `DeviceHubEvents.DocumentChanged`（客户端回拉 manifest）；推送失败不影响落库。
```

错误码表补：

```markdown
| `409` | `DocumentsFileReclaimed` | 文档或修订物理文件已被清理，无法恢复 |
```

配置项章节补：

```markdown
| `Documents:CleanupGraceMinutes` | 30 | 出厂默认宽限期；管理台配置后以 `retention_settings` 表为准 |
```

备份文档配置项同理补 `Backups:CleanupGraceMinutes` 说明。

- [ ] **Step 3: 更新执行计划**

`docs/平台删除能力_分批执行计划.md` 末尾追加：

```markdown
## 回收站批次（2026-11-20）

- 软删除行在宽限期内可见、可恢复；宽限期改为库内配置（`retention_settings`），管理台可编辑。
- 新增 `backup.restore` / `backup.purge` / `document.restore` / `document.purge` / `retention.cleanup.manage`。
- 定时清理与「立即清理」共用执行器；命名空间/集合删除仍以「含待清理行」阻止。
```

- [ ] **Step 4: 全量服务端绿灯**

```bash
cd "D:/SVNCheckOut/WorkSpace/AppFrameworkCrossPlatform/appframework-platform"
dotnet build Platform.slnx -c Release --disable-build-servers -v:q
```

```bash
dotnet test tests/Platform.UnitTests/Platform.UnitTests.csproj -c Release --no-build --disable-build-servers -v:q
```

```bash
dotnet test tests/Platform.ContractTests/Platform.ContractTests.csproj -c Release --no-build --disable-build-servers -v:q
```

```bash
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj -c Release --no-build --disable-build-servers -v:q
```

Expected: 全部 PASS，0 警告。Postgres 用例若本机没有 PG 容器则跳过（与既有基线一致）。

- [ ] **Step 5: 前端全量绿灯**

```bash
cd "D:/SVNCheckOut/WorkSpace/AppFrameworkCrossPlatform/appframework-platform/web"
npm run verify
```

Expected: `tokens:check`、`eslint`、`tsc`、`vitest` 全绿。

- [ ] **Step 6: 浏览器验收（需人工起栈）**

1. 启动 API 与 `npm run dev`，用 `Admin@2026` 登录（首次登录会落到改密页，别真改密）。
2. 备份页：删除一份备份 →「回收站」页签出现该行，剩余宽限与文件状态正确 → 恢复 → 回到「备份记录」。
3. 大文件备份删除后，手工删掉 Blob 文件 → 回收站行显示「文件已缺失」、恢复按钮禁用带 Tooltip → 立即清理成功。
4. 全部清理：多删两条 → 确认框显示总条数 → 执行后回收站清空。
5. 清理宽限期：改为 1 分钟后保存 → 再删一条，剩余宽限 ≤5 分钟标红；刷新页面设置仍为库内值。
6. 文档页同构走一遍；文档恢复后若原 `visibility=client`，客户端清单接口能重新看到该文档。
7. 用只读账号登录：看不到「回收站」页签，直接调 `GET /deleted` 返回 403。

- [ ] **Step 7: 提交**

```bash
git add docs
git commit -m "docs: 回收站端点、宽限期配置与执行计划"
```

---

## Self-Review

**1. Spec coverage**

| Spec 条目 | 覆盖任务 |
|---|---|
| §3 `retention_settings` 表 + Kernel 抽象 + 迁移 | Task 2 |
| §4.1 清理执行器抽取（定时/手动共用） | Task 3、4、5 |
| §4.2 备份端点（列表/恢复/清理/全部清理） | Task 4 |
| §4.2 文档端点 + 修订一起恢复 + `DocumentChanged` | Task 5 |
| §4 错误码 `BackupsBlobReclaimed` / `DocumentsFileReclaimed` | Task 1 |
| §5 四码 + `retention.cleanup.manage` + 种子分配 | Task 1 |
| §5 审计动作串 | Task 2、4、5 |
| §5 权限文案双语 | Task 6 |
| §6 页签、面板、弹窗、权限门控、i18n | Task 7、8 |
| §6 `GET|PUT /admin/retention/settings` | Task 2 |
| §7.4 命名空间文案/注释修正 | Task 4 |
| §8 服务端/前端测试 | Task 2–8 |
| §9 文档更新 | Task 9 |
| §10 不做项 | 计划内无多选恢复、无新事件、无 appKey 级宽限、未改 delete 角色分配 |

**2. Placeholder scan**

计划中无 TBD/TODO；所有新增文件给出可编译代码，所有修改点给出锚点与替换代码；Task 7/8 中「原有区块整体缩进进页签」是纯机械搬移，已给出搬移边界（工具栏 / Alert / Table 三段）与目标结构。

**3. Type consistency**

- `PurgeResultDto(RowsDeleted, FilesDeleted)` 服务端与 `PurgeResult { rowsDeleted, filesDeleted }` 前端一致。
- `DeletedBackupDto`/`DeletedDocumentDto` 字段与两个面板的列定义一一对应。
- `IRetentionSettingsStore.SetGraceMinutesAsync` 在 Task 2 定义，Task 3 的测试替身与 Task 2 端点均使用同一签名。
- `BackupService.ToDto`、`DocumentService.ToSummary` 均为 `internal static`，回收站服务通过 `BackupService.ToDto(...)` / `DocumentService.ToSummary(...)` 调用。
- `DocumentChangeNotifier` 在 Task 5 抽出后，`DocumentService` 与 `DocumentRecycleService` 使用同一方法名 `NotifyClientChangedAsync`。

**4. 已知风险与对策**

- InMemory 提供程序对 `DocumentRevision.Document.AppKey` 的导航过滤可用；Task 5 的服务测试覆盖了按 appKey 作用域。
- `PlatformApiFactory` 的宽限期配置为 1 分钟，端点测试里「剩余宽限」断言用 `InRange(0, 1)` 避免时钟抖动。
- 浏览器验收需要真实起栈（本机 PostgreSQL），自动化测试不覆盖视觉效果。

---

## Execution Handoff

实施方式二选一：

1. **Subagent-Driven（推荐）** —— 每个任务派一个新子代理，任务间做两阶段评审，迭代快、上下文干净。
2. **Inline Execution** —— 在当前会话按 `superpowers:executing-plans` 批量执行，设检查点评审。

选哪种？
