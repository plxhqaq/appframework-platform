import { describe, expect, it } from 'vitest';
import type { NodeSummary } from './api/types';
import { isNodeLicenseSelectable } from './nodeSelection';

function node(overrides: Partial<NodeSummary>): NodeSummary {
  return {
    id: 'node-1',
    shortId: 'SHORT0000000001',
    machineCode: '0000000000000001',
    displayName: 'PC-01',
    onlineStatus: 'online',
    stationStatus: 'idle',
    alarmLevel: 'none',
    installationCount: 1,
    onlineInstallationCount: 1,
    appKeys: ['demo'],
    isEnabled: true,
    ...overrides,
  };
}

describe('nodeSelection', () => {
  it('授权按机器码，离线或运行中的启用节点都可选', () => {
    expect(isNodeLicenseSelectable(node({}))).toBe(true);
    expect(isNodeLicenseSelectable(node({ onlineStatus: 'offline' }))).toBe(true);
    expect(isNodeLicenseSelectable(node({ stationStatus: 'online' }))).toBe(true);
    expect(isNodeLicenseSelectable(node({ isEnabled: false }))).toBe(false);
  });
});
