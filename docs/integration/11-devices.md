# 节点与安装实例（设备域）与按安装实例下发

设备域是**通用能力**：物理机器是节点（`DeviceNode`），某台机器上的某个应用安装是安装实例
（`DeviceInstallation`）。任何带应用上下文的客户端都可以连 `/hubs/devices` 注册自己的安装实例、
上报心跳与工位状态、接收定向下发。平台只维护这两份事实，不绑定某个产品；老 WPF 客户端只是其中一个消费者。

三条硬边界：

1. **机器码只属于节点**，全局唯一；同一台机器在 3 个应用里是 1 个节点、3 个安装实例。
2. **安装实例只属于一个应用**；在线/工位/报警/版本分支/命令回执都落在安装实例上。
3. **授权席位按「产品 + 机器码」计数**，与安装实例数量无关。

> 两个 Hub 别混：`/hubs/devices` 是**安装实例连接**（注册/心跳/工位/回执，以及全部设备侧事件），
> `/hubs/admin` 是**管理台浏览器连接**（按权限域分组的管理侧事件，`Touch` 上行保活）。
> 按安装实例下发只走 `installation:{installationId}` 分组；应用级广播走 `app:{appKey}` 分组。

| 能力 | 端点 | 权限 |
|---|---|---|
| 安装实例连接（注册/心跳/工位/回执） | SignalR `/hubs/devices` | 任何带 `app` 声明的令牌 |
| 管理台实时事件 | SignalR `/hubs/admin` | 用户令牌，按 `perms` 入域组（`domain:devices` 等） |
| 物理节点（跨应用，平台级） | `/api/v1/admin/nodes` | `node.read` / `node.manage` / `node.restart` |
| 应用安装实例 | `/api/v1/admin/installations` | `installation.read` / `installation.manage` / `installation.restart` |
| 客户端备份上传/列表/下载 | `/api/v1/client/backups` | `backup.read` / `backup.create` |

`node.*` 只发给平台级角色（应用级角色不能持有）；`installation.*` 属于 `devices` 模块的应用级权限。

---

## 1. 身份与稳定键

安装实例注册时首帧提交 `DeviceRegistrationRequest`：

| 字段 | 说明 |
|---|---|
| `identity.installGuid` | 客户端首次安装生成并持久化的 Guid；**必填**，空 Guid 返回 `801` |
| `identity.smbiosUuid` | SMBIOS UUID（主标识首选） |
| `identity.systemDiskSerial` | 系统盘序列号（SMBIOS 不可用时的次选） |
| `identity.macAddress` | 辅助信息，不参与机器码计算 |
| `identity.frameworkVersion` | 客户端框架版本，只记录，不参与身份计算 |
| `displayName` | 节点机器名（客户端计算机名），每次注册刷新 |
| `ipAddress` | 客户端上报的本机 IP |
| `branch` | 版本分支；为空表示不改既有值 |
| `heartbeatSeconds` | 心跳周期，决定离线阈值 |
| `customName` | 应用内录入的安装实例名称，写入安装实例而非节点；管理端改过名后不再覆盖展示 |

平台不信任客户端自算的键，统一按契约包里的 `DeviceIdentityHasher` 计算：

- 机器码 = `SHA-256("AF-MACHINE|" + 规范化主标识)` 前 10 字节的 Crockford Base32（16 字符）；
  主标识取 SMBIOS UUID，缺失或为占位值（全 0/全 F、`NOTSPECIFIED` 等）时取系统盘序列号；
  两者都不可用**拒绝注册**（`808`），不再回退到安装 Guid 或 MAC——回退会让同一台机器每次重装
  都变成新节点，授权席位无法锚定；
- `installGuid` 只标识「某个应用的一次安装」；它与 `appKey` 组成安装实例唯一键
  `ux_device_installations_app_install (app_key, install_guid)`；
