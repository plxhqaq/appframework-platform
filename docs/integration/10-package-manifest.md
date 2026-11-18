# 发布包与清单协议（B8 / B9）

> 本文是「发布包」接入的唯一口径：怎么把 Setup exe / msi / 增量 zip 变成 `release_packages`、
> 服务端如何识别清单、`check` 如何下发 `installer` / `incrementalPackage` / `updater`，
> 以及管理台/脚本的上传路径。文件级版本清单见 [`02-versions.md`](02-versions.md)。
> 增量 zip 的异步摄取见 §3.1，更新器版本线见 §4.1。

---

## 1. 模型与状态

```text
Release (channel + version, draft/published/archived)
 ├─ ReleaseArtifact / ReleaseFile      ← 文件级清单（B4，原样保留）
 └─ ReleasePackage                     ← 发布包（B8）
      kind=full        全量安装包，登记即 ready
      kind=incremental 增量 zip，登记后 pending，由摄取 Worker 转为 ready/failed

AppUpdaterRelease                      ← 更新器版本线（B9，独立于应用版本）
      每个 (appKey, target, version) 一份内容
```

- 同一版本可以登记多个包；每个 `(release_id, kind, target)` 只有一行 `is_active=true` 作为**启用包**，
  `check` 只下发启用包。新登记的同级包自动接管启用位；已有包可用
  `POST /packages/{id}/activate`（`versions.artifact.upload`，仅 `ready` 包）手动切回。
- 全量包 `target` 可为 `any`；增量包必须是对应运行时（`win-x64` 等六个白名单值）。
- 包字节仍按内容寻址存放（`versions/{appKey}/{sha256}`），重复内容不占额外空间。
- `status`：`pending` / `processing` / `ready` / `failed`。full 包登记即 `ready`；incremental 包
  进入摄取队列（§3.1）。`failed` 可由 `POST /packages/{id}/retry` 重新排队。
- 已发布/已归档版本不可删除包（`222`）；草稿版本的包可删（只删登记行，字节留给孤儿清理）。
- 向**已归档**版本再次登记包会把版本恢复为草稿（清空 `publishedAt` / `archivedAt`），
  用于误归档后的补救；核对后可重新发布。
- 列表按状态排序时已归档的行排在最后。

## 2. 清单来源与识别顺序

### 2.1 `af-package/1`（推荐）

zip 根目录（`app/` 前缀与大小写不敏感），或作为 sidecar JSON：

```json
{
  "schema": "af-package/1",
  "kind": "full",
  "appKey": "demo",
  "channelKey": "stable",
  "version": "1.2.3.0",
  "runtimeIdentifier": "win-x64",
  "description": "全功能 1.2.3.0 安装包（full）",
  "publishTime": "2026-11-13T10:00:00Z",
  "gitVersion": "1.2.3.0",
  "gitCommit": "abc123",
  "entryPoint": "App.exe",
  "files": [
    { "path": "App.exe", "sha256": "<64位小写十六进制>", "sizeBytes": 123456 }
  ]
}
```

- `kind` / `version` 必填；`appKey` / `channelKey` / `runtimeIdentifier` / `description` 可选，但出现即校验。
- `runtimeIdentifier` 决定登记时的 `target`（六个白名单值或 `any`），上传人无需再手选运行时。
- `description` 是发布说明，登记为草稿时写入版本 `notes`，管理台在草稿阶段可继续修正；不超过 4000 字符。
- `kind=incremental` 必须带非空 `files`；`kind=full` 可省略（安装器自包含）。
- 文件 `path` 归一后（`\ → /`、去 `./`、去 `app/` 顶层前缀）必须唯一且大小写不敏感不重复；
  拒绝绝对路径、`..`、空段、NUL。条目按 `path` 排序后计算 `manifest_hash`。

### 2.2 exe：PE `VERSIONINFO Comments`

```text
AF-PACKAGE/1|kind=full|version=1.2.3.0|channel=stable|rid=win-x64|description=…|entryPoint=Setup.exe
```

服务端按 PE/COFF 规范跨平台读取（不依赖 `FileVersionInfo`），资源上限 1 MB；解析失败按「无清单」处理。

