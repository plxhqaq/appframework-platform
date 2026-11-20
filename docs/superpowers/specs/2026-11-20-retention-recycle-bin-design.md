# 备份/文档「回收站」能力设计（retention recycle bin）

- 日期：2026-11-20
- 状态：已评审（决策点已与用户逐条确认）
- 涉及模块：`Platform.Modules.Backups`、`Platform.Modules.Documents`、`Platform.Kernel`、`Platform.Infrastructure`、`Platform.Workers`、`web/` 管理台
- 前置事实：删除能力两批已落地（备份/文档 DELETE 为软删除 + 后台清理），但宽限期是静态配置、回收站不可见、无恢复路径，导致「宽限期」名义上是反悔窗口、实际只是不可见期。

## 1. 问题与目标

现状（设计评审时核实）：

- 软删除只覆盖 `backups` / `documents` / `document_revisions` 三张表，靠可空列 `deleted_at` 表达；到期后由 `RetentionWorker`（默认 600s 一轮）执行 `BackupCleanupTask` / `DocumentsCleanupTask`，先删 Blob 再删行。
- 宽限期只有 `BackupsOptions.CleanupGraceMinutes` / `DocumentsOptions.CleanupGraceMinutes`（appsettings 各 30），管理台不可见不可改。
- 所有读路径带 `DeletedAt == null`，且全仓无 restore/undelete 端点 → 宽限期内数据对管理台与客户端均不可见、不可拉回。
- `BackupNamespaceService` 注释声称「软删除的备份仍占配额、可被恢复」，代码无此路径（注释与实现不一致，本次一并修正）。

目标：

1. 宽限期可在管理台修改，且真正成为可恢复窗口。
2. 备份、文档各提供回收站列表 + 恢复 + 立即清理 + 全部清理。
3. 恢复语义可信：能恢复出来的记录一定可用（文件必须仍在）。

## 2. 决策记录（已与用户确认）

| 决策点 | 结论 |
|---|---|
| 宽限期粒度 | **域级单值**：backups 一个值、documents 一个值；appsettings 的 `CleanupGraceMinutes` 降级为「库里没配时的出厂默认」 |
| 恢复遇文件缺失 | **先验文件，缺失即拒绝恢复**，回新错误码，提示改用「立即清理」收掉该行 |
| 权限粒度 | 新增 `backup.restore` / `backup.purge` / `document.restore` / `document.purge` 四码，授予应用管理员及以上（现场工程师不授予）；宽限期编辑单码 `retention.cleanup.manage`，`PlatformAdminOnly` |
| 立即清理边界 | 行内单条清理 + 回收站顶部「全部清理」（cutoff=now、限定当前 appKey），**复用**清理任务抽出的执行器循环 |

## 3. 数据模型

新增表 `retention_settings`（Platform.Infrastructure 持有迁移与模型配置）：

| 列 | 类型 | 说明 |
|---|---|---|
| `module_key` | text PK | `"backups"` / `"documents"` |
| `cleanup_grace_minutes` | int | 软删除到物理清理的宽限期（分钟），最小 1 |
| `updated_at` | timestamptz | 最近一次修改时间 |
| `updated_by` | text | 最近一次修改人（登录名） |

Kernel 新增抽象（与 `IRetentionCleanupTask` 同层）：

```csharp
public interface IRetentionSettingsStore
{
    Task<int?> GetGraceMinutesAsync(string moduleKey, CancellationToken ct = default);
    Task SetGraceMinutesAsync(string moduleKey, int minutes, string updatedBy, CancellationToken ct = default);
}
```

读取规则：`GetGraceMinutesAsync` 返回 `null` 时，调用方回落对应 `*Options.CleanupGraceMinutes`。清理任务每 600s 执行一次、宽限期接口只在编辑时读，**不做缓存**。

迁移：在现有单基线之后追加一笔 EF 迁移（`--configuration Release` 生成）创建该表。

## 4. 服务端设计

### 4.1 清理执行器抽取

把 `BackupCleanupTask.RunAsync` / `DocumentsCleanupTask.RunAsync` 的批循环抽成执行器（模块内 `PurgeExecutor`，或任务内的静态方法），签名形态：

```csharp
Task<RetentionCleanupResult> PurgeAsync(DateTimeOffset cutoff, string? appKey, CancellationToken ct)
```

- 定时任务：`PurgeAsync(now - graceMinutes, appKey: null)`。
- 手动全部清理：`PurgeAsync(now, 当前 appKey)`。
- 不破坏既有不变式：每批 200、**先删文件后删行**、文件删失败保留行下轮重试。
- 执行器与任务都必须遵循「批次继续扫描直到空」的既有语义。

