# 授权（B5）

> 本文面向**客户端接入方**，说明授权产品、授权主体、离线码与签名密钥四件套，
> 以及客户端侧的三条采信铁律（快照验签、离线码验签、应用/产品/机器码绑定）。端点、字段、常量、错误码与默认值均取自源码。
> 概览与最短路径见 [`00-quickstart.md`](00-quickstart.md) 第 6 节。

---

## 1. 概念

授权是「**在某个应用下，某个产品对某台机器签发的、带 ECDSA 签名的到期凭证**」。三要素缺一不可：

| 要素 | 载体 | 说明 |
|---|---|---|
| 授权产品 | `LicenseProduct`（`productKey`） | 同一应用内产品键唯一，规则 `^[a-z][a-z0-9-]{1,31}$`，创建后不可改 |
| 授权主体 | `LicenseEntitlement`（`machineCode`） | 一台机器在一个产品下最多一条授权；机器码是授权主体，跨机器复制无效 |
| 机器码 | 16 位 Crockford Base32（10 字节哈希） | 客户端「软件授权」页展示，见第 2 节 |

**应用隔离轴**：授权数据按应用隔离，产品键只在应用内唯一。所有管理端与客户端授权端点都只从令牌的
`app` 声明取应用键（`RequireAppKey`），令牌没有应用上下文时返回 `403`/`4`。进入授权模块前先按
00-quickstart 4.3 切到目标应用。**签名密钥是全局资源**，不按应用隔离。

**两条通路**：

- **在线快照**：客户端调 `/client/licensing/status`，服务端返回带签名的 `snapshot`，验签后采信；随时可续期/吊销。
- **离线码**：无网现场用，服务端签发一串 `AF4` 文本码（内含应用键、产品键与机器码），客户端本地用内置公钥验签；**码本身没有吊销通道**，只能等到期。
  但授权主体的「完全吊销」会让该机器在线状态恒为 `revoked`，客户端据此锁定并忽略本地离线码；
  「仅在线注销」则不影响离线码（见 §5）。

授权类型只有两种（`LicenseConventions.KindTrial` / `KindOfficial`，即 `trial` / `official`）；
状态有五种：`active` / `expired` / `revoked` / `unlicensed` / `unmanaged`。状态的判定口径是
「**吊销优先，其次看到期 + 宽限**」（`LicenseStatusResolver`）：已吊销恒为 `revoked`；否则
`now <= ExpiresAt` 为 `active`，超过为 `expired`。其中 `ExpiresAt = NotAfter + GraceDays`。
仅在线注销的授权不参与这条判定：状态接口按 `unlicensed` 返回（无快照），客户端回退到本地离线码。

---

## 2. 机器码规范

机器码是主标识哈希的 **10 字节**，用 **Crockford Base32** 编码成 **16 个字符**
（`MachineCodeBytes=10`、`MachineCodeChars=16`）。

- 字母表：`0123456789ABCDEFGHJKMNPQRSTVWXYZ`，刻意剔除 `I` / `L` / `O` / `U`，现场口述不会把 1/0 认错。
- 解码侧容错：忽略 `-`、`_`、空格与大小写，并把 **`I`/`L` 归一为 `1`、`O` 归一为 `0`**。
- 末尾不足 8 位的填充位必须为 0，否则视为截断/篡改，解码失败（不做静默丢弃，避免把错码解成另一个有效码）。

`LicenseConventions.TryNormalizeMachineCode` 会剥离分隔符、转大写、解码为 10 字节后再重新编码得到**规范形态**。
因此：

- **手抄 O/I/L 会被自动纠正**（`008J4CT4ANK7F2O S` 之类的误抄能还原成正确机器码）。
- **但长度必须整串抄对**：只要少一位或多一位，长度就不是 16 个字符，`TryNormalizeMachineCode` 直接返回 false，
  签发/校验会以 `308`（HTTP 400）拒绝。客户端 `LicenseVerifier` 比对机器码时也用同一套归一规则，
  所以大小写、误抄、分隔符都不会造成「同机判不匹配」，唯独漏抄/多抄会。

