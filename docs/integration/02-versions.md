# 版本、渠道与发布包（B4 / B8 / B9 / B10）

> 本文覆盖客户端集成的完整路径：**发布 → 检查更新 → 清单 → 下载 → 校验 → 回退**。
> 所有路由、权限码、字段名、错误码与默认值都取自当前源码
> （`Platform.Contracts/Versions/VersionContracts.cs`、`Platform.Modules.Versions`、
> `AppFramework.Platform.Client`）。最小可跑命令见 [`00-quickstart.md`](00-quickstart.md) 第 5 节；
> 安装包登记与清单协议见 [`10-package-manifest.md`](10-package-manifest.md)。

版本数据按**应用**隔离：渠道键只在应用内唯一，`stable` 在 `demo` 与在 `line-a` 是两个渠道。
应用键只从令牌的 `app` 声明取（端点里的 `RequireAppKey`），请求体与查询串里的 `appKey` 一律不被接受。

| 能力 | 管理端 | 客户端 |
|---|---|---|
| 渠道 | `/api/v1/admin/versions/channels` | — |
| 版本 | `/api/v1/admin/versions/releases` | `/api/v1/client/versions/check` |
| 清单 | `/api/v1/admin/versions/releases/{id}` | `/api/v1/client/versions/{channelKey}/{version}/manifest` |
| 上传 | `/api/v1/admin/versions/uploads` | — |
| 下载 | — | `/api/v1/client/versions/download/{sha256}` |
| 发布包 | `/api/v1/admin/versions/packages` | `/api/v1/client/versions/packages/{id}/download` |
| 浏览器下载 | `POST /api/v1/client/versions/{packages,updaters}/{id}/download-ticket` | `GET /api/v1/client/versions/downloads/{ticket}`（匿名） |

---

## 1. 概念：三条轴

**应用隔离轴**。令牌没有应用上下文时，本模块任何端点返回 `Forbidden`（码 `4`，HTTP 403），文案
「当前令牌没有应用上下文，无法访问版本资源。」切换方式见 00-quickstart 4.3 的
`POST /api/v1/admin/auth/context`。

**内容寻址**。文件字节按 SHA-256 存放，相对路径固定为 `versions/{appKey}/{sha256}`：同一应用内
相同内容只存一份，多个版本/产物引用同一哈希不重复占盘；不同应用互不可见，下载端点先确认该哈希属于
本应用的已发布版本。根目录由 `BlobStorage:RootPath` 决定（默认 `data/blobs`，相对进程基目录解析；
compose 里是 `BlobStorage__RootPath=/data/blobs` 挂在 `blobs` 卷上）。

**三态**（`VersionConventions` 的三个字符串常量）：`draft` 是 `publish=false` 的落点，对
`check`/`manifest`/`download` 全不可见；`published` 是 `check` 的候选集，也只有被已发布版本引用的
内容可下载；`archived` 移出下发序列，`check` 自动落到上一个已发布版本，即全量回退。版本重复判定只在
渠道内：`(ChannelId, Version)` 唯一，草稿也占位。

客户端下载语义如上受三态约束；管理台浏览器下载票据链路（`download-ticket` → `/downloads/{ticket}`）
不受三态限制：草稿与归档版本的安装包同样可下载核对，只受权限、包 `ready` 与 Blob 在盘约束。

## 2. 渠道

| 方法与路径 | 权限码 | 幂等 |
|---|---|---|
| `GET /api/v1/admin/versions/channels` | `versions.channel.read` | — |
| `POST /api/v1/admin/versions/channels` | `versions.channel.manage` | `Idempotency-Key`，作用域 `versions.channel.create` |
| `PATCH /api/v1/admin/versions/channels/{id}` | `versions.channel.manage` | — |

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/versions/channels \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: channel-stable-001' \
  -d '{"key":"stable","name":"稳定版","description":"生产默认渠道"}'

curl -s -X PATCH "http://localhost:5080/api/v1/admin/versions/channels/$CHANNEL_ID" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"稳定版","description":null,"isEnabled":false}'
```

约束（逐条来自 `ReleaseChannelService` / `VersionConventions`）：

- `key` 必须匹配 `^[a-z][a-z0-9-]{1,31}$`（小写字母开头，共 2–32 位小写字母/数字/连字符），
  不匹配返回通用校验失败 `2`（HTTP 400）；`name` 去空格后不能为空；应用内键重复返回 `201`（409），
  唯一索引冲突同样映射 `201`。
- **键创建后不可修改**：`UpdateReleaseChannelRequest` 只有 `name` / `description` / `isEnabled` /
  `allowDowngrade` 四个字段，`PATCH` 里没有 `key`。客户端按渠道键引用，改名可以、改键不行。字段缺省
  即不修改，`description` 传空串表示清空为 `null`。
- 停用（`isEnabled=false`）后**不能发布新版本**，创建版本时返回校验失败 `2`，文案
  「渠道 {key} 已停用，不能发布新版本。」；已发布版本的 `check` 与 `download` 不受停用影响。
- `allowDowngrade` 默认 `false`：归档最新版本后，已装更高版本的客户端不会被自动降级。需要强制回退时
  临时打开（或客户端在 `check` 请求里显式传 `allowDowngrade=true`）。
- 列表项 `ReleaseChannelDto`：`id` / `key` / `name` / `description` / `isEnabled` / `releaseCount`
  （含草稿与归档）/ `latestVersion`（语义版本最高的已发布版本号，无则 `null`）/ `createdAt` /
  `updatedAt` / `allowDowngrade` / `audienceTags` / `autoCreated`。
- **受众标签（B11）**：创建/更新渠道可传 `audienceTags`，服务端去空白、转小写、去重并排序后存
  `text[]`。单标签须匹配 `^[a-z0-9][a-z0-9._:-]{0,63}$`，单渠道最多 16 个；更新时传空数组表示清空。
  标签非法返回通用校验失败 `2`。
- **标签匹配口径**：客户端 `check` 省略 `channelKey` 时可改传 `audienceTags`；**请求标签必须包含渠道的
  全部受众标签**才命中（渠道声明「需要什么能力」，客户端声明「本机有什么能力」）。多个渠道命中时取
  标签最多的一个，标签数相同按渠道键序；停用渠道不参与匹配。显式 `channelKey` 始终优先于标签。
- **无命中与自动建渠道**：默认返回 `200`（404）。设 `Versions:AudienceAutoCreateChannels=true` 后，按
  规范化标签摘要创建 `auto-{sha8}` 渠道（`autoCreated=true`，同名同标签复用）；新渠道还没有已发布版本
  时 `check` 仍按 `212` 返回，管理员发布版本后即正常下发。`channelKey` 与 `audienceTags` 都不传返回 `2`。

## 3. 上传两步流：票据 → 直传

上传不接受 multipart，而是「先声明哈希与长度拿票据，再把裸字节 `PUT` 到票据地址」。

| 方法与路径 | 权限码 | 幂等 |
|---|---|---|
| `POST /api/v1/admin/versions/uploads` | `versions.artifact.upload` | `Idempotency-Key`，作用域 `versions.upload.ticket.create` |
| `PUT /api/v1/admin/versions/uploads/{id}` | `versions.artifact.upload` | — |

```bash
SHA=$(sha256sum app.zip | cut -d' ' -f1); SIZE=$(stat -c%s app.zip)
RESP=$(curl -s -X POST http://localhost:5080/api/v1/admin/versions/uploads \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: ticket-app-zip-001' \
  -d "{\"sha256\":\"$SHA\",\"sizeBytes\":$SIZE}")
