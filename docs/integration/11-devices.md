# 设备域与按设备下发（S1–S4）

设备域是**通用能力**：任何带应用上下文的客户端都可以连 `/hubs/devices` 登记自己、上报心跳与工位状态、
接收定向下发的配置。平台只维护一份设备事实，不绑定某个产品；旧 WPF 客户端只是其中一个消费者。

> 两个 Hub 别混：`/hubs/devices` 是**设备连接**（注册/心跳/工位/回执），
> `/hubs/platform` 是**应用级广播**（`ConfigPush`、`ReleasePublished`、`LicenseChanged` 等）。
> 定向下发只走设备分组 `device:{deviceId}`，不会广播到整个应用。

| 能力 | 端点 | 权限 |
|---|---|---|
| 设备连接（注册/心跳/工位/回执） | SignalR `/hubs/devices` | 任何带 `app` 声明的令牌 |
| 设备管理 | `/api/v1/admin/devices` | `device.read` / `device.manage` / `device.restart` |
| 客户端备份上传/列表/下载 | `/api/v1/client/backups` | `backup.read` / `backup.create` |

---

## 1. 设备身份与稳定键

客户端连接后首帧提交 `DeviceRegistrationRequest.Identity`：

| 字段 | 说明 |
|---|---|
| `installGuid` | 客户端首次安装生成并持久化的 Guid；**必填**，空 Guid 直接 400 |
| `primaryIdentifier` | SMBIOS UUID 或系统盘序列号；缺失时回退 MAC（仅展示） |
| `macAddress` | 辅助信息，不参与身份计算 |
| `frameworkVersion` | 客户端框架版本，只记录，不参与身份计算 |

平台不信任客户端自算的键，统一按契约包里的 `DeviceIdentityHasher` 计算：

- `DeviceKey = SHA-256("{PrimaryIdentifier}|{InstallGuid:N}")`（小写十六进制），
  版本号不参与，升级不会产生幽灵设备；
- 主标识缺失时 `DeviceKey` 只由 InstallGuid 决定——**同一台机器重装会变成新设备**，现场应优先提供主标识；
- 机器码 = SHA-256 前 10 字节的 Crockford Base32（16 字符），主标识优先、缺失时回退 `DeviceKey`，
  只用于展示与排障，不能当作永久授权锚点；
- 同一应用内按 `(app_key, device_key)` 唯一（`ux_devices_app_device_key`），重复注册复用同一条记录并刷新连接信息；
- 不同应用各自一条记录，互不影响。

设备展示名 `DisplayName = 自定义名-自动名`（`DeviceNaming.Compose`），已带前缀时不重复拼接；
管理端清空自定义名后回落到自动名，自动名也为空时回落到机器码。

## 2. 连接与注册

SignalR 连接用任意带应用上下文的 JWT（M2M 令牌或用户令牌均可），浏览器/设备无法自定义
`Authorization` 头时走 `access_token` 查询参数（`AuthenticationSetup` 已支持）：

```text
wss://<服务端>/hubs/devices?access_token=<JWT>
```

令牌没有 `app` 声明时 `RegisterDevice` 抛 `HubException`；连接未注册前调用心跳/工位/回执同样被拒。

设备 → 服务端方法与返回：

| 方法 | 入参 | 返回 |
|---|---|---|
| `RegisterDevice` | `DeviceRegistrationRequest` | `DeviceRegistrationResponse`（`deviceId` / `deviceShortId` / `connectedAt`） |
| `Heartbeat` | `DeviceHeartbeatRequest` | 最新 `DeviceSummaryDto` |
| `ReportStationState` | `DeviceStationStateRequest` | 最新 `DeviceSummaryDto` |
| `AcknowledgeConfigPush` | `DeviceConfigPushAck` | `void`（无等待者时忽略） |

服务端 → 设备事件：

