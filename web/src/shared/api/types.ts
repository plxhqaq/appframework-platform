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
}

/** 创建渠道。 */
export interface CreateReleaseChannelRequest {
  key: string;
  name: string;
  description?: string | null;
}

/** 更新渠道；字段缺省表示不修改。 */
export interface UpdateReleaseChannelRequest {
  name?: string | null;
  description?: string | null;
  isEnabled?: boolean | null;
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
