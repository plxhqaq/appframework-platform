# 版本与渠道（B4）

> 本文覆盖客户端集成的完整路径：**发布 → 检查更新 → 清单 → 下载 → 校验 → 回退**。
> 所有路由、权限码、字段名、错误码与默认值都取自当前源码
> （`Platform.Contracts/Versions/VersionContracts.cs`、`Platform.Modules.Versions`、
> `AppFramework.Platform.Client`）。最小可跑命令见 [`00-quickstart.md`](00-quickstart.md) 第 5 节。

版本数据按**应用**隔离：渠道键只在应用内唯一，`stable` 在 `demo` 与在 `line-a` 是两个渠道。
应用键只从令牌的 `app` 声明取（端点里的 `RequireAppKey`），请求体与查询串里的 `appKey` 一律不被接受。

| 能力 | 管理端 | 客户端 |
|---|---|---|
| 渠道 | `/api/v1/admin/versions/channels` | — |
| 版本 | `/api/v1/admin/versions/releases` | `/api/v1/client/versions/check` |
| 清单 | `/api/v1/admin/versions/releases/{id}` | `/api/v1/client/versions/{channelKey}/{version}/manifest` |
| 上传 | `/api/v1/admin/versions/uploads` | — |
| 下载 | — | `/api/v1/client/versions/download/{sha256}` |

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
- **键创建后不可修改**：`UpdateReleaseChannelRequest` 只有 `name` / `description` / `isEnabled`
  三个字段，`PATCH` 里没有 `key`。客户端按渠道键引用，改名可以、改键不行。字段缺省即不修改，
  `description` 传空串表示清空为 `null`。
- 停用（`isEnabled=false`）后**不能发布新版本**，创建版本时返回校验失败 `2`，文案
  「渠道 {key} 已停用，不能发布新版本。」；已发布版本的 `check` 与 `download` 不受停用影响。
- 列表项 `ReleaseChannelDto`：`id` / `key` / `name` / `description` / `isEnabled` / `releaseCount`
  （含草稿与归档）/ `latestVersion`（最新**已发布**版本号，无则 `null`）/ `createdAt` / `updatedAt`。

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
  服务端先 `Trim` 再转小写）；`sizeBytes` 必须 `> 0` 且 `<= Versions:MaxFileSizeBytes`，
  否则 `12`（HTTP 413）。
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
| `POST /api/v1/admin/versions/releases/{id}/publish` | `versions.release.publish` | — |
| `POST /api/v1/admin/versions/releases/{id}/archive` | `versions.release.archive` | — |

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
| `version` | 匹配 `^\d{1,5}\.\d{1,5}\.\d{1,5}(-[0-9A-Za-z.-]{1,32})?$`，如 `1.2.3`、`1.2.3-rc.1`（`2`） |
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
`updatedAt` / `artifacts[]`。产物按 `runtimeIdentifier` 升序，`ReleaseArtifactDto` 为
`runtimeIdentifier` / `manifestHash` / `fileCount` / `totalSizeBytes` / `files[]`，而
`ReleaseFileDto` 为 `path` / `sha256` / `sizeBytes`（按路径升序）。

发布草稿（`/{id}/publish`）：已发布时**幂等返回**当前详情，不报错、不重复广播；已归档版本不能重新
发布，返回 `203`（409）；没有任何产物时返回 `2`；发布前把所有产物的文件合并再核对一次 blob（`206`），
草稿创建后文件被清理会在这一步暴露。发布成功（创建即发布与显式发布都算）广播平台事件
`version.released`，负载含 `releaseId` / `channelKey` / `version` / `publishedAt`。

---

## 5. 客户端接口：check / manifest / download

| 方法与路径 | 权限码 | 关键行为 |
|---|---|---|
| `POST /api/v1/client/versions/check` | `versions.release.read` | 返回最新已发布版本的清单 |
| `GET /api/v1/client/versions/{channelKey}/{version}/manifest?runtimeIdentifier=win-x64` | `versions.release.read` | 指定版本的单产物清单 |
| `GET /api/v1/client/versions/download/{sha256}` | `versions.artifact.download` | ETag / Range / 304，纯流式 |

