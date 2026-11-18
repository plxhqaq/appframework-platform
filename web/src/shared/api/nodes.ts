import { request } from './client';
import type {
  NodeDetail,
  NodeRestartAccepted,
  NodeRestartRequest,
  NodeSummary,
  PageResult,
  UpdateNodeRequest,
} from './types';

/**
 * 物理节点（`/api/v1/admin/nodes`）。
 *
 * 节点是平台级资源，跨应用；状态由该节点全部安装实例聚合，
 * 在线/工位/报警不由客户端单独上报。
 */
function toQueryString(
  params: Record<string, string | number | boolean | undefined>,
): string {
  const search = new URLSearchParams();
  Object.entries(params).forEach(([key, value]) => {
    if (value !== undefined && value !== '') {
      search.set(key, String(value));
    }
  });
  const text = search.toString();
  return text ? `?${text}` : '';
}

export interface NodeListQuery {
  page?: number;
  pageSize?: number;
  onlineStatus?: string;
  isEnabled?: boolean;
  keyword?: string;
}

export const nodesApi = {
  listNodes: (query: NodeListQuery, signal?: AbortSignal) =>
    request<PageResult<NodeSummary>>(
      `/api/v1/admin/nodes${toQueryString({ ...query })}`,
      { signal },
    ),

  getNode: (id: string, signal?: AbortSignal) =>
    request<NodeDetail>(`/api/v1/admin/nodes/${id}`, { signal }),

  updateNode: (id: string, body: UpdateNodeRequest) =>
    request<NodeDetail>(`/api/v1/admin/nodes/${id}`, { method: 'PATCH', body }),

  restartNode: (id: string, body: NodeRestartRequest) =>
    request<NodeRestartAccepted>(`/api/v1/admin/nodes/${id}/restart`, {
      method: 'POST',
      body,
    }),
};