UPLOAD_URL=$(echo "$RESP" | jq -r '.data.uploadUrl')

curl -s -X PUT "http://localhost:5080$UPLOAD_URL" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/octet-stream' \
  --data-binary @app.zip
```

- 请求体 `CreateUploadTicketRequest`：`sha256` 必须是 64 位**小写**十六进制（`^[0-9a-f]{64}$`，
  服务端先 `Trim` 再转小写）；`sizeBytes` 必须 `> 0` 且
  `<= max(Versions:MaxFileSizeBytes, Versions:MaxPackageSizeBytes)`，否则 `12`（HTTP 413）。
  票据只承载整包字节数，单文件上限在发布清单校验时才逐条生效。
- 响应 `UploadTicketDto`：`id`（票据号，即 PUT 地址末段）、`uploadUrl`（**相对本服务的路径**，
  形如 `/api/v1/admin/versions/uploads/{guid}`，不含主机名）、`expiresAt`（`now + Versions:UploadTicketMinutes`）。
- 直传前服务端把 `MaxRequestBodySize` 收紧为票据声明的 `SizeBytes`，超大请求不会写满磁盘；请求体
  `ContentLength` 与声明不一致 → `210`（422）。**消费是原子的**：一条带条件的 `UPDATE`（同应用、
  未消费、未过期）决定归属，受影响行数为 1 的请求才能写内容；并发落败方按现状分类为 `207`/`208`/`209`。
- `reused=true` 表示内容已存在且长度一致：**此时服务端不读取请求体**（省一次全量哈希），但票据已被
  消费。审计 `versions.upload.complete` 的 `message` 相应记 `reused` 或 `stored`。
- 同哈希但磁盘长度不一致 → 判定 blob 损坏，删除后按本次上传重写；落盘后再次校验字节数与哈希，不符
  则删掉刚写入的 blob 并抛 `210`，不留坏内容。响应 `UploadResultDto`：`sha256`/`sizeBytes`/`reused`。
- 失败码：`207` 票据无效（不存在或不属于本应用）、`208` 已过期（410）、`209` 已消费（409）、`210` 字节与声明不符。

## 4. 创建并发布版本

| 方法与路径 | 权限码 | 幂等 |
|---|---|---|
| `GET /api/v1/admin/versions/releases` | `versions.release.read` | — |
| `GET /api/v1/admin/versions/releases/{id}` | `versions.release.read` | — |
| `POST /api/v1/admin/versions/releases` | `versions.release.publish` | `Idempotency-Key`，作用域 `versions.release.create` |
| `PATCH /api/v1/admin/versions/releases/{id}` | `versions.release.publish` | —（仅草稿，只改 `notes`） |
| `POST /api/v1/admin/versions/releases/{id}/publish` | `versions.release.publish` | — |
| `POST /api/v1/admin/versions/releases/{id}/archive` | `versions.release.archive` | — |
| `DELETE /api/v1/admin/versions/releases/{id}` | `versions.release.delete` | — |

列表参数 `page`（默认 1）、`pageSize`（默认 20，超 200 被钳制）、`channelKey`、`status`（三态之一，
否则 `2`），返回 `PageResult<ReleaseSummaryDto>`，按 `createdAt` 倒序。

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/versions/releases \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: release-1.2.3-win-x64' \
  -d '{
    "channelKey":"stable", "version":"1.2.3", "notes":"首个稳定版", "publish":true,
    "artifacts":[{"runtimeIdentifier":"win-x64","files":[
      {"path":"app/App.exe","sha256":"<64位小写十六进制>","sizeBytes":123456},
      {"path":"app/appsettings.json","sha256":"<64位小写十六进制>","sizeBytes":789}]}]
  }'
```

`CreateReleaseRequest` 逐字段校验（括号内为错误码 / HTTP）：

