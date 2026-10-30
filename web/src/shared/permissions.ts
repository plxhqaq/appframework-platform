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
  identityUserRead: 'identity.user.read',
  identityUserManage: 'identity.user.manage',
  identityUserPasswordReset: 'identity.user.password.reset',
  identityRoleRead: 'identity.role.read',
  identityRoleManage: 'identity.role.manage',
  identitySessionRead: 'identity.session.read',
  identitySessionRevoke: 'identity.session.revoke',
  identitySingleKeyIssue: 'identity.singlekey.issue',
  identityQrApprove: 'identity.qr.approve',
  versionsChannelRead: 'versions.channel.read',
  versionsChannelManage: 'versions.channel.manage',
  versionsReleaseRead: 'versions.release.read',
  versionsReleasePublish: 'versions.release.publish',
  versionsReleaseArchive: 'versions.release.archive',
  versionsArtifactUpload: 'versions.artifact.upload',
  versionsArtifactDownload: 'versions.artifact.download',
} as const;
