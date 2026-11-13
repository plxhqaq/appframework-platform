# 演练手册：备份恢复 / 密钥轮换 / 版本回滚 / 容量与故障

> B7 出口要求：这三件事**必须能在 30 分钟内照着本文做完**，并且每步都有可验证的信号。
> B10 追加 §四「容量与故障演练」：大文件、条目上限、并发检查、摄取中断、磁盘不足、多副本。
> 演练环境用本地 compose 栈即可（`bash deploy/deploy.sh up`），不要第一次就在生产做。
> 相关：部署 [`00-quickstart.md`](00-quickstart.md)、告警 [`08-observability.md`](08-observability.md)、
> Webhook/审计 [`06-webhooks-and-audit.md`](06-webhooks-and-audit.md)。

## 通用约定

```bash
cd appframework-platform
API=http://localhost:8080                       # compose 默认；本地直跑是 http://localhost:5080
export TOKEN=$(curl -s -X POST $API/api/v1/admin/auth/login \
  -H 'Content-Type: application/json' \
  -d "{\"userName\":\"admin\",\"password\":\"$SEED_ADMIN_PASSWORD\"}" | jq -r '.data.accessToken')
```

> 每次演练前先记三件事：**开始时间**、**要动的对象**（应用键/渠道/产品键）、**回退路径**。
> 演练结束把结论写进变更记录；`deploy/.env` 里的口令与密钥属于机密，不要贴进工单。

---

## 一、备份恢复演练

平台有两层「备份」，含义完全不同，**都要练**：

| 层 | 备份的是什么 | 谁负责 | 恢复手段 |
|---|---|---|---|
| A 平台自身 | PostgreSQL 数据 + Blob 卷（版本产物、备份、文档、离线快照的字节） | 运维 | `pg_dump` / `tar` → 新实例 → 迁移对齐 → 探针 |
| B 业务配置 | 客户端应用自己的配置（`/api/v1/admin/backups` 存的命名空间快照） | 平台功能 | 列出历史版本 → 下载 → 下发（push）到目标渠道 |

### A. 平台数据面：全量备份 → 破坏 → 恢复

**A1 备份**

```bash
# 1) 数据库逻辑备份（停写入最稳，但平台没有只读开关，实务上选低峰期）
docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec -T postgres \
  pg_dump -U platform -d appframework_platform -Fc -f /tmp/platform.dump
docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec -T postgres \
  cat /tmp/platform.dump > backup-$(date +%Y%m%d-%H%M).dump

# 2) Blob 卷（内容寻址，丢了就等于所有已发布产物、备份与文档全丢）
docker run --rm -v appframework-platform_blobs:/src -v "$PWD:/out" alpine \
  tar czf /out/blobs-$(date +%Y%m%d-%H%M).tgz -C /src .

# 3) 校验：备份没校验等于没有备份
sha256sum backup-*.dump blobs-*.tgz > checksums-$(date +%Y%m%d).txt
```

> **`deploy/.env` 也要单独备份**（`POSTGRES_PASSWORD`、`JWT_SIGNING_KEY`、`LICENSING_SIGNING_KEY_PKCS8`、
> `SEED_ADMIN_PASSWORD`）。丢了签名私钥 = 已发出的授权快照与离线码永久无法续签；
> 丢了 `JWT_SIGNING_KEY` = 全部在线会话失效（可接受，但要预案）。密钥走 KMS/密码库，不进 git。

**A2 破坏（演练专用）**

```bash
docker compose -f deploy/docker-compose.yml --env-file deploy/.env down -v   # -v 会删掉 pgdata 与 blobs
```

**A3 恢复**

