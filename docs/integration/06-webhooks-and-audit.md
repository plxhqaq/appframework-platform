# Webhook 投递与审计查询（B7）

> 本文覆盖两件收口能力：**Webhook**（领域事件 → 外部系统，带 HMAC 签名与指数退避重投）
> 与**审计查询**（谁在什么时候做了什么，管理台「审计」页的唯一数据源）。
> 端点、字段、常量与默认值全部取自源码；标识符一律用代码里的原词。
> 前置链路（登录、应用上下文）见 [`00-quickstart.md`](00-quickstart.md)。

---

## 0. 一条铁律

Webhook 是**旁路通知，不是事实来源**。

- 事件发布失败只记日志，绝不会把版本发布 / 授权吊销这些主业务拖垮
  （`src/Platform.Modules.Webhooks/Services/WebhookEventPublisher.cs:13-17`）；
- 接收方收到 `license.revoked` 之后，仍然要回查自己的授权系统或平台状态接口确认；
- 平台没有应用上下文的平台事件**不投递**（订阅按应用隔离，`AppKey` 为空直接跳过）。

---

## 1. 事件目录

| 事件 | 触发点 | `data` 字段 |
|---|---|---|
| `version.released` | 版本发布成功（`ReleaseService.cs:438`） | `releaseId` / `channelKey` / `version` / `publishedAt` |
| `license.revoked` | 授权吊销（`LicenseEntitlementService.cs:331`） | `entitlementId` / `productKey` / `machineCode` / `revokedAt` / `reason` |
| `backup.pushed` | 备份下发成功（`BackupPushService.cs:140`） | `backupId` / `namespaceKey` / `targetDeviceId` / `snapshotId` |
| `document.uploaded` | 文档上传生成修订（`DocumentService.cs:270`） | `documentId` / `collectionKey` / `title` / `visibility` / `revision` |
| `document.deleted` | 文档删除（`DocumentService.cs:439`） | `documentId` / `collectionKey` |
| `webhook.test` | 手动点「测试」（`WebhookSubscriptionService.cs:234`） | `message` |

白名单与判定函数在 `WebhookConventions.EventTypes` / `IsKnownEvent`，
单条订阅最多 **20** 个事件（`WebhookConventions.MaxEventTypes`）。

> **留空 = 全订阅**：`EventTypes` 传空数组表示该应用下所有事件都投给这个订阅
> （`WebhookEventPublisher.cs:67-71` 只在列表非空时做过滤）。

---

## 2. 订阅管理

全部在管理端 `/api/v1/admin/webhooks`（匿名自述端点另见 §8）。

| 方法 | 路由 | 权限码 | 幂等键 |
|---|---|---|---|
| `GET` | `/api/v1/admin/webhooks` | `webhook.read` | — |
| `POST` | `/api/v1/admin/webhooks` | `webhook.manage` | **支持**（作用域 `webhooks.subscription.create`，TTL 24h） |
| `PATCH` | `/api/v1/admin/webhooks/{id}` | `webhook.manage` | — |
| `DELETE` | `/api/v1/admin/webhooks/{id}` | `webhook.manage` | 订阅须先停用，且无待投递/待重试记录 |
| `POST` | `/api/v1/admin/webhooks/{id}/secret/rotate` | `webhook.manage` | — |
| `POST` | `/api/v1/admin/webhooks/{id}/test` | `webhook.manage` | — |
| `GET` | `/api/v1/admin/webhooks/deliveries` | `webhook.read` | — |
| `POST` | `/api/v1/admin/webhooks/deliveries/{deliveryId}/redeliver` | `webhook.manage` | — |

订阅删除（`DELETE /api/v1/admin/webhooks/{id}`）有两道门：**启用中的订阅拒绝**（先 `PATCH` 置
`isEnabled=false`），且仍有 `pending` / `failed` 投递时拒绝——这两种状态会被 dispatcher 继续重试，
删掉订阅等于丢弃在途投递。两者都返回通用冲突 `6`（HTTP 409）。放行时该订阅的投递记录随订阅一并删除；
`succeeded` / `dead` 这类已终结记录不阻塞删除。