```bash
curl -s -X POST http://localhost:5080/api/v1/client/versions/check \
  -H "Authorization: Bearer $CLIENT_TOKEN" -H 'Content-Type: application/json' \
  -d '{"channelKey":"stable","currentVersion":"1.2.2","runtimeIdentifier":"win-x64"}'

curl -s "http://localhost:5080/api/v1/client/versions/stable/1.2.3/manifest?runtimeIdentifier=win-x64" \
  -H "Authorization: Bearer $CLIENT_TOKEN"

curl -s -o part.bin -H "Authorization: Bearer $CLIENT_TOKEN" -H 'Range: bytes=1024-' \
  "http://localhost:5080/api/v1/client/versions/download/$SHA"        # 206
curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $CLIENT_TOKEN" \
  -H "If-None-Match: \"$SHA\"" "http://localhost:5080/api/v1/client/versions/download/$SHA"  # 304
```

`check`（`ReleaseService.CheckAsync`）：

- `channelKey` 非法 → `2`；`runtimeIdentifier` 不在白名单 → `211`；渠道不存在 → `200`；渠道下没有
  已发布版本 → `212`（后两者均 404）。
- 选版依据：`status == published` 内按 `publishedAt` 倒序，其次按 `version` **字符串**倒序；主排序
  是发布时间，不是版本号数值大小。`hasUpdate` = `currentVersion` 与 `release.version` 是否不同
  （`Trim` 后序数字符串比较），服务端不做语义化版本比较。
- **无更新时也返回完整清单**（`hasUpdate=false` + `release` 带 `manifestHash` 与 `files[]`），这是
  完整性修复入口：客户端逐条比对本地文件，缺哪个补哪个。例外：最新已发布版本**没有**请求的
  `runtimeIdentifier` 产物时返回 `hasUpdate=false` 且 `release=null`（HTTP 200）。

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

本模块**没有**配置限流策略（`PlatformRateLimitPolicies` 里没有版本项），只有创建类端点的幂等：
`Idempotency-Key` 上限 256 字符、结果保留 24 小时；同键重放返回首次成功响应（`traceId` 换成当前请求
的），首次失败会释放占位允许同键重试，并发占位不到返回 `6`（409）。

## 6. 错误码与 HTTP 状态

版本段 `200–299`，已定义到 `212`；HTTP 状态由 `ApiErrorHttpStatus.Map` 唯一映射。

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

结构类问题（键/版本规则、路径非法、重复路径、空产物、渠道停用）走通用码 `2` `ValidationFailed`（400）；
大小与条数超限走 `12` `PayloadTooLarge`（413）；缺应用上下文走 `4` `Forbidden`（403）。

## 7. SDK 用法：比对清单并原子落盘

`AppFramework.Platform.Client` 的三个方法覆盖上面的客户端路径；`DownloadVersionFileAsync` 返回
`bool`（`true` = 真的下载并落盘，`false` = `304` 未变更）。

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

# 客户端视角：本机 1.2.4、最新已发布 1.2.3 → hasUpdate=true 且回 1.2.3 清单
curl -s -X POST http://localhost:5080/api/v1/client/versions/check \
  -H "Authorization: Bearer $CLIENT_TOKEN" -H 'Content-Type: application/json' \
  -d '{"channelKey":"stable","currentVersion":"1.2.4","runtimeIdentifier":"win-x64"}'
