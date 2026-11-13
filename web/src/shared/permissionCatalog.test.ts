import { describe, expect, it } from 'vitest';
import { groupPermissionsByModule } from './permissionCatalog';
import type { PermissionDescriptor } from './api/types';

const descriptor = (
  code: string,
  moduleKey: string,
  sort: number,
  platformAdminOnly = false,
): PermissionDescriptor => ({
  code,
  moduleKey,
  displayNameKey: `perm.${code}`,
  sort,
  platformAdminOnly,
});

describe('权限目录分组', () => {
  it('按模块归组，模块与组内都按 sort 升序', () => {
    const groups = groupPermissionsByModule([
      descriptor('versions.release.read', 'versions', 320),
      descriptor('apps.manage', 'apps', 110),
      descriptor('versions.channel.read', 'versions', 300),
      descriptor('apps.read', 'apps', 100),
    ]);

    expect(groups.map((group) => group.moduleKey)).toEqual(['apps', 'versions']);
    expect(groups[0].label).toBe('应用接入');
    expect(groups[0].items.map((item) => item.code)).toEqual(['apps.read', 'apps.manage']);
    expect(groups[1].items.map((item) => item.code)).toEqual([
      'versions.channel.read',
      'versions.release.read',
    ]);
  });

  it('未知模块回退显示模块键本身', () => {
    const groups = groupPermissionsByModule([descriptor('custom.thing.read', 'custom', 1)]);

    expect(groups).toHaveLength(1);
    expect(groups[0].label).toBe('custom');
  });

  it('空目录返回空数组', () => {
    expect(groupPermissionsByModule([])).toEqual([]);
  });
});
