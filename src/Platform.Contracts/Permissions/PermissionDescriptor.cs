namespace Platform.Contracts.Permissions;

/// <summary>
/// 单个权限码的元数据。权限码是**稳定字符串**：一旦发布不得改名（改名等于破坏性变更），
/// 契约保证见 <c>Platform.Contracts/README.md</c>。
/// </summary>
/// <param name="Code">权限码，形如 <c>identity.user.manage</c>。</param>
/// <param name="ModuleKey">归属模块键；客户端侧权限统一归 <c>client</c>。</param>
/// <param name="DisplayNameKey">显示名 i18n 键（后台与客户端共用同一份文案键）。</param>
/// <param name="Sort">管理端权限树排序。</param>
/// <param name="PlatformAdminOnly">仅平台管理员可授予（例如签名密钥管理）。</param>
public sealed record PermissionDescriptor(
    string Code,
    string ModuleKey,
    string DisplayNameKey,
    int Sort = 0,
    bool PlatformAdminOnly = false);
