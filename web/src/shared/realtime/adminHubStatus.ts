import { createContext, useContext } from 'react';

/** AdminHub 连接态：徽标与调试入口共用，独立成模块以保住组件文件的 Fast Refresh。 */
export type AdminHubStatus = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

export const AdminHubStatusContext = createContext<AdminHubStatus>('disconnected');

export function useAdminHubStatus(): AdminHubStatus {
  return useContext(AdminHubStatusContext);
}
