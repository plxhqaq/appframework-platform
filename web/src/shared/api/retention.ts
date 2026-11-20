import { request } from './client';
import type { RetentionSettings, UpdateRetentionSettingsRequest } from './types';

/** 清理宽限期（`/api/v1/admin/retention/settings`，平台级）。 */
export const retentionApi = {
  getSettings: (signal?: AbortSignal) =>
    request<RetentionSettings>('/api/v1/admin/retention/settings', { signal }),

  updateSettings: (body: UpdateRetentionSettingsRequest) =>
    request<RetentionSettings>('/api/v1/admin/retention/settings', { method: 'PUT', body }),
};
