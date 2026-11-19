# 备份与配置下发（B6 + S4）

备份按**应用 + 命名空间**隔离：命名空间对应一个配置域（例如 `global`、`line-a`），
同一命名空间下的备份共享渠道与保留条数配额。所有端点都需要令牌带应用上下文（见 00-quickstart 4.3），
服务端只从令牌 `app` 声明取应用键，不接受请求体里的 `appKey`。

| 能力 | 管理端 | 客户端 |
|---|---|---|
| 命名空间 | `/api/v1/admin/backups/namespaces` | — |
| 备份 | `/api/v1/admin/backups` | `/api/v1/client/backups` |
| 下载 | `/api/v1/admin/backups/{id}/download` | `/api/v1/client/backups/{id}/download` |

客户端入口见 §3.1；按安装实例下发见 §4。

## 1. 命名空间

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/backups/namespaces \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: backup-ns-global-001' \
  -d '{"key":"global","name":"全局配置","description":"默认配置域","channel":"stable","maxVersions":50}'
```

- 键规则 `^[a-z][a-z0-9-]{1,31}$`，创建后不可修改。
- `channel` 不填或空白按 `default` 处理，统一小写；备份未声明渠道时继承命名空间渠道。
- `maxVersions` 缺省 50，上限 500；计数只含未软删的备份。
- `PATCH /namespaces/{id}` 可改名称、说明、渠道、配额与启停；已有备份的渠道不会随之迁移。

命名空间键在应用内唯一，重复创建返回 `401`（HTTP 409）。

## 2. 创建备份

负载是 **JSON 文本**，服务端按字节数决定内联还是转存：

- `<= Backups:InlineMaxBytes`（默认 64 KB）：直接落 `payload_json` 列；
- 超过阈值：先写 `.pending` 临时文件 → 数据库提交 → rename 发布；任一步失败都不会留下「有行无文件」。

```bash
PAYLOAD='{"theme":"dark","locale":"zh-CN"}'
SHA=$(printf '%s' "$PAYLOAD" | sha256sum | cut -d' ' -f1)

curl -s -X POST http://localhost:5080/api/v1/admin/backups \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: backup-global-001' \
  -d "{
    \"namespaceKey\":\"global\",
    \"kind\":\"config\",
    \"name\":\"dark-theme\",
    \"installationId\":null,
    \"moduleId\":null,
    \"schemaVersion\":1,
    \"frameworkVersion\":\"3.2.0\",
    \"channel\":\"stable\",
    \"payloadJson\":$(printf '%s' "$PAYLOAD" | jq -Rs .),
    \"sha256\":\"$SHA\"
  }"
```

- `kind` 只接受 `config` / `profile`（其它值返回 `405`）。
- `installationId` 可选：提供时服务端解析所属节点，并把 `installation_id` / `node_id` / `node_name`
  作为创建时快照落库；管理端手工创建的备份通常不带来源安装实例。
- `sha256` 可省略；提供时必须与负载实际哈希一致，否则返回 `405`。
- `schemaVersion` 不得为负数；`frameworkVersion` 用于下发兼容判断。
- 负载超过 `Backups:MaxPayloadBytes`（默认 64 MB）返回通用 `PayloadTooLarge`。
- 创建端点的请求体上限在启动时按 `Backups:MaxPayloadBytes + 8 MB` 显式声明（给 JSON 字段转义与
  信封留余量），不会再被 Kestrel 默认 30 MB 提前拒绝；超过端点上限由宿主直接返回 413。
- 命名空间内未软删备份达到 `maxVersions` 时返回 `406`。

响应里的 `hasInline` / `hasFile` 表示实际存储形态；`downloadUrl` 可直接用于下载。

## 3. 列表 / 详情 / 下载

```bash
curl -s "http://localhost:5080/api/v1/admin/backups?namespaceKey=global&kind=config&pageSize=20" \
  -H "Authorization: Bearer $TOKEN"

curl -s "http://localhost:5080/api/v1/admin/backups/$ID/download" \
  -H "Authorization: Bearer $TOKEN" -o backup.json
