# 可观测：健康检查、指标、日志与告警

> 适用版本：B7（`Platform.Api` 0.1.0-preview.1）。
> 相关文档：Webhook 投递与审计查询见 [`06-webhooks-and-audit.md`](06-webhooks-and-audit.md)，
> 部署与拓扑见 [`00-quickstart.md`](00-quickstart.md)。

---

## 1. 三个诊断端点

| 端点 | 鉴权 | 查库 | 用途 |
|---|---|---|---|
| `GET /healthz` | 匿名 | **否** | 存活探针（liveness）。进程起来就返回 `ok` |
| `GET /ready` | 匿名 | 是（`postgres` 检查项） | 就绪探针（readiness），决定能不能接流量 |
| `GET /metrics` | 可选令牌 | 是（Webhook 积压） | Prometheus 抓取 |

```bash
curl -s http://localhost:5080/healthz   # {"isSuccess":true,"code":0,"data":{"status":"ok"},"traceId":"..."}
curl -s http://localhost:5080/ready     # 库不可达时 Unhealthy
curl -s http://localhost:5080/metrics | head
```

`/healthz` **刻意不查库**（`PlatformEndpoints.cs:22`）：数据库抖动时如果存活探针也失败，
编排器会把好端端的容器反复重启，雪上加霜。就绪探针才是接流量的依据
（`InfrastructureServiceCollectionExtensions.cs:48` 注册了唯一一条 `ready` 标签检查：
`AddDbContextCheck<PlatformDbContext>("postgres", tags: ["ready"])`）。

`/ready` 返回纯文本健康状态（不是 `ApiResult` 信封），因为编排器只认 `Healthy`/`Unhealthy`。

---

## 2. `/metrics` 端点行为

- 内容类型固定 `text/plain; version=0.0.4; charset=utf-8`（Prometheus exposition 0.0.4）。
- **不引入采集库**：`PlatformMetrics` 是自己渲染文本的极简实现（`Platform.Kernel/Metrics`），
  每个副本各自暴露，聚合与告警交给采集端。
- 开关与令牌（`appsettings.json` 的 `Metrics` 节）：

  | 配置 | 缺省 | 行为 |
  |---|---|---|
  | `Metrics:Enabled` | `true` | `false` 时端点**完全不注册**（404），不是 403 |
  | `Metrics:ScrapeToken` | 空 | 空 = 不鉴权；非空时校验 `X-Metrics-Token`，其次 `Authorization: Bearer` |

  令牌比对用 `CryptographicOperations.FixedTimeEquals`（先比长度），不匹配返回
  **纯文本 401** `metrics scrape token missing or invalid` —— 不能返回 JSON 信封，
  采集端会把非文本响应判为抓取失败。
- `/metrics` 自身**不计入 HTTP 直方图**（`PlatformMetricsMiddleware.cs:22`）：
  否则每次抓取都新增一条时间序列，还会把真实流量稀释掉。
- `/metrics` 被 `ExcludeFromDescription()` 排除，因此不出现在 OpenAPI 文档与 Scalar 页面。
- 采集是**拉取时执行**的：`IPlatformMetricSource.CollectAsync` 只在被抓取时调用，
  没有常驻采集循环；抓取频率就是数据新鲜度（建议 15s）。单个数据源抛异常不会让整次抓取 500，
  改为累加 `platform_metric_source_errors_total{source}` 并照常渲染其余指标。

> **未配置 `Metrics:ScrapeToken` 时端点是匿名的**，进程指标与积压量会外泄。
> 生产二选一：设令牌，或在反向代理上限制来源（见 §6）。

---

## 3. 指标清单

标签值一律低基数（**禁止** id / traceId / 原始路径）。新增维度只能改
`Platform.Kernel/Metrics/PlatformMetricNames.cs`，不要在调用点就地拼新名字
—— 渲染时 help 与标签顺序以**首次注册**为准，同名不同标签集会写出错误的时间序列。

### 3.1 进程（`ProcessMetricSource`，每次抓取刷新）

| 指标 | 类型 | 标签 | 说明 |
|---|---|---|---|
| `platform_process_start_time_seconds` | gauge | 无 | 进程启动 Unix 秒。采集端用它识别副本重启 |
| `platform_process_uptime_seconds` | gauge | 无 | 已运行秒数 |
| `platform_process_memory_bytes` | gauge | 无 | 托管堆**已分配**字节数（`GC.GetTotalMemory`），不是 RSS |
| `platform_process_gc_collections` | gauge | `generation`（`0`/`1`/`2`/`3`=LOH） | 各代回收次数 |
| `platform_process_thread_pool_size` | gauge | `kind`（`worker_busy`/`io_busy`/`handles`） | 当前忙碌工作线程、IO 完成端口线程、句柄数 |