- 节点按 `machine_code` 全局唯一（`ux_device_nodes_machine_code`）；同一机器上的多个应用共用一条节点；
- **重装**：同一应用重新安装后 `installGuid` 变化 → 新增一条安装实例（旧实例保留为历史，
  不再心跳并可单独停用）；节点与授权席位都不变。旧安装的在线连接会被新安装注册时顶替（见 §2）；
- 机器码同时是授权主体：签发授权/离线码可给 `nodeId` 或 `machineCode`（两者都传时必须指向同一台机器）。

命名口径按「节点=硬件、安装实例=软件」划分，两条名字各自独立，`DisplayName` 都由服务端在注册/改名时重算：

| 对象 | 展示名优先级 | 客户端注册可刷新 | 管理端可覆盖 |
|---|---|---|---|
| 节点 | `custom_name` > `auto_name`（机器名） > `machine_code` | 仅机器名 | `custom_name`，传 `""` 清除后回退机器名 |
| 安装实例 | `custom_name` > `client_name`（应用内录入名） | 仅 `client_name` | `custom_name`，传 `""` 清除后回退应用内录入名 |

客户端上报的名称只会刷新机器名与应用内录入名，不会覆盖管理端自定义名；安装实例两个名都为空时
管理端按短键 `short_id` 展示（不再有 `custom_name_source` 来源列，专列即来源）。

## 2. 连接与注册

SignalR 连接用任意带应用上下文的 JWT（M2M 令牌或用户令牌均可），浏览器/设备无法自定义
`Authorization` 头时走 `access_token` 查询参数：

```text
wss://<服务端>/hubs/devices?access_token=<JWT>
```

令牌没有 `app` 声明时 `RegisterInstallation` 抛 `HubException`；连接未注册前调用心跳/工位/回执
同样被拒（`HubException`）。服务端在每次上行调用时校验令牌 `exp`，过期即断开连接
（最多滞后一个心跳周期）；传输层 ping 保活 15s/30s，与业务心跳（10s，承载在线状态）各司其职。

安装实例 → 服务端方法与返回：

| 方法 | 入参 | 返回 |
|---|---|---|
| `RegisterInstallation` | `DeviceRegistrationRequest` | `DeviceRegistrationResponse`（`installationId` / `nodeId` / `machineCode` / 两个短 id / `connectedAt`） |
| `Heartbeat` | `DeviceHeartbeatRequest` | 最新 `InstallationSummaryDto` |
| `ReportStationState` | `DeviceStationStateRequest` | 最新 `InstallationSummaryDto` |
| `AcknowledgeConfigPush` | `DeviceConfigPushAck` | `void`（无等待者时忽略） |

服务端 → 安装实例事件：

| 事件 | 负载 | 说明 |
|---|---|---|
| `ConfigPush` | `DeviceConfigPush` | 定向下发或应用组广播；大载荷只给 `downloadUrl`，不给内联内容 |
| `Restart` | `DeviceRestart` | 重启命令，`reason` 透传给设备显示 |
| `SessionReplaced` | `InstallationSessionReplaced` | 本连接已被顶替，应停止重连并让位 |
| `LicenseChanged` | `LicenseChangedNotification` | 授权变更提示（吊销/续期/签发），定向到目标机器上该应用的活跃安装实例；客户端必须回拉验签，推送不作事实来源 |
| `QrLoginApproved` | `QrLoginApprovedNotification` | 扫码审批结果，只发给发起会话的安装实例（`installGuid` 优先，退而机器码）；客户端仍以轮询领取为准 |
| `InstallationDisabled` | `InstallationDisabledNotification` | 本安装实例被管理端停用；随后服务端在下次上行即断线 |
| `NodeDisabled` | `NodeDisabledNotification` | 所在物理节点被停用，节点组广播（同机全部应用都受影响）；随后同上行即断线 |
| `UserSessionRevoked` | `UserSessionRevokedNotification` | 会话被吊销提示（登出/管理员吊销/改密），定向 `installGuid` 优先、退机器码；`sessionId` 非空表示单会话吊销；强制兜底是用户 epoch 守卫 |
| `ReleasePublished` | `ReleasePublishedNotification` | 新版本发布提示（`appKey`/`channelKey`/`version`），应用组广播；创建即发布、草稿发布与摄取自动发布都触发（幂等重复发布不重发）；客户端 0–5s 随机抖动后回拉 `versions/check`，推送不作事实来源 |
| `DocumentChanged` | `DocumentChangedNotification` | 客户端可见文档上传/修订/变更/删除提示（`appKey`/`collectionKey`），应用组广播；仅 `visibility=client` 触发，内部文档静默；客户端回拉文档 manifest 比对 |
| `PermissionManifestChanged` | `PermissionManifestChangedNotification` | 权限清单上传锁定/人工解锁提示（`appKey`/`locked`），应用组广播（仅锁态翻转触发）；客户端抖动后强制换令牌复核锁态，解锁则重新上报清单 |

