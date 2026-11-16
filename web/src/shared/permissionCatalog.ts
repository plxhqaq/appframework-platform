import type { PermissionDescriptor } from './api/types';

import i18n from '../i18n';

/** 平台权限模块键的当前语言显示名；未知模块回退显示模块键本身。 */
export function permissionModuleLabel(moduleKey: string): string {
  return i18n.t(`modules.${moduleKey}`, { ns: 'permissions', defaultValue: moduleKey });
}

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
      label:
        items.find((item) => item.moduleLabel)?.moduleLabel ?? permissionModuleLabel(moduleKey),
      items: [...items].sort((left, right) => left.sort - right.sort),
    }));
}

function minSort(items: PermissionDescriptor[]): number {
  return Math.min(...items.map((item) => item.sort));
}