---

## 3. 服务端启用授权（签名密钥）

签名算法 `ECDSA-P256-SHA256`，签名统一为 **IEEE-P1363 定长 64 字节**（不是 DER），哈希为 SHA-256。
私钥**只从部署配置/环境变量读取**，不落库、不写日志；配置项：

| 键（环境变量） | 配置节点 | 说明 |
|---|---|---|
| `Licensing__SigningKeyPkcs8` | `Licensing:SigningKeyPkcs8` | 签发私钥：ECDSA P-256 的 **PKCS#8 DER 的 Base64** |
| `Licensing__MaxLicenseDays` | `Licensing:MaxLicenseDays` | 单次签发最大授权天数，`appsettings.json` 默认 **3650**（10 年） |

> 私钥在启动时经 `services.Configure<LicensingOptions>(GetSection("Licensing"))` 绑定，**换钥需重启进程才生效**。
> `deploy/deploy.sh` 首装会自动生成一对 P-256 密钥并把私钥与派生的公钥写进 `deploy/.env`（600），
> 现场用 `bash deploy/deploy.sh info` 读 keyId 与公钥指纹交给客户端打包；`up --no-license-key`
> 或 `.env` 里 `LICENSING_GENERATE_KEY=false` 则整个模块保持 `unmanaged`。
> 私钥一旦非空，重跑部署**绝不重新生成**（本地直跑见 00-quickstart 6.1 的 `export ... && dotnet run`）。

生成并注入开发私钥：

```bash
# 生成 P-256 私钥（PKCS#8 DER），Base64 单行
openssl ecparam -name prime256v1 -genkey -noout -out license-key.pem
openssl pkcs8 -topk8 -nocrypt -in license-key.pem -outform DER -out license-key.pkcs8.der
export Licensing__SigningKeyPkcs8="$(base64 -w0 license-key.pkcs8.der)"
dotnet run --project src/Platform.Api
```

**keyId 指纹**：`keyId = "lk-" + 公钥 SPKI SHA-256 指纹的前 16 个十六进制字符`（指纹本身是小写十六进制、64 字符）。
换钥后 keyId 随之变化。管理端查询总览：

```bash
curl -s http://localhost:5080/api/v1/admin/licensing/signing-keys \
  -H "Authorization: Bearer $TOKEN"
```

返回 `isManaged` / `currentKeyId` / `fingerprint` / `keys[]`（每把含 `keyId`、`algorithm`、`publicKey`、
`fingerprint`、`status`、`isCurrent`、`activatedAt`、`retiredAt`）。公钥不是秘密，管理端与客户端都能拿到；
私钥永不出现在任何响应里。

**`isManaged=false` 的三处后果**（未配置私钥，或配置了但格式非法 —— 后者会记一条 Warning 日志并按未托管处理）：

1. 客户端状态接口一律返回 `unmanaged`，`snapshot` 为 `null`、`trustedKeys` 为空数组；客户端据此**拒绝一切授权**。
2. 签发离线码返回 `305`（HTTP 503）。
3. 管理台**仍可**建产品、签发/续期/吊销授权（这些端点不校验私钥），只是快照签不出来，`snapshot` 为空。

---

## 4. 产品端点与字段

| 方法 | 路由（前缀 `/api/v1`） | 权限码 | 备注 |
|---|---|---|---|
| GET | `/admin/licensing/products` | `license.product.read` | 返回带授权计数 |
| POST | `/admin/licensing/products` | `license.product.manage` | 支持 `Idempotency-Key` |
| PATCH | `/admin/licensing/products/{id}` | `license.product.manage` | 字段缺省表示不修改 |
| DELETE | `/admin/licensing/products/{id}` | `license.product.manage` | 仍有授权或离线码台账时返回 `6`（409） |

