import { request } from './client';
import { idempotencyKey } from './idempotency';
import type {
  ApiKeySummary,
  AppDetail,
  AppPermissionManifest,
  AppSummary,
  CreateApiKeyRequest,
  CreateAppRequest,
  CreatedApiKey,
  ReplaceAppPermissionsRequest,
  UpdateAppRequest,
} from './types';

/**
 * 应用接入模块（`/api/v1/admin/apps`）。
 *
 * 创建类接口都带 `Idempotency-Key`：网络重试/双击不能造出两个应用或两把密钥。
 * 键由调用方生成并在重试时复用，服务端在 TTL 内回放首次结果。
 */
export const appsApi = {
  list: (signal?: AbortSignal) => request<AppSummary[]>('/api/v1/admin/apps', { signal }),

  create: (body: CreateAppRequest) =>
    request<AppDetail>('/api/v1/admin/apps', {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  update: (id: string, body: UpdateAppRequest) =>
    request<AppDetail>(`/api/v1/admin/apps/${id}`, { method: 'PATCH', body }),

  listKeys: (id: string, signal?: AbortSignal) =>
    request<ApiKeySummary[]>(`/api/v1/admin/apps/${id}/keys`, { signal }),

  createKey: (id: string, body: CreateApiKeyRequest) =>
    request<CreatedApiKey>(`/api/v1/admin/apps/${id}/keys`, {
      method: 'POST',
      body,
      headers: { 'Idempotency-Key': idempotencyKey() },
    }),

  revokeKey: (id: string, keyId: string) =>
    request<ApiKeySummary>(`/api/v1/admin/apps/${id}/keys/${keyId}/revoke`, { method: 'POST' }),

  deleteKey: (id: string, keyId: string) =>
    request<boolean>(`/api/v1/admin/apps/${id}/keys/${keyId}`, { method: 'DELETE' }),

  getPermissions: (appKey: string, signal?: AbortSignal) =>
    request<AppPermissionManifest>(`/api/v1/apps/${appKey}/permissions`, { anonymous: true, signal }),

  replacePermissions: (appKey: string, body: ReplaceAppPermissionsRequest) =>
    request<AppPermissionManifest>(`/api/v1/apps/${appKey}/permissions`, { method: 'PUT', body }),

  unlockPermissions: (id: string) =>
    request<AppPermissionManifest>(`/api/v1/admin/apps/${id}/permissions/unlock`, { method: 'POST' }),

  lockPermissions: (id: string) =>
    request<AppPermissionManifest>(`/api/v1/admin/apps/${id}/permissions/lock`, { method: 'POST' }),
};