| 字段 | 规则 |
|---|---|
| `channelKey` | 匹配渠道键规则（`2`）；须存在于当前应用（`200`）且启用（`2`） |
| `version` | 匹配 `^\d{1,5}\.\d{1,5}\.\d{1,5}(\.\d{1,5})?(-[0-9A-Za-z.-]{1,32})?(\+[0-9A-Za-z.-]{1,32})?$`，如 `1.2.3`、`1.2.3.0`、`1.2.3-rc.1`（`2`） |
| `notes` / `publish` | `notes` 去空格后为空存 `null`；`publish=true` 创建即发布并写 `publishedAt`，`false` 落 `draft` |
| `artifacts` | 至少一个产物（`2`） |
| `artifacts[].runtimeIdentifier` | 白名单 `win-x64` / `win-arm64` / `linux-x64` / `linux-arm64` / `osx-x64` / `osx-arm64`（`211`，400）；同请求内不得重复（`2`） |
| `artifacts[].files` | 至少一条（`2`）；条数超 `Versions:MaxFilesPerArtifact`（默认 50000）返回 `12`（413） |
| `files[].path` | 先 `Trim` 再把 `\` 换成 `/`；拒绝空、超 512 字符、以 `/` 开头、含 `\0`、含空段或 `.` / `..` 段（`2`）；产物内不得重复（`2`） |
| `files[].sha256` / `files[].sizeBytes` | 哈希须 64 位小写十六进制（`2`）；大小须 `> 0` 且 `<= Versions:MaxFileSizeBytes`（默认 4294967296，即 4 GiB），否则 `12` |

**先校验清单结构，再核对 blob，最后落库**：所有声明的哈希都必须已在 `versions/{appKey}/{sha256}`
存在且长度与声明一致；缺失或长度不符时一次性列出（消息最多展示 5 项，超出追加「等 N 项」）并返回
`206`（422）。这一步在版本重复检查之前，所以发布脚本漏传文件时看到 `206` 而不是 `204`。渠道内版本
号重复返回 `204`（409），唯一索引冲突同样 `204`。

**清单哈希算法**（`ReleaseManifest.Compute`，两端共用）：该产物的文件条目按 `path` 以
`StringComparer.Ordinal` 升序排序，逐条把 `{path}\n{sha256}\n{sizeBytes}\n` 以 UTF-8 追加进
`IncrementalHash(SHA-256)`，摘要转十六进制并小写。路径、哈希、长度任一变化都会改变 `manifestHash`，
条目顺序不影响结果。

详情 `ReleaseDetailDto`：`id` / `channelKey` / `version` / `status` / `notes` / `artifactCount` /
`fileCount` / `totalSizeBytes` / `publishedAt` / `archivedAt` / `createdBy` / `createdAt` /
`updatedAt` / `artifacts[]` / `packages[]`。列表 `ReleaseSummaryDto` 额外带 `packageCount` 与
`packageSizeBytes`（B8 新增，旧客户端忽略即可）。产物按 `runtimeIdentifier` 升序，
`ReleaseArtifactDto` 为 `runtimeIdentifier` / `manifestHash` / `fileCount` / `totalSizeBytes` / `files[]`，
而 `ReleaseFileDto` 为 `path` / `sha256` / `sizeBytes`（按路径升序）；`packages[]` 字段见
[`10-package-manifest.md`](10-package-manifest.md) §3。

**零产物的纯安装包版本**（只上传了 Setup exe / 增量 zip，没有文件级清单）不再显示 0：详情与列表的
`fileCount` / `totalSizeBytes` 回退为发布包汇总（包 `fileCount` 之和，`null` 按 1 计；`sizeBytes` 之和），
列表同时回填 `packageCount` / `packageSizeBytes`。单 exe 全量包因此显示 1 个文件与包字节数。

发布草稿（`/{id}/publish`）：已发布时**幂等返回**当前详情，不报错、不重复广播；已归档版本不能重新
发布，返回 `203`（409）；**零产物但存在 `ready` 全量安装包时允许发布**（B8 起，纯安装包版本）；
既无产物也无就绪安装包返回 `2`；发布前把所有产物的文件合并再核对一次 blob（`206`），
草稿创建后文件被清理会在这一步暴露。发布成功（创建即发布与显式发布都算）广播平台事件
`version.released`，负载含 `releaseId` / `channelKey` / `version` / `publishedAt`。

**草稿说明编辑**（`PATCH /{id}`，`UpdateReleaseRequest` 只有 `notes`）：用于「上传安装包登记为草稿 →
核对/修正包内说明 → 再发布」。`notes` 去空格后为空存 `null`，最长 4000 字符（`2`）；
只有 `draft` 可改，已发布/已归档返回 `222`（409）。上传登记的版本一律先落草稿，发布动作始终由
管理员在确认页显式触发（见 [`10-package-manifest.md`](10-package-manifest.md) §8）。

**删除版本**（`DELETE /releases/{id}`）：草稿即彻底丢弃（补齐「只能逐包删、删不完版本」的缺口），
归档版本即彻底删除，两者都会级联删除产物、文件清单与发布包登记；已发布版本拒绝，返回 `222`（409），
须先归档。行提交后按内容哈希回收 Blob：仍被其它版本/媒体/更新器引用的共享内容保留（与孤儿扫描同口径
的引用判定），源文件与其 `{sha}.zip` 下载派生缓存一并删除，立即省出空间；`bundles/` 组合缓存属纯派生
内容，仍由保留任务的孤儿扫描按宽限期回收。删除不可恢复，误删已归档版本只能重新上传同版本包。

---

## 5. 客户端接口：check / manifest / download

| 方法与路径 | 权限码 | 关键行为 |
|---|---|---|
| `POST /api/v1/client/versions/check` | `versions.release.read` | 返回最新已发布版本的清单与安装包 |
| `GET /api/v1/client/versions/{channelKey}/{version}/manifest?runtimeIdentifier=win-x64` | `versions.release.read` | 指定版本的单产物清单 |
| `GET /api/v1/client/versions/download/{sha256}` | `versions.artifact.download` | ETag / Range / 304，纯流式 |
| `GET /api/v1/client/versions/packages/{id}/download` | `versions.artifact.download` | 安装包下载，ETag / Range / 304，带原文件名 |
| `GET /api/v1/client/versions/updater?runtimeIdentifier=win-x64` | `versions.release.read` | 最新已发布更新器（同 RID 优先、`any` 兜底），无则 `223` |
| `GET /api/v1/client/versions/updaters/{id}/download` | `versions.artifact.download` | 更新器下载，ETag / Range / 304 |

```bash
curl -s -X POST http://localhost:5080/api/v1/client/versions/check \
  -H "Authorization: Bearer $CLIENT_TOKEN" -H 'Content-Type: application/json' \
  -d '{"channelKey":"stable","currentVersion":"1.2.2","runtimeIdentifier":"win-x64","allowDowngrade":false}'

curl -s "http://localhost:5080/api/v1/client/versions/stable/1.2.3/manifest?runtimeIdentifier=win-x64" \
  -H "Authorization: Bearer $CLIENT_TOKEN"

curl -s -o part.bin -H "Authorization: Bearer $CLIENT_TOKEN" -H 'Range: bytes=1024-' \
  "http://localhost:5080/api/v1/client/versions/download/$SHA"        # 206
curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $CLIENT_TOKEN" \
  -H "If-None-Match: \"$SHA\"" "http://localhost:5080/api/v1/client/versions/download/$SHA"  # 304
