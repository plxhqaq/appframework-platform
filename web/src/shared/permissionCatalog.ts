import type { PermissionDescriptor } from './api/types';

/**
 * 权限码模块键 → 中文名。
 *
 * 权限目录由 `/api/v1/platform/permissions` 下发，服务端只给模块键；
 * 新增模块时在这里补一行，未知模块回退显示模块键本身。
 */
export const PERMISSION_MODULE_LABELS: Record<string, string> = {
  apps: '应用接入',
  identity: '身份与权限',
  versions: '版本与渠道',
  licensing: '授权',
  backups: '备份',
  documents: '文档',
  webhooks: 'Webhook 订阅',
  platform: '平台',
  client: '客户端',
};

export interface PermissionGroup {
  moduleKey: string;
  label: string;
  items: PermissionDescriptor[];
}

/** 按模块归组：模块按组内最小 sort 升序，组内按 sort 升序，保证每次渲染顺序稳定。 */
export function groupPermissionsByModule(permissions: PermissionDescriptor[]): PermissionGroup[] {
  const groups = new Map<string, PermissionDescriptor[]>();
  for (const permission of permissions) {
    const list = groups.get(permission.moduleKey) ?? [];
    list.push(permission);
    groups.set(permission.moduleKey, list);
  }

  return [...groups.entries()]
    .sort(([, left], [, right]) => minSort(left) - minSort(right))
    .map(([moduleKey, items]) => ({
      moduleKey,
      label: PERMISSION_MODULE_LABELS[moduleKey] ?? moduleKey,
      items: [...items].sort((left, right) => left.sort - right.sort),
    }));
}

function minSort(items: PermissionDescriptor[]): number {
  return Math.min(...items.map((item) => item.sort));
}
