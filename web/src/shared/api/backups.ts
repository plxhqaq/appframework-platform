import { request } from './client';
import { idempotencyKey } from './idempotency';
import type {
  BackupNamespace,
  BackupSummary,
  CreateBackupNamespaceRequest,
  CreateBackupRequest,
  PageResult,
  PushBackupRequest,
  PushBackupResult,
  UpdateBackupNamespaceRequest,
} from './types';

/**
 * 命名空间备份（`/api/v1/admin/backups`）。
 *
 * 应用隔离轴来自令牌的应用上下文；下发只发推送提示，目标客户端回拉下载并回执。
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

export interface BackupListQuery {
  page?: number;
  pageSize?: number;
  namespaceKey?: string;
  kind?: string;
  keyword?: string;
}

export const backupsApi = {
  listNamespaces: (signal?: AbortSignal) =>
    request<BackupNamespace[]>('/api/v1/admin/backups/namespaces', { signal }),

  createNamespace: (body: CreateBackupNamespaceRequest) =>
    request<BackupNamespace>('/api/v1/admin/backups/namespaces', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  updateNamespace: (id: string, body: UpdateBackupNamespaceRequest) =>
    request<BackupNamespace>(`/api/v1/admin/backups/namespaces/${id}`, {
      method: 'PATCH',
      body,
    }),

  /** 删除备份命名空间；仍有任何备份（含软删除待清理）时服务端拒绝。 */
  deleteNamespace: (id: string) =>
    request<boolean>(`/api/v1/admin/backups/namespaces/${id}`, { method: 'DELETE' }),

  listBackups: (query: BackupListQuery, signal?: AbortSignal) =>
    request<PageResult<BackupSummary>>(
      `/api/v1/admin/backups${toQueryString({ ...query })}`,
      { signal },
    ),

  getBackup: (id: string, signal?: AbortSignal) =>
    request<BackupSummary>(`/api/v1/admin/backups/${id}`, { signal }),

  createBackup: (body: CreateBackupRequest) =>
    request<BackupSummary>('/api/v1/admin/backups', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  pushBackup: (id: string, body: PushBackupRequest) =>
    request<PushBackupResult>(`/api/v1/admin/backups/${id}/push`, { method: 'POST', body }),

  deleteBackup: (id: string) =>
    request<BackupSummary>(`/api/v1/admin/backups/${id}`, { method: 'DELETE' }),
};