删除产品只在**没有任何签发留痕**时放行：授权行（`LicenseEntitlement`，含已吊销）与离线码台账
（`OfflineLicenseCode`）都按 `ProductId` 挂在产品行上，任一存在即返回通用冲突 `6`（HTTP 409），
文案「产品 {key} 仍有 N 条授权、M 条离线码台账，请先处理留痕再删除产品。」产品不存在返回 `300`（404）。

创建请求 `CreateLicenseProductRequest` 字段：`key`、`name`（必填非空）、`description`、`seatLimit`、
`defaultGraceDays`（默认 **7**）、`trialDays`（默认 **0**）。响应 `LicenseProductDto` 额外带 `isEnabled`、
`entitlementCount`、`activeEntitlementCount`、时间戳。

字段口径（`LicenseConventions`）：

| 字段 | 规则/默认 |
|---|---|
| `key` | `^[a-z][a-z0-9-]{1,31}$`，最长 32；应用内唯一，重复返回 `301`（HTTP 409） |
| `seatLimit` | `null`/`<=0` 归一为「不限」；上限 **100000**，超限返回校验失败 `2`（HTTP 400）。有效授权数达到上限即席位用尽 |
| `defaultGraceDays` | `0–30`（`MaxGraceDays=30`），默认 7；仅在签发/自动试用**当时**作为缺省值写入授权，之后改产品不影响已签发授权 |
| `trialDays` | `0–365`（`MaxTrialDays=365`）。`>0` 时启用自动试用 |
| `description` | 最长 1024 |

- `trialDays > 0`：**未签发的机器首次调用状态接口会自动获得一条 `trial` 授权**（`IssuedBy=system`，
  `notAfter = now + trialDays`，宽限取产品 `defaultGraceDays`）。同一机器只发一次（并发首次会命中已存在记录回读）。
- **有效试用也占席位**：席位统计与试用签发都只数「未吊销且 `ExpiresAt >= now`」的授权。
- 停用产品（`isEnabled=false`）后：状态接口、签发授权、签发离线码都会拒绝（产品已停用）。

---

## 5. 签发 / 续期 / 吊销

| 方法 | 路由（前缀 `/api/v1/admin/licensing`） | 权限码 | 备注 |
|---|---|---|---|
| GET | `/entitlements` | `license.read` | 查询 `page`/`pageSize`/`productKey`/`status`/`keyword` |
| GET | `/entitlements/{id}` | `license.read` | — |
| POST | `/entitlements` | `license.issue` | 支持 `Idempotency-Key` |
| POST | `/entitlements/{id}/renew` | `license.issue` | 已吊销不可续期 |
| POST | `/entitlements/{id}/revoke` | `license.revoke` | 重复吊销幂等 |

`Idempotency-Key` 的 TTL 为 **24 小时**，作用域分别为 `licensing.product.create` /
`licensing.entitlement.issue` / `licensing.offlinecode.issue`。列表的 `status` 过滤只接受
`active` / `expired` / `revoked`（其余值校验失败）。

**签发** `IssueLicenseRequest`：`productKey`、`machineCode`、`nodeId`、`licenseNo`、`notAfter`、`addDays`、`graceDays`、`notes`。

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/licensing/entitlements \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: license-issue-001' \
  -d '{"productKey":"erp-pro","machineCode":"008J4CT4ANK7F24S","addDays":365,"licenseNo":"LIC-20261030-001"}'