### 2.1 创建

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/webhooks \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: whk-erp-hook-001' \
  -d '{"url":"https://erp.example.com/hooks/platform","eventTypes":["version.released","license.revoked"],"description":"ERP 集成"}'
```

响应 `data` 是 `CreatedWebhookSubscriptionDto`：订阅字段 + **明文 `secret`，只出现这一次**。
服务端把它存进 `webhook_subscriptions.secret`（列长 128），列表接口只回 `secretHint`
（前 14 字符 + `…`，见 `WebhookSigner.Hint`），用于人工辨认这条订阅由哪个密钥签名。

密钥形态：`whsec_` + 32 字节随机数的 base64url（`WebhookSigner.cs:15-21`）。

### 2.2 校验规则

| 项 | 规则 | 违反时的错误码 |
|---|---|---|
| `url` | 必须 http/https 绝对地址，≤ 2048 字符 | `701` `WebhooksUrlInvalid` |
| `url` 目标地址 | 默认拒绝环回、私网、链路本地与保留地址；内网回调须列入 `Webhooks:AllowedHosts` | `701` `WebhooksUrlInvalid` |
| `eventTypes` | 必须在 §1 白名单内，去重后 ≤ 20 | `702` `WebhooksEventUnknown` |
| `description` | ≤ 1024 字符 | 校验失败（通用码） |
| 订阅不存在 | 任何按 `{id}` 的操作 | `700` `WebhooksSubscriptionNotFound` |

出站地址策略（SSRF）：

- 循环判断覆盖 IPv4 的 `0.0.0.0/8`、`10/8`、`100.64/10`、`127/8`、`169.254/16`、
  `172.16/12`、`192.168/16`、`198.18/15`、组播与保留段，以及 IPv6 的 `::1`、`::/96`（IPv4 兼容格式，
  可夹带 `127/8` 等地址）、`fc00::/7`、`fe80::/10`、`ff00::/8`、6to4/Teredo/NAT64 前缀；
  `localhost` 与 `*.localhost` 直接拒绝。
- 主机名会在创建/修改时解析一次，**每次投递前再解析一次** —— 防止先解析到公网、随后被改成内网
  的 DNS 重绑定。解析超时或失败按「当前不可达」放行，投递阶段仍会复检。
- 投递客户端**不跟随重定向**（3xx 直接记失败）：否则公网地址 302 到内网同样能绕过地址校验。
- 内网目标必须显式配置白名单：支持精确主机名、`*.example.com` 通配子域、精确 IP 与 CIDR，
  多个值用逗号或分号分隔：

```json
{ "Webhooks": { "AllowedHosts": "hooks.internal.example.com,10.20.0.0/16,192.168.5.10" } }
```

`AllowedHosts` 是**逗号/分号分隔的字符串**（不是数组），这样容器里可以直接用单个环境变量：
`Webhooks__AllowedHosts=10.20.0.0/16,hooks.internal.example.com`。
白名单按「显式信任」处理：命中白名单的主机名不再解析校验；IP/CIDR 只匹配解析结果中的地址，
同一次解析里混有未列入白名单的私网地址时仍然拒绝。

### 2.3 轮换密钥

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/webhooks/$ID/secret/rotate
```

返回 `WebhookSecretRotatedDto`（新明文密钥 + 提示）。**轮换即失效旧密钥**：
接收端必须先把新密钥配置好再调用，否则切换窗口内的投递全部验签失败。

### 2.4 测试

`POST /api/v1/admin/webhooks/{id}/test` 生成一条 `webhook.test` 投递并立即进入队列，
返回 `WebhookTestResultDto(deliveryCount = 1)`。这是唯一由手动触发的
事件；`data.message` 固定为「这是一条 Webhook 测试事件。」。

---

## 3. 签名与接收端验签

每次投递都带四个头（`WebhookConventions.cs:28-37`，发送在 `WebhookSender.cs:91-98`）：