| 事件 | 负载 | 说明 |
|---|---|---|
| `ConfigPush` | `DeviceConfigPush` | 定向下发；大载荷只给 `downloadUrl`，不给内联内容 |
| `Restart` | `DeviceRestart` | 管理端重启命令，`reason` 透传给设备显示 |
| `SessionReplaced` | `DeviceSessionReplaced` | 同一设备新连接顶替旧连接，旧连接应停止重连并让位 |
| `StateChanged` | — | 契约已预留，当前服务端不发送 |

顶号语义：新连接注册成功后加入 `device:{deviceId}` 分组，旧连接被移出分组并收到
`SessionReplaced`。设备端收到该事件**不要立刻重连**，否则两台进程会互相顶号死循环；
应先确认本机是否还有另一实例在跑。停用设备注册时返回 `DevicesDisabled`。

### 心跳、工位与在线判定

心跳的 `status` 是三值词表，落库时拆成两列：

| `status` | `online_status` | `station_status` |
|---|---|---|
| `offline` | `offline` | 保持不变 |
| `online` | `online` | `online`（运行中） |
| `idle` | `online` | `idle`（待机，允许下发） |

- `alarmLevel` 取 `none` / `info` / `warning` / `error`；`none` 时报警摘要强制清空；
- 心跳时间以**服务端时间**为准，客户端时间只作诊断（工控机不联网对时很常见）；
- 心跳周期 `heartbeatSeconds` 允许 1–3600，未上报时取 `Devices:DefaultHeartbeatSeconds`（默认 10）；
- 离线阈值 = `max(OfflineMultiplier × 心跳周期, MinimumOfflineSeconds)`，默认 3 倍且不低于 60 秒；
- `DeviceOfflineWorker` 每 `Devices:ScanIntervalSeconds`（默认 30，最小 5）扫描一次并批量标记离线；
- **推送判定要同时满足**：数据库 `online_status=online` **且** 连接登记表里还有活跃连接。
  未等到离线扫描落库但连接已断时，命令不会被发到不存在的连接上。

`ReportStationState` 只接受 `idle` / `online`，会同时把设备置为在线；下发门禁只看 `station_status`。

## 3. 管理端 API

```bash
# 列表：关键字匹配展示名/机器码/自动名/IP；可按在线状态与分支过滤
curl -s "http://localhost:5080/api/v1/admin/devices?page=1&pageSize=20&onlineStatus=online&branch=line-a&keyword=coater" \
  -H "Authorization: Bearer $TOKEN"

# 详情（含 deviceKey、installGuid、首连时间）
curl -s http://localhost:5080/api/v1/admin/devices/$DEVICE_ID \
  -H "Authorization: Bearer $TOKEN"

# 改名/启停：字段为空表示不改；customName 传空串表示清除
curl -s -X PATCH http://localhost:5080/api/v1/admin/devices/$DEVICE_ID \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"customName":"3号线涂布机","isEnabled":true}'

# 重启在线设备（reason 可选，≤256 字符）
curl -s -X POST http://localhost:5080/api/v1/admin/devices/$DEVICE_ID/restart \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"reason":"现场维护"}'
```

- 列表固定按 `lastHeartbeatAt` 倒序、再按展示名排序；`onlineStatus` 只接受 `online`/`offline`；
- 所有管理端点都要求令牌带应用上下文（无 `app` 返回 403），只返回本应用的设备；
- 改名/启停写审计 `devices.update`，重启写 `devices.restart`（成功与失败都记，原因进 `message`）；
- 重启只发给在线设备：设备不存在 404、停用 403、离线 409；
- 停用只阻止注册/重启/下发，不删除记录，重新启用后设备重连即恢复。

## 4. 按设备下发

`POST /api/v1/admin/backups/{id}/push` 必须用 `targetDeviceIds` 显式指定目标（可多选）；
**留空不再广播到应用内全部在线连接**，需要应用组广播时只能显式传 `broadcastToApp=true`。
设备目标走 `/hubs/devices` 的设备分组，门禁与回执都按设备维度：

