import type { InstallationSummary } from './api/types';

/** 有效启用：安装实例开关与节点开关同时为真。 */
export function isInstallationAvailable(installation: InstallationSummary): boolean {
  return installation.isEnabled && installation.nodeEnabled;
}

/** 可下发备份的安装实例：有效启用 + 在线 + 待机。运行中与停用实例仍展示，但不可勾选。 */
export function isInstallationSelectable(installation: InstallationSummary): boolean {
  return (
    isInstallationAvailable(installation) &&
    installation.onlineStatus === 'online' &&
    installation.stationStatus === 'idle'
  );
}