注册成功后连接加入三个分组：`installation:{installationId}`（按安装实例下发）、
`node:{nodeId}`（节点级重启）与 `app:{appKey}`（应用级广播）。顶号规则：

- **同一安装实例**的新连接顶替旧连接：旧连接移出三个分组并收到 `SessionReplaced`；
- **同一节点同一应用**的旧安装（重装产生）的连接也会被顶替：旧安装不可能与新安装同时在线；
- **不同应用**（同机不同安装实例）互不顶号，可同时在线；
- 同一条 SignalR 连接重复 `RegisterInstallation` 到同一安装实例不会通知自己被顶替。

设备端收到 `SessionReplaced` **不要立刻重连**，否则同一安装的两个进程会互相顶号死循环；
应先确认本机是否还有另一实例在跑。节点停用返回 `DevicesNodeDisabled`（`806`），
安装实例停用返回 `DevicesInstallationDisabled`（`807`）。

**停用即时踢线**（管理端 PATCH 启停触发，顺序固定为「先推事件、再打待踢标记」）：

- 停用瞬间按连接登记表快照该安装实例/该节点的全部 connectionId 进入待踢集合；
- 服务端 Hub 守卫在每次上行调用（心跳/工位/回执）前检查：令牌 `exp` 过期或命中待踢标记即 `Abort`，
  业务方法不再执行——因此停用后断线不依赖空闲扫描周期，下一次上行即断开（验收 ≤10s）；
- 待踢标记随连接登记/注销自动清理，不会漂移到重连后的新连接上；重连的新连接会被
  注册路径按 `806`/`807` 拒绝，形成「断开—拒入」闭环；
- 重复停用不重复推事件（只认真实启用→停用翻转）；事件推送失败只记日志，踢线标记仍生效。

客户端收到 `InstallationDisabled`/`NodeDisabled` 应置为停用态并把重连退避拉长（≥5 分钟），
注册成功后清除标记；收到 `UserSessionRevoked` 且归属命中本地会话时立即登出。

断线处理：连接的安装实例**最后一条**连接断开时，服务端立即把该安装实例标记为离线（带时间条件，
不会覆盖随后新连接的在线状态），不等待心跳超时；离线扫描仍作为兜底。

心跳/工位/回执的安装实例归属只从连接登记表取，客户端不能自报 `installationId`。

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
- `InstallationOfflineWorker` 每 `Devices:ScanIntervalSeconds`（默认 30，最小 5）扫描一次并批量标记离线；
- **推送判定要同时满足**：数据库 `online_status=online` **且** 连接登记表里还有活跃连接。
  未等到离线扫描落库但连接已断时，命令不会被发到不存在的连接上；
- 节点在线/工位/报警是**聚合结果**，不落列：任一安装实例在线即节点在线；任一在线安装实例工位运行
  即节点运行；报警取最严重级别（同级取最近心跳）；`lastHeartbeatAt` 取最大值。

