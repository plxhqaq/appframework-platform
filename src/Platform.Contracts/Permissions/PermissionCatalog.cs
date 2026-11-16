namespace Platform.Contracts.Permissions;

/// <summary>
/// 权限码目录：平台侧（管理端 + 服务端到服务端）稳定权限码的**唯一事实源**。
/// 角色表 <c>roles.permissions</c> 存的就是这里的字符串；客户端应用自己的权限由各应用通过
/// 权限清单注册（见 <c>/api/v1/apps/{appKey}/permissions</c>），平台内核不感知任何具体客户端。
/// </summary>
/// <remarks>
/// 命名规则：<c>{域}.{资源}.{动作}</c>，全部小写点分。域与模块键一致，唯一例外是授权域用
/// <c>license</c>（语义比 <c>licensing</c> 更贴合"授权实例"，与现网 <c>license.issue</c> 口径一致）。
/// </remarks>
public static class PermissionCatalog
{
    /// <summary>通配符：持有该值的角色等价平台管理员，绕过所有权限判定。</summary>
    public const string Wildcard = "*";

    // ━━━ 应用接入（apps）━━━
    public const string AppsRead = "apps.read";
    public const string AppsManage = "apps.manage";
    public const string AppsApiKeyManage = "apps.apikey.manage";
    public const string AppsPermissionsManage = "apps.permissions.manage";

    // ━━━ 身份（identity）━━━
    public const string IdentityUserRead = "identity.user.read";
    public const string IdentityUserManage = "identity.user.manage";
    public const string IdentityUserPasswordReset = "identity.user.password.reset";
    public const string IdentityRoleRead = "identity.role.read";
    public const string IdentityRoleManage = "identity.role.manage";
    public const string IdentitySessionRead = "identity.session.read";
    public const string IdentitySessionRevoke = "identity.session.revoke";
    public const string IdentitySingleKeyIssue = "identity.singlekey.issue";
    public const string IdentityQrApprove = "identity.qr.approve";

    // ━━━ 版本（versions）━━━
    public const string VersionsChannelRead = "versions.channel.read";
    public const string VersionsChannelManage = "versions.channel.manage";
    public const string VersionsReleaseRead = "versions.release.read";
    public const string VersionsReleasePublish = "versions.release.publish";
    public const string VersionsReleaseArchive = "versions.release.archive";
    public const string VersionsArtifactUpload = "versions.artifact.upload";
    public const string VersionsArtifactDownload = "versions.artifact.download";

    // ━━━ 授权（license）━━━
    public const string LicenseProductRead = "license.product.read";
    public const string LicenseProductManage = "license.product.manage";
    public const string LicenseRead = "license.read";
    public const string LicenseIssue = "license.issue";
    public const string LicenseRevoke = "license.revoke";
    public const string LicenseOfflineCodeIssue = "license.offlinecode.issue";
    public const string LicenseSigningKeyManage = "license.signingkey.manage";

    // ━━━ 备份（backup）━━━
    public const string BackupNamespaceRead = "backup.namespace.read";
    public const string BackupNamespaceManage = "backup.namespace.manage";
    public const string BackupRead = "backup.read";
    public const string BackupCreate = "backup.create";
    public const string BackupPush = "backup.push";
    public const string BackupDelete = "backup.delete";

    // ━━━ 文档（document）━━━
    public const string DocumentCollectionRead = "document.collection.read";
    public const string DocumentCollectionManage = "document.collection.manage";
    public const string DocumentRead = "document.read";
    public const string DocumentManage = "document.manage";
    public const string DocumentDelete = "document.delete";

    // ━━━ Webhook（webhook）━━━
    public const string WebhookRead = "webhook.read";
    public const string WebhookManage = "webhook.manage";

    // ━━━ 设备（devices；权限域用单数 device，与管理端习惯口径一致）━━━
    public const string DeviceRead = "device.read";
    public const string DeviceManage = "device.manage";
    public const string DeviceRestart = "device.restart";

    // ━━━ 平台级 ━━━
    public const string AuditRead = "audit.read";
    public const string PlatformAdmin = "platform.admin";