### 2.3 旧格式适配（老构建脚本无需改动）

| 来源 | 标记 | 映射 |
|---|---|---|
| exe Comments | `AF-MANIFEST/1` 管道串 | `kind=full`，`branch → channelKey` |
| zip `build-info.json` | `AF-UPDATE-PACKAGE/1` | `kind=incremental`，配合 `manifest.json` 的 `entries[]` |
| zip `updater-info.json` | `version` / `entry` / `files[]` | `kind=updater`（B9） |
| sidecar `af-package.json` | 登记请求带 `manifestSha256` | 优先于包内清单 |

当前构建脚本在两个旧来源里都写 `runtimeIdentifier=win-x64` 与 `description`；老包没有这两段时，
全量包运行时回退 `any`、说明为空，增量包若也没有运行时则登记返回 `211`（只能由 API 调用方显式补
`target`，管理台上传口不再提供手选）。

### 2.4 识别顺序与失败口径

1. 请求带 `manifestSha256` → 读该 Blob（sidecar，需先用票据上传）。
2. zip → `af-package.json`，回退旧格式；exe → PE Comments，回退 `AF-MANIFEST/1`。
3. 都没有 → 仅 `kind=full` 允许**通过 API 人工登记**（`manifestSource=manual`，需显式传
   `channelKey` / `version`）；管理台上传口已不接受人工元数据，必须由包内或旁挂清单提供。
   增量包缺清单直接拒绝 `215`。

**安全上限**（均可配，见 §6）：清单 JSON ≤1 MB；zip 条目 ≤100 000；单条目 ≤4 GiB；
解压总量 ≤20 GiB；压缩比 ≤200:1；拒绝加密条目、符号链接与越界路径，越界返回 `220`（422）。

## 3. 登记链路（先传字节，再登记）

```bash
# 1) 申请一次性票据（先声明哈希与长度）
TICKET=$(curl -s -X POST http://localhost:5080/api/v1/admin/versions/uploads \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"sha256":"'$SHA'","sizeBytes":'$SIZE'}' | jq -r '.data.uploadUrl')

# 2) 直传裸字节（一次性；内容寻址去重）
curl -s -X PUT "http://localhost:5080$TICKET" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/octet-stream' \
  --data-binary @Setup-stable-1.2.3.exe

# 3) 登记发布包（只传哈希/长度/文件名，类型、渠道、版本、运行时与说明全部取自包内清单）
curl -s -X POST http://localhost:5080/api/v1/admin/versions/packages \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"sha256":"'$SHA'","sizeBytes":'$SIZE',"fileName":"Setup-stable-1.2.3.exe"}'
```

登记请求字段：

| 字段 | 必填 | 说明 |
|---|---|---|
| `sha256` / `sizeBytes` | 是 | Blob 必须已存在且长度一致，否则 `206` |
| `fileName` | 是 | 下载时 `Content-Disposition` 用；禁止路径分隔符与控制字符 |
| `kind` | 否 | 缺省取包内清单；`full` / `incremental`，显式值必须与清单一致（`224`） |
| `target` | 否 | 缺省取清单 `runtimeIdentifier`；full 仍为空则 `any`，incremental 缺失返回 `211` |
| `format` | 否 | 缺省按扩展名推断（exe/msi/msp/zip/7z/bin） |
| `channelKey` / `version` | 否 | 缺省取清单；没有清单的人工登记 full 包必须提供 |
| `manifestSha256` | 否 | sidecar 清单的哈希（需已上传） |
| `publish` | 否 | 默认 `false`，登记后留作草稿；`true` 且包 ready 时直接发布 |
| `notes` | 否 | 显式说明优先于清单 `description`；都不传则版本说明为空 |

行为要点：

- 版本不存在时自动创建**草稿**（`draft`），失败也可见；`publish=true` 时登记成功即发布。
- 管理台上传口固定 `publish=false` 且不传任何元数据：类型、目标、渠道、版本与说明都从包内清单读取，
  避免上传人填写与包内事实不一致。登记后进入草稿，管理员核对/修正说明后再走发布确认（§8）。