| 头 | 含义 |
|---|---|
| `X-Platform-Signature` | `sha256=` + `hex(HMAC-SHA256(secret, "{timestamp}.{rawBody}"))`，**十六进制小写** |
| `X-Platform-Timestamp` | Unix 秒 |
| `X-Platform-Event` | 事件类型，等于信封里的 `type` |
| `X-Platform-Delivery` | 投递 id（Guid），等于信封里的 `id`，接收方据此做幂等 |

签名串是 `{timestamp}.{原始请求体}`，其中原始体按字节参与计算，所以验签必须用
**未解析的原始 body**，不能用反序列化后重新序列化的 JSON。

信封（`WebhookSigner.BuildPayload`）：

```json
{
  "id": "01a199e4-…",
  "type": "version.released",
  "appKey": "demo",
  "occurredAt": "2026-11-02T01:20:33+00:00",
  "data": { "releaseId": "…", "channelKey": "stable", "version": "1.2.3", "publishedAt": "…" }
}
```

Node 接收端的最小实现：

```js
import crypto from "node:crypto";

function verify(rawBody, signatureHeader, timestampHeader, secret, toleranceSeconds = 300) {
  const expected = "sha256=" + crypto
    .createHmac("sha256", secret)
    .update(`${timestampHeader}.${rawBody}`)
    .digest("hex");
  const supplied = (signatureHeader ?? "").trim();
  const a = Buffer.from(expected);
  const b = Buffer.from(supplied);
  if (a.length !== b.length || !crypto.timingSafeEqual(a, b)) return false;

  // 平台不带重放窗口，容忍度由接收端自己定：超时的旧投递直接拒
  const age = Math.abs(Date.now() / 1000 - Number(timestampHeader));
  return age <= toleranceSeconds;
}
```

> 服务端**没有**重放窗口的常量或判断，`X-Platform-Timestamp` 只进签名。
> 防重放靠接收端：核对时间戳容差 + 用 `X-Platform-Delivery` 去重（同一投递 id 只处理一次）。

---

## 4. 投递生命周期与退避

状态机（`WebhookConventions.cs:16-25`）：

```
pending ──成功(2xx)──> succeeded
   │
   └──失败──> failed ──退避到期重投──> succeeded / failed …
                                     └─次数用尽─> dead
```

| 参数 | 默认 | 配置键（`Webhooks` 段） |
|---|---|---|
| 退避基数 | 30 秒 | `BaseDelaySeconds` |
| 退避上限 | 3600 秒 | `MaxDelaySeconds` |
| 最大投递次数（含首次） | 8 | `MaxAttempts` |
| 单次请求超时 | 10 秒 | `TimeoutSeconds` |
| 扫描间隔 | 15 秒（下限 5） | `DispatchIntervalSeconds` |
| 单轮批量 | 50（下限 1） | `BatchSize` |
| 投递器开关 | `true` | `DispatcherEnabled` |
| 终态台账保留 | 30 天（下限 1） | `DeliveryRetentionDays` |

退避公式：`delay = min(BaseDelaySeconds × 2^(attempts-1), MaxDelaySeconds)`，指数封顶 16，
**没有抖动**（`WebhooksOptions.cs:33-40`）。前 8 次的等待：

| 第 n 次尝试 | 距上次的等待 |
|---|---|
| 1 | 立即 |
| 2 | 30s |
| 3 | 1m |
| 4 | 2m |
| 5 | 4m |
| 6 | 8m |
| 7 | 16m |
| 8 | 32m |

判定标准只有 HTTP：**2xx 记 `succeeded`**，其余（含 3xx/4xx/5xx、超时、连接失败）记 `failed` 并写
`last_error`（列长 1024）。接收端想终止重投，就返回 2xx。

变成 `dead` 的三种成因：

1. 尝试次数达到 `MaxAttempts`；
2. 投递时订阅已被删除 → `dead`，原因「订阅不存在或已停用。」；
3. 投递时订阅已停用 → 同上。

### 4.1 台账保留与清理

`WebhookDeliveryCleanupTask`（名称 `webhooks`）挂在 `RetentionWorker` 上，每轮按
`Webhooks:DeliveryRetentionDays`（默认 30 天）分批删除 `succeeded` / `dead` 的投递记录，
按 `updated_at` 计时，单批 500 行。`pending` / `failed` 即使很旧也不动，避免误删待投递队列。
删除数量累加到 `platform_retention_deleted_rows_total{task="webhooks"}`。需要长期留存的投递明细
请在保留期之前导出。