| 条件 | 结果 |
|---|---|
| 目标设备留空且未显式广播 | `2`（HTTP 400） |
| 设备不存在或不属于当前应用 | `800`（HTTP 404） |
| 设备已停用 | `802`（HTTP 403） |
| 设备离线（库或活跃连接任一不满足） | `803`（HTTP 409） |
| 工位不是 `idle`（运行中） | `804`（HTTP 409） |
| 备份来源设备与任一目标不同且未确认 | `407`（HTTP 409），`confirmCrossDevice=true` 放行 |
| 全部目标都不回执 | `408`（HTTP 504），超时 `Backups:PushAckTimeoutSeconds`（默认 60 秒） |
| 发送时全部目标连接刚好断开 | `803`（HTTP 409） |

多设备下发会先对所有目标跑完门禁再并发发送：任一目标不满足就整批拒绝，不会出现
「一部分设备已收到命令」。响应 `targets` 逐台返回 `delivered/acked/success/message`。

兼容矩阵（渠道/主版本/`force`）在设备门禁**之前**执行，与广播下发共用同一份规则，见
[`04-backups.md`](04-backups.md)。设备下发命令里的下载地址固定为
`/api/v1/client/backups/{id}/download`，客户端用同一令牌回拉；大备份只下发地址，不内联负载。

Ack 等待键是 `(appKey, deviceId, backupId)`：同一设备同一备份只认第一个回执，
超时后迟到的回执被忽略（不会补记成功）。全部目标成功时发布 `backup.pushed` Webhook，
负载里带 `targetDeviceIds`、`snapshotId`、`successCount` 与 `failureCount`。

## 5. 客户端备份端点（S4）

```bash
# 上传：deviceId 必填，设备必须已在当前应用注册
curl -s -X POST http://localhost:5080/api/v1/client/backups \
  -H "Authorization: Bearer $CLIENT_TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: device-backup-line3-001' \
  -d '{
    "namespaceKey":"device-backups",
    "kind":"config",
    "name":"appdata-20261113",
    "deviceId":"<DEVICE_ID>",
    "schemaVersion":1,
    "frameworkVersion":"1.2.2",
    "channel":"default",
    "payloadJson":"{\"theme\":\"dark\"}"
  }'

# 列表：只在本应用、默认 device-backups 命名空间；可按设备/类型过滤
curl -s "http://localhost:5080/api/v1/client/backups?deviceId=<DEVICE_ID>&kind=config&pageSize=20" \
  -H "Authorization: Bearer $CLIENT_TOKEN"

# 元数据与下载（下发后回拉同一对端点）
curl -s http://localhost:5080/api/v1/client/backups/$BACKUP_ID -H "Authorization: Bearer $CLIENT_TOKEN"
curl -s http://localhost:5080/api/v1/client/backups/$BACKUP_ID/download -H "Authorization: Bearer $CLIENT_TOKEN"
```

- `deviceId` 缺失或设备未注册返回 `800`（HTTP 404）；设备属于其它应用返回 403；
- `namespaceKey` 为空时默认 `device-backups`，该命名空间首次上传时自动创建（名称「设备备份」，
  渠道 `default`、配额取默认值），不需要管理端预先建；
- `deviceName` 为空时回填设备展示名；`channel` 用客户端上报值，未上报时继承命名空间渠道，
  **不按设备分支强制回填**（避免与命名空间渠道门禁冲突）；
- 与 `/api/v1/admin/backups` 复用同一条 `IBackupService` 创建路径：同一套 `<64 KB` 内联/超限转存、
  SHA-256 校验、配额、幂等与软删规则；
- 上传写审计 `backups.client.upload`、列表写 `backups.client.list`，`Idempotency-Key` 可选；
- 权限为 `backup.create` / `backup.read`，跨应用/无应用上下文一律 403。

## 6. 设备客户端接入要点（现场网络）

- **设备通路走明文 HTTP**：`http://<内网 IP>:<API 端口>`，默认部署里 API 端口 8080；
  管理台 https（8443）只是浏览器入口，设备端不要配 https（自签证书客户端不信任，会直接连不上）。
- **令牌用 M2M**：`POST /api/v1/apps/token`，请求头 `X-App-Key` + `X-Api-Key`，
  令牌自带 `app` 声明；不要把管理台用户令牌发给现场设备。
