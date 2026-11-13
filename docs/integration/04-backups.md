# 备份与配置下发（B6）

备份按**应用 + 命名空间**隔离：命名空间对应一个配置域（例如 `global`、`line-a`），
同一命名空间下的备份共享渠道与保留条数配额。所有端点都需要令牌带应用上下文（见 00-quickstart 4.3），
服务端只从令牌 `app` 声明取应用键，不接受请求体里的 `appKey`。

| 能力 | 管理端 | 客户端 |
|---|---|---|
| 命名空间 | `/api/v1/admin/backups/namespaces` | — |
| 备份 | `/api/v1/admin/backups` | `/api/v1/client/backups/{id}` |

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
    \"deviceId\":null,
    \"deviceName\":\"line-a-pc\",
    \"moduleId\":null,
    \"schemaVersion\":1,
    \"frameworkVersion\":\"3.2.0\",
    \"channel\":\"stable\",
    \"payloadJson\":$(printf '%s' "$PAYLOAD" | jq -Rs .),
    \"sha256\":\"$SHA\"
  }"
```

- `kind` 只接受 `config` / `profile`（其它值返回 `405`）。
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

## 4. 下发与 Ack

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/backups/$ID/push \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{
    "targetDeviceId":null,
    "targetChannel":"stable",
    "targetFrameworkVersion":"3.2.0",
    "restartAfterApply":false,
    "force":false,
    "currentHash":"<目标设备当前配置哈希，可省略>",
    "confirmCrossDevice":false
  }'
```

兼容矩阵（`force` 只放宽主版本）：

| 条件 | 结果 |
|---|---|
| 目标渠道与备份渠道不同 | 拒绝 `404`（HTTP 409），`force` 不放宽 |
| 目标框架主版本与备份不同 | 拒绝 `403`（HTTP 422）；`force=true` 时放行 |
| 目标框架版本为空 | 无法判定，按不兼容处理 |
| 备份来源设备与目标设备不同 | 需 `confirmCrossDevice=true`，否则 `407` |
| 应用当前无在线连接 | 立即拒绝 `407`（HTTP 409） |
| 在线但超时未回执 | `408`（HTTP 504），超时由 `Backups:PushAckTimeoutSeconds` 控制（默认 60 秒） |

> 「无在线连接」检查先于兼容矩阵：没有客户端在线时，渠道/版本不匹配也统一返回 `407`。
> 只有存在在线连接时才会进入兼容判断并得到 `403` / `404`。

下发走 SignalR `/hubs/platform` 的 `app:{appKey}` 分组：

1. 服务端先注册等待，再广播 `ConfigPush`（`ConfigPushNotification`）；
2. 客户端收到后回拉 `GET /api/v1/client/backups/{id}/download`，应用完成再回 `ConfigPushAck`
   （`{ backupId, succeeded, message }`）；
3. 服务端只把 Ack 当结果，不把广播当事实来源；超时即失败，客户端后续应用不会补记成功。

`currentHash` 非空时，下发前会在同一命名空间落一条 `pre-push-snapshot`
（`name=pre-push-snapshot`，只记录目标设备与当前哈希，`sizeBytes=0`），返回 `snapshotBackupId`。

## 5. 删除与清理

`DELETE /api/v1/admin/backups/{id}` 是软删除：立刻从列表/下载中消失，物理文件与行由
`RetentionWorker` 在宽限期后清理。宽限期取 `Backups:CleanupGraceMinutes`，最小 1 分钟；
清理顺序固定为**先删 Blob、再删行**，文件删除失败时保留行，下一轮重试。

调度间隔由 `Workers:Retention:IntervalSeconds` 控制（默认 600 秒，最小 5 秒）。

## 6. 错误码

| 码 | 含义 |
|---|---|
| `400` | 命名空间不存在 |
| `401` | 命名空间键重复 |
| `402` | 备份不存在 |
| `403` | 架构版本不兼容 |
| `404` | 渠道不匹配 |
| `405` | 负载非法（类型/名称/哈希/落盘校验） |
| `406` | 命名空间保留条数超限 |
| `407` | 下发被拒（无连接/跨设备未确认） |
| `408` | 等待回执超时 |

## 7. 配置项

| 键 | 默认 | 说明 |
|---|---|---|
| `Backups:InlineMaxBytes` | 65536 | 内联阈值 |
| `Backups:MaxPayloadBytes` | 67108864 | 单次负载上限 |
| `Backups:PushAckTimeoutSeconds` | 60 | 回执等待秒数 |
| `Backups:CleanupGraceMinutes` | 30 | 软删宽限期（最小 1 分钟） |
| `Backups:DefaultMaxVersions` | 50 | 命名空间默认配额 |
