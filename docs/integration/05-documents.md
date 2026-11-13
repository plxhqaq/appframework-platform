# 文档集合、修订与可见性（B6）

文档按**应用 + 集合**隔离，内容按修订保存：每次上传生成一条 `document_revisions`，
旧修订保留可追溯。可见性由**服务端**在清单与下载两处强制，客户端不能靠拼 URL 绕过。

| 能力 | 管理端 | 客户端 |
|---|---|---|
| 集合 | `/api/v1/admin/documents/collections` | — |
| 文档 | `/api/v1/admin/documents` | `/api/v1/client/documents` |

## 1. 集合

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/documents/collections \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: doc-coll-manual-001' \
  -d '{"key":"manual","name":"用户手册","description":"随版本下发的手册"}'
```

- 键规则 `^[a-z][a-z0-9-]{1,31}$`，创建后不可修改；应用内唯一，重复返回 `501`（HTTP 409）。
- `PATCH /collections/{id}` 可改名称、说明与启停。
- 停用集合后，其文档**不会**出现在客户端清单，客户端下载同样被拒绝。

## 2. 上传文档

上传是 `multipart/form-data`，字段固定：

| 字段 | 必填 | 说明 |
|---|---|---|
| `file` | 是 | 文件字节；单文件上限 `Documents:MaxFileSizeBytes`（默认 200 MB） |
| `collectionKey` | 是 | 目标集合键 |
| `title` | 是 | 文档标题，≤256 字符 |
| `visibility` | 否 | `client` / `internal`，缺省 **`internal`** |

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/documents \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Idempotency-Key: doc-manual-v1' \
  -F 'collectionKey=manual' \
  -F 'title=安装手册' \
  -F 'visibility=internal' \
  -F 'file=@install.pdf;type=application/pdf'
```

服务端边写边算 SHA-256，写入顺序同样是 **pending → 库提交 → rename**：

1. 先写 `{docId}/r1.bin.pending`；
2. 长度/哈希校验通过后写 `documents` + `document_revisions` 并提交；
3. 提交成功再 rename 为 `r1.bin`。

发布失败会回滚数据库记录并丢弃临时文件，不会出现「有行无文件」。上传/修订端点的请求体上限与
`FormOptions.MultipartBodyLengthLimit` 在启动时统一按 `Documents:MaxFileSizeBytes + 8 MB` 配置
（给 multipart 头留余量），200 MB 文档不会被默认 128 MB 的表单上限提前拒绝；实际单文件大小仍由
服务层按 `Documents:MaxFileSizeBytes` 校验。

## 3. 修订

```bash
curl -s -X POST http://localhost:5080/api/v1/admin/documents/$ID/revisions \
  -H "Authorization: Bearer $TOKEN" \
  -F 'file=@install-v2.pdf;type=application/pdf'
```

- 修订号从 1 单调递增，不接受客户端指定。
- 旧修订文件保留，可按下标下载：`GET /api/v1/admin/documents/{id}/revisions/{revision}/download`。
- 文档摘要把最新修订号放在 `revision`，详情返回完整 `revisions` 数组。

## 4. 可见性与服务端强制

| 可见性 | 管理端 | 客户端清单 | 客户端下载 |
|---|---|---|---|
| `internal`（默认） | 可见/可下载 | 不出现 | `504`（HTTP 403） |
| `client` | 可见/可下载 | 出现 | 允许 |

```bash
# 放开给客户端
curl -s -X PATCH http://localhost:5080/api/v1/admin/documents/$ID \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"visibility":"client"}'
```

清单和下载是两个独立检查点：即使知道文档 id，`internal` 文档也会在下载入口被拒；
集合停用则在两处都拒绝。客户端不应自行拼接下载地址，清单里的 `downloadUrl` 才是唯一入口。

## 5. 客户端清单与下载

```bash
curl -s http://localhost:5080/api/v1/client/documents/manifest \
  -H "Authorization: Bearer $CLIENT_TOKEN"

curl -s -OJ "http://localhost:5080/api/v1/client/documents/$ID/download" \
  -H "Authorization: Bearer $CLIENT_TOKEN"
```

清单条目包含 `documentId`、`collectionKey`、`title`、`revision`、`fileName`、`contentType`、
`sizeBytes`、`sha256`、`updatedAt` 与服务端生成的 `downloadUrl`。客户端应用文档前应校验 SHA-256。

下载支持 `ETag`（内容哈希）、`Range`/`206` 与 `If-None-Match`/`304`，文件名按 RFC 5987 下发。

## 6. 更新、删除与清理

- `PATCH /documents/{id}` 可改标题与可见性。
- `DELETE /documents/{id}` 是软删除：立刻从管理端列表、客户端清单与下载中消失。
- 宽限期后由 `RetentionWorker` 清理：先删修订文件与文档文件，再删 `document_revisions` 与 `documents` 行；
  文件删不掉时保留行，下一轮重试。
- 宽限期取 `Documents:CleanupGraceMinutes`（最小 1 分钟），调度间隔取
  `Workers:Retention:IntervalSeconds`（默认 600 秒，最小 5 秒）。

## 7. 错误码

| 码 | 含义 |
|---|---|
| `500` | 集合不存在 |
| `501` | 集合键重复 |
| `502` | 文档不存在或已删除 |
| `503` | 修订不存在 |
| `504` | 可见性拒绝（内部文档被客户端访问 / 集合停用） |
| `505` | 文件缺失（行存在但 Blob 不在） |

## 8. 配置项

| 键 | 默认 | 说明 |
|---|---|---|
| `Documents:MaxFileSizeBytes` | 209715200 | 单文件上限（200 MB） |
| `Documents:CleanupGraceMinutes` | 30 | 软删宽限期（最小 1 分钟） |
| `Workers:Retention:IntervalSeconds` | 600 | 清理调度间隔（最小 5 秒） |
