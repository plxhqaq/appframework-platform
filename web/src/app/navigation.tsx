import {
  AppstoreOutlined,
  DashboardOutlined,
  DatabaseOutlined,
  FileTextOutlined,
  HistoryOutlined,
  KeyOutlined,
  MobileOutlined,
  QrcodeOutlined,
  SafetyOutlined,
  SendOutlined,
  SwapOutlined,
} from '@ant-design/icons';
import type { ReactNode } from 'react';
import { PERMISSIONS, hasAnyPermissionCode } from '../shared/permissions';

export interface NavItem {
  /** 路由路径，同时作为菜单选中键。 */
  key: string;
  icon: ReactNode;
  label: string;
  /** 命中任一权限码才显示；空数组 = 所有登录用户可见。 */
  anyOf: string[];
}

/** 登录后没有任何可访问页面时的落点，也用于「返回总览」。 */
export const HOME_PATH = '/overview';

/**
 * 导航与路由守卫的同一份事实源。
 *
 * 现场工程师排在最前：他们进管理台基本只做两件事——发密钥、批扫码，
 * 这两个页面不该埋在「身份与权限」的标签页里。
 * 隐藏只是不打扰用户，越权请求仍由服务端 `RequirePermission` 拒绝。
 */
export const NAV_ITEMS: NavItem[] = [
  { key: '/overview', icon: <DashboardOutlined />, label: '总览', anyOf: [] },
  {
    key: '/single-key',
    icon: <MobileOutlined />,
    label: '密钥登录',
    anyOf: [PERMISSIONS.identitySingleKeyIssue],
  },
  {
    key: '/qr-approval',
    icon: <QrcodeOutlined />,
    label: '扫码登录',
    anyOf: [PERMISSIONS.identityQrApprove],
  },
  { key: '/apps', icon: <AppstoreOutlined />, label: '应用接入', anyOf: [PERMISSIONS.appsRead] },
  {
    key: '/identity',
    icon: <SafetyOutlined />,
    label: '身份与权限',
    anyOf: [
      PERMISSIONS.identityUserRead,
      PERMISSIONS.identityRoleRead,
      PERMISSIONS.identitySessionRead,
    ],
  },
  {
    key: '/versions',
    icon: <SwapOutlined />,
    label: '版本与渠道',
    anyOf: [PERMISSIONS.versionsChannelRead, PERMISSIONS.versionsReleaseRead],
  },
  {
    key: '/backups',
    icon: <DatabaseOutlined />,
    label: '配置备份',
    anyOf: [PERMISSIONS.backupNamespaceRead, PERMISSIONS.backupRead],
  },
  {
    key: '/documents',
    icon: <FileTextOutlined />,
    label: '文档',
    anyOf: [PERMISSIONS.documentCollectionRead, PERMISSIONS.documentRead],
  },
  {
    key: '/licensing',
    icon: <KeyOutlined />,
    label: '授权',
    anyOf: [PERMISSIONS.licenseProductRead, PERMISSIONS.licenseRead],
  },
  { key: '/webhooks', icon: <SendOutlined />, label: 'Webhook', anyOf: [PERMISSIONS.webhookRead] },
  { key: '/audit', icon: <HistoryOutlined />, label: '审计日志', anyOf: [PERMISSIONS.auditRead] },
];

export function visibleNavItems(permissions: string[] | null | undefined): NavItem[] {
  return NAV_ITEMS.filter((item) => hasAnyPermissionCode(permissions, item.anyOf));
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
