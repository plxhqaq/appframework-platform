namespace Platform.Contracts.Permissions;

/// <summary>
/// 单个权限码的元数据。权限码是**稳定字符串**：一旦发布不得改名（改名等于破坏性变更），
/// 契约保证见 <c>Platform.Contracts/README.md</c>。
/// </summary>
/// <param name="Code">权限码，形如 <c>identity.user.manage</c>。</param>
/// <param name="ModuleKey">归属模块键；应用声明的权限用 <c>app:{appKey}:{group}</c>。</param>
/// <param name="DisplayNameKey">显示名 i18n 键（平台权限用；应用声明的权限用空串）。</param>
/// <param name="Sort">管理端权限树排序。</param>
/// <param name="PlatformAdminOnly">仅平台管理员可授予（例如签名密钥管理）。</param>
/// <param name="Description">权限说明；应用声明的权限由清单携带，平台权限为 <c>null</c>。</param>
/// <param name="ModuleLabel">模块显示名；应用清单的分组名，平台权限为 <c>null</c>。</param>
public sealed record PermissionDescriptor(
    string Code,
    string ModuleKey,
    string DisplayNameKey,
    int Sort = 0,
    bool PlatformAdminOnly = false,
    string? Description = null,
    string? ModuleLabel = null);