```

- `notAfter` 与 `addDays` 二选一；`addDays` 缺省 **365**（`DefaultLicenseDays`），且必须在 `1–max` 内
  （`max = Math.Clamp(Licensing:MaxLicenseDays, 1, 3650)`）。
- `graceDays` 缺省取产品 `defaultGraceDays`；`0–30`。`ExpiresAt = notAfter + graceDays`，签发后宽限是**本条授权的快照**：
  状态判定与客户端校验只读授权自己的 `graceDays`/`ExpiresAt`，再改产品默认值不会回写已签发授权。
- `licenseNo` 留空自动生成：`LIC-yyyyMMdd-机器码前4位-3位Crockford随机`（最长 32，仅可打印 ASCII）。
- 该机器码**已有未吊销的非试用授权**时返回通用 `Conflict`（HTTP 409，"请使用续期"）；新发时席位用尽返回 `309`（HTTP 409）。
- 该机器码已有的是**同产品自动试用**（`kind=trial`、未吊销）时，签发正式授权**就地升级**同一行：沿用原 `id`
  （客户端不用改绑定）、不再占新席位，`kind` 变为 `official`，`notBefore`/`notAfter`/`graceDays` 按本次请求重写，无需先吊销。
- `nodeId` 与 `machineCode` 至少给一个，缺两者返回 `308`（HTTP 400）；两个都给时必须指向同一台机器，
  否则返回 `2`（HTTP 400）。只给 `nodeId` 时服务端按节点解析机器码（节点不存在返回 `809`/404，
  设备域未启用返回 `10`/503）。
- 列表/详情按机器码关联设备节点，回显 `nodeId` / `nodeDisplayName`（只读回显，授权表不落 `node_id`，
  签发时机器可能还没注册节点）。
- 类型固定为 `official`。

**续期** `RenewLicenseRequest`：`notAfter`、`addDays`、`graceDays`、`notes`。`addDays` 的**追加基准**是
`max(当前 NotAfter, 现在)` —— 未过期从原到期时间往后加，已过期从现在往后加：

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/licensing/entitlements/$ID/renew \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"addDays":180}'
```

- `addDays` 必须在 `1–3650`；`notAfter`（显式）或算出的新到期时间必须晚于当前时间，否则校验失败。
- `graceDays`/`notes` 省略则保留原值。已吊销的授权续期返回 `304`（HTTP 403）。

**吊销** `RevokeLicenseRequest`：`reason`（最长 512）、`scope`（`all` / `online`，缺省 `all`）。
**不可恢复**：想恢复只能重新签发一条新授权。吊销时：落库 `revokedAt` / `revokedReason` / `revokeScope`
→ SignalR 广播 `LicenseChanged`（reason=`revoked`）→ 发布 Webhook 事件 `EventLicenseRevoked`
（负载带 `scope`）。

```bash
# 完全吊销：在线授权与已签发的离线授权码一起失效（默认）
curl -s -X POST http://localhost:5080/api/v1/admin/licensing/entitlements/$ID/revoke \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"reason":"客户退订","scope":"all"}'

# 仅注销在线授权：离线码继续有效（例如只停掉联网机器、保留无网现场）
curl -s -X POST http://localhost:5080/api/v1/admin/licensing/entitlements/$ID/revoke \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"reason":"仅停在线","scope":"online"}'
```

两种范围的判定差异：

| scope | `GET/POST` 管理端状态 | 客户端 `/status` | 离线码 |
|---|---|---|---|
| `all` | `revoked` | `status=revoked` + 非空 `snapshot`，客户端验签后锁定 | 客户端锁定，忽略本地离线码 |
| `online` | `revoked`（`revokeScope=online`） | `status=unlicensed`、`snapshot=null` | 客户端清除在线快照，回退本地离线码，仍可激活 |

- 重复吊销幂等：首次吊销写入时间与范围；第二次只在「`online` → `all`」升级时更新范围（可同时更新
  `reason`），`all → online` **不降级**、不改时间、不重复推送。
- 历史行 `revokeScope` 为 `null` 视为 `all`；响应里的 `revokeScope` 对未吊销授权为 `null`，
  对已吊销授权回填实际范围。`scope` 传其他值返回通用校验失败 `2`。
- **老客户端无需升级即可适配**：老客户端对在线 `revoked` 立即锁定、对 `unlicensed` 清除在线快照后
  回退离线码，正好对应上表的两种行为；服务端只负责返回 `unlicensed`，不下发可验证的吊销快照。
- 离线码本身仍无吊销通道（见 §7）：`online` 注销后旧码在到期前依旧可本地验签通过；要彻底让某台机器
  不可用，用 `all`（或之后升级为 `all`）。

---

## 6. 客户端在线校验

