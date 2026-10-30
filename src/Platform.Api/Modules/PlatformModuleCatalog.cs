using Platform.Kernel.Modules;
using Platform.Modules.Apps;
using Platform.Modules.Backups;
using Platform.Modules.Documents;
using Platform.Modules.Identity;
using Platform.Modules.Licensing;
using Platform.Modules.Versions;

namespace Platform.Api.Modules;

/// <summary>
/// **集中模块清单**：全平台唯一的模块装配点。
/// </summary>
/// <remarks>
/// 新增一个服务模块只需两步：① 新建 <c>Platform.Modules.X</c> 工程并引用它；
/// ② 在这里加一行。删除模块反向操作即可，其它模块与内核都不受影响——
/// 这是"模块化单体可摘除"的落地方式，也是不拆微服务的前提。
/// </remarks>
public static class PlatformModuleCatalog
{
    /// <summary>全部模块（顺序即启动装配顺序，也是后台菜单顺序的依据）。</summary>
    public static IReadOnlyList<IPlatformModule> Modules { get; } =
    [
        // apps 必须最先：其余模块的 app_id 隔离轴由它产生
        new AppsModule(),
        new IdentityModule(),
        new VersionsModule(),
        new LicensingModule(),
        new BackupsModule(),
        new DocumentsModule(),
    ];
}