### 3.2 HTTP（中间件）

| 指标 | 类型 | 标签 | 桶 |
|---|---|---|---|
| `platform_http_request_duration_seconds` | histogram | `method`、`route`、`status_class` | `0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, +Inf` |

- `route` 取**端点模板**（`/api/v1/admin/apps/{id:guid}`），组内空模板注册产生的结尾斜杠会被裁掉；
  未匹配到端点（扫路径、探测）统一记为 `unmatched`。
- `status_class` ∈ `2xx`/`3xx`/`4xx`/`5xx`/`other`，不是精确状态码。
- 前提：管道里显式调了 `UseRouting()` 且在 `UsePlatformMetrics()` 之前
  （`Program.cs:193`），否则拿不到端点，**所有请求都记成 `unmatched`**，路由级告警全废。
- 分桶刻意与常见告警阈值对齐（250ms / 1s / 5s）。

### 3.3 横切失败（counter，**首次自增后才出现**）

| 指标 | 类型 | 标签 | 触发点 |
|---|---|---|---|
| `platform_audit_write_failures_total` | counter | `action` | `EfAuditWriter`：审计写库失败（只记日志，不阻断业务） |
| `platform_metric_source_errors_total` | counter | `source` | 抓取时某个 `IPlatformMetricSource` 抛异常 |

> 空指标即零：这三类 counter（含下面的投递计数）在第一次自增前**不会渲染 HELP/TYPE 行**。
> 写 PromQL 时不要用 `absent()` 之外的方式假设它们一定存在，仪表盘变量也要允许缺失。

### 3.4 Webhook（`WebhookMetricSource` + `WebhookDispatcher`）

| 指标 | 类型 | 标签 | 说明 |
|---|---|---|---|
| `platform_webhook_deliveries` | gauge | `app`、`status` | 投递行数量，`status` ∈ `pending`/`failed`/`succeeded`/`dead` |
| `platform_webhook_oldest_pending_age_seconds` | gauge | `app` | 最久一条 `pending`/`failed` 的等待秒数 |
| `platform_webhook_deliveries_total` | counter | `app`、`event`、`result` | `result` ∈ `success`/`retry`/`dead` |

积压用 **gauge 而非 counter**：告警要的是「现在还有多少没投出去」，累计值只能靠 `rate()` 推断，
而且重启清零。每次抓取按当前分组结果覆盖写入，投递成功后状态迁移，残留旧值会被清零，
不会让告警误判为持续积压。**只有 `dead` 与积压时长适合直接告警**，
`pending`/`failed` 在退避窗口内短暂存在是正常形态（退避见 [`06` §3](06-webhooks-and-audit.md)）。

---

## 4. 告警规则

### 4.1 语义前提

- **进程指标是 per-replica**：`up`、`uptime`、内存、线程池都要按 `instance` 看；
  聚合规则用 `min`/`max` 而不是 `sum`（`sum` 会把「一个副本重启」平均掉）。
- **Webhook 指标来自数据库**，任一副本给出同一答案，因此可以多副本部署时随便抓一个实例；
  但前提是**只有一个副本开启投递器**（`Webhooks:DispatcherEnabled`，无行级抢占）。
- counter 全部**进程内**，重启即清零：用 `rate()`/`increase()`，不要用原始值做阈值。

### 4.2 建议规则

