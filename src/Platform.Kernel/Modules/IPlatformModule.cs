using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Kernel.Modules;

/// <summary>
/// 服务模块契约。与客户端 <c>ITestModuleDescriptor</c> 六面契约同构的最小化版本：
/// 一个模块 = 一个键 + 一段服务注册 + 一批端点 + 一份权限声明。
/// </summary>
/// <remarks>
/// **可摘除性是硬约束**：模块之间不得相互引用。唯一装配点是宿主的集中清单
/// （<c>Platform.Api/Modules/PlatformModuleCatalog</c>），新增模块只需在清单里加一行 + csproj 引用。
/// </remarks>
public interface IPlatformModule
{
    /// <summary>模块键，必须唯一，取自 <see cref="Platform.Contracts.PlatformModuleKeys"/>。</summary>
    string Key { get; }

    /// <summary>显示名 i18n 键（后台菜单与客户端页面共用）。</summary>
    string DisplayNameKey { get; }

    /// <summary>注册本模块的服务（DbContext 由 Infrastructure 统一注册，这里只注册模块自身服务）。</summary>
    void RegisterServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>挂载本模块端点。只允许挂在 <c>/api/v1/{module}/...</c> 之下。</summary>
    void MapEndpoints(IEndpointRouteBuilder api);

    /// <summary>本模块声明的全部权限码；必须能在 <c>PermissionCatalog</c> 中找到，否则启动即失败。</summary>
    IReadOnlyList<string> RequiredPermissions { get; }
}