`ReportStationState` 只接受 `idle` / `online`，会同时把安装实例置为在线；下发门禁只看 `station_status`。

### 管理台实时事件（`/hubs/admin` `domain:devices`）

令牌 `perms` 含 `installation.*` 或 `node.*` 前缀的连接进入 `domain:devices`。状态跃迁落库后单向推送，**只是提示**：管理台收到即失效回拉列表/详情，推送失败只记日志、不影响已落库事实。

| 事件 | 负载 | 触发条件 |
|---|---|---|
| `InstallationStateChanged` | `AdminInstallationStateChangedNotification{ appKey, installationId, nodeId, onlineStatus, stationStatus, alarmLevel, occurredAt }` | 安装实例三值（在线/工位/报警）任一变化；相同心跳完全静默 |
| `NodeStateChanged` | `AdminNodeStateChangedNotification{ nodeId, onlineStatus, stationStatus, alarmLevel, occurredAt }` | 节点**聚合**三值变化；实例跃迁但聚合不变时不发 |

- 覆盖全部跃迁路径：心跳/工位上报、断开即时离线、离线扫描批量（每实例一条、每节点聚合至多一条）、重连注册即上线；注册新行的首帧不发（无前值可比）。
- 节点聚合口径与 `NodeSummaryDto` 一致：任一实例在线即在线、任一在线实例工位运行即运行、报警取最严重级别。
- 节点事件故意不带 `appKey`：节点跨应用，管理台对该事件一律失效回拉。

## 3. 管理端 API

### 3.1 物理节点（平台级）

```bash
# 列表：关键字匹配机器码/展示名/机器名/IP；可按在线状态与启用状态过滤
curl -s "http://localhost:5080/api/v1/admin/nodes?page=1&pageSize=20&onlineStatus=online&isEnabled=true&keyword=coater" \
  -H "Authorization: Bearer $TOKEN"

# 详情：内嵌该节点全部安装实例（跨应用）
curl -s http://localhost:5080/api/v1/admin/nodes/$NODE_ID \
  -H "Authorization: Bearer $TOKEN"

# 改名/启停：字段为空表示不改；customName 传空串表示清除（清除后回退客户端上报的机器名）
curl -s -X PATCH http://localhost:5080/api/v1/admin/nodes/$NODE_ID \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"customName":"3号线涂布机","isEnabled":true}'

# 重启节点：该节点全部在线安装实例都收到命令（reason 可选，≤256 字符）
curl -s -X POST http://localhost:5080/api/v1/admin/nodes/$NODE_ID/restart \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"reason":"现场维护"}'

# 节点下安装实例分页
curl -s "http://localhost:5080/api/v1/admin/nodes/$NODE_ID/installations?pageSize=20" \
  -H "Authorization: Bearer $TOKEN"

# 删除节点：仅当该节点已不挂任何安装实例（含停用实例）
curl -s -X DELETE http://localhost:5080/api/v1/admin/nodes/$NODE_ID \
  -H "Authorization: Bearer $TOKEN"
```

- 列表固定按 `lastSeenAt` 倒序、再按展示名排序；`onlineStatus` 只接受 `online`/`offline`；
- 节点详情/编辑/重启不要求应用上下文（跨应用），但只有平台级角色能持有 `node.*`；
- 节点停用是**全局开关**：该机器所有应用拒绝注册/心跳/重启/下发，重新启用后设备重连即恢复；
- 重启只发给在线连接：节点不存在 404（`809`）、停用 403（`806`）、没有任何在线安装实例 409（`803`）；
  响应 `installationCount` 为实际收到命令的在线安装实例数；
- 改名/启停写审计 `nodes.update`，重启写 `nodes.restart`（成功与失败都记，原因进 `message`）；
- 删除写审计 `nodes.delete`：仍挂有安装实例时 409（通用冲突码 `6`，消息里带上实例数），
  需先在对应应用下逐个删除安装实例；不存在 404（`809`）。

