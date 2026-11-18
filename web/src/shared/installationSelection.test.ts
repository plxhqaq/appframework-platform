import { describe, expect, it } from 'vitest';
import type { InstallationSummary } from './api/types';
import { isInstallationAvailable, isInstallationSelectable } from './installationSelection';

function installation(overrides: Partial<InstallationSummary>): InstallationSummary {
  return {
    id: 'installation-1',
    nodeId: 'node-1',
    nodeDisplayName: 'PC-01',
    appKey: 'demo',
    machineCode: '0000000000000001',
    shortId: 'SHORT0000000001',
    installGuid: '1f0b6f0e-0000-0000-0000-000000000000',
    onlineStatus: 'online',
    stationStatus: 'idle',
    alarmLevel: 'none',
    heartbeatSeconds: 10,
    isEnabled: true,
    nodeEnabled: true,
    ...overrides,
  };
}

describe('installationSelection', () => {
  it('备份下发只允许有效启用、在线、待机的安装实例', () => {
    expect(isInstallationSelectable(installation({}))).toBe(true);
    expect(isInstallationSelectable(installation({ onlineStatus: 'offline' }))).toBe(false);
    expect(isInstallationSelectable(installation({ stationStatus: 'online' }))).toBe(false);
    expect(isInstallationSelectable(installation({ isEnabled: false }))).toBe(false);
    expect(isInstallationSelectable(installation({ nodeEnabled: false }))).toBe(false);
  });

  it('来源登记只看有效启用，离线安装实例也能选', () => {
    expect(isInstallationAvailable(installation({ onlineStatus: 'offline' }))).toBe(true);
    expect(isInstallationAvailable(installation({ stationStatus: 'online' }))).toBe(true);
    expect(isInstallationAvailable(installation({ isEnabled: false }))).toBe(false);
    expect(isInstallationAvailable(installation({ nodeEnabled: false }))).toBe(false);
  });
});
