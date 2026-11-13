/**
 * 与后端 Platform.Contracts 对齐的 JSON 形状。
 *
 * 刻意手写而不是生成：S0 阶段契约还在变，生成管线留到契约稳定后再上，
 * 但字段名必须与契约里的 record 属性名完全一致（服务端 camelCase）。
 */

/** 统一返回：所有端点（含错误）都是这个形状。 */
export interface ApiResult<T> {
  isSuccess: boolean;
  code: number;
  message?: string | null;
  data?: T | null;
  traceId?: string | null;
}

/** 模块自述（GET /api/v1/platform/modules）。 */
export interface ModuleInfo {
  key: string;
  displayNameKey: string;
  version: string;
  routes: string[];
  requiredPermissions: string[];
  /** 该模块当前落地的阶段（S0 = 只有骨架，业务端点待接入）。 */
  stage: string;
}

/** 当前主体自述（GET /api/v1/platform/whoami）。 */
export interface WhoAmI {
  isAuthenticated: boolean;
  userId?: string | null;
  userName?: string | null;
  isService: boolean;
  isTemporary: boolean;
  appKey?: string | null;
  roles: string[];
  permissions: string[];
  appKeys: string[];
}

/** 分页结果（与服务端 PageResult<T> 对齐）。 */
export interface PageResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
  nextCursor?: string | null;
}

/** 登录后的主体信息（令牌声明与响应共用同一份口径）。 */
export interface UserSession {
  userId: string;
  userName: string;
  displayName?: string | null;
  roles: string[];
  permissions: string[];
  appKeys: string[];
  /** 当前令牌固定的应用上下文；平台上下文为 null。 */
  appKey?: string | null;
  mustChangePassword: boolean;
}

/** 管理端登录/刷新响应：刷新令牌只在 httpOnly Cookie，响应体不含它。 */
export interface AdminSession {
  accessToken: string;
  expiresAt: string;
  user: UserSession;
}

/** 自助修改口令结果。 */
export interface PasswordChanged {
  changedAt: string;
  revokedSessions: number;
}

/** 角色引用。 */
export interface RoleRef {
  id: string;
  name: string;
  displayName?: string | null;
}

/** 用户概要。 */
export interface UserSummary {
  id: string;
  userName: string;
  displayName?: string | null;
  isEnabled: boolean;
  mustChangePassword: boolean;
  roles: RoleRef[];
  lastLoginAt?: string | null;
  createdAt: string;
}

/** 用户详情。 */
export interface UserDetail extends UserSummary {
  roleIds: string[];
  loginCount: number;
  updatedAt: string;
}

/** 创建用户。 */
export interface CreateUserRequest {
  userName: string;
  password: string;
  displayName?: string | null;
  isEnabled?: boolean;
  roleIds?: string[] | null;
  mustChangePassword?: boolean;
}

/** 更新用户；字段缺省表示不修改。 */
export interface UpdateUserRequest {
  displayName?: string | null;
  isEnabled?: boolean | null;
  roleIds?: string[] | null;
}

/** 管理员重置口令。 */
export interface ResetUserPasswordRequest {
  newPassword: string;
  requireChange?: boolean;
}

/** 角色概要。 */
export interface RoleSummary {
  id: string;
  name: string;
  displayName?: string | null;
  scope: string;
  appKey?: string | null;
  permissions: string[];
  isSystem: boolean;
  userCount: number;
  createdAt: string;
}

/** 创建角色。 */
export interface CreateRoleRequest {
  name: string;
  displayName?: string | null;
  scope?: string;
  appKey?: string | null;
  permissions?: string[] | null;
}

/** 更新角色；字段缺省表示不修改。 */
export interface UpdateRoleRequest {
  displayName?: string | null;
  scope?: string | null;
  appKey?: string | null;
  permissions?: string[] | null;
}

