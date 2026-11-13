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
  /** 二进制请求体（上传票据 PUT），与 `body` 互斥，不参与 JSON 序列化。 */
  rawBody?: Blob;
  /** 多段表单（文档上传），Content-Type 由浏览器带 boundary 生成。 */
  formData?: FormData;
  signal?: AbortSignal;
  /** 登录、健康检查等无需令牌的端点。 */
  anonymous?: boolean;
  headers?: Record<string, string>;
}

function parsePayload<T>(text: string): ApiResult<T> | null {
  if (!text) {
    return null;
  }

  try {
    return JSON.parse(text) as ApiResult<T>;
  } catch {
    return null;
  }
}

async function execute<T>(path: string, options: RequestOptions, retried: boolean): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json', ...options.headers };

  const token = options.anonymous ? null : tokenProvider();
  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }

  let body: BodyInit | undefined;
  if (options.formData !== undefined) {
    body = options.formData;
  } else if (options.rawBody !== undefined) {
    body = options.rawBody;
    headers['Content-Type'] = options.rawBody.type || 'application/octet-stream';
  } else if (options.body !== undefined) {
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
  const payload = parsePayload<T>(text);

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

/**
 * 下载二进制内容（备份 JSON / 文档文件）。
 *
 * 下载端点同样要求 Bearer 令牌，不能直接用 `<a href>`，否则浏览器不会带 Authorization。
 */
export async function requestBlob(path: string, retried = false): Promise<Blob> {
  const headers: Record<string, string> = {};
  const token = tokenProvider();
  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }

  const response = await fetch(`${API_BASE_URL}${path}`, { headers, credentials: 'include' });
  if (!response.ok) {
    if (response.status === 401 && !retried && refreshHandler) {
      const renewed = await refreshHandler().catch(() => null);
      if (renewed) {
        return requestBlob(path, true);
      }
    }

    const payload = parsePayload<unknown>(await response.text());

    throw new ApiError(
      payload?.code ?? -1,
      payload?.message ?? `下载失败（HTTP ${response.status}）`,
      response.status,
      payload?.traceId,
    );
  }

  return response.blob();
}

/** 上传进度回调（已发送字节 / 总字节）。 */
export type UploadProgressHandler = (loaded: number, total: number) => void;

export interface UploadOptions {
  onProgress?: UploadProgressHandler;
  signal?: AbortSignal;
}

/**
 * 带进度的二进制上传（上传票据 PUT）。
 *
 * `fetch` 无法上报请求体进度，只能用 XHR；鉴权与 401 续期口径与 `request` 保持一致。
 */
export function uploadWithProgress<T>(
  path: string,
  blob: Blob,
  options: UploadOptions = {},
): Promise<T> {
  const send = (retried: boolean): Promise<T> =>
    new Promise<T>((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      xhr.open('PUT', `${API_BASE_URL}${path}`);
      xhr.withCredentials = true;
      xhr.setRequestHeader('Accept', 'application/json');
      xhr.setRequestHeader('Content-Type', blob.type || 'application/octet-stream');

      const token = tokenProvider();
      if (token) {
        xhr.setRequestHeader('Authorization', `Bearer ${token}`);
      }

      const abort = () => xhr.abort();
      if (options.signal?.aborted) {
        reject(new DOMException('上传已取消', 'AbortError'));
        return;
      }
      options.signal?.addEventListener('abort', abort, { once: true });

      const finish = () => options.signal?.removeEventListener('abort', abort);

      if (options.onProgress) {
        xhr.upload.onprogress = (event) => {
          options.onProgress!(event.loaded, event.lengthComputable ? event.total : blob.size);
        };
      }

      xhr.onerror = () => {
        finish();
        reject(new ApiError(-1, '网络异常，上传失败', 0));
      };
      xhr.onabort = () => {
        finish();
        reject(new DOMException('上传已取消', 'AbortError'));
      };
      xhr.onload = () => {
        finish();
        const payload = parsePayload<T>(xhr.responseText);

        if (xhr.status === 401 && !retried && refreshHandler) {
          void refreshHandler()
            .catch(() => null)
            .then((renewed) => {
              if (renewed) {
                send(true).then(resolve, reject);
              } else {
                reject(toError(payload, xhr.status, '上传失败'));
              }
            });
          return;
        }

        if (xhr.status < 200 || xhr.status >= 300 || !payload || payload.isSuccess === false) {
          reject(toError(payload, xhr.status, '上传失败'));
          return;
        }

        resolve(payload.data as T);
      };

      xhr.send(blob);
    });

  return send(false);
}

function toError(payload: ApiResult<unknown> | null, status: number, fallback: string): ApiError {
  return new ApiError(
    payload?.code ?? -1,
    payload?.message ?? `${fallback}（HTTP ${status}）`,
    status,
    payload?.traceId,
  );
}