- 同类型同目标同 `sha256` 重放返回已有行（`reused=true`）并重新设为启用包；不同 `sha256` 会在同一
  `(kind, target)` 下新增一行并自动接管启用位，旧包保留可回退或手动切换。
- 清单与同 RID 已有产物的 `manifest_hash` 不一致 → `219`（发布不可变）。
- 文件名模板默认只告警；`Versions:RequirePackageFileNameMatch=true` 时返回 `218`。
  默认模板：`Setup-{channel}-{version}{ext}`、`Update-{channel}-{version}{ext}`、
  `AppFramework.Updater-{version}{ext}`。
- `kind=updater` 不在本端点受理（`224`），更新器走 §4.1 的独立版本线。

### 3.1 增量包摄取（pending → processing → ready / failed）

登记响应返回 `pending` 后，由后台 Worker（`ReleasePackageIngestionWorker`）异步处理：

1. **登记时先校验**：清单、kind、渠道/版本/RID、zip 结构、条目上限、压缩比、加密/符号链接、
   路径越界都在写库前完成。坏归档直接拒绝（`215` / `220` / `228`），**不会留下包行**。
2. **认领**：PostgreSQL 用 `UPDATE … FOR UPDATE SKIP LOCKED … RETURNING` 抢占一行并写
   `status=processing`、`locked_by`、`locked_until=now+IngestionLockMinutes`；InMemory 用进程内
   信号量 + 重新加载复核。多副本同时只有一个 Worker 处理同一包。
3. **校验内容**：按清单逐条比对 zip 条目——缺文件、哈希不符、长度不符、清单外文件都判失败
   （`225`，`errorMessage` 带首个差异路径）。
4. **落库**：`ReleaseArtifact`（按 target 复用或新建）+ `ReleaseFile`（按路径同步）在**同一事务**内
   写入，`manifest_hash` 与包清单一致；任一步失败整体回滚，不产生“清单有了文件没到”的中间态。
5. **完成**：`status=ready`、`fileCount`、`completedAt` 写入；若登记时 `publish=true`、包仍是启用包且版本
   仍是草稿，在提交后发布版本（`publishedNow`），随后才可被 `check` 下发。

失败与重试：

- 失败写 `status=failed` + `errorCode` / `errorMessage`，`check` 不会选中该包。
- `POST /api/v1/admin/versions/packages/{id}/retry`（`versions.artifact.upload`）把 `failed` 改回
  `pending` 并清空错误；非 `failed` 状态返回 `226`。重试是幂等的，重复点击只会重新排队。
- Worker 中途取消或进程崩溃：`processing` 行的租约到期后由 `RecoverStaleAsync` 回收；
  未超过尝试上限会重新排队，超过上限置 `failed`（`225`，原因写明尝试次数）。
- 剩余磁盘低于 `MinFreeDiskBytes` 时摄取失败为 `227`（507），清理磁盘后可直接重试。

## 4. `check` 的下发口径

`POST /api/v1/client/versions/check` 在原有清单字段外追加：

- `installer`：同 RID 的 `ready` full 包优先，其次 `target=any`；只考虑 `isActive` 的启用包。
- `incrementalPackage`：只认同 RID 的 **`ready`** 且 `isActive` 的增量包（摄取完成后才出现）；
  `pending` / `processing` / `failed` 以及未启用的包一律不下发。
- `hasUpdaterUpdate` / `updater`：`updater` 是当前 RID 实际会拿到的最新已发布更新器（同 RID 优先、
  再回退 `any`）；`hasUpdaterUpdate` 由 `updaterVersion` 与更新器版本按同一版本语义比较，客户端没传
  `updaterVersion` 或版本不可解析时为 `true`。没有更新器时两者分别为 `false` / `null`。
- 最新已发布版本**既没有该 RID 产物、也没有可用安装包**时：`hasUpdate=false`、`release=null`、
  `installer=null`（HTTP 200）。客户端据此判断「本平台没有我能用的包」，不要当成网络错误。