```bash
# 1) 先起一个空库（compose 会创建 pgdata）
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d postgres

# 2) 还原 Blob 卷
docker run --rm -v appframework-platform_blobs:/dst -v "$PWD:/src" alpine \
  sh -c "tar xzf /src/blobs-<时间戳>.tgz -C /dst && chown -R 1001:1001 /dst"

# 3) 还原数据库
docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec -T postgres \
  sh -c "cat > /tmp/platform.dump" < backup-<时间戳>.dump
docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec -T postgres \
  pg_restore -U platform -d appframework_platform --clean --if-exists /tmp/platform.dump

# 4) 让库结构对齐当前镜像（迁移历史表也一起回来了，--migrate 通常是幂等无操作）
bash deploy/deploy.sh migrate
bash deploy/deploy.sh up

# 5) 起 API 前确认 .env 里的 JWT_SIGNING_KEY / LICENSING_SIGNING_KEY_PKCS8 与备份当时一致
```

> `chown -R 1001:1001` 是必须的：容器以 `platform`(uid 1001) 运行，
> 用 root 解包会让 API 写不进临时文件，症状是上传/备份接口 500。

**A4 验证（逐条打勾）**

```bash
curl -s $API/healthz | jq .
curl -s $API/ready   | tail -1          # Healthy

# 数据回来了吗
curl -s $API/api/v1/platform/modules | jq '.data | length'
curl -s -H "Authorization: Bearer $TOKEN" "$API/api/v1/admin/apps" | jq '.data.total'

# 最关键：内容寻址还能命中吗——取一个已发布版本的产物哈希，下载并比对
# 注意：列表端点只有计数（artifactCount/fileCount），文件清单在详情端点里
RELEASE_ID=$(curl -s -H "Authorization: Bearer $TOKEN" "$API/api/v1/admin/versions/releases?status=published&pageSize=1" \
             | jq -r '.data.items[0].id')
SHA=$(curl -s -H "Authorization: Bearer $TOKEN" "$API/api/v1/admin/versions/releases/$RELEASE_ID" \
      | jq -r '.data.artifacts[0].files[0].sha256')
curl -s -H "Authorization: Bearer $TOKEN" -o /tmp/f "$API/api/v1/client/versions/download/$SHA"
sha256sum /tmp/f     # 必须等于 $SHA
```

失败信号对照：`/ready` 不健康 → 库没起或连接串不对；下载 404/`205` → Blob 卷没挂回来或属主不对；
登录 401/`101` → 恢复的是旧 dump，口令已改过（属预期，用当时的口令）。

**A5 恢复用时上限**：目标 RPO/RTO 由业务定；本文只保证「按上述步骤可恢复」。
第一次演练请把耗时记下来，超过 30 分钟说明缺自动化，把步骤收敛进 `deploy/restore.sh`。

### B. 业务配置面：历史快照找回

```bash
# 1) 找到命名空间与它的历史版本
curl -s -H "Authorization: Bearer $TOKEN" "$API/api/v1/admin/backups/namespaces" | jq '.data'
curl -s -H "Authorization: Bearer $TOKEN" \
  "$API/api/v1/admin/backups?namespaceKey=global&page=1&pageSize=20" | jq '.data.items[] | {id,name,version,createdAt}'

# 2) 下载某个历史版本（内联返回 payloadJson，或走 /download 流式）
curl -s -H "Authorization: Bearer $TOKEN" "$API/api/v1/admin/backups/$ID" | jq '.data.payloadJson'
curl -s -H "Authorization: Bearer $TOKEN" "$API/api/v1/admin/backups/$ID/download" -o restore.json

# 3) 下发到目标渠道：同渠道是硬门禁，force 只放宽主版本
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  "$API/api/v1/admin/backups/$ID/push" \
  -d '{"targetChannel":"default","targetFrameworkVersion":"3.2.0","force":false}' | jq .

# 4) 确认客户端 Ack（下发返回里带 ack 状态；超时见 Backups:PushAckTimeoutSeconds=60）
```

要点：
- 下发前服务端会按 `currentHash` 落一条 **pre-push 快照**，所以下发本身也可回退 —— 回退动作就是再 push 上一条历史版本。
- `maxVersions`（命名空间，缺省 50）之外的历史版本会被 `RetentionWorker` 清掉（软删 → 宽限 30 分钟 → 先删文件再删行）。
  **演练时故意选一条接近上限的**，确认它还在；如果没了，说明保留数不够，业务要调 `maxVersions`。
