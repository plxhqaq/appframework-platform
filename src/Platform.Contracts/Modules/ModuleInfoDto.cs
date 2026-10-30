namespace Platform.Contracts.Modules;

/// <summary>
/// 模块自述信息。<c>GET /api/v1/{module}/info</c> 返回该结构，
/// 用于接入联调（确认服务在线、拿到本模块权限码与路由清单），也是健康探针的语义化补充。
/// </summary>
/// <param name="Key">模块键，见 <see cref="PlatformModuleKeys"/>。</param>
/// <param name="DisplayNameKey">显示名 i18n 键。</param>
/// <param name="Version">模块程序集版本（信息性版本）。</param>
/// <param name="RequiredPermissions">本模块声明的权限码（应全部存在于 <see cref="Permissions.PermissionCatalog"/>）。</param>
/// <param name="Routes">本模块挂载的绝对路由前缀（只含静态段，不含参数）。</param>
/// <param name="Stage">实现阶段标记：<c>S0</c> 骨架期各模块只有自述端点，业务端点随阶段补齐。</param>
public sealed record ModuleInfoDto(
    string Key,
    string DisplayNameKey,
    string Version,
    IReadOnlyList<string> RequiredPermissions,
    IReadOnlyList<string> Routes,
    string Stage = "S0");
