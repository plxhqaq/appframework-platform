import { request } from './client';
import type {
  InstallationDetail,
  InstallationRestartAccepted,
  InstallationRestartRequest,
  InstallationSummary,
  PageResult,
  UpdateInstallationRequest,
} from './types';

/**
 * 应用级安装实例（`/api/v1/admin/installations`）。
 *
 * 应用隔离轴来自令牌上下文；节点级事实（机器名/机器码/IP）只读，
 * 安装实例名取应用内录入名，管理端 customName 优先。
 * 注册/心跳走 `/hubs/devices`，这里只读管理视图与启停/重启。
 */
function toQueryString(params: Record<string, string | number | undefined>): string {
  const search = new URLSearchParams();
  Object.entries(params).forEach(([key, value]) => {
    if (value !== undefined && value !== '') {
      search.set(key, String(value));
    }
  });
  const text = search.toString();
  return text ? `?${text}` : '';
}

export interface InstallationListQuery {
  page?: number;
  pageSize?: number;
  onlineStatus?: string;
  branch?: string;
  keyword?: string;
}

/** 安装实例标题：展示名（管理端自定义名 > 应用内录入名），都空时用短键兜底。 */
export function installationLabel(row: InstallationSummary): string {
  return row.displayName || row.shortId;
}

export const installationsApi = {
  listInstallations: (query: InstallationListQuery, signal?: AbortSignal) =>
    request<PageResult<InstallationSummary>>(
      `/api/v1/admin/installations${toQueryString({ ...query })}`,
      { signal },
    ),

  getInstallation: (id: string, signal?: AbortSignal) =>
    request<InstallationDetail>(`/api/v1/admin/installations/${id}`, { signal }),

  updateInstallation: (id: string, body: UpdateInstallationRequest) =>
    request<InstallationDetail>(`/api/v1/admin/installations/${id}`, {
      method: 'PATCH',
      body,
    }),

  restartInstallation: (id: string, body: InstallationRestartRequest) =>
    request<InstallationRestartAccepted>(`/api/v1/admin/installations/${id}/restart`, {
      method: 'POST',
      body,
    }),
};