### 4.2 多副本：当前由启动守卫拒绝

`DispatchDueAsync` 是「查出到期行 → 逐条发 → 回写」，**没有行级抢占**
（`WebhookDispatcher.cs:66-72`）。两个副本同开 `DispatcherEnabled` 会把同一事件投递两次。

当前版本在启动期用 PostgreSQL advisory lock 强制单副本（`Deployment:RequireSingleInstance`），
共用同一数据库的第二个实例会启动失败，因此正常情况下不会出现双投递。
只有在实现投递行级认领后再关闭守卫、改为多副本时，才需要让其余副本设
`Webhooks__DispatcherEnabled=false`（入队仍在任意副本发生），并补齐 SignalR 背板与回执路由。

---

## 5. 台账与手动重投

```bash
# 全部投递（分页）；也可按订阅过滤
curl -s "http://localhost:5080/api/v1/admin/webhooks/deliveries?page=1&pageSize=20&subscriptionId=$ID" \
  -H "Authorization: Bearer $TOKEN"

# 重投：只允许从 failed / dead 出发，且会把 attempts 归零重新计
curl -s -X POST http://localhost:5080/api/v1/admin/webhooks/deliveries/$DELIVERY_ID/redeliver \
  -H "Authorization: Bearer $TOKEN"
```

| 情况 | 错误码 |
|---|---|
| 投递不存在 | `703` `WebhooksDeliveryNotFound` |
| 状态是 `succeeded`/`pending`，不能重投 | `704` `WebhooksDeliveryNotRetryable`（HTTP 409） |

重投的语义是「当作第一次投递重新排队」，因此接收端**必须**按 `X-Platform-Delivery` 幂等，
否则同一事件会被下游处理两遍。

---

## 6. 模块自述与错误码段

`GET /api/v1/webhooks/info` 匿名返回模块键、路由与所需权限码（与其他模块同形态）。

| 码 | 枚举 | HTTP |
|---|---|---|
| 700 | `WebhooksSubscriptionNotFound` | 404 |
| 701 | `WebhooksUrlInvalid` | 400 |
| 702 | `WebhooksEventUnknown` | 400 |
| 703 | `WebhooksDeliveryNotFound` | 404 |
| 704 | `WebhooksDeliveryNotRetryable` | 409 |

HTTP 状态一律由 `ApiErrorHttpStatus.Map` 给出（`src/Platform.Kernel/Errors/ApiErrorHttpStatus.cs:117-121`）。

---

## 7. 审计查询

唯一查询端点：

```bash
curl -s -G "http://localhost:5080/api/v1/admin/audit-logs" \
  --data-urlencode "action=versions.release" \
  --data-urlencode "result=failure" \
  --data-urlencode "from=2026-11-01T00:00:00Z" \
  --data-urlencode "to=2026-11-02T23:59:59Z" \
  --data-urlencode "keyword=LIC-20261030-001" \
  -d page=1 -d pageSize=50 \
  -H "Authorization: Bearer $TOKEN"
```

| 参数 | 语义 |
|---|---|
| `page` / `pageSize` | 默认 `1` / `20`，`pageSize` 被夹到 1–200 |
| `appKey` | 精确匹配 |
| `action` | 子串匹配（`contains`） |
| `actor` | 子串匹配，同时比 `actorId` 与 `actorName` |
| `result` | 精确匹配：`success` / `failure` |
| `keyword` | 在 `action` / `actorName` / `targetId` / `message` 四列里找 |
| `from` / `to` | 按 `createdAt` 闭区间 |

排序固定 `createdAt DESC`，**不支持**自定义排序参数。权限码 `audit.read`
（`platformAdminOnly = true`：只发给平台管理员角色，普通应用管理员拿不到）。

### 7.1 记录了什么

每条审计（`AuditEntry`，表 `audit_logs`）包含：
`appKey`、`actorId`、`actorName`、`action`、`targetType`、`targetId`、`result`、`message`、
`ipAddress`、`userAgent`、`traceId`、`detailJson`（`jsonb`）、`createdAt`。