```

- 内联备份直接返回 JSON；转存备份走 `Results.Stream`，不缓冲整文件。
- `ETag` 固定为内容 SHA-256，支持 `Range`（`206`）与 `If-None-Match`（`304`）。
- 下载响应头带 RFC 5987 文件名，中文名不会乱码。
- 客户端可读取 `/api/v1/client/backups/{id}` 与 `/{id}/download`，权限同为 `backup.read`。

### 3.1 客户端按安装实例上传与列表（S4）

```bash
curl -s -X POST http://localhost:5080/api/v1/client/backups \
  -H "Authorization: Bearer $CLIENT_TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: installation-backup-line3-001' \
  -d '{
    "namespaceKey":"device-backups",
    "kind":"config",
    "name":"appdata-20261118",
    "installationId":"<INSTALLATION_ID>",
    "schemaVersion":1,
    "frameworkVersion":"1.2.2",
    "payloadJson":"{\"theme\":\"dark\"}"
  }'

curl -s "http://localhost:5080/api/v1/client/backups?installationId=<INSTALLATION_ID>&kind=config&pageSize=20" \
  -H "Authorization: Bearer $CLIENT_TOKEN"
```

- 上传必须带 `installationId`（缺失 400），服务端经设备域窄接口校验归属：安装实例不存在或不属于
  当前应用返回 `805`（HTTP 404）；
- `namespaceKey` 为空默认 `device-backups`，首次上传时自动创建该命名空间（名称「设备备份」、
  渠道 `default`、配额取默认值），管理端无需预建；
- 服务端按 `installationId` 解析节点，快照 `installation_id` / `node_id` / `node_name`；
  `channel` 用客户端上报值，未上报继承命名空间渠道，不按安装实例分支强制回填；
- 与管理端创建复用同一条 `IBackupService` 路径（内联/转存阈值、SHA-256、配额、幂等、软删规则一致）；
- 上传/列表分别写审计 `backups.client.upload` / `backups.client.list`，权限 `backup.create` / `backup.read`；
- 安装实例连接、心跳与按安装实例下发见 [`11-devices.md`](11-devices.md)。

## 4. 下发与 Ack

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/backups/$ID/push \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{
    "targetInstallationIds":["<安装实例 id>","<安装实例 id>"],
    "targetChannel":"stable",
    "targetFrameworkVersion":"3.2.0",
    "restartAfterApply":false,
    "force":false,
    "currentHash":"<仅单安装实例下发时可省略>",
    "confirmCrossInstallation":false
  }'
```

目标安装实例必须显式选择（单选或多选，`targetInstallationId` 与 `targetInstallationIds` 合并去重），
**留空不再下发到应用内全部在线连接**：

| 条件 | 结果 |
|---|---|
| 目标留空且 `broadcastToApp=false` | 拒绝 `2`（HTTP 400） |
| 同时指定目标安装实例与 `broadcastToApp=true` | 拒绝 `2`（HTTP 400） |
| 广播或任一目标提供 `currentHash` | 拒绝 `2`（HTTP 400），快照只能对应单个安装实例 |
| 任一目标不满足安装实例门禁 | 整批拒绝，**不会**先发给已通过门禁的安装实例 |
| 全部目标送达但都没有回执 | `408`（HTTP 504） |
| 全部目标连接已断开 | `803`（HTTP 409） |
| 部分目标回执失败/超时 | HTTP 200，`success=false`，逐台结果见 `targets` |

`broadcastToApp=true` 保留应用组广播能力，但必须显式声明，管理台不提供该入口。

兼容矩阵（`force` 只放宽主版本）：

| 条件 | 结果 |
|---|---|
| 目标渠道与备份渠道不同 | 拒绝 `404`（HTTP 409），`force` 不放宽 |
| 目标框架主版本与备份不同 | 拒绝 `403`（HTTP 422）；`force=true` 时放行 |
| 目标框架版本为空 | 无法判定，按不兼容处理 |
| 备份来源安装实例与任一目标不同（含应用组广播） | 需 `confirmCrossInstallation=true`，否则 `407` |