| 方法 | 路由（前缀 `/api/v1/client/licensing`） | 权限 |
|---|---|---|
| POST | `/status` | `license.read` |
| POST | `/offline/verify` | `license.read` |
| GET | `/keys` | **匿名**（公钥发现，可内置安装包） |

```bash
curl -s -X POST http://localhost:5080/api/v1/client/licensing/status \
  -H "Authorization: Bearer $CLIENT_TOKEN" -H 'Content-Type: application/json' \
  -d '{"productKey":"erp-pro","machineCode":"008J4CT4ANK7F24S"}'
```

`LicenseStatusResponse` 关键字段：`appKey`、`productKey`、`machineCode`、`status`、`kind`、`notBefore`、
`notAfter`、`graceDays`、`serverTime`、`licenseNo`、`keyId`、`snapshot`、`trustedKeys[]`、`nodeId`
（按机器码关联节点，未注册节点为 `null`）。`trustedKeys[]` 每把 `LicenseKeyDescriptorDto` 含
`keyId`/`algorithm`/`publicKey`/`fingerprint`/`status`。

`snapshot` 是 Base64 的**签名快照**，载荷字段顺序固定（格式版本 2，魔数 `AFLIC2\0`）：
`ver | kind | status | 保留 | grace(4) | notBefore(8) | notAfter(8) | serverTime(8) | machine(10) |
appKey | productKey | licenseNo | keyId | deviceCode | 签名(64)`，字符串为 1 字节长度 + UTF-8，
时间是大端 Unix 秒；`deviceCode` 目前服务端写空。字段顺序一经发布不得改动，新增只能追加在签名前并升版本。

**必须验签才采信**（`LicenseVerifier.Verify` 的三条硬规则）：

1. 用 `trustedKeys` 里的公钥验 ECDSA P-256（IEEE-P1363 64 字节，SHA-256）。先按 `keyId` 命中对应公钥验，
   再回退遍历全部受信任公钥；**验签不过一律不信**（哪怕响应 `status` 写着 `active`）。
2. 核对快照里的 `appKey` / `productKey` / `machineCode` 与期望一致（机器码走归一比对）。
3. **到期判定用快照里的 `serverTime`，不用本机时钟**：`notBefore > serverTime` 判未生效，
   `notAfter + graceDays < serverTime` 判过期。回拨本机时钟对在线校验无效。

另外：`status=unmanaged` 直接拒绝；快照缺失（未授权/未签发）拒绝；快照 `status=revoked` 拒绝。
**例外是「仅在线注销」**：服务端返回 `unlicensed` + `snapshot=null`，客户端应清除已缓存的在线快照并
回退本地离线码，而不是当成永久吊销锁定。

**SignalR `LicenseChanged` 只是提示，不是事实来源**：吊销/续期/签发时服务端经 `/hubs/devices`
向目标机器上该应用的活跃安装实例定向发送 `LicenseChanged`
（负载 `LicenseChangedNotification{ appKey, machineCode, reason }`，
`reason` 为 `issued`/`renewed`/`revoked`），同时向管理台 `/hubs/admin` 的 licensing 域组广播。
推送失败只记日志，不影响已落库结果。客户端收到提示后**必须回拉
`/status`**：完全吊销拿到 `revoked` 快照并验签后锁定；仅在线注销拿到 `unlicensed`（无快照），
清除在线快照并回退离线码。断线或推送丢失都不会改变最终判定。

> 注：SignalR 事件名一律用源码常量（`DeviceHubEvents.LicenseChanged`，PascalCase），
> 00-quickstart 与客户端 README 已统一按常量书写，Webhook 事件名才是 `snake.dot` 形式（`license.revoked`）——
> 两套命名不要混用。
> 代码中**没有**「吊销后 ≤30 秒强制生效」的定时器：在线生效即时性取决于客户端收到提示后多久回拉，
> 离线/断连客户端则到下次轮询状态接口才反映吊销。

---

## 7. 离线授权码

