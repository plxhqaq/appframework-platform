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
