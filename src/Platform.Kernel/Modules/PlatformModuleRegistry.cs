using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Contracts.Modules;
using Platform.Contracts.Permissions;

namespace Platform.Kernel.Modules;

/// <summary>
/// 模块注册表：校验 + 装配 + 自述。宿主只与它交互，不直接接触具体模块类型。
/// </summary>
public sealed class PlatformModuleRegistry
{
    private readonly List<IPlatformModule> _modules;

    /// <summary>用给定模块集合构造；键为空或重复立即抛错（启动期失败优于运行期诡异 404）。</summary>
    public PlatformModuleRegistry(IEnumerable<IPlatformModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        _modules = [.. modules];
        var duplicated = _modules
            .GroupBy(static m => m.Key, StringComparer.Ordinal)
            .Where(static g => g.Count() > 1)
            .Select(static g => g.Key)
            .ToList();
        if (duplicated.Count > 0)
        {
            throw new InvalidOperationException($"模块键重复：{string.Join(", ", duplicated)}");
        }

        var blank = _modules.Where(static m => string.IsNullOrWhiteSpace(m.Key)).ToList();
        if (blank.Count > 0)
        {
            throw new InvalidOperationException("存在未声明 Key 的模块。");
        }

        Modules = _modules;
        Keys = _modules.Select(static m => m.Key).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>已注册模块（顺序即集中清单顺序）。</summary>
    public IReadOnlyList<IPlatformModule> Modules { get; }

    /// <summary>已注册模块键集合。</summary>
    public IReadOnlySet<string> Keys { get; }

    /// <summary>按键查找模块。</summary>
    public IPlatformModule? Find(string key) =>
        _modules.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// 校验各模块声明的权限码都存在于 <see cref="PermissionCatalog"/>。
    /// 拼错权限码会让端点永久 403 且难以排查，因此在启动期就炸掉。
    /// </summary>
    public void ValidatePermissions()
    {
        var unknown = new List<string>();
        foreach (var module in _modules)
        {
            foreach (var code in module.RequiredPermissions)
            {
                if (!PermissionCatalog.IsKnown(code))
                {
                    unknown.Add($"{module.Key}:{code}");
                }
            }
        }

        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"模块声明了未登记的权限码：{string.Join(", ", unknown)}。请先在 PermissionCatalog 登记。");
        }
    }

    /// <summary>依次调用各模块的服务注册。</summary>
    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var module in _modules)
        {
            module.RegisterServices(services, configuration);
        }
    }

    /// <summary>依次挂载各模块端点。</summary>
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        foreach (var module in _modules)
        {
            module.MapEndpoints(api);
        }
    }

    /// <summary>全部模块的自述信息（用于 <c>GET /api/v1/platform/modules</c>）。</summary>
    public IReadOnlyList<ModuleInfoDto> Describe() => [.. _modules.Select(Describe)];

    /// <summary>单个模块的自述信息。</summary>
    public static ModuleInfoDto Describe(IPlatformModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        var assembly = module.GetType().Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "0.0.0";

        return new ModuleInfoDto(
            module.Key,
            module.DisplayNameKey,
            version,
            [.. module.RequiredPermissions],
            [PlatformRoutes.Module(module.Key)]);
    }

    /// <summary>把注册表登记进容器，并把 ModuleInfo 能力暴露给宿主。</summary>
    public PlatformModuleRegistry AddTo(IServiceCollection services)
    {
        services.AddSingleton(this);
        return this;
    }
}

/// <summary>宿主侧装配扩展。</summary>
public static class PlatformModuleHostExtensions
{
    /// <summary>
    /// 集中装配：校验 → 登记 → 逐模块注册服务。
    /// </summary>
    public static PlatformModuleRegistry AddPlatformModules(
        this IServiceCollection services,
        IEnumerable<IPlatformModule> modules,
        IConfiguration configuration)
    {
        var registry = new PlatformModuleRegistry(modules);
        registry.ValidatePermissions();
        registry.AddTo(services);
        registry.RegisterServices(services, configuration);
        return registry;
    }

    /// <summary>集中挂载端点：在管道装配阶段调用一次。</summary>
    public static WebApplication MapPlatformModules(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var registry = (PlatformModuleRegistry?)app.Services.GetService(typeof(PlatformModuleRegistry))
                       ?? throw new InvalidOperationException("未注册 PlatformModuleRegistry，请先调用 AddPlatformModules。");
        registry.MapEndpoints(app);
        return app;
    }
}