| 方法 | 路由（前缀 `/api/v1/admin/licensing`） | 权限码 | 备注 |
|---|---|---|---|
| GET | `/offline-codes` | `license.read` | 台账分页，`keyword` 命中机器码/编号/产品键 |
| POST | `/offline-codes` | `license.offlinecode.issue` | 支持 `Idempotency-Key`；未托管返回 `305` |
| DELETE | `/offline-codes/{id}` | `license.offlinecode.issue` | 仅已过有效期（含宽限）可删；已发出的码到期前仍可用 |

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/licensing/offline-codes \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: license-offline-001' \
  -d '{"productKey":"erp-pro","machineCode":"008J4CT4ANK7F24S","addDays":365,"licenseNo":"LIC-OFF-001"}'
```

`IssueOfflineLicenseCodeRequest`：`productKey`、`machineCode`、`nodeId`、`notAfter`、`addDays`、`licenseNo`、`remark`。
`nodeId` 与 `machineCode` 的规则同 §5（至少给一个，二者都给必须指向同一台机器）。
`addDays` 缺省 365、上限同样按 `Licensing:MaxLicenseDays` 夹到 `1..3650`。类型固定 `official`，
**离线码宽限天数恒为 0**（码一经发出无人可控，宽限只属于可在线吊销的授权）。

响应 `OfflineLicenseCodeDto` 有 `code`（原始串，入库）与 `formattedCode`（`Base32Crockford.FormatGroups(code, 8)`，
即**每 8 个字符插一个 `-`**，仅供人读，不参与存储与校验）。`licenseNo` 重复返回 `Conflict`（HTTP 409）。

**AF4 线格式**：`AF4 | payload 字符数(2) | payload | 签名(103) | 校验位(1)`，全串 Crockford Base32。
`payload = ver(1)=4 | kind(1) | grace(1) | flags(1) | issued(4) | notAfter(4) | machine(10) |
appKeyLen(1) | appKey | productKeyLen(1) | productKey | noLen(1) | licenseNo`。时间用 32 位 Unix 秒（1970–2106），
签名是 IEEE-P1363 的 64 字节再 Base32 编码成 103 字符；末尾校验位取 `SHA-256(payload+签名)` 首字节高 5 位，
只用于先挡手抄/截断错误，**真正信任仍由验签决定**。AF4 把**应用键与产品键**都写进码里：
同一机器在不同应用、不同产品的离线码不能混用；服务端**只解析 AF4**，AF3 一律按「版本不支持」拒绝。
码长比 AF3 多出 `appKey` 对应的 Base32 字符，不压缩、不缩短。

服务端校验端点（客户端离线时改用本地验签，见第 10 节）：

```bash
curl -s -X POST http://localhost:5080/api/v1/client/licensing/offline/verify \
  -H "Authorization: Bearer $CLIENT_TOKEN" -H 'Content-Type: application/json' \
  -d '{"code":"AF4...","machineCode":"008J4CT4ANK7F24S","productKey":"erp-pro"}'
```

应用键以令牌 `app` 声明为准；请求里的 `appKey` 可省略，省略即按令牌应用校验，填了就必须与令牌一致。
`OfflineLicenseVerificationDto`：`isValid`、`appKey`、`productKey`、`machineCode`、`kind`、`issuedAt`、
`notAfter`、`graceDays`、`licenseNo`。校验顺序：机器码格式 → 解析验签（前缀/长度/校验位/版本/签名）→
应用绑定（码内 `appKey` 与令牌应用一致；请求 `appKey` 若给出也须一致）→ 机器码匹配 →
产品匹配 → 产品存在且启用 → 是否过期（`now > notAfter + graceDays`）。
**离线码绑定「应用键 + 产品键 + 机器码」，复制到另一个应用、另一台机器或另一个产品都会被拒。**

> 码本身无吊销通道：`DELETE /offline-codes/{id}` 只删台账行，**不影响已发出的码**，它在到期前依旧可被本地验签通过。
> 台账是唯一的签发留痕，因此**未过期（`notAfter + graceDays >= now`）的记录拒绝删除**，返回通用冲突 `6`（HTTP 409）；
> 到期后台账行才可清理。请保留台账用于对账，签发时尽量带上可追踪的 `licenseNo`。要停用某台机器的离线能力，
> 对该授权主体做**完全吊销**（`scope=all`）：客户端在线校验拿到 `revoked` 后锁定，不再使用本地离线码；
> `scope=online` 不会影响离线码。

---

## 8. 密钥轮换 runbook

1. 生成新的 P-256 私钥（第 3 节命令），更新部署环境变量 `Licensing__SigningKeyPkcs8`；
2. **重启服务**（私钥在启动时绑定，运行中改环境不生效）；
3. 调用同步端点，或等任一授权请求触发自动同步：

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/licensing/signing-keys/sync \
  -H "Authorization: Bearer $TOKEN"
```