- 完整语义与错误码（`400–408`）见 [`04-backups.md`](04-backups.md)。

---

## 二、密钥轮换演练

平台有两把互相独立的密钥，**别混为一谈**：

| 密钥 | 配置 | 算法 | 泄露影响 | 轮换代价 |
|---|---|---|---|---|
| JWT 签名密钥 | `Jwt:SigningKey`（≥32 字符） | HMAC-SHA256 对称 | 任何人可自签管理员令牌 | 在线访问令牌**全部立即失效** |
| 授权签名私钥 | `Licensing:SigningKeyPkcs8`（PKCS#8 DER Base64，P-256） | ECDSA-P256-SHA256 | 任何人可签发授权快照/离线码 | 新快照用新钥签，旧快照仍可验 |

### 2.1 JWT 签名密钥

```bash
NEW=$(openssl rand -hex 32)
# compose：改 deploy/.env 的 JWT_SIGNING_KEY=$NEW 后
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d api

# 本地直跑：重启前 export
export Jwt__SigningKey="$NEW" && dotnet run --project src/Platform.Api
```

验证顺序（这一步的**预期是失败**）：

```bash
# 1) 用轮换前拿到的旧令牌 —— 应当 401 / code 3
curl -si -H "Authorization: Bearer $OLD_TOKEN" $API/api/v1/platform/whoami | head -1

# 2) 重新登录 —— 应当 200
curl -s -X POST $API/api/v1/admin/auth/login -H 'Content-Type: application/json' \
  -d "{\"userName\":\"admin\",\"password\":\"$SEED_ADMIN_PASSWORD\"}" | jq '.data.user.userName'

# 3) SDK 客户端：旧访问令牌 + 仍有效的刷新令牌 —— 自动续期，业务无感
```

关键事实：
- 私钥只在启动时读取（`AuthenticationSetup.cs:86` 把 `SymmetricSecurityKey` 写进静态校验参数），
  **必须重启**，改配置不重启无效。
- 轮换只废**访问令牌**；刷新令牌是随机串 + 库里存哈希，与签名密钥无关，所以
  管理台 Cookie 会话与桌面 SDK 会静默续期（一次 401 后自愈）。
- 老令牌不会「过期前还能用」——签名不匹配就是 `401`/`3`。
- 长度不足 32 字符会在启动期抛（`JwtOptions` 校验），症状是容器反复重启；
  用 §4.2 的 `PlatformRestartLoop` 告警兜住。

### 2.2 授权签名密钥（ECDSA）

```bash
# 1) 生成新 P-256 私钥（PKCS#8 DER）
openssl ecparam -name prime256v1 -genkey -noout -out new-lic.key.pem
openssl pkcs8 -topk8 -nocrypt -in new-lic.key.pem -outform DER -out new-lic.key.der
export LICENSING_SIGNING_KEY_PKCS8=$(base64 -w0 new-lic.key.der)

# 2) 写进 deploy/.env 并重启（私钥只在启动时读取）
bash deploy/deploy.sh up

# 3) 同步密钥登记：新钥 active，旧钥自动 retired
curl -s -X POST -H "Authorization: Bearer $TOKEN" $API/api/v1/admin/licensing/signing-keys/sync | jq .
curl -s -H "Authorization: Bearer $TOKEN" $API/api/v1/admin/licensing/signing-keys \
  | jq '.data.items[] | {keyId,status}'
```

验证：

```bash
# 换钥前签发的快照仍要能验（旧钥在 trustedKeys 里没被摘掉）
curl -s -X POST -H "Authorization: Bearer $CLIENT_TOKEN" -H 'Content-Type: application/json' \
  $API/api/v1/client/licensing/status -d '{"productKey":"erp-pro","machineCode":"<16位机器码>"}' \
  | jq '.data | {status, keyId: (.trustedKeys | length)}'

# 新签发的离线码用新钥签名，服务端校验端点通过
```

