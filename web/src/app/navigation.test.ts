import { describe, expect, it } from 'vitest';
import { PERMISSIONS } from '../shared/permissions';
import { HOME_PATH, NAV_ITEMS, requiredPermissions, selectedNavKey, visibleNavItems } from './navigation';

/** 与 IdentitySeeder 里现场工程师角色的读侧权限一致（写侧不影响导航）。 */
const ENGINEER_PERMISSIONS = [
  PERMISSIONS.appsRead,
  PERMISSIONS.identitySingleKeyIssue,
  PERMISSIONS.identityQrApprove,
  PERMISSIONS.versionsChannelRead,
  PERMISSIONS.versionsReleaseRead,
  PERMISSIONS.licenseProductRead,
  PERMISSIONS.licenseRead,
  PERMISSIONS.backupNamespaceRead,
  PERMISSIONS.backupRead,
  PERMISSIONS.documentCollectionRead,
  PERMISSIONS.documentRead,
];

const keys = (
  permissions: string[] | null | undefined,
  visibility?: { hasAppContext?: boolean },
) => visibleNavItems(permissions, visibility).map((item) => item.key);

describe('导航按权限裁剪', () => {
  it('平台管理员通配看得到全部页面', () => {
    expect(keys(['*'])).toEqual(NAV_ITEMS.map((item) => item.key));
  });

  it('现场工程师看得到密钥登录与扫码登录，看不到身份与权限和审计', () => {
    const visible = keys(ENGINEER_PERMISSIONS);
    expect(visible).toContain('/single-key');
    expect(visible).toContain('/qr-approval');
    expect(visible).not.toContain('/identity');
    expect(visible).not.toContain('/audit');
    expect(visible).not.toContain('/webhooks');
    // 设备管理权限只给 app-admin/viewer，现场工程师不露出设备页
    expect(visible).not.toContain('/devices');
  });

  it('临时会话摘掉两个入口权限后，这两页就不该出现', () => {
    const temporary = ENGINEER_PERMISSIONS.filter(
      (code) =>
        code !== PERMISSIONS.identitySingleKeyIssue && code !== PERMISSIONS.identityQrApprove,
    );
    const visible = keys(temporary);
    expect(visible).not.toContain('/single-key');
    expect(visible).not.toContain('/qr-approval');
    expect(visible).toContain(HOME_PATH);
  });

  it('未登录态只剩人人可进的总览', () => {
    expect(keys(null)).toEqual([HOME_PATH]);
    expect(keys([])).toEqual([HOME_PATH]);
  });
});

describe('没有应用上下文时不露出应用级入口', () => {
  it('通配权限但没有上下文时，应用级页面全部隐藏', () => {
    const visible = keys(['*'], { hasAppContext: false });
    expect(visible).toEqual(NAV_ITEMS.filter((item) => !item.requiresApp).map((item) => item.key));
    expect(visible).not.toContain('/versions');
    expect(visible).not.toContain('/licensing');
    expect(visible).not.toContain('/webhooks');
    expect(visible).not.toContain('/devices');
  });

  it('有上下文时应用级页面照常出现', () => {
    const visible = keys(['*'], { hasAppContext: true });
    expect(visible).toContain('/versions');
    expect(visible).toContain('/licensing');
    expect(visible).toContain('/webhooks');
    expect(visible).toContain('/devices');
  });

  it('上下文不能补权限：没有读权限的页面仍然不出现', () => {
    expect(keys([], { hasAppContext: true })).toEqual([HOME_PATH]);
    expect(keys([PERMISSIONS.versionsReleaseRead], { hasAppContext: false })).toEqual([
      HOME_PATH,
    ]);
  });
});

describe('路由守卫与菜单选中', () => {
  it('守卫用的权限与导航是同一份，手输地址也进不去', () => {
    expect(requiredPermissions('/single-key')).toEqual([PERMISSIONS.identitySingleKeyIssue]);
    expect(requiredPermissions('/qr-approval')).toEqual([PERMISSIONS.identityQrApprove]);
    expect(requiredPermissions('/identity')).toContain(PERMISSIONS.identityUserRead);
    expect(requiredPermissions('/devices')).toEqual([PERMISSIONS.deviceRead]);
  });

  it('子路径按所属页面判权限，改口令页人人可进', () => {
    expect(requiredPermissions('/versions/releases')).toEqual(
      requiredPermissions('/versions'),
    );
    expect(requiredPermissions('/password')).toEqual([]);
  });

  it('扫码详情页把菜单停在「扫码登录」，不是总览', () => {
    expect(selectedNavKey('/qr-login/9f2c1b7a6d5e4f001122334455667788')).toBe('/qr-approval');
    expect(selectedNavKey('/qr-approval')).toBe('/qr-approval');
    expect(selectedNavKey('/password')).toBe(HOME_PATH);
  });
});
