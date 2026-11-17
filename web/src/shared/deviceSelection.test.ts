import { describe, expect, it } from 'vitest';
import type { DeviceSummary } from './api/types';
import { isDeviceLicenseSelectable, isDeviceSelectable } from './deviceSelection';

function device(overrides: Partial<DeviceSummary>): DeviceSummary {
  return {
    id: 'device-1',
    shortId: 'SHORT0000000001',
    machineCode: '0000000000000001',
    displayName: 'PC-01',
    onlineStatus: 'online',
    stationStatus: 'idle',
    alarmLevel: 'none',
    heartbeatSeconds: 10,
    isEnabled: true,
    ...overrides,
  };
}

describe('deviceSelection', () => {
  it('备份下发只允许启用、在线、待机设备', () => {
    expect(isDeviceSelectable(device({}))).toBe(true);
    expect(isDeviceSelectable(device({ onlineStatus: 'offline' }))).toBe(false);
    expect(isDeviceSelectable(device({ stationStatus: 'online' }))).toBe(false);
    expect(isDeviceSelectable(device({ isEnabled: false }))).toBe(false);
  });

  it('授权按机器码，离线或运行中的启用设备都可选', () => {
    expect(isDeviceLicenseSelectable(device({}))).toBe(true);
    expect(isDeviceLicenseSelectable(device({ onlineStatus: 'offline' }))).toBe(true);
    expect(isDeviceLicenseSelectable(device({ stationStatus: 'online' }))).toBe(true);
    expect(isDeviceLicenseSelectable(device({ isEnabled: false }))).toBe(false);
  });
});