要点：
- `keyId` = `lk-` + 公钥 SPKI SHA-256 指纹前 16 个十六进制字符；换钥后指纹变化，
  客户端**必须**通过 `trustedKeys`/`/client/licensing/keys` 发现新公钥，硬编码单钥的客户端会全部拒签。
- 旧密钥转 `retired`：不再签新快照，但继续出现在 `trustedKeys`，让现场存量快照可验 —— 这是设计，不是遗漏。
- **当前私钥对应的密钥不能退役**（会签出无人可验的快照），服务端直接拒绝。
- 离线码没有吊销通道：换钥不会让旧离线码失效。真要阻断只能等它到期，
  所以离线码尽量签短（`addDays`）。
- 忘记旧私钥又必须续验存量快照？没有补救 —— 这正是 §A1 里要求备份 `.env` 的原因。
- 完整语义见 [`03-licensing.md`](03-licensing.md)。

---

## 三、版本回滚演练

「回滚」在平台里有三个不同动作，练清楚是哪一个：

| 目标 | 手段 | 客户端影响 |
|---|---|---|
| 让后续检查更新回到上一版 | 归档（archive）当前已发布版本 | `check` 自动落到上一版已发布版本 |
| 只对新装机生效，保留现网 | 停用渠道 → 重新发布正确版本 | 停用渠道**不**影响已发布版本的读取/下载 |
| 平台自身服务回滚 | `docker compose` 指回旧镜像 tag + `deploy.sh migrate` | 全部客户端 |

### 3.1 归档当前版本（主路径）

```bash
# 1) 确认渠道上「已发布」的到底是哪一版
curl -s -H "Authorization: Bearer $TOKEN" \
  "$API/api/v1/admin/versions/releases?channelKey=stable&status=published&pageSize=5" \
  | jq '.data.items[] | {id,version,publishedAt}'

# 2) 记下要回退到的目标版本 id，归档出问题的那一版
curl -s -X POST -H "Authorization: Bearer $TOKEN" \
  "$API/api/v1/admin/versions/releases/$BAD_RELEASE_ID/archive" | jq '.data | {version,status,archivedAt}'

# 3) 客户端侧验证：check 现在给出上一版
curl -s -X POST -H "Authorization: Bearer $CLIENT_TOKEN" -H 'Content-Type: application/json' \
  "$API/api/v1/client/versions/check" \
  -d '{"channelKey":"stable","currentVersion":"9.9.9","runtimeIdentifier":"win-x64"}' \
  | jq '.data | {hasUpdate, isDowngrade, version: .release.version}'

# 4) 强制回退演练：临时打开渠道降级开关，再以「现场已装高版本」的视角检查
curl -s -X PATCH -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  "$API/api/v1/admin/versions/channels/$CHANNEL_ID" \
  -d '{"allowDowngrade":true}' | jq '.data | {key,allowDowngrade}'
curl -s -X POST -H "Authorization: Bearer $CLIENT_TOKEN" -H 'Content-Type: application/json' \
  "$API/api/v1/client/versions/check" \
  -d '{"channelKey":"stable","currentVersion":"9.9.10","runtimeIdentifier":"win-x64"}' \
  | jq '.data | {hasUpdate, isDowngrade, version: .release.version}'
# 演练完成后关闭开关
curl -s -X PATCH -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  "$API/api/v1/admin/versions/channels/$CHANNEL_ID" \
  -d '{"allowDowngrade":false}' | jq '.data | {key,allowDowngrade}'
```

必须知道的三个语义（都和直觉不符）：

1. **归档 = 该版本文件立即不可下载**。下载定位要求 `release.status == published`，
   归档/草稿版本的哈希会返回 `404`/`205`。已装好旧版本的机器不受影响，
   但**已经半更新到坏版本的机器无法再拉它的文件**——所以坏版本宁可先用「发布正确版本覆盖」再归档。
   （对照：停用渠道只禁止发布新版本，已发布版本的 check/manifest/download 全部照常。）