安装实例门禁（发送前逐台预检）：

| 条件 | 结果 |
|---|---|
| 安装实例不存在/不属于当前应用 | `805`（HTTP 404） |
| 所在节点已停用 | `806`（HTTP 403） |
| 安装实例已停用 | `807`（HTTP 403） |
| 离线（库里在线状态与活跃连接任一不满足） | `803`（HTTP 409） |
| 工位非待机（运行中） | `804`（HTTP 409） |

下发在 `/hubs/devices` 的 `installation:{installationId}` 分组上进行，Ack 等待键是
`(appKey, installationId, backupId)`；命令里的下载地址固定为 `/api/v1/client/backups/{id}/download`，
大备份只下发地址。安装实例侧接入见 [`11-devices.md`](11-devices.md)。

响应中的 `targets` 逐台给出 `{ installationId, nodeId, delivered, acked, success, message }`；
`acked` / `success` 顶层字段表示**全部目标**是否回执/成功。

应用组广播（仅 `broadcastToApp=true`）走 SignalR `/hubs/devices` 的 `app:{appKey}` 分组：

1. 服务端先注册等待，再广播 `ConfigPush`（`DeviceConfigPush`，与定向下发同一负载）；
2. 客户端收到后回拉 `GET /api/v1/client/backups/{id}/download`，应用完成再调 `AcknowledgeConfigPush`
   （`DeviceConfigPushAck{ backupId, succeeded, message }`）；回执先到者胜，只认第一个；
3. 服务端只把 Ack 当结果，不把广播当事实来源；超时即失败，客户端后续应用不会补记成功；
4. 应用无在线连接时立即返回 `407`（HTTP 409），且该检查先于兼容矩阵；
5. 广播等同于下发到应用内全部安装实例，因此备份有来源安装实例时同样需要
   `confirmCrossInstallation=true`。

`currentHash` 仅单安装实例下发允许；非空时下发前会在同一命名空间落一条 `pre-push-snapshot`
（`name=pre-push-snapshot`，记录目标安装实例、节点与当前哈希，`sizeBytes=0`），返回 `snapshotBackupId`。

## 5. 删除与清理

`DELETE /api/v1/admin/backups/{id}` 是软删除：立刻从列表/下载中消失，物理文件与行由
`RetentionWorker` 在宽限期后清理。宽限期取 `Backups:CleanupGraceMinutes`，最小 1 分钟；
清理顺序固定为**先删 Blob、再删行**，文件删除失败时保留行，下一轮重试。

调度间隔由 `Workers:Retention:IntervalSeconds` 控制（默认 600 秒，最小 5 秒）。

## 6. 错误码

| 码 | 含义 |
|---|---|
| `2` | 通用参数校验失败（目标安装实例留空、广播与目标互斥、广播/多目标带快照） |
| `400` | 命名空间不存在 |
| `401` | 命名空间键重复 |
| `402` | 备份不存在 |
| `403` | 架构版本不兼容 |
| `404` | 渠道不匹配 |
| `405` | 负载非法（类型/名称/哈希/落盘校验） |
| `406` | 命名空间保留条数超限 |
| `407` | 下发被拒（显式广播无连接/跨安装实例未确认） |
| `408` | 等待回执超时 |
| `805` | 目标安装实例不存在或不属于当前应用（下发/客户端上传） |
| `806` | 所在节点已停用 |
| `807` | 目标安装实例已停用 |
| `803` | 目标安装实例离线 |
| `804` | 目标安装实例运行中，非待机 |

## 7. 配置项

| 键 | 默认 | 说明 |
|---|---|---|
| `Backups:InlineMaxBytes` | 65536 | 内联阈值 |
| `Backups:MaxPayloadBytes` | 67108864 | 单次负载上限 |
| `Backups:PushAckTimeoutSeconds` | 60 | 回执等待秒数 |
| `Backups:CleanupGraceMinutes` | 30 | 软删宽限期（最小 1 分钟） |
| `Backups:DefaultMaxVersions` | 50 | 命名空间默认配额 |
