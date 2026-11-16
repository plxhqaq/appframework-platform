import {
  AppstoreOutlined,
  BranchesOutlined,
  DashboardOutlined,
  DatabaseOutlined,
  DesktopOutlined,
  FileTextOutlined,
  HistoryOutlined,
  KeyOutlined,
  QrcodeOutlined,
  RocketOutlined,
  SafetyCertificateOutlined,
  SendOutlined,
  TeamOutlined,
} from '@ant-design/icons';
import type { ReactNode } from 'react';
import { PERMISSIONS, hasAnyPermissionCode } from '../shared/permissions';

export interface NavItem {
  /** 路由路径，同时作为菜单选中键。 */
  key: string;
  icon: ReactNode;
  /** i18n 键，渲染时经 `t` 落到当前语言。 */
  labelKey: string;
  /** 命中任一权限码才显示；空数组 = 所有登录用户可见。 */
  anyOf: string[];
  /** 页面数据按应用隔离；令牌没有应用上下文时页面只能显示「没有可用的应用上下文」，菜单不露出。 */
  requiresApp?: boolean;
}

/** 登录后没有任何可访问页面时的落点，也用于「返回总览」。 */
export const HOME_PATH = '/overview';

/**
 * 导航与路由守卫的同一份事实源。
 *
 * 顺序即分组：总览 → 现场作业（密钥、扫码）→ 平台管理（身份与权限、应用接入）
 * → 应用级页面（版本、设备、备份等）→ 审计日志收尾。
 * 现场工程师排在最前：他们进管理台基本只做两件事——发密钥、批扫码，
 * 这两个页面不该埋在「身份与权限」的标签页里。
 * 隐藏只是不打扰用户，越权请求仍由服务端 `RequirePermission` 拒绝。
 */
export const NAV_ITEMS: NavItem[] = [
  { key: '/overview', icon: <DashboardOutlined />, labelKey: 'overview', anyOf: [] },
  {
    key: '/single-key',
    icon: <KeyOutlined />,
    labelKey: 'singleKey',
    anyOf: [PERMISSIONS.identitySingleKeyIssue],
  },
  {
    key: '/qr-approval',
    icon: <QrcodeOutlined />,
    labelKey: 'qrApproval',
    anyOf: [PERMISSIONS.identityQrApprove],
  },
  {
    key: '/identity',
    icon: <TeamOutlined />,
    labelKey: 'identity',
    anyOf: [
      PERMISSIONS.identityUserRead,
      PERMISSIONS.identityRoleRead,
      PERMISSIONS.identitySessionRead,
    ],
  },
  { key: '/apps', icon: <AppstoreOutlined />, labelKey: 'apps', anyOf: [PERMISSIONS.appsRead] },
  {
    key: '/versions',
    icon: <BranchesOutlined />,
    labelKey: 'versions',
    anyOf: [PERMISSIONS.versionsChannelRead, PERMISSIONS.versionsReleaseRead],
    requiresApp: true,
  },
  {
    key: '/updaters',
    icon: <RocketOutlined />,
    labelKey: 'updaters',
    anyOf: [PERMISSIONS.versionsReleaseRead],
    requiresApp: true,
  },
  {
    key: '/devices',
    icon: <DesktopOutlined />,
    labelKey: 'devices',
    anyOf: [PERMISSIONS.deviceRead],
    requiresApp: true,
  },
  {
    key: '/backups',
    icon: <DatabaseOutlined />,
    labelKey: 'backups',
    anyOf: [PERMISSIONS.backupNamespaceRead, PERMISSIONS.backupRead],
    requiresApp: true,
  },
  {
    key: '/documents',
    icon: <FileTextOutlined />,
    labelKey: 'documents',
    anyOf: [PERMISSIONS.documentCollectionRead, PERMISSIONS.documentRead],
    requiresApp: true,
  },
  {
    key: '/licensing',
    icon: <SafetyCertificateOutlined />,
    labelKey: 'licensing',
    anyOf: [PERMISSIONS.licenseProductRead, PERMISSIONS.licenseRead],
    requiresApp: true,
  },
  {
    key: '/webhooks',
    icon: <SendOutlined />,
    labelKey: 'webhooks',
    anyOf: [PERMISSIONS.webhookRead],
    requiresApp: true,
  },
  { key: '/audit', icon: <HistoryOutlined />, labelKey: 'audit', anyOf: [PERMISSIONS.auditRead] },
];

export interface NavVisibility {
  /** 令牌里是否有可用的应用上下文（无 app 角色且平台上下文时为 false）。 */
  hasAppContext?: boolean;
}

export function visibleNavItems(
  permissions: string[] | null | undefined,
  visibility: NavVisibility = {},
): NavItem[] {
  const hasAppContext = visibility.hasAppContext ?? true;
  return NAV_ITEMS.filter(
    (item) =>
      hasAnyPermissionCode(permissions, item.anyOf) && (!item.requiresApp || hasAppContext),
  );
}

/** 路由守卫：按路径取需要的权限；不在表里的路径（如 /password）人人可进。 */
export function requiredPermissions(pathname: string): string[] {
  const item = NAV_ITEMS.find(
    (nav) => pathname === nav.key || pathname.startsWith(`${nav.key}/`),
  );
  return item?.anyOf ?? [];
}

/** 菜单选中键。扫码详情页本身不在导航里，但它属于「扫码登录」这一段。 */
export function selectedNavKey(pathname: string): string {
  if (pathname.startsWith('/qr-login/')) {
    return '/qr-approval';
  }
  return (
    NAV_ITEMS.find((nav) => pathname === nav.key || pathname.startsWith(`${nav.key}/`))?.key ??
    HOME_PATH
  );
}