2. `check` 取的是渠道内**最新已发布**版本（按 `publishedAt` 倒序）。
   所以「回滚」要么归档坏的，要么把好的重新发布一次 —— 版本号本身不可变，同一渠道同版本号重复创建报 `204`。
3. `check` 的 `release` 可能为 `null` 而渠道其实有已发布版本：
   **最新版本没有请求的那个 `runtimeIdentifier` 产物**时按「无更新」返回，不是错误。
   真正的「渠道没有已发布版本」是 `404`/`212`。
4. **默认不降级**。归档后已装高版本的机器拿到的是 `hasUpdate=false` + `isDowngrade=true`，
   只做提示不动文件。真要强制回退，先把渠道的 `allowDowngrade` 打开（管理台渠道表单「允许降级」
   或 `PATCH /api/v1/admin/versions/channels/{id}`），客户端再检查一次就会拿到上一版清单；
   演练结束务必关掉，避免后续归档操作意外降级现场。客户端也可单次显式传 `allowDowngrade=true`。

### 3.2 演练必做的回归项

```bash
# a) 上一版的清单可读取，哈希自校验通过
curl -s -H "Authorization: Bearer $CLIENT_TOKEN" \
  "$API/api/v1/client/versions/stable/1.2.2/manifest?runtimeIdentifier=win-x64" | jq '.data.files | length'

# b) 逐文件下载并比对（这一步就是「能不能真的装回去」）
for SHA in $(curl -s -H "Authorization: Bearer $CLIENT_TOKEN" \
  "$API/api/v1/client/versions/stable/1.2.2/manifest?runtimeIdentifier=win-x64" | jq -r '.data.files[].sha256'); do
  curl -s -H "Authorization: Bearer $CLIENT_TOKEN" -o /tmp/$SHA "$API/api/v1/client/versions/download/$SHA"
  sha256sum /tmp/$SHA | grep -q "^$SHA" || echo "× $SHA 不匹配"
done

# c) 审计里留下这次回滚的痕迹
curl -s -H "Authorization: Bearer $TOKEN" \
  "$API/api/v1/admin/audit-logs?action=versions.release.archive&pageSize=5" | jq '.data.items[] | {action,result,createdAt,traceId}'
```

没有 `× 不匹配` 输出才算通过。出现 `206`（清单与 blob 不符）说明 Blob 被外部动过，
按 §一 A 的恢复流程处理，而不是继续回滚。

### 3.3 平台自身服务回滚

```bash
# 记录当前镜像，回滚就是指回旧 tag
docker compose -f deploy/docker-compose.yml --env-file deploy/.env build api
bash deploy/deploy.sh status
# 回滚：改 compose 里的 image（或重新 build 旧 commit），先迁移再起重
git checkout <上一个可用提交> && bash deploy/deploy.sh up   # deploy.sh up 内部会先 migrate
```

EF 迁移**只向前**：本仓没有 down-migration 流程，含破坏性 DDL 的版本回滚前必须先做 §一 A 的全量备份。

### 3.4 通知订阅方

回滚后如果配了 Webhook，注意 `version.released` 只在**发布**时产生，归档不发事件。
需要让订阅方重新拉取，用 `webhook.test` 事件触发一次投递（见 [`06` §2](06-webhooks-and-audit.md)），
或直接看投递台账确认对端存活。

---

## 四、容量与故障演练

发布链路的风险集中在「大、多、并发、半途死」。下面每项都给出触发方式、**预期信号**与恢复动作；
先在演练环境做，生产只做只读观察与阈值核对。

### 4.1 1 GiB 级上传 / 下载

