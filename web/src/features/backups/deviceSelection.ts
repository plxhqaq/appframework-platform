import type { DeviceSummary } from '../../shared/api/types';

/** 可下发的设备：启用 + 在线 + 待机。运行中与停用设备仍然展示，但不可勾选。 */
export function isDeviceSelectable(device: DeviceSummary): boolean {
  return device.isEnabled && device.onlineStatus === 'online' && device.stationStatus === 'idle';
}
