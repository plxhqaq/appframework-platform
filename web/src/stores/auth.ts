import { create } from 'zustand';
import { authApi } from '../shared/api/auth';
import { setRefreshHandler, setTokenProvider } from '../shared/api/client';
import type { UserSession } from '../shared/api/types';

export type AuthStatus = 'unknown' | 'authenticated' | 'anonymous';

interface AuthState {
  accessToken: string | null;
  expiresAt: string | null;
  session: UserSession | null;
  status: AuthStatus;
  pending: boolean;
  signIn: (userName: string, password: string) => Promise<UserSession>;
  /** 首屏静默续期：Cookie 里的 refresh token 还有效就恢复登录态。 */
  bootstrap: () => Promise<void>;
  refreshAccessToken: () => Promise<string | null>;
  signOut: () => Promise<void>;
  hasPermission: (code: string) => boolean;
}

let bootstrapPromise: Promise<void> | null = null;

const cleared = { accessToken: null, expiresAt: null, session: null, status: 'anonymous' as const };

/**
 * 登录态：access token 只放内存；刷新令牌是 httpOnly Cookie，
 * 刷新页面后通过 `/admin/auth/refresh` 静默恢复，不把长期凭据暴露给脚本。
 */
export const useAuthStore = create<AuthState>((set, get) => ({
  accessToken: null,
  expiresAt: null,
  session: null,
  status: 'unknown',
  pending: false,

  signIn: async (userName, password) => {
    set({ pending: true });
    try {
      const issued = await authApi.login({ userName, password });
      set({
        accessToken: issued.accessToken,
        expiresAt: issued.expiresAt,
        session: issued.user,
        status: 'authenticated',
      });
      return issued.user;
    } finally {
      set({ pending: false });
    }
  },

  bootstrap: () => {
    if (get().status !== 'unknown') {
      return Promise.resolve();
    }

    bootstrapPromise ??= (async () => {
      try {
        const issued = await authApi.refresh();
        set({
          accessToken: issued.accessToken,
          expiresAt: issued.expiresAt,
          session: issued.user,
          status: 'authenticated',
        });
      } catch {
        set(cleared);
      } finally {
        bootstrapPromise = null;
      }
    })();

    return bootstrapPromise;
  },

  refreshAccessToken: async () => {
    try {
      const issued = await authApi.refresh();
      set({
        accessToken: issued.accessToken,
        expiresAt: issued.expiresAt,
        session: issued.user,
        status: 'authenticated',
      });
      return issued.accessToken;
    } catch {
      set(cleared);
      return null;
    }
  },

  signOut: async () => {
    try {
      await authApi.logout();
    } catch {
      // 登出必须幂等：网络失败也要清掉本地态
    } finally {
      set(cleared);
    }
  },

  hasPermission: (code) => {
    const permissions = get().session?.permissions ?? [];
    return permissions.includes('*') || permissions.includes(code);
  },
}));

// 令牌注入点：fetch 封装只认这两个 provider，不直接依赖 store
setTokenProvider(() => useAuthStore.getState().accessToken);
setRefreshHandler(() => useAuthStore.getState().refreshAccessToken());
