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

/** 令牌签发结果（开发令牌端点与 S1 的登录端点同形状）。 */
export interface IssuedToken {
  accessToken: string;
  expiresAt: string;
  permissions: string[];
  appKey: string;
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
