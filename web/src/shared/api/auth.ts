import { request } from './client';
import type { IssuedToken } from './types';

export interface LoginRequest {
  userName: string;
  password: string;
}

/**
 * 登录。
 *
 * S0 还没有身份服务，所以默认走开发令牌端点（仅 Development 注册）。
 * S1 落地 `/api/v1/identity/login` 后把 `VITE_USE_DEV_TOKEN` 置为 false 即可切换到真实登录，
 * 两端返回形状一致（IssuedToken），调用方无需改动。
 */
const useDevToken = import.meta.env.VITE_USE_DEV_TOKEN === 'true';

export function isDevTokenMode(): boolean {
  return useDevToken;
}

export function login(request_: LoginRequest): Promise<IssuedToken> {
  if (useDevToken) {
    return request<IssuedToken>('/api/v1/dev/token', {
      method: 'POST',
      anonymous: true,
      body: { name: request_.userName, subject: request_.userName },
    });
  }

  return request<IssuedToken>('/api/v1/identity/login', {
    method: 'POST',
    anonymous: true,
    body: request_,
  });
}
