import type { DeviceSummary } from './api/types';

/** 可下发的设备：启用 + 在线 + 待机。运行中与停用设备仍然展示，但不可勾选。 */
export function isDeviceSelectable(device: DeviceSummary): boolean {
  return device.isEnabled && device.onlineStatus === 'online' && device.stationStatus === 'idle';
}

/** 可授权的设备：授权以机器码为锚点，离线或运行中的设备也能签发；停用设备不选。 */
export function isDeviceLicenseSelectable(device: DeviceSummary): boolean {
  return device.isEnabled;
}