- 渠道里**没有任何已发布版本、但存在更新器**时同样返回 200（`hasUpdate=false`、`release=null`、
  `updater` 有值），不会返回 `212`——更新器升级不依赖应用版本。
- 有安装包但没有文件清单时 `release=null` 是正常形态：全新机直接下载安装包，
  已装机仍可走归档回退后的文件清单。

### 4.1 更新器版本线

更新器是**独立版本线**，不挂在 `Release` 下，也不进 `check` 的 `release` 字段。

| 方法与路径 | 权限码 | 行为 |
|---|---|---|
| `GET /api/v1/admin/versions/updaters?target=&status=` | `versions.release.read` | 分页列出（`published` / `archived`） |
| `POST /api/v1/admin/versions/updaters` | `versions.artifact.upload` | 登记（`Idempotency-Key`，内容须先经票据落盘） |
| `POST /api/v1/admin/versions/updaters/{id}/archive` | `versions.release.archive` | 归档（幂等），之后 `check` 回退到上一版 |
| `GET /api/v1/client/versions/updater?runtimeIdentifier=` | `versions.release.read` | 同 RID 优先、`any` 兜底的最新已发布更新器 |
| `GET /api/v1/client/versions/updaters/{id}/download` | `versions.artifact.download` | ETag / Range / 304，带原文件名与 `X-Content-Sha256` |

登记规则：

- 清单 `kind` 必须是 `updater`（`af-package/1`），否则 `224`；`appKey` 不一致 `216`；
  清单 RID 与请求 `target` 不一致 `217`。
- 版本来源顺序：`af-package.json` → 老 `updater-info.json` → PE `FileVersion` → 请求显式 `version`；
  都取不到合法版本时返回通用 `2`（400），要求手填。
- `target` 缺省取清单 RID，仍为空则 `any`；必须是六个 RID 或 `any`，否则 `211`。
- 同 `(target, version)` 只允许一份内容：`sha256` 相同返回已有行，不同返回 `219`。
- 包大小超限 `221`；内容缺失或长度不符 `206` / `210`。
- 归档后 `GET /client/versions/updater` 跳过该行回退到上一已发布版本；按 id 下载返回 `223`。

## 5. 包下载

```bash
curl -s -o Setup.exe -D headers.txt \
  -H "Authorization: Bearer $CLIENT_TOKEN" \
  "http://localhost:5080/api/v1/client/versions/packages/$PACKAGE_ID/download"
```

- 归属校验：包属于本应用、所属版本已发布、`status=ready`、Blob 存在；任一不过 `213`（404）。
- `ETag = "\"{sha256}\""`、`enableRangeProcessing: true` → `Range` 返回 `206`，
  `If-None-Match` 返回 `304`。
- `Content-Disposition` 带原文件名；`X-Content-Sha256` 回传整包哈希，方便断点续传后校验。
- 归档版本后包立即不可下载（`213`），`check` 回退到上一已发布版本。

## 6. 配置项（`Versions` 节点）

| 键 | 默认 | 说明 |
|---|---|---|
| `MaxPackageSizeBytes` | 4 GiB | 单包上限；票据放行取它与 `MaxFileSizeBytes` 的较大值 |
| `MaxExtractedBytesPerPackage` | 20 GiB | 增量包解压总量上限（B9 使用） |
| `MaxZipEntries` | 100000 | zip 条目数上限 |
| `MaxCompressionRatio` | 200 | 解压总量 / 压缩包大小 |
| `MinFreeDiskBytes` | 5 GiB | 摄取前最小剩余磁盘（B9） |
| `RetainRawPackage` | `true` | 摄取后保留原始包（B9） |
| `RequirePackageFileNameMatch` | `false` | 严格文件名模板，不匹配返回 `218` |
| `PackageFileNamePatterns` | 见 §3 | CSV，占位符 `{channel}` / `{version}` / `{kind}` / `{ext}` |
| `IngestionConcurrency` / `IngestionLockMinutes` | 1 / 10 | 摄取 Worker（B9） |

## 7. 错误码速查