```

- 归档后可用 `GET /channels` 复核：该版本 `status=archived` 且 `archivedAt` 有值，渠道的
  `latestVersion` 回到上一版；重复归档同一版本直接返回详情，不报错。
- 已装 1.2.4 的机器会收到 `hasUpdate=true` 加 1.2.3 的清单 —— 这是「降级 + 文件级修复」的组合，客户端
  必须按清单覆盖本地内容，而不是只判断「有没有新版本」。
- 已归档版本的文件不再可下载（`205`），`manifest` 以 `203` 拒绝；要恢复下发只能发新版本号，`publish`
  不会把归档版本请回来。
- 草稿不参与回退序列（从未成为「最新已发布」），归档它不影响 `check`；停用渠道只挡新发布、不影响下发，
  想冻结渠道又不让旧版本下线就用停用而不是归档。

## 9. 常见坑

| 现象 | 根因 | 处理 |
|---|---|---|
| `4` / 403「当前令牌没有应用上下文」 | 令牌 `app` 声明为空；本模块全部是应用级资源 | 先 `POST /api/v1/admin/auth/context` 换绑目标应用 |
| `206` / 422「以下文件未上传或长度不符」 | 声明的哈希在 Blob 里找不到，或磁盘实际字节数与声明不同；也可能是清单与这批上传内容不是同一批 | 按消息列出的哈希补传；补传要新票据，旧票据已消费就是 `209` |
| `212` / 404 | 渠道存在但没有 `published` 版本（全是草稿，或已发布版被归档） | 发布一个版本；或确认 `channelKey` 是否写到了别的渠道 |
| `211` / 400 | `runtimeIdentifier` 不在六值白名单，或请求里根本没带 | 检查请求体字段与 `GetReleaseManifestAsync` 的查询参数；SDK 里配好 `Options.RuntimeIdentifier` |
| `208` / `209` | 票据过期（410）或已消费（409）：超过 `Versions:UploadTicketMinutes`（默认 30 分钟）、或复用旧 `uploadUrl` 重传 | 每次重传重新申请票据并换 `Idempotency-Key`（同键会回放同一张票） |
| 清单一致但本地缺文件，客户端却没补 | `ifNoneMatch` 默认传了内容哈希，服务端回 `304`，SDK 返回 `false` 跳过落盘 | 只在确认本地文件哈希已匹配时才传（见第 7 节） |
| `download` 返回 `205` | 该哈希不属于本应用的已发布版本：跨应用复制了清单，或版本已归档 | 用当前应用当前已发布版本的清单 |
| 渠道键 / 版本号看着合法却报 `2` | 键须小写字母开头、2–32 位、不含下划线；版本号必须三段数字（`1.10` 非法） | 用 `-` 不用 `_`；写成 `1.10.0` 或 `1.10.0-rc.1` |

## 10. 与管理台「版本与渠道」页的对应

左侧菜单「版本与渠道」（路由 `/versions`）就是本文管理端端点的直连映射，页面提示「版本与渠道按应用
隔离，前端不传 appKey」：

| 页面元素 | 权限码 | 端点 |
|---|---|---|
| 渠道表格 / 新建、编辑渠道 | `versions.channel.read` / `versions.channel.manage` | `GET` / `POST` / `PATCH` `/channels` |
| 版本列表（渠道与状态筛选，`pageSize=50`） | `versions.release.read` | `GET /releases?channelKey=&status=` |
| 新建版本、创建并发布 | `versions.release.publish` | `POST /releases`（`publish` 由「同时发布」开关决定） |
| 选文件后浏览器算哈希并直传 | `versions.artifact.upload` | `POST /uploads` → `PUT {uploadUrl}` |
| 详情抽屉的产物与文件清单 | `versions.release.read` | `GET /releases/{id}` |
| 归档按钮 | `versions.release.archive` | `POST /releases/{id}/archive` |
| 顶部「当前应用」选择器 | — | `POST /api/v1/admin/auth/context` |

页面细节：状态标签草稿/已发布/已归档对应三个状态常量；创建类请求由前端生成 `Idempotency-Key`
（`crypto.randomUUID()`），`PATCH` 与 publish / archive 两个动作不带幂等键；浏览器哈希用
`crypto.subtle.digest`（整文件进内存），几 GB 的大包请走脚本或 CLI 申请票据后 `PUT`；前端提交的清单
路径已做与服务端 `NormalizePath` 一致的规范化（`\` 转 `/`、去掉所选根目录段）。

---

## 附：本模块配置项

| 键 | 默认 | 说明 |
|---|---|---|
| `Versions:UploadTicketMinutes` | 30 | 票据有效期（分钟），代码 `Math.Max(1, …)` 兜底 |
| `Versions:MaxFileSizeBytes` | 4294967296 | 单文件上限（4 GiB），票据与清单两处都校验 |
| `Versions:MaxFilesPerArtifact` | 50000 | 单产物文件条数上限 |
| `BlobStorage:RootPath` | `data/blobs` | Blob 根目录；compose 中 `/data/blobs`（`blobs` 卷） |
| `BlobStorage:BufferSize` / `TempDirectoryName` | 81920 / `.tmp` | 缓冲区实际取 `Math.Max(4096, 值)`；写入先落 `.part` 再 rename，不留半成品 |
| `Workers:Maintenance:IdempotencyRetentionHours` | 24 | 幂等记录保留小时数，与端点内 24 小时 TTL 一致 |

内容寻址路径 `versions/{appKey}/{sha256}` 不是配置项，是 `VersionBlobPaths` 的固定约定；接入 S3/MinIO 只需替换 `IBlobStorage` 实现，本模块逻辑不变。

相关文档：[`00-quickstart.md`](00-quickstart.md)（端到端最小命令）、
[`01-identity.md`](01-identity.md)（令牌与应用上下文）、[`03-licensing.md`](03-licensing.md)。