/** 会话（一条 refresh token 记录）。 */
export interface SessionSummary {
  id: string;
  userId: string;
  userName: string;
  familyId: string;
  deviceFingerprint?: string | null;
  createdByIp?: string | null;
  createdAt: string;
  expiresAt: string;
  lastUsedAt?: string | null;
  isActive: boolean;
}

/** 权限目录条目（后台权限树的数据源）。 */
export interface PermissionDescriptor {
  code: string;
  moduleKey: string;
  displayNameKey: string;
  sort: number;
  platformAdminOnly: boolean;
}

/** 签发单次密钥。 */
export interface IssueSingleLoginKeyRequest {
  durationMinutes?: number | null;
  deviceFingerprint?: string | null;
}

/** 单次密钥；明文只在此响应出现一次。 */
export interface SingleLoginKey {
  key: string;
  expiresAt: string;
  durationMinutes: number;
}

/** 创建扫码会话。 */
export interface CreateQrSessionRequest {
  deviceFingerprint?: string | null;
  clientName?: string | null;
}

/** 扫码会话创建结果。 */
export interface QrSessionCreated {
  requestId: string;
  expiresAt: string;
  payload: string;
}

/** 临时会话（单次密钥/扫码登录）：没有刷新令牌，过期即失效。 */
export interface TemporarySession {
  accessToken: string;
  expiresAt: string;
  user: UserSession;
}

/** 扫码会话状态。 */
export interface QrSessionStatus {
  requestId: string;
  status: string;
  createdAt: string;
  expiresAt: string;
  approvedAt?: string | null;
  session?: TemporarySession | null;
}

/** 管理端扫码会话概要。 */
export interface QrSessionSummary {
  requestId: string;
  status: string;
  clientName?: string | null;
  deviceFingerprint?: string | null;
  createdByIp?: string | null;
  createdAt: string;
  expiresAt: string;
  approvedAt?: string | null;
  approvedByName?: string | null;
}

/** 批准扫码会话。 */
export interface ApproveQrSessionRequest {
  durationMinutes?: number | null;
}

/** 应用概要（GET /api/v1/admin/apps）。 */
export interface AppSummary {
  id: string;
  key: string;
  name: string;
  description?: string | null;
  isEnabled: boolean;
  createdAt: string;
  updatedAt: string;
}

/** 应用详情。 */
export interface AppDetail extends AppSummary {
  settingsJson?: string | null;
}

/** 创建应用请求。 */
export interface CreateAppRequest {
  key: string;
  name: string;
  description?: string | null;
}

/** 更新应用请求；字段缺省表示不修改。 */
export interface UpdateAppRequest {
  name?: string | null;
  description?: string | null;
  isEnabled?: boolean | null;
}

/** API Key 概要：永不包含明文或哈希。 */
export interface ApiKeySummary {
  id: string;
  prefix: string;
  name: string;
  scopes: string[];
  expiresAt?: string | null;
  revokedAt?: string | null;
  lastUsedAt?: string | null;
  createdAt: string;
  isActive: boolean;
}

/** 签发 API Key 请求。 */
export interface CreateApiKeyRequest {
  name?: string | null;
  scopes?: string[] | null;
  expiresAt?: string | null;
}

/** 新签发的 API Key；明文只出现这一次。 */
export interface CreatedApiKey {
  id: string;
  prefix: string;
  plaintext: string;
  name: string;
  scopes: string[];
  expiresAt?: string | null;
  createdAt: string;
}

/** 版本渠道（同一应用下的 stable / beta / canary 等）。 */
export interface ReleaseChannel {
  id: string;
  key: string;
  name: string;
  description?: string | null;
  isEnabled: boolean;
  releaseCount: number;
  latestVersion?: string | null;
  createdAt: string;
  updatedAt: string;
  allowDowngrade: boolean;
  /** 受众标签；客户端省略渠道键时，需包含渠道的全部标签才会命中。 */
  audienceTags?: string[] | null;
  /** 是否由客户端带标签自动创建。 */
  autoCreated?: boolean;
}