```

`check`（`ReleaseService.CheckAsync`）：

- 渠道解析（B11）：`channelKey` 与 `audienceTags` 至少给一个，都给时以渠道键为准；只给标签时的匹配规则
  见 §2「受众标签」。响应里的 `channelKey` 是实际命中的渠道键（自动匹配时由服务端回填），客户端后续
  `manifest` / 下载都按它寻址。
- `channelKey` 非法 / 两个字段都不传 → `2`；`runtimeIdentifier` 不在白名单 → `211`；渠道不存在或无标签命中
  → `200`；渠道下没有已发布版本 → `212`（后两者均 404）。
- **选版按语义版本**：`status == published` 内用 `VersionComparer` 取最高版本，同版本再取更晚发布的。
  不再按发布时间主排序，因此「先发 1.10.0、后发 1.9.0」仍以 `1.10.0` 为最新。支持三段 SemVer 与
  四段 GitVersion：`1.2.3` 与 `1.2.3.0` 相等，预发布按 SemVer 优先级（`-rc.1` < 正式版），
  `+build` 不参与比较。
- **`hasUpdate`**：`latest > current` → `true`；`current` 为空或不可解析 → `true`；
  `latest < current` → 默认 `false`（不降级），除非请求 `allowDowngrade=true` 或渠道
  `allowDowngrade=true`，此时 `true`。只要客户端版本更高，`isDowngrade` 就为 `true`（即使本次
  因未允许降级而没有下发）。
- 响应新增 `latestVersion` / `isDowngrade` / `notes` / `publishedAt`；`release` 仍是清单。
  `installer` / `incrementalPackage` 由发布包填充；`hasUpdaterUpdate` / `updater` 由更新器版本线填充：
  `updater` 按「同 RID 已发布 → `any` 已发布」取最新，`hasUpdaterUpdate` 用同一套版本语义比较
  请求里的 `updaterVersion`，未传或不可解析时为 `true`。
- `installer` 选包规则：同 RID 的 `ready` 全量包优先，其次 `target=any`；同目标按格式优先级
  `exe > msi > msp > zip > 7z > bin`，同格式取最新。纯安装包版本可以没有 `release` 清单。
- `incrementalPackage` 只认同 RID 且 `ready` 的增量包（`pending/processing/failed` 不下发），
  多个候选取清单版本最高的；它随 `hasUpdate` 一起出现，`hasUpdate=false` 时不填充。
- **无更新时也返回完整清单**（`hasUpdate=false` + `release` 带 `manifestHash` 与 `files[]`），这是
  完整性修复入口：客户端逐条比对本地文件，缺哪个补哪个。例外：最新已发布版本**没有**请求的
  `runtimeIdentifier` 产物、也没有可用安装包时返回 `hasUpdate=false` 且 `release=null`
  （HTTP 200，`installer` 同样为 `null`）。
- 渠道下没有任何已发布版本但存在更新器时返回 200（`hasUpdate=false`、`release=null`、`updater` 有值），
  不返回 `212`；`212` 只用于「既没有已发布版本、也没有更新器」。

`manifest` 的拒绝顺序：渠道键非法或 `version` 为空 → `2`；`runtimeIdentifier` 不支持 → `211`；版本
不存在 → `202`；草稿或已归档 → `203`（409）；该版本没有此运行时产物 → `205`。`runtimeIdentifier`
省略时按空串处理必定命中 `211`，客户端必须显式传。

`download`（`ReleaseContentService.ResolveAsync` + `Results.Stream`）：

- 三重归属校验，任一不过统一 `205`（404）：哈希为空、该哈希不属于本应用的**已发布**版本、blob 文件
  缺失。所以归档一个版本后它的文件立即不可下载。
- `ETag` 就是内容哈希本身（带双引号输出），`enableRangeProcessing: true`，因此支持 `Range` → `206`、
  `If-None-Match` → `304`。
- 直接读 Blob 文件流返回，不把整文件缓冲进内存；不设 `Content-Disposition`（`fileDownloadName` 为
  `null`），文件名由客户端按清单路径决定；`Content-Type` 固定 `application/octet-stream`。

安装包下载（`/packages/{id}/download`，`IReleasePackageService.ResolveDownloadAsync`）与文件下载同口径，
但归属校验换成包：包属于本应用、所属版本已发布、`status=ready`、Blob 存在，任一不过统一 `213`（404）。
响应带 `Content-Disposition: attachment; filename="..."`（原文件名）与 `X-Content-Sha256`；归档版本后
包立即不可下载。`check` 返回的 `installer.id` 直接拼该路径，不需要再查清单。

### 发布与摄取的实时提示

**SignalR `ReleasePublished` 只是提示，不是事实来源**：版本发布成功（创建即发布、草稿 `publish`、
增量包摄取携带 `publish=true` 自动发布，均收敛在 `ReleaseService`/摄取服务的同一落库点）后，服务端经
`/hubs/devices` 向该应用全部注册连接（`app:{appKey}` 组）广播 `ReleasePublished`
（负载 `ReleasePublishedNotification{ appKey, channelKey, version }`）。幂等重复发布不重发；
推送失败只记日志，不影响已落库的发布结果。客户端收到后应在 **0–5 秒随机抖动**后回拉
`POST /client/versions/check`（抖动是为了避免广播同刻到达全部在线实例时把回拉变成限流洪峰），
是否有更新以 `check` 结果为准，载荷里的版本号只用于提示文案。

增量包摄取的状态迁移（认领 `processing`、完成 `ready`、失败 `failed`）另外向管理台
`/hubs/admin` 的 `domain:versions` 组广播 `ReleaseIngestionProgress`
（负载 `AdminReleaseIngestionProgressNotification{ appKey, packageId, status, progress, occurredAt }`，
`progress` 认领 0、完成 1、失败 0，语义由 `status` 承载），管理台据此失效回拉包详情；登记弹窗与详情抽屉只保留 60 秒兜底轮询。事件定义见 `Platform.Contracts`（`DeviceHubEvents` / `AdminHubEvents`），
载荷与接口的对应关系见 11-devices。

### 管理台浏览器下载票据

管理台的安装包 / 更新器下载不把整文件读进页面内存，而是先换一张短期票据，再把 URL 交给浏览器下载
管理器（流式落盘、暂停与断点续传都由浏览器负责）：

| 方法与路径 | 权限码 | 说明 |
|---|---|---|
| `POST /api/v1/client/versions/packages/{id}/download-ticket` | `versions.artifact.download` | 签发安装包下载票据 |
| `POST /api/v1/client/versions/updaters/{id}/download-ticket` | `versions.artifact.download` | 签发更新器下载票据 |
| `GET /api/v1/client/versions/downloads/{ticket}` | 匿名 | 凭票据流式下载，支持 `Range` → `206` / `If-None-Match` → `304` |

- 票据用 DataProtection 限时签名，不落库，有效期 `VersionConventions.DownloadTicketHours`（24 小时）。
  浏览器下载被暂停后往往很久才续传，票据太短会让续传请求直接 401，所以按天给。
- 票据只绑定 `appKey` + 资源 ID，**GET 时仍按当前状态重新校验归属**：版本归档、包删除或更新器归档后，
  已签发票据立即失效（`213` / `223` 等 404）。
- `<a href>` 无法带 `Authorization` 头，因此 GET 端点匿名；票据本身就是短期凭证，不要贴到日志或外部系统。
- 源文件名不是 `.zip` 时（exe / msi / msp / 7z / bin），签发时生成 zip 缓存
  `versions/{appKey}/{sha256}.zip`，下发文件名追加 `.zip`（如 `Setup-x.exe.zip`），避免浏览器对 exe 的
  安全检查拦截；`.zip` 源原样下发，不二次打包。
- zip 以 Store（不再压缩，exe 本身已压缩）模式单遍流式生成，条目名只取原文件名并清掉路径与非法字符，
  时间戳固定，重复生成字节稳定；缓存与源同 sha，源不再被引用时由孤儿清理一起回收。生成发生在签发
  票据的请求里，因此**首次下载非 zip 大包时，点下载后要等打包完成浏览器才开始接收**；同一内容的后续
  下载直接命中缓存。
- zip 表现的 `ETag` 为 `"{sha256}-zip"`，原始源仍是 `"{sha256}"`；`X-Content-Sha256` 始终是源内容哈希；
  `Content-Type` 为 `application/zip` 或 `application/octet-stream`。
- 客户端 / SDK 继续使用上面的原始下载端点，签名与限流口径不变，不要换成票据端点。

本模块**没有**配置限流策略（`PlatformRateLimitPolicies` 里没有版本项），只有创建类端点的幂等：
`Idempotency-Key` 上限 256 字符、结果保留 24 小时；同键重放返回首次成功响应（`traceId` 换成当前请求
的），首次失败会释放占位允许同键重试，并发占位不到返回 `6`（409）。

## 6. 错误码与 HTTP 状态

版本段 `200–299`，已定义到 `228`；HTTP 状态由 `ApiErrorHttpStatus.Map` 唯一映射。
发布包错误码（213–228）的触发细节见 [`10-package-manifest.md`](10-package-manifest.md) §7。

| 码 | 枚举名 | HTTP | 触发点 |
|---|---|---|---|
| `200` | `VersionsChannelNotFound` | 404 | 创建版本 / `check` 渠道不存在；`PATCH` 按 id 找不到渠道 |
| `201` | `VersionsChannelKeyExists` | 409 | 渠道键在应用内重复 |
| `202` | `VersionsReleaseNotFound` | 404 | 按 id 取详情、`manifest` 定位不到版本 |
| `203` | `VersionsReleaseNotPublished` | 409 | `manifest` 指向草稿/归档；重新发布已归档版本 |
| `204` | `VersionsReleaseVersionExists` | 409 | 渠道内版本号重复 |
| `205` | `VersionsArtifactNotFound` | 404 | `manifest` 缺该运行时产物；`download` 归属校验不过 |
| `206` | `VersionsManifestMismatch` | 422 | 创建/发布时声明的哈希未上传或长度不符 |
| `207` | `VersionsUploadTicketInvalid` | 400 | 票据不存在或不属于本应用 |
| `208` | `VersionsUploadTicketExpired` | 410 | 票据已过期 |
| `209` | `VersionsUploadTicketConsumed` | 409 | 票据已被消费 |
| `210` | `VersionsPackageHashMismatch` | 422 | 直传长度/哈希与声明不符；客户端本地校验下载内容哈希同样用它 |
| `211` | `VersionsRuntimeIdentifierUnsupported` | 400 | `runtimeIdentifier` 不在六个白名单内 |
| `212` | `VersionsNoPublishedRelease` | 404 | 渠道下没有任何已发布版本 |
| `213` | `VersionsPackageNotFound` | 404 | 包不存在 / 未发布 / 已归档 / Blob 缺失 |
| `214` | `VersionsPackageNotReady` | 409 | 包尚未 `ready`（摄取中或失败） |
| `215` | `VersionsPackageManifestMissing` | 422 | 缺清单且未声明 `kind=full`；增量包缺清单 |
| `216` | `VersionsPackageAppMismatch` | 422 | 清单 `appKey` 与当前应用不一致 |
| `217` | `VersionsPackageChannelVersionMismatch` | 422 | 清单与请求渠道/版本/RID 不一致 |
| `218` | `VersionsPackageFileNameMismatch` | 422 | 严格模式下文件名不匹配模板 |
| `219` | `VersionsPackageManifestConflict` | 409 | 清单与既有产物不一致；并发写入冲突 |
| `220` | `VersionsPackageUnsafeArchive` | 422 | zip 越界 / 加密 / 符号链接 |
| `221` | `VersionsPackageTooLarge` | 413 | 超过包大小上限 |
| `222` | `VersionsReleaseImmutable` | 409 | 已发布/已归档版本仍尝试删除包 |
| `223` | `VersionsUpdaterNotFound` | 404 | 更新器不存在（B9） |
| `224` | `VersionsPackageKindUnsupported` | 400 | 本端点不支持的包类型（如 updater） |
| `225` | `VersionsPackageIngestionFailed` | 422 | 摄取失败（B9） |
| `226` | `VersionsPackageStatusInvalid` | 409 | 包状态不允许该操作 |
| `227` | `VersionsStorageInsufficient` | 507 | 存储空间不足（B9/B10） |
| `228` | `VersionsPackageManifestInvalid` | 422 | 清单 JSON / 字段非法 |

结构类问题（键/版本规则、路径非法、重复路径、空产物、渠道停用）走通用码 `2` `ValidationFailed`（400）；
大小与条数超限走 `12` `PayloadTooLarge`（413）；缺应用上下文走 `4` `Forbidden`（403）。

## 7. SDK 用法：比对清单并原子落盘

`AppFramework.Platform.Client` 覆盖上面的客户端路径；`DownloadVersionFileAsync` /
`DownloadReleasePackageAsync` / `DownloadUpdaterAsync` 均返回 `bool`（`true` = 真的下载并落盘，
`false` = `304` 未变更）。

```csharp
using System.Security.Cryptography;
using AppFramework.Platform.Client;
using Platform.Contracts.Versions;