### 3.2 安装实例（应用级）

```bash
# 列表：只返回当前应用；关键字匹配机器码/节点名/IP/安装实例展示名/应用内录入名，branch 按关键词模糊匹配
curl -s "http://localhost:5080/api/v1/admin/installations?onlineStatus=online&branch=line-a&keyword=coater" \
  -H "Authorization: Bearer $TOKEN"

# 详情：跨应用返回 404
curl -s http://localhost:5080/api/v1/admin/installations/$INSTALLATION_ID \
  -H "Authorization: Bearer $TOKEN"

# 应用级改名与启停：customName 传空串清除（清除后回退应用内录入名）
curl -s -X PATCH http://localhost:5080/api/v1/admin/installations/$INSTALLATION_ID \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"customName":"一号工位","isEnabled":false}'

# 重启单个安装实例
curl -s -X POST http://localhost:5080/api/v1/admin/installations/$INSTALLATION_ID/restart \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"reason":"现场维护"}'

# 删除安装实例：仅当前应用、且离线无活跃连接
curl -s -X DELETE http://localhost:5080/api/v1/admin/installations/$INSTALLATION_ID \
  -H "Authorization: Bearer $TOKEN"
```

- 列表固定按 `lastHeartbeatAt` 倒序、再按节点展示名排序；`onlineStatus` 只接受 `online`/`offline`；
  `branch` 与 `keyword` 都是关键词模糊匹配（`%关键词%`），不做全值相等判定；
- 所有安装实例端点都要求令牌带应用上下文（无 `app` 返回 403），只返回/只接受本应用的行；
- 有效启用 = 节点启用 **且** 安装实例启用；节点停用优先，应用管理员即使启用安装实例也无法注册/心跳；
- 重启门禁顺序：不存在 404（`805`）→ 节点停用 403（`806`）→ 安装实例停用 403（`807`）→ 离线 409（`803`）；
- 改名/启停写审计 `installations.update`，重启写 `installations.restart`；
- 停用只阻止注册/心跳/重启/下发，不删除记录；旧安装（重装遗留）可单独停用；
- 删除写审计 `installations.delete`：仍有活跃连接 409（通用冲突码 `6`）、状态仍为 `online` 同样 409
  （在线判定以真实连接为准，状态列由心跳与离线扫描维护，可能滞后一两拍）、跨应用或不存在 404（`805`）。
  删除会同步失效心跳缓存条目，后台批量回写不会再去更新已删除的行。

### 3.3 删除与留痕边界

设备域的删除只清设备登记本身：**授权与会话按机器码 / 安装标识记账，不随节点行或安装实例行删除而
清理，也不作为删除的前置条件**（`license_entitlements`、`offline_license_codes`、`refresh_tokens`、
`qr_login_sessions`、`single_login_keys` 与备份的 `node_id`/`installation_id` 都是跨模块留痕列，
设备模块不查询它们）。设备重新注册会新建节点行与安装实例行，旧留痕继续按原机器码/安装标识可查。
需要清理授权时走授权台账自身的停用/吊销入口。

## 4. 按安装实例下发

`POST /api/v1/admin/backups/{id}/push` 必须用 `targetInstallationId` 或 `targetInstallationIds`
显式指定目标（可多选，合并去重）；**留空不再广播到应用内全部在线连接**，需要应用组广播时只能
显式传 `broadcastToApp=true`（管理台不提供该入口，属于兼容能力）。

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/backups/$ID/push \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{
    "targetInstallationIds":["<安装实例 id>","<安装实例 id>"],
    "targetChannel":"default",
    "targetFrameworkVersion":"3.2.0",
    "restartAfterApply":false,
    "force":false,
    "currentHash":"<仅单安装实例下发时可省略>",
    "confirmCrossInstallation":false
  }'