/** 创建渠道。 */
export interface CreateReleaseChannelRequest {
  key: string;
  name: string;
  description?: string | null;
  allowDowngrade?: boolean;
  audienceTags?: string[] | null;
}

/** 更新渠道；字段缺省表示不修改（audienceTags 传空数组表示清空）。 */
export interface UpdateReleaseChannelRequest {
  name?: string | null;
  description?: string | null;
  isEnabled?: boolean | null;
  allowDowngrade?: boolean;
  audienceTags?: string[] | null;
}

/** 版本状态：draft / published / archived。 */
export type ReleaseStatus = 'draft' | 'published' | 'archived';

/** 清单条目：相对路径 + 内容哈希 + 字节数。 */
export interface ReleaseFile {
  path: string;
  sha256: string;
  sizeBytes: number;
}

/** 版本摘要（列表用）。 */
export interface ReleaseSummary {
  id: string;
  channelKey: string;
  version: string;
  status: ReleaseStatus;
  notes?: string | null;
  artifactCount: number;
  fileCount: number;
  totalSizeBytes: number;
  packageCount: number;
  packageSizeBytes: number;
  publishedAt?: string | null;
  createdAt: string;
  updatedAt: string;
}

/** 单个运行时产物（含文件清单）。 */
export interface ReleaseArtifact {
  runtimeIdentifier: string;
  manifestHash: string;
  fileCount: number;
  totalSizeBytes: number;
  files: ReleaseFile[];
}

/** 版本详情。 */
export interface ReleaseDetail extends ReleaseSummary {
  archivedAt?: string | null;
  createdBy?: string | null;
  artifacts: ReleaseArtifact[];
  packages?: ReleasePackage[] | null;
}

/** 发布时提交的单个文件条目。 */
export interface ReleaseFileInput {
  path: string;
  sha256: string;
  sizeBytes: number;
}

/** 发布时提交的单个运行时产物。 */
export interface CreateReleaseArtifactRequest {
  runtimeIdentifier: string;
  files: ReleaseFileInput[];
}

/** 创建版本；publish 为 true 时创建即发布。 */
export interface CreateReleaseRequest {
  channelKey: string;
  version: string;
  notes?: string | null;
  publish: boolean;
  artifacts: CreateReleaseArtifactRequest[];
}

/** 申请上传票据（先声明内容哈希与长度，再直传字节）。 */
export interface CreateUploadTicketRequest {
  sha256: string;
  sizeBytes: number;
}

/** 上传票据；uploadUrl 为相对本服务的 PUT 地址。 */
export interface UploadTicket {
  id: string;
  uploadUrl: string;
  expiresAt: string;
}

/** 上传结果；reused 表示内容已存在（内容寻址去重）。 */
export interface UploadResult {
  sha256: string;
  sizeBytes: number;
  reused: boolean;
}

/** 包类型：full（全量安装包）/ incremental（文件级增量包）。 */
export type ReleasePackageKind = 'full' | 'incremental';

/** 摄取状态：pending / processing / ready / failed。 */
export type ReleasePackageStatus = 'pending' | 'processing' | 'ready' | 'failed';

/** 清单来源：embedded（重写进 PE）/ zip（包内 JSON）/ sidecar / manual（人工全量）/ legacy。 */
export type PackageManifestSource = 'embedded' | 'zip' | 'sidecar' | 'manual' | 'legacy';

/** 发布包（管理台登记与 check 下发用）。 */
export interface ReleasePackage {
  id: string;
  releaseId: string;
  channelKey: string;
  version: string;
  kind: ReleasePackageKind;
  target: string;
  format: string;
  fileName: string;
  sha256: string;
  sizeBytes: number;
  status: ReleasePackageStatus;
  manifestSource: PackageManifestSource;
  manifestHash?: string | null;
  /** 摄取完成后的清单文件数；全量包或尚未完成时为 null。 */
  fileCount?: number | null;
  reused: boolean;
  errorCode?: string | null;
  errorMessage?: string | null;
  completedAt?: string | null;
  createdAt: string;
  updatedAt: string;
}