同步（`EnsureCurrentAsync`）会把当前配置私钥对应的密钥登记为 `active`，并把**其它 active 全部改为 `retired`**
（写入 `retiredAt`）。因此：

- **新钥 active**：用于签新快照与新离线码。
- **旧钥 retired**：不再签发，但仍出现在 `/keys`（`DescribeTrustedAsync` 返回 active + retired，active 优先），
  客户端手里的旧快照/旧离线码仍能验签——这是轮换过渡期的关键。
- **当前私钥对应的密钥不可退役**：`POST /signing-keys/{id}/retire` 会拒绝（返回 `305`，"请先切换
  `Licensing:SigningKeyPkcs8` 再退役旧密钥"），否则会签出无人可验的快照。
- `POST /signing-keys/{id}/activate` 只允许激活与当前配置私钥公钥一致的密钥（否则 `305`）。

---

## 9. 错误码与 HTTP 状态

授权段为 `300–399`，HTTP 由 `ApiErrorHttpStatus.Map` 唯一映射，客户端业务分支只看 `code`：

| 码 | 枚举 | 含义 | HTTP |
|---|---|---|---|
| `300` | LicensingProductNotFound | 产品不存在 | 404 |
| `301` | LicensingProductKeyExists | 产品键重复 | 409 |
| `302` | LicensingEntitlementNotFound | 授权不存在 | 404 |
| `303` | LicensingEntitlementExpired | 已过期（含离线码过期） | 403 |
| `304` | LicensingEntitlementRevoked | 已吊销（续期被拒） | 403 |
| `305` | LicensingSigningKeyUnavailable | 未配置/不匹配私钥，无法签发或改密钥 | 503 |
| `306` | LicensingOfflineCodeInvalid | 离线码非法/验签失败/产品不符 | 422 |
| `307` | LicensingOfflineCodeMachineMismatch | 离线码绑定的是另一台机器 | 422 |
| `308` | LicensingMachineCodeMissing | 机器码不合法（非 16 位 Crockford） | 400 |
| `309` | LicensingSeatExhausted | 席位用尽 | 409 |
| `310` | LicensingPayloadUnsupported | 载荷格式不支持 | 422 |

> 通用码也会出现在授权流程：缺应用上下文 `4`（403）；产品键为空 / 产品停用等入参问题 `2`（400）；
> 「同机已有有效授权」「并发签发」「授权编号重复」`6`（409）；删除不存在的离线码台账行 `5`（404）。

---

## 10. SDK 用法（`AppFramework.Platform.Client`）

三个授权方法：`GetLicenseStatusAsync(productKey, machineCode)`、
`VerifyOfflineLicenseCodeAsync(code, machineCode, productKey)`、
`GetTrustedSigningKeysAsync()`（匿名，无需令牌）。本地采信一律走 `LicenseVerifier`，
**不要**直接读 `status` 字段判权限：