```bash
head -c 1073741824 /dev/urandom > big.zip                  # 1 GiB
SHA=$(sha256sum big.zip | cut -d' ' -f1)

# 1) 申请票据（30 分钟有效，声明哈希与长度）
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  "$API/api/v1/admin/versions/uploads" \
  -d "{\"sha256\":\"$SHA\",\"sizeBytes\":1073741824}" | jq -r '.data.uploadUrl' > /tmp/upload-url

# 2) 凭票据直传（一次性，PUT 完即消费）
curl -s -X PUT -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/octet-stream' \
  --data-binary @big.zip "$(cat /tmp/upload-url)" | jq

# 3) 登记为 full 包并下载回验
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  "$API/api/v1/admin/versions/packages" \
  -d "{\"sha256\":\"$SHA\",\"sizeBytes\":1073741824,\"fileName\":\"Setup-stable-9.9.9.zip\",\"kind\":\"full\",\"target\":\"win-x64\",\"format\":\"zip\",\"channelKey\":\"stable\",\"version\":\"9.9.9\"}" | jq '.data | {id,status}'
curl -s -H "Authorization: Bearer $TOKEN" -o big-back.zip "$API/api/v1/client/versions/packages/<id>/download"
sha256sum big-back.zip                                      # 必须等于 $SHA
```

预期与注意：
- 默认上限是**单包 4 GiB / 单文件 4 GiB**（`Versions:MaxPackageSizeBytes`、`MaxFileSizeBytes`），
  超过分别返回 `221`（413）。反向代理的 `client_max_body_size` 与 `proxy_read_timeout` 必须同步放大。
- 下载走 `Results.Stream` + Range/ETag，**不要用 HTTP P95 判断健康度**（长连接会把分位数拉满）；
  看 `versions_package_download_bytes_total{kind="package"}` 增量是否约等于文件大小。
- 记录首字节时间与吞吐；Blob 卷与库在同一磁盘时应低于本机写带宽的 1/2。

### 4.2 条目/文件数上限（100 000 条目、50 000 文件）

```bash
# 100 001 个空条目：zip 安全校验直接拒绝
python - <<'PY'
import zipfile
with zipfile.ZipFile('entries-over.zip', 'w') as z:
    for i in range(100001):
        z.writestr(f'f{i}.txt', '')
PY
# 登记时返回 220 / 422（VersionsPackageUnsafeArchive），提示「zip 条目数 … 超过上限」
```

清单文件数上限是 **50 000**（`VersionConventions.MaxFilesPerArtifact`）：清单 `files` 超过时增量包被拒，
全量包以文件数做展示但不逐文件摄取。两个上限都**先在登记阶段拒绝**，
不会进入摄取队列，所以演练后 `release_packages` 里应只有一条 `failed`（或直接 422 无行）。

### 4.3 检查更新风暴（100 并发）

```bash
seq 1 100 | xargs -P 100 -I{} curl -s -o /dev/null -w '%{http_code}\n' \
  -X POST -H "Authorization: Bearer $CLIENT_TOKEN" -H 'Content-Type: application/json' \
  "$API/api/v1/client/versions/check" \
  -d '{"channelKey":"stable","currentVersion":"1.0.0","runtimeIdentifier":"win-x64"}'
```

预期：全部 `200`（check 不在限流策略内），`versions_check_total{result="update"}` 增加 100；
库连接池不应打满（`pg_stat_activity` 峰值远低于 `MaxPoolSize`）。出现零星 `5xx` 先看
`platform_http_request_duration_seconds{route=...,status_class="5xx"}` 与日志 `traceId`。

### 4.4 摄取中 `kill -9`

```bash
# 1) 提交一个较大的增量包并确认进入 processing
curl -s -H "Authorization: Bearer $TOKEN" \
  "$API/api/v1/admin/versions/packages?status=processing" | jq '.data.items[] | {id,attempts}'

# 2) 直接杀进程（systemd 会拉起；compose 用 docker kill）
docker compose -f deploy/docker-compose.yml --env-file deploy/.env kill api
```

预期：
- 行留在 `processing`，`locked_until` 到期（`Versions:IngestionLockMinutes`，默认 10 分钟）后
  `RecoverStaleAsync` 把 `attempts < MaxAttempts` 的行改回 `pending`，否则置 `failed`。