/** 登记发布包；字节须先经上传票据直传。 */
export interface RegisterReleasePackageRequest {
  sha256: string;
  sizeBytes: number;
  fileName: string;
  kind?: ReleasePackageKind | null;
  target?: string | null;
  format?: string | null;
  channelKey?: string | null;
  version?: string | null;
  manifestSha256?: string | null;
  publish: boolean;
  notes?: string | null;
}

/** 更新器版本状态：published（客户端可见）/ archived（回退到上一版）。 */
export type UpdaterStatus = 'published' | 'archived';

/** 更新器分发包；独立版本线，客户端先更更新器再更应用。 */
export interface UpdaterInfo {
  id: string;
  target: string;
  version: string;
  fileName: string;
  sha256: string;
  sizeBytes: number;
  manifestHash?: string | null;
  status: UpdaterStatus;
  notes?: string | null;
  publishedAt?: string | null;
  createdAt: string;
  updatedAt: string;
}

/** 登记更新器；版本留空时服务端依次取清单、PE FileVersion。 */
export interface RegisterUpdaterRequest {
  target?: string | null;
  version?: string | null;
  fileName: string;
  sha256: string;
  sizeBytes: number;
  manifestSha256?: string | null;
  notes?: string | null;
}

/** 授权产品（同一应用下按产品分别签发授权）。 */
export interface LicenseProduct {
  id: string;
  key: string;
  name: string;
  description?: string | null;
  isEnabled: boolean;
  seatLimit?: number | null;
  defaultGraceDays: number;
  trialDays: number;
  entitlementCount: number;
  activeEntitlementCount: number;
  createdAt: string;
  updatedAt: string;
}

/** 创建授权产品。 */
export interface CreateLicenseProductRequest {
  key: string;
  name: string;
  description?: string | null;
  seatLimit?: number | null;
  defaultGraceDays: number;
  trialDays: number;
}

/** 更新授权产品；字段缺省表示不修改。 */
export interface UpdateLicenseProductRequest {
  name?: string | null;
  description?: string | null;
  isEnabled?: boolean | null;
  seatLimit?: number | null;
  defaultGraceDays?: number | null;
  trialDays?: number | null;
}

/** 授权状态：active / expired / revoked / unlicensed / unmanaged。 */
export type LicenseStatus = 'active' | 'expired' | 'revoked' | 'unlicensed' | 'unmanaged';

/** 授权类型：trial / official。 */
export type LicenseKind = 'trial' | 'official';

/** 授权主体（配额绑定机器码，跨机器复制无效）。 */
export interface LicenseEntitlement {
  id: string;
  productKey: string;
  machineCode: string;
  kind: LicenseKind;
  status: Exclude<LicenseStatus, 'unlicensed' | 'unmanaged'>;
  notBefore: string;
  notAfter: string;
  expiresAt: string;
  graceDays: number;
  licenseNo?: string | null;
  notes?: string | null;
  issuedBy?: string | null;
  revokedAt?: string | null;
  revokedReason?: string | null;
  lastSeenAt?: string | null;
  createdAt: string;
  updatedAt: string;
}

/** 签发正式授权。 */
export interface IssueLicenseRequest {
  productKey: string;
  machineCode: string;
  licenseNo?: string | null;
  notAfter?: string | null;
  addDays?: number | null;
  graceDays?: number | null;
  notes?: string | null;
}

/** 续期授权；notAfter 与 addDays 二选一。 */
export interface RenewLicenseRequest {
  notAfter?: string | null;
  addDays?: number | null;
  graceDays?: number | null;
  notes?: string | null;
}

/** 吊销授权。 */
export interface RevokeLicenseRequest {
  reason?: string | null;
}

/** 签发离线授权码。 */
export interface IssueOfflineLicenseCodeRequest {
  productKey: string;
  machineCode: string;
  notAfter?: string | null;
  addDays?: number | null;
  licenseNo?: string | null;
  remark?: string | null;
}