### 4.2 端点（admin，两个模块同构）

以备份为例；文档侧把 `backups` 换成 `documents`、错误码换成 `DocumentsFileReclaimed`。

| 端点 | 权限 | 语义 |
|---|---|---|
| `GET /api/v1/admin/backups/deleted` | `backup.restore` | 回收站分页列表：名称、命名空间、删除时间、剩余宽限（服务端计算）、文件完好标记（**当前页逐行 `ExistsAsync`，每页最多 pageSize 次 stat**）；支持 appKey 上下文、关键字、分页 |
| `POST /api/v1/admin/backups/{id}/restore` | `backup.restore` | 先验文件（`BlobPath` 非空时 `IBlobStorage.ExistsAsync`；内联负载 `PayloadJson` 场景跳过），缺失回 `BackupsBlobReclaimed`；通过则清 `DeletedAt` 并回 DTO |
| `POST /api/v1/admin/backups/{id}/purge` | `backup.purge` | 单条立即物理删除：先删文件后删行；仅接受已软删除的行 |
| `POST /api/v1/admin/backups/purge-all` | `backup.purge` | 全部清理：`PurgeAsync(now, 当前 appKey)`，返回删除行数/文件数 |

文档恢复额外要求：

- 文档行与其**全部修订行**在同一事务里清 `DeletedAt`（删除时是一起盖的同一个时间戳）。
- 逐修订验文件；任一缺失即整体拒绝（不做部分恢复）。
- 恢复后若 `visibility=client`，调用与删除/更新同一条 `NotifyClientChangedAsync`（`DeviceHubEvents.DocumentChanged` + `DocumentChangedNotification(appKey, collectionKey)`，推送失败不影响落库）；备份恢复不发布事件，也不新增任何事件类型。

错误码（`src/Platform.Contracts/ApiErrorCode.cs`，按既有分段）：

- `BackupsBlobReclaimed = 409`（备份段 400–499）
- `DocumentsFileReclaimed = 506`（文档段 500–599）

文案：恢复被拒时提示「物理文件已被回收，无法恢复；请改用『立即清理』删除该记录」。

## 5. 权限与审计

- 权限码（`Platform.Contracts/Permissions/PermissionCatalog.cs`，单数域命名，插空 sort，不重排既有编号）：
  - `backup.restore`(560)、`backup.purge`(570) → Backups 模块组
  - `document.restore`(650)、`document.purge`(660) → Documents 模块组
  - `retention.cleanup.manage`(915) → 平台专属模块组，`PlatformAdminOnly: true`
- 角色种子（`IdentitySeeder`）：
  - 平台管理员：通配 `*`，自动获得。
  - 应用管理员：追加四码（restore/purge）；**不追加** `backup.delete` / `document.delete`（保持现状，避免本次顺手改既有权限语义）。
  - 现场工程师、只读观察员：不追加。
- 审计动作串沿用端点内联复数写法（既有事实，不借此统一）：
  - `"backups.restore"`、`"backups.purge"`、`"documents.restore"`、`"documents.purge"`、`"retention.settings.update"`
  - `purge-all` 记一条汇总（含行数、文件数）。
- 权限文案：`web/src/shared/permissions.ts` 加键；en-US / zh-CN 的 `permissions.ts` 各补 5 条说明（前端测试强制每个码都有双语说明）。

## 6. 管理台 UI

- 备份页（`BackupsPage.tsx`）：「备份」卡片表体套 `Tabs`：`备份记录`（现有列表）/ `回收站`；仿 `IdentityPage` 的 `items` + 权限过滤写法。文档页（`DocumentsPage.tsx`）同形（`文档列表` / `回收站`）。
- 回收站面板独立组件：`BackupRecyclePanel.tsx` / `DocumentRecyclePanel.tsx`，props 收 appKey、权限布尔、宽限分钟；不自带外层 Card。挂载在父页面 Tabs 内。
- 回收站列：名称/标题、命名空间或集合、删除时间、剩余宽限（≤5 分钟标红）、文件状态（完好 / 文件已缺失）、操作（恢复 / 立即清理）。
  - 文件缺失的行：状态标红「文件已缺失，只能清理」，恢复按钮禁用 + Tooltip 说明原因。
  - 恢复/清理均走 `Modal.confirm`（danger），文案明说立即清理不可恢复；行内按钮沿用 link 按钮 + Tooltip 包裹禁用原因。