- 同一包最终 `attempts` 递增，文件 Blob 按哈希去重，不会因重试产生重复内容。
- 想立刻恢复可把 `IngestionLockMinutes` 临时调小后重启，或直接改库把 `locked_until` 置空（演练专用）。

### 4.5 磁盘不足（不真填满生产盘）

优先用配置**模拟**，比真的写满安全：

```bash
# 让「最小剩余空间」阈值高于实际可用空间，上传票据立即被拒
export Versions__MinFreeDiskBytes=10995116277760        # 10 TiB
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d api
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  "$API/api/v1/admin/versions/uploads" -d '{"sha256":"'$SHA'","sizeBytes":1024}' | jq '.code'
# 预期 227 / 507（VersionsStorageInsufficient）
```

真实写满（限演练机）：摄取写文件时 IOException/ENOSPC 会转成 `227`，可重试错误按
`IngestionMaxAttempts` 退避；清出空间后包自动回到 `pending` 继续。收尾务必恢复默认 5 GiB。

### 4.6 多副本并发摄取

```bash
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --scale api=2
# 连续提交 20 个增量包（见 4.2 的脚本，逐个改文件名/版本），观察：
curl -s -H "Authorization: Bearer $TOKEN" \
  "$API/api/v1/admin/versions/packages?pageSize=50" | jq '[.data.items[] | {id,status,attempts}]'
```

预期：每个包 `attempts == 1`、终态为 `ready`/`failed` 之一；不出现同一包被两个副本同时处理
（PostgreSQL `FOR UPDATE SKIP LOCKED`）。`versions_package_ingest_total{result="ready"}` 增量等于成功包数。
该语义在 `tests/Platform.PostgresTests` 有 16 副本并发用例，InMemory 集成测试覆盖不到，别只跑本地测试就下结论。

### 4.7 归档回滚

按 §三 3.1–3.3 执行；额外核对归档后**下载立即失效**（404/`205`）、
`versions_check_total{result="none"}` 上升且 `isDowngrade=true` 的响应只做提示。

### 4.8 演练判定表

| 演练 | 通过信号 | 失败信号（先查什么） |
|---|---|---|
| 4.1 大文件 | 回验哈希一致；下载字节指标 ≈ 文件大小 | 413 → 代理/上限；超时 → `proxy_read_timeout` |
| 4.2 条目/文件数 | 422 / `220`，无摄取排队 | 进入 `pending` → 校验被绕过 |
| 4.3 100 并发 check | 全 200，P95 在桶边界内 | 5xx → 连接池/库；429 → 误配限流策略 |
| 4.4 kill -9 | 租约到期后回到 `pending` 并完成，`attempts+1` | 永久 `processing` → Worker 没跑/租约配置过大 |
| 4.5 磁盘不足 | `227`/507，清空间后自动续跑 | 500 → 磁盘异常没被识别成可重试 |
| 4.6 多副本 | 每包 `attempts=1`，无重复产物 | `attempts>1` → 认领没走 SKIP LOCKED |
| 4.7 归档回滚 | 下载 404/`205`，check 落到上一版 | 仍能下载 → 状态判断失效 |

---

## 五、演练记录模板

| 项 | 值 |
|---|---|
| 演练日期 / 执行人 | |
| 环境（compose / 生产） | |
| 板斧（备份恢复 / 密钥轮换 / 版本回滚 / 容量与故障） | |
| 开始—结束耗时 | |
| 各验证步骤结果（逐条勾选） | |
| 非预期现象与处置 | |
| 需要补的告警规则 / 脚本 | |
| 结论（通过 / 需整改） | |

> 频次建议：备份恢复每季度一次且**必须真的恢复一次**（只备份不恢复等于没有备份）；
> 密钥轮换按泄露风险随时可做，生产至少每年一次；版本回滚每次大版本发布前在预发演练。