/** 离线授权码台账。 */
export interface OfflineLicenseCode {
  id: string;
  productKey: string;
  machineCode: string;
  licenseNo: string;
  code: string;
  formattedCode: string;
  kind: LicenseKind;
  issuedAt: string;
  notAfter: string;
  graceDays: number;
  remark?: string | null;
  issuedBy?: string | null;
  createdAt: string;
}

/** 签名密钥（管理端视图）。 */
export interface LicenseSigningKey {
  id: string;
  keyId: string;
  algorithm: string;
  publicKey: string;
  fingerprint: string;
  status: 'active' | 'retired';
  isCurrent: boolean;
  activatedAt?: string | null;
  retiredAt?: string | null;
  createdAt: string;
}

/** 签名密钥总览；unmanaged 表示部署未配置私钥，授权校验不生效。 */
export interface LicenseSigningKeyStatus {
  isManaged: boolean;
  currentKeyId?: string | null;
  fingerprint?: string | null;
  keys: LicenseSigningKey[];
}

/** 备份命名空间：同一应用下的配置域，承载渠道与保留配额。 */
export interface BackupNamespace {
  id: string;
  key: string;
  name: string;
  description?: string | null;
  channel: string;
  maxVersions: number;
  isEnabled: boolean;
  backupCount: number;
  createdAt: string;
  updatedAt: string;
}

/** 创建命名空间。 */
export interface CreateBackupNamespaceRequest {
  key: string;
  name: string;
  description?: string | null;
  channel?: string | null;
  maxVersions?: number | null;
}

/** 更新命名空间；字段缺省表示不修改。 */
export interface UpdateBackupNamespaceRequest {
  name?: string | null;
  description?: string | null;
  channel?: string | null;
  maxVersions?: number | null;
  isEnabled?: boolean | null;
}

/** 备份类型：config / profile。 */
export type BackupKind = 'config' | 'profile';

/** 备份摘要。 */
export interface BackupSummary {
  id: string;
  namespaceKey: string;
  kind: BackupKind;
  name: string;
  deviceId?: string | null;
  deviceName?: string | null;
  moduleId?: string | null;
  schemaVersion: number;
  frameworkVersion: string;
  channel: string;
  sizeBytes: number;
  sha256?: string | null;
  hasInline: boolean;
  hasFile: boolean;
  fileName?: string | null;
  downloadUrl?: string | null;
  createdAt: string;
  createdBy?: string | null;
}

/** 创建备份。 */
export interface CreateBackupRequest {
  namespaceKey: string;
  kind: BackupKind;
  name: string;
  deviceId?: string | null;
  deviceName?: string | null;
  moduleId?: string | null;
  schemaVersion: number;
  frameworkVersion: string;
  channel?: string | null;
  payloadJson: string;
  sha256?: string | null;
}

/** 下发备份；服务端复核渠道硬门禁与主版本兼容。 */
export interface PushBackupRequest {
  targetDeviceId?: string | null;
  targetChannel?: string | null;
  targetFrameworkVersion?: string | null;
  restartAfterApply: boolean;
  force: boolean;
  currentHash?: string | null;
  confirmCrossDevice: boolean;
}

/** 下发结果。 */
export interface PushBackupResult {
  acked: boolean;
  success: boolean;
  message?: string | null;
  snapshotBackupId?: string | null;
}

/** 文档集合。 */
export interface DocumentCollection {
  id: string;
  key: string;
  name: string;
  description?: string | null;
  isEnabled: boolean;
  documentCount: number;
  createdAt: string;
  updatedAt: string;
}

/** 创建集合。 */
export interface CreateDocumentCollectionRequest {
  key: string;
  name: string;
  description?: string | null;
}

/** 更新集合。 */
export interface UpdateDocumentCollectionRequest {
  name?: string | null;
  description?: string | null;
  isEnabled?: boolean | null;
}