```

| 条件 | 结果 |
|---|---|
| 目标留空且未显式广播 | `2`（HTTP 400） |
| 同时指定目标安装实例与 `broadcastToApp=true` | `2`（HTTP 400） |
| 广播或任一目标带 `currentHash` | `2`（HTTP 400），快照只能对应单个安装实例 |
| 安装实例不存在或不属于当前应用 | `805`（HTTP 404） |
| 所在节点已停用 | `806`（HTTP 403） |
| 安装实例已停用 | `807`（HTTP 403） |
| 离线（库在线状态与活跃连接任一不满足） | `803`（HTTP 409） |
| 工位不是 `idle`（运行中） | `804`（HTTP 409） |
| 备份来源安装实例与任一目标不同（含应用组广播）且未确认 | `407`（HTTP 409），`confirmCrossInstallation=true` 放行 |
| 全部目标都不回执 | `408`（HTTP 504），超时 `Backups:PushAckTimeoutSeconds`（默认 60 秒） |
| 发送时全部目标连接刚好断开 | `803`（HTTP 409） |

多安装实例下发会先对所有目标跑完门禁再并发发送：任一目标不满足就整批拒绝，不会出现
「一部分安装实例已收到命令」。响应 `targets` 逐台返回
`{ installationId, nodeId, delivered, acked, success, message }`；顶层 `acked` / `success`
表示全部目标是否回执/成功。部分目标回执失败时 HTTP 200、`success=false`。

兼容矩阵（渠道/主版本/`force`）在安装实例门禁**之前**执行，与广播下发共用同一份规则，见
[`04-backups.md`](04-backups.md)。下发命令里的下载地址固定为
`/api/v1/client/backups/{id}/download`，客户端用同一令牌回拉；大备份只下发地址，不内联负载。

Ack 等待键是 `(appKey, installationId, backupId)`：同一安装实例同一备份只认第一个回执，
超时后迟到的回执被忽略（不会补记成功）。全部目标成功时发布 `backup.pushed` Webhook，
负载里带 `targetInstallationIds`、`snapshotId`、`successCount` 与 `failureCount`。

提供 `currentHash` 时下发前会在同一命名空间落一条 `pre-push-snapshot`
（`name=pre-push-snapshot`，记录目标安装实例、节点与当前哈希，`sizeBytes=0`），返回 `snapshotBackupId`。

应用组广播（`broadcastToApp=true`）走 `/hubs/devices` 的 `app:{appKey}` 分组，
负载与定向下发同为 `DeviceConfigPush`；回执键是 `(appKey, backupId)`、只认第一个回执
（任一安装实例的 `AcknowledgeConfigPush` 都可完成等待）；应用无在线连接时立即返回 `407`（HTTP 409）。
广播同样受跨安装实例确认约束：备份来源安装实例非空时，必须 `confirmCrossInstallation=true`。

## 5. 客户端备份端点

```bash
# 上传：installationId 必填，安装实例必须已在本应用注册
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
    "channel":"default",
    "payloadJson":"{\"theme\":\"dark\"}"
  }'

# 列表：只在本应用、默认 device-backups 命名空间；可按安装实例/类型过滤
curl -s "http://localhost:5080/api/v1/client/backups?installationId=<INSTALLATION_ID>&kind=config&pageSize=20" \
  -H "Authorization: Bearer $CLIENT_TOKEN"

