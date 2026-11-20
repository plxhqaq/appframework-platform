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
  appsPermissionsManage: 'apps.permissions.manage',
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
  versionsReleaseDelete: 'versions.release.delete',
  versionsArtifactUpload: 'versions.artifact.upload',
  versionsArtifactDownload: 'versions.artifact.download',
  nodeRead: 'node.read',
  nodeManage: 'node.manage',
  nodeRestart: 'node.restart',
  installationRead: 'installation.read',
  installationManage: 'installation.manage',
  installationRestart: 'installation.restart',
  backupNamespaceRead: 'backup.namespace.read',
  backupNamespaceManage: 'backup.namespace.manage',
  backupRead: 'backup.read',
  backupCreate: 'backup.create',
  backupPush: 'backup.push',
  backupDelete: 'backup.delete',
  backupRestore: 'backup.restore',
  backupPurge: 'backup.purge',
  documentCollectionRead: 'document.collection.read',
  documentCollectionManage: 'document.collection.manage',
  documentRead: 'document.read',
  documentManage: 'document.manage',
  documentDelete: 'document.delete',
  documentRestore: 'document.restore',
  documentPurge: 'document.purge',
  licenseProductRead: 'license.product.read',
  licenseProductManage: 'license.product.manage',
  licenseRead: 'license.read',
  licenseIssue: 'license.issue',
  licenseRevoke: 'license.revoke',
  licenseOfflineCodeIssue: 'license.offlinecode.issue',
  licenseSigningKeyManage: 'license.signingkey.manage',
  webhookRead: 'webhook.read',
  webhookManage: 'webhook.manage',
  auditRead: 'audit.read',
  platformApiDocsRead: 'platform.apidocs.read',
  retentionCleanupManage: 'retention.cleanup.manage',
} as const;

/** `*` 是平台管理员通配，命中即全放行。 */
export function hasPermissionCode(permissions: string[] | null | undefined, code: string): boolean {
  const granted = permissions ?? [];
  return granted.includes('*') || granted.includes(code);
}

/** 命中任一码即可；空要求表示人人可进。 */
export function hasAnyPermissionCode(
  permissions: string[] | null | undefined,
  codes: string[],
): boolean {
  return codes.length === 0 || codes.some((code) => hasPermissionCode(permissions, code));
}