var client = new PlatformClient(new PlatformClientOptions
{
    BaseUrl = "http://localhost:5080",
    ClientVersion = "3.2.0",
    RuntimeIdentifier = "win-x64",   // 不配且调用不传参时 check 会带 null，服务端回 211
});
await client.SignInAsync("updater", "…");

// 1) 检查更新：无更新时同样拿到最新清单，可直接进入修复分支
CheckUpdateResponse check = await client.CheckUpdateAsync("stable", currentVersion: LocalVersion);

// 也可以不指定渠道，由服务端按本机能力标签匹配（需渠道声明 audienceTags）：
// CheckUpdateResponse check = await client.CheckUpdateAsync(
//     channelKey: null, currentVersion: LocalVersion,
//     audienceTags: ["win-x64", "hardware:scanner"]);
if (check.Release is null) { return; }          // 该渠道最新版本的产物里没有本机运行时

ReleaseManifestDto manifest = check.Release;
if (manifest.ManifestHash == LocalManifestHash && LocalAllFilesPresent()) { return; }

string root = Path.Combine(AppContext.BaseDirectory, "app");
foreach (ReleaseFileDto file in manifest.Files)
{
    string target = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
    bool same = File.Exists(target) && Sha256Hex(target) == file.Sha256;

    // 关键：只有 same 为真时才传 ifNoneMatch，绝不能默认传 file.Sha256。
    // ETag 就是内容哈希，传了它等于告诉服务端「我已有这个内容」，
    // 服务端回 304、方法返回 false、跳过落盘 —— 本地缺文件时修复永远不会发生。
    bool written = await client.DownloadVersionFileAsync(
        file.Sha256, target, ifNoneMatch: same ? file.Sha256 : null);
    Console.WriteLine(written ? $"已更新 {file.Path}（{file.SizeBytes} 字节）" : $"{file.Path} 未变更");
}

