/**
 * 权限码：与后端 `PermissionCatalog` 字符串一一对应。
 *
 * 这里只收录管理台真正用到的码，未收录不代表不存在；
 * 前端只用于隐藏按钮，服务端 `RequirePermission` 才是强制边界。
 */
export const PERMISSIONS = {
  appsRead: 'apps.read',
  appsManage: 'apps.manage',
  appsApiKeyManage: 'apps.apikey.manage',
} as const;