# 元数据与下载（下发后回拉同一对端点）
curl -s http://localhost:5080/api/v1/client/backups/$BACKUP_ID -H "Authorization: Bearer $CLIENT_TOKEN"
curl -s http://localhost:5080/api/v1/client/backups/$BACKUP_ID/download -H "Authorization: Bearer $CLIENT_TOKEN"
```

- `installationId` 缺失返回 400；安装实例不存在或不属于当前应用返回 `805`（HTTP 404）；
- `namespaceKey` 为空或传基键 `device-backups` 时，按上报 `channel` 映射为 `device-backups-{channel}`，
  首次上传自动创建（名称「设备备份（channel）」、渠道即该 channel、配额取默认值），不需要管理端预先建；
  **一渠道一命名空间**，各渠道独立保留备份，互不挤占配额；
- 服务端按 `installationId` 解析节点，备份行同时快照 `installation_id` / `node_id` / `node_name`，
  节点页与备份列表据此展示，客户端不传 `nodeId`；
- `channel` 用客户端上报值，未上报按 `default` 渠道建空间，**不按安装实例分支强制回填**；
  列表按 `namespaceKey` 取对应渠道空间，恢复与下发按备份自身渠道匹配；
- 与 `/api/v1/admin/backups` 复用同一条 `IBackupService` 创建路径：同一套 `<64 KB` 内联/超限转存、
  SHA-256 校验、配额（含并发兜底）、幂等与软删规则；
- 上传写审计 `backups.client.upload`、列表写 `backups.client.list`，`Idempotency-Key` 可选；
- 权限为 `backup.create` / `backup.read`，无应用上下文一律 403。

## 6. 客户端接入要点（现场网络）

- **设备通路走明文 HTTP**：`http://<内网 IP>:<API 端口>`，默认部署里 API 端口 8080；
  管理台 https（8443）只是浏览器入口，设备端不要配 https（自签证书客户端不信任，会直接连不上）。
- **令牌用 M2M**：`POST /api/v1/apps/token`，请求头 `X-App-Key` + `X-Api-Key`，
  令牌自带 `app` 声明；不要把管理台用户令牌发给现场设备。
- **身份**：`installGuid` 必须持久化（重装才允许变化）；主标识优先 SMBIOS UUID，
  取不到时上报系统盘序列号；都取不到会被拒绝注册（`808`），不要伪造机器码。
- **重连策略**：指数退避 + 抖动；收到 `SessionReplaced` 先检查本机是否已有实例在跑，不要盲目抢连。
- **心跳**：按 `heartbeatSeconds` 周期发 `Heartbeat`，状态随工位切换在 `online`/`idle` 间切换；
  断线期间不需要补发，重连后重新 `RegisterInstallation` 即可。
- **下发回执**：应用成功后必须调用 `AcknowledgeConfigPush({backupId, succeeded, message})`；
  服务端只认回执，发送成功不算成功。
- **载荷**：内联内容只在 ≤64 KB 时才有；大备份一律用 `downloadUrl` 流式下载并校验 `sha256`。

## 7. 统计口径

仪表盘（`GET /api/v1/platform/dashboard`）的设备统计由 `IInventoryStatistics` 提供：

| 字段 | 含义 |
|---|---|
| `nodeTotal` / `nodeOnline` / `nodeRunning` / `nodeIdle` / `nodeOffline` / `nodeDisabled` | 按物理节点计数；在线/运行按「任一安装实例满足」聚合 |
| `installationTotal` / `installationOnline` | 全平台安装实例数与库内在线数 |
| `connectedInstallations` | 当前实时连接数（进程内连接登记表） |

同一台机器跑 3 个应用只计 **1 个节点、3 个安装实例**，不会再被重复计成 3 台设备。

## 8. 配置项

| 键 | 默认 | 说明 |
|---|---|---|
| `Devices:ScanIntervalSeconds` | 30 | 离线扫描周期（秒，最小 5） |
| `Devices:DefaultHeartbeatSeconds` | 10 | 客户端未上报时的默认心跳周期 |
| `Devices:OfflineMultiplier` | 3 | 离线阈值倍数 |
| `Devices:MinimumOfflineSeconds` | 60 | 离线阈值下限（秒） |
| `Devices:OfflineScanEnabled` | true | 是否启用后台离线扫描（测试可关） |

## 9. 错误码

设备域错误码段为 `800–899`：