LocalVersion = manifest.Version;
LocalManifestHash = manifest.ManifestHash;

static string Sha256Hex(string path)
{
    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    using var stream = File.OpenRead(path);
    Span<byte> buffer = stackalloc byte[81920];
    for (int read = stream.Read(buffer); read > 0; read = stream.Read(buffer))
    {
        hasher.AppendData(buffer[..read]);
    }
    return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
}
```

B9 起 `CheckUpdateAsync` 的可选参数为 `currentVersion` / `runtimeIdentifier` / `updaterVersion` /
`allowDowngrade`，并新增安装包与更新器的查询/下载方法：

```csharp
CheckUpdateResponse check = await client.CheckUpdateAsync(
    "stable",
    currentVersion: LocalVersion,
    runtimeIdentifier: "win-x64",
    updaterVersion: LocalUpdaterVersion);

// 先升级更新器，再应用增量包；下载同样走临时文件 + sha256 校验
if (check.HasUpdaterUpdate && check.Updater is { } updater)
{
    await client.DownloadUpdaterAsync(updater.Id, updaterPath, expectedSha256: updater.Sha256);
}

if (check.IncrementalPackage is { } incremental)
{
    await client.DownloadReleasePackageAsync(
        incremental.Id, packagePath, expectedSha256: incremental.Sha256);
}

// 单独查询某个 RID 的更新器；没有时抛 PlatformClientException（Code=223）
UpdaterInfoDto latest = await client.GetLatestUpdaterAsync("win-x64");
```

SDK 内部的落盘纪律（`PlatformClient.DownloadAsync`），集成时可以直接依赖：

- 先写 `{目标}.{guid:N}.tmp`，边写边算 SHA-256，校验通过才 `File.Move(overwrite: true)` 原子改名；
  异常路径的 `finally` 删临时文件，**不会留下半个文件**。实际哈希与 `sha256` 不符 → 抛
  `PlatformClientException`，`Code` 为 `ApiErrorCode.VersionsPackageHashMismatch`（`210`），
  `StatusCode` 为响应 HTTP 状态码；非 2xx 且响应体是平台信封时 `Code`/`TraceId` 取信封里的值。
- `304` 直接返回 `false`，不写任何字节。401 且有刷新令牌时 JSON 类调用（`check`/`manifest`）自动
  续期并重试一次；下载走独立路径**不自动续期**，令牌过期需上层捕获后重试。
- `GetReleaseManifestAsync` 对 `channelKey` / `version` 做了 `Uri.EscapeDataString`，预发布后缀
  （`1.2.3-rc.1`）无需额外处理。

---

## 8. 回退与归档演练

回退没有专门端点：**归档当前已发布版本 = 回退**，因为 `check` 只按 `status == published` 选版。

```bash
curl -s -X POST "http://localhost:5080/api/v1/admin/versions/releases/$RELEASE_ID/archive" \
  -H "Authorization: Bearer $TOKEN"

# 客户端视角：本机 1.2.4、最新已发布 1.2.3 → 默认 hasUpdate=false 且 isDowngrade=true
curl -s -X POST http://localhost:5080/api/v1/client/versions/check \
  -H "Authorization: Bearer $CLIENT_TOKEN" -H 'Content-Type: application/json' \
  -d '{"channelKey":"stable","currentVersion":"1.2.4","runtimeIdentifier":"win-x64"}'

# 需要真正把现场降回 1.2.3 时，显式允许降级
curl -s -X POST http://localhost:5080/api/v1/client/versions/check \
  -H "Authorization: Bearer $CLIENT_TOKEN" -H 'Content-Type: application/json' \
  -d '{"channelKey":"stable","currentVersion":"1.2.4","runtimeIdentifier":"win-x64","allowDowngrade":true}'
