import { request } from './client';
import type {
  DeviceDetail,
  DeviceRestartAccepted,
  DeviceRestartRequest,
  DeviceSummary,
  PageResult,
  UpdateDeviceRequest,
} from './types';

/**
 * 设备管理（`/api/v1/admin/devices`）。
 *
 * 设备注册/心跳走 `/hubs/devices`，这里只读管理视图与改名/启停/重启。
 * 在线状态以服务端判定为准：数据库在线标记 + 实时连接登记。
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

export interface DeviceListQuery {
  page?: number;
  pageSize?: number;
  onlineStatus?: string;
  branch?: string;
  keyword?: string;
}

export const devicesApi = {
  listDevices: (query: DeviceListQuery, signal?: AbortSignal) =>
    request<PageResult<DeviceSummary>>(
      `/api/v1/admin/devices${toQueryString({ ...query })}`,
      { signal },
    ),

  getDevice: (id: string, signal?: AbortSignal) =>
    request<DeviceDetail>(`/api/v1/admin/devices/${id}`, { signal }),

  updateDevice: (id: string, body: UpdateDeviceRequest) =>
    request<DeviceDetail>(`/api/v1/admin/devices/${id}`, { method: 'PATCH', body }),

  restartDevice: (id: string, body: DeviceRestartRequest) =>
    request<DeviceRestartAccepted>(`/api/v1/admin/devices/${id}/restart`, {
      method: 'POST',
      body,
    }),
};