```yaml
groups:
- name: platform-server
  rules:
  # P0 —— 服务不可用
  - alert: PlatformInstanceDown
    expr: up{job="platform-api"} == 0
    for: 1m
    labels: {severity: critical}
    annotations:
      summary: "平台 API 副本 {{ $labels.instance }} 抓取失败"
      description: "采集端连续 1 分钟抓不到 /metrics。先看 /ready，再查进程是否被 OOM 杀掉。"

  - alert: PlatformRestartLoop
    expr: changes(platform_process_start_time_seconds[15m]) > 2
    labels: {severity: critical}
    annotations:
      summary: "{{ $labels.instance }} 15 分钟内重启超过 2 次"
      description: "多为迁移失败、连接串错误或启动期异常；日志找 FTL「平台启动失败」。"

  - alert: PlatformNotReady
    expr: increase(platform_http_request_duration_seconds_count{route="/ready",status_class="5xx"}[5m]) > 0
    labels: {severity: warning}
    annotations:
      summary: "{{ $labels.instance }} 就绪探针出现失败"
      description: "`/ready` 只查 postgres 连通性，通常是库侧问题；确认 `pg_isready` 与连接池。"

  # P1 —— 延迟与错误率
  - alert: PlatformLatencyHigh
    expr: |
      histogram_quantile(
        0.95,
        sum by (le, route) (rate(platform_http_request_duration_seconds_bucket[5m]))
      ) > 1
      and sum by (route) (rate(platform_http_request_duration_seconds_count[5m])) > 1
    for: 10m
    labels: {severity: warning}
    annotations:
      summary: "路由 {{ $labels.route }} P95 > 1s"
      description: "桶边界只有 0.05/0.1/0.25/0.5/1/2.5/5/10，阈值请落在桶上，否则插值会骗人。"

  - alert: PlatformServerErrorRate
    expr: |
      sum by (route) (rate(platform_http_request_duration_seconds_count{status_class="5xx"}[5m]))
      /
      sum by (route) (rate(platform_http_request_duration_seconds_count[5m])) > 0.05
    for: 10m
    labels: {severity: critical}
    annotations:
      summary: "路由 {{ $labels.route }} 5xx 占比 > 5%"
      description: "5xx 只有异常过滤器写出的 `Code=1`；按响应体 `traceId` 去日志捞 WRN/ERR。"

  - alert: PlatformPathScanning
    expr: sum(rate(platform_http_request_duration_seconds_count{route="unmatched"}[5m])) > 2
    for: 15m
    labels: {severity: warning}
    annotations:
      summary: "未匹配路由的请求 > 2/s"
      description: "扫路径或客户端拼错前缀。若来自公网，检查 nginx 是否只放行 `/api/v1`、`/hubs`。"

  # P1 —— 旁路能力失效（不影响下单链路，但会静默丢数据）
  - alert: PlatformAuditWriteFailing
    expr: increase(platform_audit_write_failures_total[10m]) > 0
    labels: {severity: warning}
    annotations:
      summary: "审计写入失败（action={{ $labels.action }}）"
      description: "业务不受影响，但合规记录在丢。按 `action` 定位是哪个端点的审计炸了（多为字段超长或表未迁移）。"

  - alert: PlatformMetricSourceFailing
    expr: increase(platform_metric_source_errors_total[10m]) > 3
    labels: {severity: warning}
    annotations:
      summary: "指标数据源 {{ $labels.source }} 采集失败"
      description: "`source=webhooks` 一般是 `webhook_deliveries` 表缺失或库抖动，其余指标仍会正常渲染。"

  # P0/P1 —— Webhook
  - alert: PlatformWebhookDeadLetter
    expr: increase(platform_webhook_deliveries_total{result="dead"}[30m]) > 0
    labels: {severity: critical}
    annotations:
      summary: "应用 {{ $labels.app }} 有 {{ $labels.event }} 投递进入死信"
      description: "8 次尝试全失败（约 1 小时内）。接收端要修：`GET /admin/webhooks/deliveries` 查台账，`POST /admin/webhooks/deliveries/{id}/redeliver` 重放（管理台暂无 Webhook 页面）。"

  - alert: PlatformWebhookBacklogStale
    expr: platform_webhook_oldest_pending_age_seconds > 1800
    for: 15m
    labels: {severity: critical}
    annotations:
      summary: "应用 {{ $labels.app }} 最久未投递已 > 30 分钟"
      description: "退避上限是 1 小时，30 分钟还没消说明投递器没跑：确认 `Webhooks:DispatcherEnabled` 与副本数。"
```

阈值是「最小可运行栈」的经验值，上线后按两周的实际分布调。

---

## 5. 日志

### 5.1 Sink 与降噪

Serilog（`Program.cs:46`）：控制台 + 按天滚动文件 `logs/platform-.log`，保留 14 个文件。
模板：

```
[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}
```

已做的降噪（都是踩过坑的结果）：

| 项 | 级别 | 原因 |
|---|---|---|
| 根 | `Information` | 业务事件 |
| `Microsoft.AspNetCore` | `Warning` | 正常运行时框架日志是噪音 |
| `Microsoft.EntityFrameworkCore.Database.Command` | `Warning` | 每条 SQL 会淹没业务告警 |
| `UseSerilogRequestLogging` 对 `/healthz`、`/ready` | `Verbose` | 采集/编排器每几秒探一次，不然日志全是探针 |
| 请求日志异常 | `Error` | 与异常过滤器一致 |

启动期用静态 `Log.Logger`（宿主构建前的致命错误），宿主起来后由 `UseSerilog` 接管。
**不要改成 `CreateBootstrapLogger`**：ReloadableLogger 每进程只能 Freeze 一次，
集成测试第二个宿主会抛 "logger is already frozen"。

### 5.2 traceId 链路（报障就靠它）

1. 每个响应都是统一信封，失败与成功都带 `traceId`（= `HttpContext.TraceIdentifier`）。
2. 异常过滤器把 `Code=1` 的未处理异常连同 `traceId` 写成 WRN/ERR 日志。
3. 审计行 `trace_id` 列同样存它（见 [`06` §5](06-webhooks-and-audit.md)），
   管理台审计详情页可直接复制。
