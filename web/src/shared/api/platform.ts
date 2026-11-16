import { request } from './client';
import type { ModuleInfo, PlatformDashboard, WhoAmI } from './types';

/** 平台级端点（不属于任何业务模块）。 */
export const platformApi = {
  modules: (signal?: AbortSignal) => request<ModuleInfo[]>('/api/v1/platform/modules', { anonymous: true, signal }),
  whoami: (signal?: AbortSignal) => request<WhoAmI>('/api/v1/platform/whoami', { signal }),
  dashboard: (signal?: AbortSignal) =>
    request<PlatformDashboard>('/api/v1/platform/dashboard', { signal }),
};
