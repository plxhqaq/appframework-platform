import { request } from './client';
import type { AuditLog, PageResult } from './types';

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

export interface AuditListQuery {
  page?: number;
  pageSize?: number;
  appKey?: string;
  action?: string;
  actor?: string;
  result?: string;
  keyword?: string;
  /** ISO 8601 起始时间（含）。 */
  from?: string;
  /** ISO 8601 结束时间（含）。 */
  to?: string;
}

/**
 * 审计查询（`/api/v1/admin/audit-logs`，只读）。
 *
 * 平台级视图：不按当前应用上下文过滤，`appKey` 是显式筛选条件，
 * 空串表示「全部应用」；审计本身由服务端 `audit.read` 强制。
 */
export const auditApi = {
  list: (query: AuditListQuery, signal?: AbortSignal) =>
    request<PageResult<AuditLog>>(`/api/v1/admin/audit-logs${toQueryString({ ...query })}`, { signal }),
};
