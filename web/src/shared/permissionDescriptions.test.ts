import { describe, expect, it } from 'vitest';
import { PERMISSION_DESCRIPTIONS, describePermission } from './permissionDescriptions';
import { PERMISSIONS } from './permissions';

describe('权限说明目录', () => {
  it('管理台用到的每个权限码都有说明', () => {
    const missing = [...Object.values(PERMISSIONS), '*', 'platform.admin'].filter(
      (code) => !describePermission(code),
    );

    expect(missing).toEqual([]);
  });

  it('所有说明都非空', () => {
    for (const [code, description] of Object.entries(PERMISSION_DESCRIPTIONS)) {
      expect(description.trim(), code).not.toBe('');
    }
  });

  it('未登记的权限码返回 undefined，界面回退只显示权限码', () => {
    expect(describePermission('custom.unknown')).toBeUndefined();
  });
});