| 码 | 名称 | HTTP | 触发 |
|---|---|---|---|
| `213` | `VersionsPackageNotFound` | 404 | 包不存在 / 未发布 / 已归档 / Blob 缺失 |
| `214` | `VersionsPackageNotReady` | 409 | 包仍在 `pending/processing/failed` |
| `215` | `VersionsPackageManifestMissing` | 422 | 缺清单且未声明 `kind=full`；增量包缺清单 |
| `216` | `VersionsPackageAppMismatch` | 422 | 清单 `appKey` 与当前应用不一致 |
| `217` | `VersionsPackageChannelVersionMismatch` | 422 | 清单与请求的渠道/版本/RID 不一致 |
| `218` | `VersionsPackageFileNameMismatch` | 422 | 严格模式下文件名不匹配模板 |
| `219` | `VersionsPackageManifestConflict` | 409 | 清单与既有产物不一致；并发写入冲突 |
| `220` | `VersionsPackageUnsafeArchive` | 422 | zip 越界/加密/符号链接等 |
| `221` | `VersionsPackageTooLarge` | 413 | 超过包上限 |
| `222` | `VersionsReleaseImmutable` | 409 | 已发布/已归档版本仍尝试删除包；已发布版本的增量包改文件清单 |
| `223` | `VersionsUpdaterNotFound` | 404 | 更新器不存在 / 已归档 / 没有可下发的最新版 |
| `224` | `VersionsPackageKindUnsupported` | 400 | `kind=updater` 等本端点不支持的包 |
| `225` | `VersionsPackageIngestionFailed` | 422 | 摄取失败：缺文件、哈希/长度不符、清单外文件、尝试次数耗尽 |
| `226` | `VersionsPackageStatusInvalid` | 409 | 非法状态迁移（如重试非 `failed` 包、启用非 `ready` 包） |
| `227` | `VersionsStorageInsufficient` | 507 | 摄取前剩余磁盘不足 |
| `228` | `VersionsPackageManifestInvalid` | 422 | 清单 JSON/字段不合法；zip 结构损坏（非“缺清单”） |

## 8. 管理台

「版本与渠道」页与「更新器」页覆盖全部管理端操作：

- 上传弹窗：只选文件与可选旁挂清单，**不填包类型/目标运行时/渠道/版本/说明**；浏览器分块计算 SHA-256
  并显示哈希/上传进度，>256 MB 给出提示。登记请求只有 `sha256` / `sizeBytes` / `fileName` /
  `manifestSha256`，`publish=false` 固定为草稿；成功后可直接「查看草稿版本」。
- 登记结果面板展示从清单解析出的渠道/版本/类型/运行时/大小与清单来源，帮助上传人确认包是否配对。
- 发布确认：版本详情与列表的「发布」都会先打开确认弹窗，展示渠道、版本、包与文件汇总；草稿阶段可
  修改发布说明（`PATCH /releases/{id}`），确认后保存说明并发布。已发布版本只读，不再提供改说明或重复发布。
- 增量包登记后弹窗轮询 `GET /packages/{id}`（3 秒），显示 `pending/processing/ready/failed`、
  清单文件数、`errorCode` 与原因；失败时原地「重试摄取」（`POST /packages/{id}/retry`）。
- 纯安装包版本（无文件级产物）的文件数与总大小由发布包汇总回填；单 exe 全量包显示 1 个文件与包字节数。
- 详情抽屉：kind / target / format / status / 清单来源 / 清单文件数 / 大小 / SHA-256 / 下载 / 删除
  （仅草稿）；有未完成增量包时每 3 秒自动刷新，失败包可直接重试。
- 状态颜色：待摄取、摄取中、可用、失败（失败时悬浮显示 `errorCode` 与原因）。
- 更新器页（路由 `/updaters`）：列表（目标/状态筛选）、上传、「最新」标记、下载、归档。最新标记
  由服务端 `/client/versions/updater` 逐 target 解析，与 `check` 的下发口径一致（含 `any` 兜底）。

相关文档：[`02-versions.md`](02-versions.md)（版本/渠道/文件清单）、
[`00-quickstart.md`](00-quickstart.md)（端到端最小命令）。