- 写端点**成功与失败都记**（`result` = `success` / `failure`）；
- `action` 命名是 `{module}.{resource}.{verb}`，例如 `versions.upload.complete`、
  `identity.sessions.revoke-all`、`webhooks.subscription.rotate-secret`；
- `traceId` 取自 `HttpContext.TraceIdentifier`，与统一响应里的 `traceId` 同源 —— 
  报障时拿 `traceId` 在审计里定位这一步，再去日志里用同一值串起来；
- 审计写入失败被吞掉，不影响业务请求，但会累加计数器
  `platform_audit_write_failures_total{action}`（`EfAuditWriter.cs:55-74`）。
  **这个计数器非零必须告警**：它意味着你在丢审计。

### 7.2 保留

`MaintenanceWorker` 每 `Workers:Maintenance:IntervalSeconds`（默认 600 秒）删除
`createdAt < now - AuditRetentionDays`（默认 **180 天**）的审计，单批
`max(100, BatchSize)`（默认 5000）。同一 Worker 还按 `IdempotencyRetentionHours`
（默认 24 小时）清理幂等键记录。

> 需要长期留存的合规审计请在保留期之前自行导出；平台只保证「近期可查」。

---

## 8. 管理台对应

| 能力 | 管理台页面 | 权限码 |
|---|---|---|
| 审计查询 | **「审计」页**（过滤 + 分页 + 详情含 `traceId`） | `audit.read` |
| Webhook 订阅 | **「Webhook」页**：应用上下文选择 + 新建/编辑/删除、启停、轮换密钥（明文只弹一次）、测试投递 | `webhook.read` / `webhook.manage` |
| 投递台账 | **「Webhook」页下方的「投递台账」**：按订阅过滤 + 分页，展开看负载与最近错误，失败/死信可手动重投 | `webhook.read`（重投需 `webhook.manage`） |

> 页面上的「当前应用」下拉切换的是令牌里的应用上下文，订阅与台账的应用隔离轴随之切换；
> 前端不传 `appKey`，接口仍与本文其余端点完全一致，可用 curl 替代 UI 操作。
> 密钥明文只在创建/轮换的响应里出现一次，页面不做缓存：关掉弹窗就只剩 `secretHint`。
> 种子角色只在**新库**上带 `webhook.*`；既有库升级后要在「身份与权限 → 角色」里手工补上这两个权限码
> （`platform-admin` 用通配 `*`，不受影响）。

---

## 9. 常见坑

1. **拿解析后的 JSON 验签** —— 一定失败。签名对象是原始字节，先验签再解析。
2. **只配了密钥没配事件**：`eventTypes: []` 是全订阅，不是「不订阅」；想停投用 `isEnabled: false`。
3. **删订阅 = 已有投递立即变 `dead`**，台账仍在，排查时别看错成投递失败。
4. **多副本双开投递器** —— 下游收到重复事件（§4.2）。
5. **内网回调未加白名单**：升级后默认拒绝私网/环回目标，既有内网订阅会被投递前复检拦下并记失败；
   把主机或网段加入 `Webhooks:AllowedHosts` 后重投即可。
6. **对 4xx 返回 4xx**：接收端返回非 2xx 就会退避重投，一个永久性拒绝（比如业务校验失败）
   会打满 8 次；正确处理是收下并返回 2xx，在自己的系统里记录拒绝原因。
7. **轮换密钥后立即调用旧密钥签名** —— 切换窗口的投递全部验签失败，先下游后上游。
8. **把审计当指标用**：审计是按行查询的台账，聚合看 `/metrics`（见 [`08-observability.md`](08-observability.md)）。

---

相关文档：
[`00-quickstart.md`](00-quickstart.md) ·
[`02-versions.md`](02-versions.md) ·
[`03-licensing.md`](03-licensing.md) ·
[`04-backups.md`](04-backups.md) ·
[`05-documents.md`](05-documents.md) ·
[`07-openapi.md`](07-openapi.md) ·
[`08-observability.md`](08-observability.md)
