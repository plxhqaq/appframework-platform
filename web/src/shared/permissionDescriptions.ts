import i18n from '../i18n';
import { PERMISSIONS } from './permissions';

/**
 * 权限码说明：管理台权限树、API 密钥选择器等处共用。
 *
 * 说明文本按当前界面语言从 `permissions` 命名空间取；这里只解释「这个权限能做什么」，
 * 不是放行依据，真正的边界仍在服务端 `RequirePermission`。
 */
export function describePermission(code: string, locale?: string): string | undefined {
  const key = code === '*' ? 'wildcard' : `codes.${code}`;
  const text = i18n.t(key, { ns: 'permissions', lng: locale, defaultValue: '' });
  return text || undefined;
}

/** 管理台收录的全部权限码的说明（按当前语言解析，供测试与导出使用）。 */
export const PERMISSION_DESCRIPTIONS: Record<string, string> = Object.fromEntries(
  [...Object.values(PERMISSIONS), '*', 'platform.admin'].map((code) => [
    code,
    describePermission(code) ?? '',
  ]),
);