- **重连策略**：指数退避 + 抖动；收到 `SessionReplaced` 先检查本机是否已有实例在跑，不要盲目抢连。
- **心跳**：按 `heartbeatSeconds` 周期发 `Heartbeat`，状态随工位切换在 `online`/`idle` 间切换；
  断线期间不需要补发，重连后重新 `RegisterDevice` 即可。
- **下发回执**：应用成功后必须调用 `AcknowledgeConfigPush({backupId, succeeded, message})`；
  服务端只认回执，广播/发送成功不算成功。
- **载荷**：内联内容只在 ≤64 KB 时才有；大备份一律用 `downloadUrl` 流式下载并校验 `sha256`。

## 7. 配置项

| 键 | 默认 | 说明 |
|---|---|---|
| `Devices:ScanIntervalSeconds` | 30 | 离线扫描周期（秒，最小 5） |
| `Devices:DefaultHeartbeatSeconds` | 10 | 客户端未上报时的默认心跳周期 |
| `Devices:OfflineMultiplier` | 3 | 离线阈值倍数 |
| `Devices:MinimumOfflineSeconds` | 60 | 离线阈值下限（秒） |
| `Devices:OfflineScanEnabled` | true | 是否启用后台离线扫描（测试可关） |

## 8. 错误码

设备域错误码段为 `800–899`：

| 码 | HTTP | 含义 |
|---|---|---|
| `800` | 404 | 设备不存在或未注册 |
| `801` | 400 | 设备身份非法（缺安装 Guid、字段超长等） |
| `802` | 403 | 设备已停用 |
| `803` | 409 | 设备离线 |
| `804` | 409 | 设备运行中（工位非待机） |

下发还会复用备份段：`407` 跨设备未确认 / 无连接，`408` 回执超时；兼容矩阵失败仍是
`403`/`404`（与 04 文档一致）。所有业务失败都在统一信封里，`traceId` 与服务端日志对齐。

## 9. 迁移与部署

设备域与文档适用版本各带来一条迁移（EF 只向前，回滚只能恢复备份 + 回退镜像）：

| 迁移 | 内容 |
|---|---|
| `20261113085954_AddDevices` | `devices` 表、`ux_devices_app_device_key` 唯一索引、心跳/机器码查询索引 |
| `20261113092424_AddDocumentApplicableVersion` | `documents.applicable_version`（可空，`varchar(64)`） |

```bash
# 生成新迁移（必须从 Platform.Api 启动，装配全部模块贡献者；统一 Release）
dotnet ef migrations add <Name> \
  --project src/Platform.Infrastructure \
  --startup-project src/Platform.Api \
  --configuration Release

# 升级现场：先迁库，再放量（多副本不能 AutoMigrate）
bash deploy/deploy.sh migrate
```

迁移前先备份数据库；`deploy/deploy.sh migrate` 与 `dotnet run --project src/Platform.Api -- --migrate`
走同一条迁移入口。空库一次性执行全部迁移已由 `tests/Platform.PostgresTests/MigrationsTests` 覆盖，
设备表行为由 `tests/Platform.PostgresTests/Devices/DeviceServiceTests` 覆盖（同一应用去重、
跨应用隔离、心跳/离线/停用、分页过滤、改名回落、非法身份）。

## 10. 验收清单

```bash
# 设备是否已注册、是否在线、最后心跳
curl -s "http://localhost:5080/api/v1/admin/devices?keyword=<机器名片段>" \
  -H "Authorization: Bearer $TOKEN"

# 模块清单里应有 devices 与三条权限码
curl -s http://localhost:5080/api/v1/platform/modules | jq '.data[] | select(.key=="devices")'
```

管理台路径：`/devices`（`device.read`），改名/启停需 `device.manage`，重启需 `device.restart`；
现场工程师角色默认没有这三条，只能做密钥/扫码登录与读写备份、文档。

相关文档：[`04-backups.md`](04-backups.md) · [`05-documents.md`](05-documents.md) ·
[`00-quickstart.md`](00-quickstart.md) · [`07-openapi.md`](07-openapi.md)