4. `429`（限流拒绝）响应也带 `traceId`，值形如 `0HNPOG5NL5P14:00000001`。

反向代理后的 `traceId` 前缀仍由本服务生成，与 `X-Request-Id` 无关；
如果网关自己有一套请求号，把它写进日志字段再对齐，不要在业务代码里手写。

排障顺序：`traceId` → 日志（同一天文件）→ 审计查询 → `/metrics` 看是否面上问题。

---

## 6. 采集配置样例

### 6.1 Prometheus

```yaml
scrape_configs:
- job_name: platform-api
  scrape_interval: 15s
  metrics_path: /metrics
  scheme: http
  authorization:                 # 与 Metrics:ScrapeToken 一致
    type: Bearer
    credentials: <SCRAPE_TOKEN>
  static_configs:
  - targets: ["api:8080"]
```

抓取间隔 15s 与 Webhook 派发周期（`Webhooks:DispatchIntervalSeconds`，默认 15s）同量级，
积压告警不会出现锯齿。

### 6.2 不用令牌时：nginx 限制来源

```nginx
location = /metrics {
    allow 10.0.0.0/8;        # 采集端网段
    deny  all;
    proxy_pass http://platform_api;
}
```

二选一即可，两者都上更稳（令牌 + 网段）。

### 6.3 容器里的注意项

- `deploy.sh` 的 `health` 动作已按上面的规则读取 `/metrics`，配了令牌会自动带 `X-Metrics-Token`。
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` 时必须让 nginx 传 `X-Forwarded-For`，
  否则限流分区与审计 IP 全是 `127.0.0.1`（表现是「所有用户一起被限流」）。
- 多副本时**不要**把 `/metrics` 放到负载均衡后面聚合：采集端要按实例抓，
  否则进程指标会被随机副本污染。

---

## 7. Worker 与后台任务的可观测

| 任务 | 周期 | 配置 | 观测方式 |
|---|---|---|---|
| `MaintenanceWorker` | 600s | `Workers:Maintenance:*` | 幂等记录保留 `IdempotencyRetentionHours=24`；审计保留 `AuditRetentionDays=180`，每轮最多删 `max(100, BatchSize=5000)` 行。表行数下降是**预期**，不用告警 |
| `RetentionWorker` | 600s | `Workers:Retention` | 备份/文档软删宽限 30 分钟后「先删文件、再删行」。Blob 体积异常增长说明它没跑（看 `logs/` 里的异常） |
| Webhook 派发器 | 15s | `Webhooks:DispatchIntervalSeconds` | `platform_webhook_*` 三个指标，见 §3.4 |

Maintenance/Retention 目前**没有专属指标**，只有日志；判断它们是否在跑，
看 `logs/platform-<日期>.log` 里的周期性记录，或用审计行数是否停止增长反推。

---

## 8. 当前明确没有的东西

知道边界比假装齐全有用：

- 没有 APM/链路追踪导出：只有 `traceId`，没有 OpenTelemetry span 上报。
- 没有数据库侧指标：连接池、慢查询、锁等待要去 PostgreSQL 自己的 exporter。
- 没有业务计数器：登录次数、发布次数、授权签发次数都不暴露；这类计数请查审计接口。
- 没有推送网关（Pushgateway）：所有指标都是拉取式。
- `/metrics` 不进 OpenAPI 文档，也没有管理台页面。
- 单测里断言的是渲染文本，不保证指标值单调（counter 重启清零是设计如此）。

---

## 9. 常见坑

1. **`route="unmatched"` 一片** —— 管道少了 `UseRouting()`，或把它挪到了指标中间件之后。
2. **抓取报 401 但令牌没错** —— 令牌比对是定长的且区分大小写；检查是否被 shell 吃掉尾部空格，
   或 `Metrics:ScrapeToken` 在 `.env` 里带了引号。
3. **`/metrics` 返回 404** —— `Metrics:Enabled=false`（端点根本没注册），不是权限问题。
4. **P95 永远是 `+Inf`** —— 请求耗时超过 10s（大文件下载走同一中间件），
   下载类端点不要用直方图判断健康度，看 `/ready` 和业务侧成功率。
5. **Webhook 积压告警常亮** —— 先看是不是 8 次退避窗口内（30s→32m）的正常重试；
   再确认只有 1 个副本 `DispatcherEnabled=true`。
6. **进程内存指标暴涨但容器没事** —— `platform_process_memory_bytes` 是托管堆已分配量，
   不等于 RSS；容器 OOM 要看 cgroup 指标。
7. **日志文件写满磁盘** —— 只保留 14 个滚动文件；高流量场景请采集端及时收走并调低保留数。