/** 文档可见性：client 才进入客户端清单。 */
export type DocumentVisibility = 'client' | 'internal';

/** 文档修订。 */
export interface DocumentRevision {
  id: string;
  revision: number;
  fileName: string;
  contentType?: string | null;
  sizeBytes: number;
  sha256: string;
  createdBy?: string | null;
  createdAt: string;
  downloadUrl: string;
}

/** 文档摘要。 */
export interface DocumentSummary {
  id: string;
  collectionKey: string;
  title: string;
  visibility: DocumentVisibility;
  revision: number;
  fileName: string;
  sizeBytes: number;
  sha256: string;
  createdAt: string;
  updatedAt: string;
}

/** 文档详情（含修订历史）。 */
export interface DocumentDetail extends DocumentSummary {
  revisions: DocumentRevision[];
}

/** 更新文档元数据。 */
export interface UpdateDocumentRequest {
  title?: string | null;
  visibility?: DocumentVisibility | null;
}

/** 客户端文档清单条目。 */
export interface ClientDocument {
  documentId: string;
  collectionKey: string;
  title: string;
  revision: number;
  fileName: string;
  contentType?: string | null;
  sizeBytes: number;
  sha256: string;
  updatedAt: string;
  downloadUrl: string;
}

/** 客户端文档清单。 */
export interface ClientDocumentManifest {
  items: ClientDocument[];
  generatedAt: string;
}

/** 审计记录（平台级只读视图）。 */
export interface AuditLog {
  id: string;
  appKey?: string | null;
  actorId?: string | null;
  actorName?: string | null;
  action: string;
  targetType?: string | null;
  targetId?: string | null;
  result: string;
  message?: string | null;
  ipAddress?: string | null;
  userAgent?: string | null;
  traceId?: string | null;
  detailJson?: string | null;
  createdAt: string;
}

/** Webhook 订阅摘要；签名密钥不在此出现，只有 `secretHint`。 */
export interface WebhookSubscription {
  id: string;
  appKey: string;
  url: string;
  eventTypes: string[];
  description?: string | null;
  isEnabled: boolean;
  secretHint: string;
  createdBy?: string | null;
  createdAt: string;
  updatedAt: string;
  lastDeliveryAt?: string | null;
}

/** 创建订阅的结果：`secret` 是明文密钥，只在这一次返回。 */
export interface CreatedWebhookSubscription {
  subscription: WebhookSubscription;
  secret: string;
}

/** 轮换签名密钥的结果；新密钥只在这一次返回。 */
export interface RotatedWebhookSecret {
  secret: string;
}

/** 创建订阅；`eventTypes` 留空表示订阅全部事件。 */
export interface CreateWebhookSubscriptionRequest {
  url: string;
  eventTypes?: string[] | null;
  description?: string | null;
  isEnabled?: boolean | null;
}

/** 更新订阅；字段缺省表示不修改，`eventTypes` 传空数组表示改为订阅全部事件。 */
export interface UpdateWebhookSubscriptionRequest {
  url?: string | null;
  eventTypes?: string[] | null;
  description?: string | null;
  isEnabled?: boolean | null;
}

/** 投递状态：待投 / 失败待重试 / 成功 / 死信（达到重试上限）。 */
export type WebhookDeliveryStatus = 'pending' | 'failed' | 'succeeded' | 'dead';

/** 投递记录（含负载原文，供排查签名与字段问题）。 */
export interface WebhookDelivery {
  id: string;
  subscriptionId: string;
  eventType: string;
  status: WebhookDeliveryStatus;
  attempts: number;
  nextAttemptAt?: string | null;
  lastAttemptAt?: string | null;
  lastStatusCode?: number | null;
  lastError?: string | null;
  payloadJson: string;
  createdAt: string;
  deliveredAt?: string | null;
}

/** 手动测试结果。 */
export interface WebhookTestResult {
  deliveryCount: number;
}