```csharp
// 在线：取状态 → 验签（只用安装包内置公钥）→ 判定
var status = await client.GetLicenseStatusAsync("erp-pro", machineCode);
IReadOnlyCollection<string> pinnedPublicKeys = await LoadBundledPublicKeysAsync();

// 响应里的 status.TrustedKeys 一律不采信：明文 HTTP 下响应可被整体替换
var verdict = LicenseVerifier.Verify(
    status,
    expectedAppKey: "demo",
    expectedProductKey: "erp-pro",
    expectedMachineCode: machineCode,
    pinnedPublicKeys);

if (!verdict.IsValid)
{
    // unmanaged / 未内置公钥 / 验签失败 / 绑定不符 / 未生效 / 已过期(含宽限) / 已吊销
    Log.Warning("授权不放行：{Status} {Reason}", verdict.Status, verdict.Reason);
    return;
}
// verdict.Payload.ServerTime 为服务端时间，到期判定已用它，回拨本机时钟无效
```

离线场景（无网时本地验签，公钥内置于安装包）：

```csharp
IReadOnlyCollection<string> publicKeys = await LoadBundledPublicKeysAsync();

var offline = LicenseVerifier.VerifyOfflineCode(
    code,              // 原始码或带 '-' 分组的 formattedCode 都可（会先归一）
    machineCode,
    "erp-pro",
    "demo",            // 期望的应用键，与码内 appKey 不一致即拒
    publicKeys);

if (!offline.IsValid)
{
    // 验签失败 / 应用不符 / 产品不符 / 机器码不符 / 已过期
    Show(offline.Reason);
    return;
}
```

要点：

- 在线到期判定用**快照里的 `serverTime`**，回拨本机时钟无效；`graceDays` 由快照给出，过期 = `notAfter + graceDays < serverTime`。
- 离线码没有服务端时间，`VerifyOfflineCode` 只能以**本机 `DateTime.UtcNow`** 判到期，这是离线通路的固有弱点，
  不要拿它替代可联网时的在线校验。
- 收到 `LicenseChanged` 提示后必须回拉 `GetLicenseStatusAsync` 重新验签，别把推送当结论。

---

## 11. 常见坑与管理台对应

- **令牌没应用上下文** → 一切授权端点 `403`/`4`。先按 00-quickstart 4.3 切上下文。
- **直接看 `status` 字段放行**：错。只有 `LicenseVerifier` 验签通过的快照才可信；被篡改的响应也能带 `status=active`。
- **用本机时钟判到期**：在线会绕过服务端时间保护，回拨时钟即"续命"。必须用 `payload.ServerTime`。
- **机器码只抄对字符不抄长度**：漏一位整串作废（`308`）；O/I/L 误抄反而能被归一纠正。
- **按机器码签发但机器还没注册节点**：授权照常可用，`nodeId`/`nodeDisplayName` 回显为空；节点注册后自动关联。
- **跨应用复用一个离线码**：AF4 拒绝。码内 `appKey` 必须与当前应用一致，`306` 提示「属于应用 X」。
- **以为删台账能吊销离线码**：删台账不影响已发出的码，只能等到期；要连带停用离线能力，对授权主体做
  `scope=all` 的完全吊销（客户端在线校验锁定）；`scope=online` 只停在线、不动离线码。
- **把「仅在线」当永久吊销**：`online` 之后可以升级为 `all`（补一次吊销即可），但 `all` 不能降回 `online`。
- **忘 `Idempotency-Key`**：产品/授权/离线码的 POST 建议带，24 小时窗口内重放安全。
- **换钥后没重启**：私钥在启动时绑定，改环境变量不重启等于没换；且旧钥未同步前仍是唯一 active。
- **管理台对应**：「授权」页按标签页提供 授权产品（`license.product.read`/`manage`）、
  授权主体（签发/续期 `license.issue`、吊销 `license.revoke`，吊销弹窗可选「完全吊销」或「仅在线」）、
  离线码台账（`license.offlinecode.issue`，复制/删除）、
  签名密钥（当前指纹、轮换同步、退役密钥，`license.signingkey.manage`，平台管理员独占）。

---

返回概览与登录/上下文：[00-quickstart.md](00-quickstart.md)。相关：版本渠道 [02-versions.md](02-versions.md)、备份下发 [04-backups.md](04-backups.md)。
