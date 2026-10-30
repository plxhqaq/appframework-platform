import { create } from 'zustand';
import { login } from '../shared/api/auth';
import { setTokenProvider } from '../shared/api/client';
import { platformApi } from '../shared/api/platform';
import type { WhoAmI } from '../shared/api/types';

/** 当前登录主体（与 whoami 响应一一对应）。 */
export interface AuthUser {
  userId?: string | null;
  userName?: string | null;
  roles: string[];
  permissions: string[];
  appKey?: string | null;
  appKeys: string[];
  isService: boolean;
  isTemporary: boolean;
}

interface AuthState {
  accessToken: string | null;
  expiresAt: string | null;
  user: AuthUser | null;
  pending: boolean;
  signIn: (userName: string, password: string) => Promise<void>;
  refreshUser: () => Promise<void>;
  signOut: () => void;
  hasPermission: (code: string) => boolean;
}

function toAuthUser(me: WhoAmI): AuthUser {
  return {
    userId: me.userId,
    userName: me.userName,
    roles: me.roles,
    permissions: me.permissions,
    appKey: me.appKey,
    appKeys: me.appKeys,
    isService: me.isService,
    isTemporary: me.isTemporary,
  };
}

/**
 * 登录态：access token 只放内存（刷新页面即失效，S1 上 refresh token 后再做静默续期），
 * 这样 XSS 也偷不到长期凭据。
 */
export const useAuthStore = create<AuthState>((set, get) => ({
  accessToken: null,
  expiresAt: null,
  user: null,
  pending: false,

  signIn: async (userName, password) => {
    set({ pending: true });
    try {
      const issued = await login({ userName, password });
      set({ accessToken: issued.accessToken, expiresAt: issued.expiresAt });
      const me = await platformApi.whoami();
      set({ user: toAuthUser(me) });
    } finally {
      set({ pending: false });
    }
  },

  refreshUser: async () => {
    if (!get().accessToken) {
      return;
    }
    const me = await platformApi.whoami();
    set({ user: toAuthUser(me) });
  },

  signOut: () => set({ accessToken: null, expiresAt: null, user: null }),

  hasPermission: (code) => {
    const permissions = get().user?.permissions ?? [];
    return permissions.includes('*') || permissions.includes(code);
  },
}));

// 令牌注入点：fetch 封装只认这个 provider，不直接依赖 store
setTokenProvider(() => useAuthStore.getState().accessToken);