```

- 归档后可用 `GET /channels` 复核：该版本 `status=archived` 且 `archivedAt` 有值，渠道的
  `latestVersion` 回到语义版本上的上一版；重复归档同一版本直接返回详情，不报错。
- 默认不降级：已装 1.2.4 的机器会收到 `hasUpdate=false` 与 `isDowngrade=true`，不会被意外回退。
  打开渠道 `allowDowngrade`（或客户端显式传 `allowDowngrade=true`）后，同样的检查会返回
  `hasUpdate=true` 加 1.2.3 的清单 —— 客户端必须按清单覆盖本地内容完成降级，而不是只判断
  「有没有新版本」。回退完成后记得把渠道开关关掉。
- 已归档版本的文件不再可下载（`205`），安装包不可下载（`213`），`manifest` 以 `203` 拒绝；
  要恢复下发只能发新版本号，`publish` 不会把归档版本请回来。
- 草稿不参与回退序列（从未成为「最新已发布」），归档它不影响 `check`；停用渠道只挡新发布、不影响下发，
  想冻结渠道又不让旧版本下线就用停用而不是归档。

## 9. 保留、配额与运维边界（B10）

### 9.1 保留期（`RetentionWorker`，每 600 秒一轮）

| 对象 | 默认 | 行为 |
|---|---|---|
| 上传票据 | 过期 24 小时后 | 直接删行；票据本就一次性，过期只是清理 |
| `failed` 发布包 | 30 天（`Versions:FailedPackageRetentionDays`） | 先删数据库行，包 Blob 交给孤儿扫描按引用判定 |
| 已归档版本 | 永久（`Versions:ArchivedReleaseRetentionDays=0`） | 设为正数后到期删除版本及其产物/文件/包行，Blob 同样交给孤儿扫描 |

命令侧只想「现在回收」时可把保留天数调小并用测试时钟/直接改 `updated_at` 触发；
**不要手工删 Blob**——跳过引用检查会删掉仍被其它版本共用的内容（内容寻址天然跨版本复用）。

### 9.2 孤儿 Blob 扫描

- 默认 `BlobSweepEnabled=true` 且 **`BlobSweepDryRun=true`**：只统计候选，不删除。
- 候选条件：`versions/{appKey}/{64位小写sha256}` 形式的文件、修改时间早于
  `BlobSweepGraceHours`（默认 48 小时）、且一轮内重新核对 `release_files` /
  `release_packages` / `app_updater_releases` 都没有引用。宽限期必须大于上传票据有效期（30 分钟），
  否则刚传完未登记的 Blob 会被误判。
- 每批 `BlobSweepBatchSize`（默认 500）个候选；PostgreSQL 会话级 advisory lock（键 `20261113`）
  保证多副本同时只有一轮在扫。
- 上线顺序：先跑一周 dry-run，观察 `versions_blob_orphans_scanned_total` 与日志里的候选是否都确实无引用，
  再把 `Versions:BlobSweepDryRun` 改为 `false`。删除后 `versions_blob_orphans_deleted_total` /
  `versions_blob_orphans_deleted_bytes_total` 才会增长。

### 9.3 磁盘与配额保护

- 上传票据申请与增量摄取开始前都会检查剩余磁盘：要求 ≥ `声明大小/解压总量 + MinFreeDiskBytes`
  （默认 5 GiB），不足返回 **227 / HTTP 507**（`VersionsStorageInsufficient`）。
- 登记只认 `Versions:MaxPackageSizeBytes`（默认 4 GiB），超过返回 **221 / 413**；
  此前误用「文件上限与包上限的较大值」，B10 起统一。
- 摄取中写文件遇到 `ENOSPC`/磁盘满 IO 异常按可重试错误退避，清出空间后自动续跑；
  清单/哈希/路径类错误不重试。
- 上传路径与登记路径各自限流（固定窗口，按用户 + 应用分区）：
  `versions-upload-ticket` 默认 **60 次/分钟**，`versions-package-finalize` 默认 **30 次/分钟**；
  超出返回 **7 / HTTP 429**。可用 `RateLimits:VersionsUploadTicketPerMinute` /
  `RateLimits:VersionsPackageFinalizePerMinute` 调整。`check` 与下载**不限流**（读多写少，跑批场景多）。

### 9.4 多副本摄取

增量包的认领使用 PostgreSQL `SELECT … FOR UPDATE SKIP LOCKED` + 租约（`locked_until` / `locked_by`），
**多个 API 副本可以同时跑摄取 Worker**，同一个包只会被一个副本处理；进程崩溃后租约到期自动回收，
超过 `IngestionMaxAttempts` 置 `failed`。这一语义由 `tests/Platform.PostgresTests` 的 16 副本并发用例守住，
InMemory 集成测试覆盖不到。

> 多副本限制没有完全解除：Webhook 投递器与 SignalR 仍是进程内状态，`Deployment:RequireSingleInstance`
> 默认仍为 `true`。只有启用多副本并确认这两项的替代方案后，才关闭实例守卫。

### 9.5 降级策略

默认**不降级**：归档最新版本后，已装高版本的客户端收到 `hasUpdate=false` + `isDowngrade=true`。
需要真正回退时打开渠道 `allowDowngrade`（或客户端单次传 `allowDowngrade=true`），演练与恢复步骤见
[`09-operations-drills.md`](09-operations-drills.md) §3 与 §4.7。

## 10. 常见坑

| 现象 | 根因 | 处理 |
|---|---|---|
| `4` / 403「当前令牌没有应用上下文」 | 令牌 `app` 声明为空；本模块全部是应用级资源 | 先 `POST /api/v1/admin/auth/context` 换绑目标应用 |
| `206` / 422「以下文件未上传或长度不符」 | 声明的哈希在 Blob 里找不到，或磁盘实际字节数与声明不同；也可能是清单与这批上传内容不是同一批 | 按消息列出的哈希补传；补传要新票据，旧票据已消费就是 `209` |
| `212` / 404 | 渠道存在但没有 `published` 版本（全是草稿，或已发布版被归档） | 发布一个版本；或确认 `channelKey` 是否写到了别的渠道 |
| `211` / 400 | `runtimeIdentifier` 不在六值白名单，或请求里根本没带 | 检查请求体字段与 `GetReleaseManifestAsync` 的查询参数；SDK 里配好 `Options.RuntimeIdentifier` |
| `208` / `209` | 票据过期（410）或已消费（409）：超过 `Versions:UploadTicketMinutes`（默认 30 分钟）、或复用旧 `uploadUrl` 重传 | 每次重传重新申请票据并换 `Idempotency-Key`（同键会回放同一张票） |
| 清单一致但本地缺文件，客户端却没补 | `ifNoneMatch` 默认传了内容哈希，服务端回 `304`，SDK 返回 `false` 跳过落盘 | 只在确认本地文件哈希已匹配时才传（见第 7 节） |
| `download` 返回 `205` | 该哈希不属于本应用的已发布版本：跨应用复制了清单，或版本已归档 | 用当前应用当前已发布版本的清单 |
| 管理台下载返回 401「下载票据无效或已过期」 | 票据超过 24 小时、被篡改，或服务端 DataProtection 密钥环换了（容器重建且密钥目录未持久化） | 回管理台重新点下载；长期部署应持久化 DataProtection 密钥目录 |
| 渠道键 / 版本号看着合法却报 `2` | 键须小写字母开头、2–32 位、不含下划线；版本号必须三或四段数字（`1.10` 非法） | 用 `-` 不用 `_`；写成 `1.10.0`、`1.10.0.0` 或 `1.10.0-rc.1` |
| 增量包一直 `pending` | `Versions:IngestionEnabled=false`（测试/关闭时）或 Worker 未启动 | 打开配置并重启 API；多副本时确认单实例约束；详情抽屉看 `processing`/租约 |
| 重试摄取返回 `226` | 只有 `failed` 允许重试 | 等 Worker 处理完；`pending/processing/ready` 不发重试 |
| 坏 zip 报 `228` 而不是 `215` | 归档自身损坏/不是 zip；`215` 只表示「合法 zip 但没有 `af-package.json`」 | 先修包；不要把 215 当通用失败码 |
| 登记更新器返回 `219` | 同 `(target, version)` 已有不同 `sha256` 的内容 | 升版本号；同版本只允许复用相同内容 |
| `check` 的 `updater` 为 `null` | 没有已发布更新器，或该 RID 与 `any` 都已归档 | 上传并保持 `published`；归档会回退，不会删除历史 |
| 增量包已 `ready` 但 `check` 不下发 | 所属版本仍是草稿（`publish=false`），或版本已归档 | 发布版本；已归档版本需发新版本号 |

## 11. 与管理台「版本与渠道 / 更新器」页的对应

左侧菜单「版本与渠道」（`/versions`）与「更新器」（`/updaters`）就是本文管理端端点的直连映射，
页面提示「版本与渠道按应用隔离，前端不传 appKey」：

| 页面元素 | 权限码 | 端点 |
|---|---|---|
| 渠道表格 / 新建、编辑渠道 | `versions.channel.read` / `versions.channel.manage` | `GET` / `POST` / `PATCH` `/channels` |
| 版本列表（渠道与状态筛选，`pageSize=50`） | `versions.release.read` | `GET /releases?channelKey=&status=` |
| 新建版本、创建并发布 | `versions.release.publish` | `POST /releases`（`publish` 由「同时发布」开关决定） |
| 选文件后浏览器算哈希并直传 | `versions.artifact.upload` | `POST /uploads` → `PUT {uploadUrl}` |
| 详情抽屉的产物与文件清单 | `versions.release.read` | `GET /releases/{id}` |
| 归档按钮 | `versions.release.archive` | `POST /releases/{id}/archive` |
| 上传安装包（全量/增量） | `versions.artifact.upload` | `POST /uploads` → `PUT` → `POST /packages` |
| 详情抽屉的发布包表 / 下载 | `versions.release.read` / `versions.artifact.download` | `GET /packages` / `POST /api/v1/client/versions/packages/{id}/download-ticket` → `GET /downloads/{ticket}` |
| 删除草稿版本的包 | `versions.release.archive` | `DELETE /packages/{id}` |
| 增量包摄取状态轮询 / 重试 | `versions.release.read` / `versions.artifact.upload` | `GET /packages/{id}` / `POST /packages/{id}/retry` |
| 更新器列表 / 上传 / 归档 / 下载 | `versions.release.read` / `versions.artifact.upload` / `versions.release.archive` / `versions.artifact.download` | `GET` / `POST /updaters`、`POST /updaters/{id}/archive`、`POST /updaters/{id}/download-ticket` → `GET /downloads/{ticket}` |
| 顶部「当前应用」选择器 | — | `POST /api/v1/admin/auth/context` |

页面细节：状态标签草稿/已发布/已归档对应三个状态常量；创建类请求由前端生成 `Idempotency-Key`
（`crypto.randomUUID()`），`PATCH` 与 publish / archive 两个动作不带幂等键；浏览器哈希按 4 MB 分块
流式计算（`hashFile`，峰值内存固定为块大小），上传用 `XMLHttpRequest` 上报进度；超大产物仍建议走
脚本或 CLI 申请票据后 `PUT`；前端提交的清单路径已做与服务端 `NormalizePath` 一致的规范化
（`\` 转 `/`、去掉所选根目录段）。上传安装包的弹窗**只选文件**（可选旁挂清单），渠道、版本、包类型、
运行时与说明全部取自包内清单，不做人工输入；登记固定为**草稿**（`publish=false`），成功后可直接
「查看草稿版本」。增量包登记后弹窗按摄取事件失效回拉（60 秒兜底轮询），`ready` 显示清单文件数，`failed` 原地重试；
版本详情有未完成包时同样按 60 秒兜底自动刷新。**发布必须先过确认弹窗**：加载详情、展示渠道/版本/包与文件汇总，
草稿阶段可改发布说明，确认后按「`PATCH` 说明 → `publish`」提交；详情抽屉另有「编辑说明」入口，
已发布版本不提供。更新器页的「最新」标记由服务端
`/client/versions/updater` 逐 target 解析，不依赖前端版本比较。安装包与更新器的「下载」按钮先
`POST .../download-ticket`，再用 `<a>` 打开返回的 `url`，由浏览器原生流式下载；非 zip 源会被服务端
打包为 zip（下发文件名带 `.zip`），页面不再把文件读进内存。

---

## 附：本模块配置项

| 键 | 默认 | 说明 |
|---|---|---|
| `Versions:UploadTicketMinutes` | 30 | 票据有效期（分钟），代码 `Math.Max(1, …)` 兜底 |
| `Versions:MaxFileSizeBytes` | 4294967296 | 单文件上限（4 GiB），发布清单逐条校验 |
| `Versions:MaxPackageSizeBytes` | 4294967296 | 增量包上限（4 GiB）；票据放行取它与单文件上限的较大值 |
| `Versions:MaxFilesPerArtifact` | 50000 | 单产物文件条数上限 |
| `Versions:MaxExtractedBytesPerPackage` | 21474836480 | 增量包解压总量上限（20 GiB，B9） |
| `Versions:MaxZipEntries` / `MaxCompressionRatio` | 100000 / 200 | zip 条目数与压缩比上限 |
| `Versions:MinFreeDiskBytes` | 5368709120 | 摄取前最小剩余磁盘（5 GiB，B9） |
| `Versions:RetainRawPackage` | `true` | 摄取后保留原始包（B9） |
| `Versions:BlobSweepEnabled` / `BlobSweepDryRun` | `true` / `true` | 孤儿 Blob 扫描开关与只报告模式，上线先 dry-run 一周（B10） |
| `Versions:BlobSweepGraceHours` / `BlobSweepBatchSize` | 48 / 500 | 孤儿判定宽限期（小时）与单批候选数（B10） |
| `Versions:FailedPackageRetentionDays` | 30 | 失败包保留天数，到期删行（B10） |
| `Versions:ArchivedReleaseRetentionDays` | 0 | 归档版本保留天数，0 = 永久（B10） |
| `Versions:AudienceAutoCreateChannels` | `false` | 客户端只带标签且无渠道命中时自动建渠道（B11）；默认关闭，避免拼错标签污染渠道表 |
| `RateLimits:VersionsUploadTicketPerMinute` | 60 | 上传票据申请限流（用户 + 应用分区，429/7） |
| `RateLimits:VersionsPackageFinalizePerMinute` | 30 | 包/更新器登记限流（用户 + 应用分区，429/7） |
| `Versions:RequirePackageFileNameMatch` | `false` | 包文件名严格匹配模板，不匹配返回 `218` |
| `Versions:PackageFileNamePatterns` | 见 10 号文档 | 文件名模板 CSV（`{channel}` / `{version}` / `{kind}` / `{ext}`） |
| `Versions:IngestionConcurrency` / `IngestionLockMinutes` | 1 / 10 | 摄取 Worker 并发与租约（B9） |
| `BlobStorage:RootPath` | `data/blobs` | Blob 根目录；compose 中 `/data/blobs`（`blobs` 卷） |
| `BlobStorage:BufferSize` / `TempDirectoryName` | 81920 / `.tmp` | 缓冲区实际取 `Math.Max(4096, 值)`；写入先落 `.part` 再 rename，不留半成品 |
| `Workers:Maintenance:IdempotencyRetentionHours` | 24 | 幂等记录保留小时数，与端点内 24 小时 TTL 一致 |

内容寻址路径 `versions/{appKey}/{sha256}` 不是配置项，是 `VersionBlobPaths` 的固定约定；接入 S3/MinIO 只需替换 `IBlobStorage` 实现，本模块逻辑不变。

相关文档：[`00-quickstart.md`](00-quickstart.md)（端到端最小命令）、
[`01-identity.md`](01-identity.md)（令牌与应用上下文）、[`03-licensing.md`](03-licensing.md)。