    /// <summary>平台侧全部权限（管理端可授予的集合；不含客户端权限）。</summary>
    public static IReadOnlyList<PermissionDescriptor> PlatformPermissions { get; } =
    [
        new(AppsRead, PlatformModuleKeys.Apps, "perm.apps.read", 100),
        new(AppsManage, PlatformModuleKeys.Apps, "perm.apps.manage", 110, PlatformAdminOnly: true),
        new(AppsApiKeyManage, PlatformModuleKeys.Apps, "perm.apps.apikey.manage", 120, PlatformAdminOnly: true),
        new(AppsPermissionsManage, PlatformModuleKeys.Apps, "perm.apps.permissions.manage", 130),

        new(IdentityUserRead, PlatformModuleKeys.Identity, "perm.identity.user.read", 200),
        new(IdentityUserManage, PlatformModuleKeys.Identity, "perm.identity.user.manage", 210),
        new(IdentityUserPasswordReset, PlatformModuleKeys.Identity, "perm.identity.user.password.reset", 220),
        new(IdentityRoleRead, PlatformModuleKeys.Identity, "perm.identity.role.read", 230),
        new(IdentityRoleManage, PlatformModuleKeys.Identity, "perm.identity.role.manage", 240),
        new(IdentitySessionRead, PlatformModuleKeys.Identity, "perm.identity.session.read", 250),
        new(IdentitySessionRevoke, PlatformModuleKeys.Identity, "perm.identity.session.revoke", 260),
        new(IdentitySingleKeyIssue, PlatformModuleKeys.Identity, "perm.identity.singlekey.issue", 270),
        new(IdentityQrApprove, PlatformModuleKeys.Identity, "perm.identity.qr.approve", 280),

        new(VersionsChannelRead, PlatformModuleKeys.Versions, "perm.versions.channel.read", 300),
        new(VersionsChannelManage, PlatformModuleKeys.Versions, "perm.versions.channel.manage", 310),
        new(VersionsReleaseRead, PlatformModuleKeys.Versions, "perm.versions.release.read", 320),
        new(VersionsReleasePublish, PlatformModuleKeys.Versions, "perm.versions.release.publish", 330),
        new(VersionsReleaseArchive, PlatformModuleKeys.Versions, "perm.versions.release.archive", 340),
        new(VersionsArtifactUpload, PlatformModuleKeys.Versions, "perm.versions.artifact.upload", 350),
        new(VersionsArtifactDownload, PlatformModuleKeys.Versions, "perm.versions.artifact.download", 360),

        new(LicenseProductRead, PlatformModuleKeys.Licensing, "perm.license.product.read", 400),
        new(LicenseProductManage, PlatformModuleKeys.Licensing, "perm.license.product.manage", 410),
        new(LicenseRead, PlatformModuleKeys.Licensing, "perm.license.read", 420),
        new(LicenseIssue, PlatformModuleKeys.Licensing, "perm.license.issue", 430),
        new(LicenseRevoke, PlatformModuleKeys.Licensing, "perm.license.revoke", 440),
        new(LicenseOfflineCodeIssue, PlatformModuleKeys.Licensing, "perm.license.offlinecode.issue", 450),
        new(LicenseSigningKeyManage, PlatformModuleKeys.Licensing, "perm.license.signingkey.manage", 460, PlatformAdminOnly: true),

        new(BackupNamespaceRead, PlatformModuleKeys.Backups, "perm.backup.namespace.read", 500),
        new(BackupNamespaceManage, PlatformModuleKeys.Backups, "perm.backup.namespace.manage", 510),
        new(BackupRead, PlatformModuleKeys.Backups, "perm.backup.read", 520),
        new(BackupCreate, PlatformModuleKeys.Backups, "perm.backup.create", 530),
        new(BackupPush, PlatformModuleKeys.Backups, "perm.backup.push", 540),
        new(BackupDelete, PlatformModuleKeys.Backups, "perm.backup.delete", 550),

        new(DocumentCollectionRead, PlatformModuleKeys.Documents, "perm.document.collection.read", 600),
        new(DocumentCollectionManage, PlatformModuleKeys.Documents, "perm.document.collection.manage", 610),
        new(DocumentRead, PlatformModuleKeys.Documents, "perm.document.read", 620),
        new(DocumentManage, PlatformModuleKeys.Documents, "perm.document.manage", 630),
        new(DocumentDelete, PlatformModuleKeys.Documents, "perm.document.delete", 640),

        new(WebhookRead, PlatformModuleKeys.Webhooks, "perm.webhook.read", 700),
        new(WebhookManage, PlatformModuleKeys.Webhooks, "perm.webhook.manage", 710),

        new(DeviceRead, PlatformModuleKeys.Devices, "perm.device.read", 800),
        new(DeviceManage, PlatformModuleKeys.Devices, "perm.device.manage", 810),
        new(DeviceRestart, PlatformModuleKeys.Devices, "perm.device.restart", 820),

        new(AuditRead, "platform", "perm.audit.read", 900, PlatformAdminOnly: true),
        new(PlatformAdmin, "platform", "perm.platform.admin", 910, PlatformAdminOnly: true),
    ];

    /// <summary>全部已知平台权限（含客户端应用权限的完整目录由运行时按应用清单合并）。</summary>
    public static IReadOnlyList<PermissionDescriptor> All { get; } = PlatformPermissions;

    /// <summary>全部已知平台权限码集合（不含通配符，也不含应用声明的权限）。</summary>
    public static IReadOnlySet<string> Codes { get; } =
        All.Select(static p => p.Code).ToHashSet(StringComparer.Ordinal);

    /// <summary>按模块分组的全部权限（后台权限树直接消费）。</summary>
    public static IReadOnlyList<PermissionDescriptor> ForModule(string moduleKey) =>
        All.Where(p => string.Equals(p.ModuleKey, moduleKey, StringComparison.Ordinal))
           .OrderBy(static p => p.Sort)
           .ToList();

    /// <summary>是否为已知权限码（<see cref="Wildcard"/> 视为已知）。</summary>
    public static bool IsKnown(string? code) =>
        !string.IsNullOrWhiteSpace(code) && (code == Wildcard || Codes.Contains(code));

    /// <summary>展开角色权限：<see cref="Wildcard"/> 展开为全部平台权限码。</summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string>? granted)
    {
        var list = granted?.Where(static c => !string.IsNullOrWhiteSpace(c)).ToList() ?? [];
        if (list.Contains(Wildcard, StringComparer.Ordinal))
        {
            return [.. Codes];
        }
        return [.. list.Distinct(StringComparer.Ordinal).Where(IsKnown)];
    }
}
