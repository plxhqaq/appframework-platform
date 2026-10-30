import type { ApiResult } from './types';

/**
 * 薄 fetch 封装。
 *
 * 三条硬约定来自计划：错误一律是 ApiResult（含 code / traceId），
 * 所以这里不区分"HTTP 错误"和"业务错误"——都转成 ApiError 抛出，
 * 由 React Query 的错误边界统一提示。
 */

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? '';

/** 业务错误：code 来自 ApiErrorCode，traceId 用于与服务端日志对齐。 */
export class ApiError extends Error {
  readonly code: number;
  readonly traceId?: string | null;
  readonly status: number;

  constructor(code: number, message: string, status = 0, traceId?: string | null) {
    super(message);
    this.name = 'ApiError';
    this.code = code;
    this.status = status;
    this.traceId = traceId;
  }
}

let tokenProvider: () => string | null = () => null;
let refreshHandler: (() => Promise<string | null>) | null = null;

/** 令牌只存内存（refresh token 走 httpOnly cookie，access token 不进 localStorage）。 */
export function setTokenProvider(provider: () => string | null): void {
  tokenProvider = provider;
}

/** 访问令牌过期时的静默续期钩子；返回新令牌则重放原请求一次，返回 null 则按原错误抛出。 */
export function setRefreshHandler(handler: () => Promise<string | null>): void {
  refreshHandler = handler;
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  body?: unknown;
  signal?: AbortSignal;
  /** 登录、健康检查等无需令牌的端点。 */
  anonymous?: boolean;
  headers?: Record<string, string>;
}

async function execute<T>(path: string, options: RequestOptions, retried: boolean): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json', ...options.headers };

  const token = options.anonymous ? null : tokenProvider();
  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }

  let body: string | undefined;
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json';
    body = JSON.stringify(options.body);
  }

  const response = await fetch(`${API_BASE_URL}${path}`, {
    method: options.method ?? 'GET',
    headers,
    body,
    signal: options.signal,
    // 管理端刷新令牌是 httpOnly cookie，必须携带凭据（同源部署）
    credentials: 'include',
  });

  const text = await response.text();
  let payload: ApiResult<T> | null = null;
  if (text) {
    try {
      payload = JSON.parse(text) as ApiResult<T>;
    } catch {
      payload = null;
    }
  }

  if (!response.ok || !payload || payload.isSuccess === false) {
    // 访问令牌过期：尝试用 httpOnly 刷新令牌静默续期并重放一次
    if (response.status === 401 && !options.anonymous && !retried && refreshHandler) {
      const renewed = await refreshHandler().catch(() => null);
      if (renewed) {
        return execute<T>(path, options, true);
      }
    }

    throw new ApiError(
      payload?.code ?? -1,
      payload?.message ?? `请求失败（HTTP ${response.status}）`,
      response.status,
      payload?.traceId,
    );
  }

  return payload.data as T;
}

export function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  return execute<T>(path, options, false);
}
