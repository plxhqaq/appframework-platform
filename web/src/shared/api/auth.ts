import { request } from './client';
import type { AdminSession, PasswordChanged } from './types';

export interface LoginRequest {
  userName: string;
  password: string;
}

/**
 * 管理端认证（`/api/v1/admin/auth`）。
 *
 * access token 只在内存里，refresh token 由服务端写入 httpOnly + SameSite=Strict Cookie，
 * 前端代码永远接触不到刷新令牌。
 */
export const authApi = {
  login: (body: LoginRequest) =>
    request<AdminSession>('/api/v1/admin/auth/login', { method: 'POST', anonymous: true, body }),

  refresh: () =>
    request<AdminSession>('/api/v1/admin/auth/refresh', { method: 'POST', anonymous: true }),

  logout: () => request<void>('/api/v1/admin/auth/logout', { method: 'POST', anonymous: true }),

  /**
   * 切换当前会话的应用上下文（管理台进入某个应用的模块）。
   * 服务端换发访问令牌，并把应用键写进刷新令牌记录，静默续期后仍停留在该应用。
   */
  switchAppContext: (appKey: string) =>
    request<AdminSession>('/api/v1/admin/auth/context', {
      method: 'POST',
      body: { appKey },
    }),

  changePassword: (currentPassword: string, newPassword: string) =>
    request<PasswordChanged>('/api/v1/identity/password/change', {
      method: 'POST',
      body: { currentPassword, newPassword },
    }),
};