- 回收站工具条：
  - `全部清理`（danger，确认框显示将删除条数）。
  - `清理宽限期` 按钮（`retention.cleanup.manage` 可见）→ 共用弹窗 `RetentionSettingsModal`：同时显示备份与文档两个 `InputNumber`（分钟，最小 1），注明「作用于平台全部应用」；保存走同一 `PUT`。
- 收敛页签可见性：没有 restore/purge 权限的用户不显示回收站页签（列表端点是 `backup.restore` 门控，页面按同一布尔隐藏）。
- 所有新文案走 `tr()` 中文原文键，中英文同步。

### 端点汇总（含配置读写）

| 端点 | 权限 | 说明 |
|---|---|---|
| `GET /api/v1/admin/retention/settings` | `retention.cleanup.manage` | 返回两域宽限分钟与默认来源（库 or 出厂默认） |
| `PUT /api/v1/admin/retention/settings` | `retention.cleanup.manage` | 整体提交两个值，避免只改一半；审计 `retention.settings.update` |

## 7. 语义边界（必须在实现与文案中体现）

1. **文件缺失窗口**：清理任务先删文件后删行，崩溃或 `SaveChanges` 失败会留下「行在、文件没了」的行。这类行在回收站可见但不可恢复，只能清理。
2. **恢复后超限**：命名空间版本数在新建时校验上限（达上限即拒绝新建，不是自动裁剪）。恢复会让版本数超过 `max_versions`，后续新建备份被拒直到删除旧行；恢复确认框明示，服务端不拦截（人工恢复优先于配额）。
3. **宽限期的真实含义**：从「软删除不可见」到「物理删除」之间的窗口，现在同时是可恢复窗口；到期仍由 `RetentionWorker` 自动执行。
4. **命名空间删除**：仍以「含已删除待清理的行」阻止删除（留痕口径不变）；`BackupNamespaceService` 的错误文案与注释改为「含待清理的已删除备份」，并修正「可被恢复」的注释为事实描述。

## 8. 测试计划

服务端：

- `RetentionCleanupTests`：补「库里配置了宽限期时覆盖 Options 默认」「库里无行时回落 Options」两条；既有构造直接传 `Options.Create(...)` 的用例保持不变。
- 新增恢复/清理用例：恢复清 `DeletedAt`（文档含全部修订）；文件缺失拒绝恢复（新错误码）；单条清理先文件后行；全部清理只作用于当前 appKey 且无视宽限期；恢复后超限可成功（语义断言）。
- Contract 测试：新权限码格式/模块归属/显示名键唯一；新错误码分段。
- 端点权限用例：无对应权限码返回 403。

前端：

- `recycleUi.ts` 纯函数（剩余宽限文案、≤5 分钟告警、文件缺失状态映射）+ vitest，仿 `packageUi.test.ts`。
- `permissionDescriptions.test.ts` 会强制五个新码的中英文说明存在。
- 浏览器验收：删除 → 回收站出现 → 恢复 → 回到列表；文件缺失行恢复按钮禁用；立即清理与全部清理；改宽限期后新删除行的剩余宽限随之变化。

## 9. 文档更新

- `docs/integration/04-backups.md` §5「删除与清理」、§6 错误码：补恢复/清理端点与宽限期可配置语义。
- `docs/integration/05-documents.md` §6「更新、删除与清理」、§7 错误码、§8 配置项：同上，补修订行一起恢复、client 可见性事件。
- `docs/平台删除能力_分批执行计划.md`：追加「回收站批次」段落。
- `docs/事件推送与实时连接_架构改造设计.md`：不新增事件，仅在需要时补一句「恢复复用 DocumentChanged」。

## 10. 不做（YAGNI）

- 不做按 appKey / 命名空间 / 集合粒度的宽限期配置。
- 不做回收站多选批量恢复。
- 不做新的 SignalR 事件类型。
- 不把审计动作串统一成单数。
- 不改 `backup.delete` / `document.delete` 的既有角色分配。

## 11. 实施批次建议

1. 数据与配置：`retention_settings` 表 + Kernel `IRetentionSettingsStore` + 迁移 + 设置读写端点 + 权限码/文案/种子。
2. 清理执行器抽取与清理任务改读库配置（保持既有测试绿灯）。
3. 备份域：回收站列表 + 恢复 + 单条/全部清理 + 错误码 + 测试。
4. 文档域：同形 + 修订行恢复 + `DocumentChanged` 事件 + 测试。
5. 管理台：页签、两个回收站面板、宽限期弹窗、权限门控、i18n、浏览器验收。
6. 文档与收尾：接口文档、执行计划文档、全量绿灯、提交。