| 码 | HTTP | 含义 |
|---|---|---|
| `800` | 404 | 设备不存在（旧的按设备语义保留，节点/安装实例用 805/809） |
| `801` | 400 | 安装实例身份非法（缺安装 Guid、字段超长等） |
| `802` | 403 | 设备已停用（旧语义保留） |
| `803` | 409 | 安装实例/节点离线 |
| `804` | 409 | 安装实例运行中（工位非待机） |
| `805` | 404 | 安装实例不存在或不属于当前应用 |
| `806` | 403 | 物理节点已停用 |
| `807` | 403 | 安装实例已停用 |
| `808` | 400 | 缺少可用的主标识（SMBIOS UUID / 系统盘序列号） |
| `809` | 404 | 物理节点不存在 |

下发还会复用备份段：`407` 跨安装实例未确认 / 应用无在线连接，`408` 回执超时；兼容矩阵失败仍是
`403`/`404`（与 04 文档一致）。所有业务失败都在统一信封里，`traceId` 与服务端日志对齐。

删除守卫用段外的通用冲突码 `6`（HTTP 409），不新占 `800–899`；不存在仍然用 `805`/`809`。

## 10. 迁移与部署

测试期只保留一条单基线，节点与安装实例的建表、索引都在其中：

| 迁移 | 内容 |
|---|---|
| `20261118030314_InitialCreate` | `device_nodes` / `device_installations`（含 `ux_device_nodes_machine_code`、`ux_device_installations_app_install`、心跳/在线/节点查询索引）、`backups.installation_id/node_id/node_name`、`refresh_tokens.install_guid` 等全部表 |

旧迁移（`AddDevices`、`MergeDevicesByMachineCode` 等）与旧快照已删除；没有 down-migration，
旧库直接重建（测试期无需兼容数据）。EF 只向前：回滚只能恢复备份 + 回退镜像。

```bash
# 生成新迁移（必须从 Platform.Api 启动，装配全部模块贡献者；统一 Release）
dotnet ef migrations add <Name> \
  --project src/Platform.Infrastructure \
  --startup-project src/Platform.Api \
  --configuration Release

# 升级现场：先迁库，再放量（多副本不能 AutoMigrate）
bash deploy/deploy.sh migrate
```

空库一次性执行基线已由 `tests/Platform.PostgresTests/MigrationsTests` 覆盖；节点/安装实例行为由
`tests/Platform.PostgresTests/Devices/DeviceServiceTests` 覆盖（同一应用重装、同机多应用隔离、
顶号与断线、心跳/离线/停用、分页过滤、改名来源、非法身份）。

## 11. 验收清单

```bash
# 节点是否已注册、是否在线、挂了几个应用
curl -s "http://localhost:5080/api/v1/admin/nodes?keyword=<机器名片段>" \
  -H "Authorization: Bearer $TOKEN"

# 当前应用的安装实例（令牌需先切到该应用）
curl -s "http://localhost:5080/api/v1/admin/installations?onlineStatus=online" \
  -H "Authorization: Bearer $TOKEN"

# 模块清单里应有 devices 与三条 installation.* 权限码
curl -s http://localhost:5080/api/v1/platform/modules | jq '.data[] | select(.key=="devices")'
```

管理台路径：`/nodes`（平台级，`node.read`；改名/启停 `node.manage`，重启 `node.restart`）与
`/installations`（应用级，`installation.read` / `installation.manage` / `installation.restart`）。
现场工程师角色默认没有这些码，只能做密钥/扫码登录与读写备份、文档。
签发授权/离线码时给 `nodeId` 或 `machineCode` 均可；备份下发只列在线且待机的安装实例。

相关文档：[`04-backups.md`](04-backups.md) · [`05-documents.md`](05-documents.md) ·
[`00-quickstart.md`](00-quickstart.md) · [`07-openapi.md`](07-openapi.md)
