import { request } from './client';
import type {
  ApiKeySummary,
  AppDetail,
  AppSummary,
  CreateApiKeyRequest,
  CreateAppRequest,
  CreatedApiKey,
  UpdateAppRequest,
} from './types';

/**
 * 应用接入模块（`/api/v1/admin/apps`）。
 *
 * 创建类接口都带 `Idempotency-Key`：网络重试/双击不能造出两个应用或两把密钥。
 * 键由调用方生成并在重试时复用，服务端在 TTL 内回放首次结果。
 */
function idempotencyKey(): string {
  return crypto.randomUUID();
}

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
};
