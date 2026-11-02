using Platform.Api.Modules;
using Platform.Contracts;
using Platform.Contracts.Permissions;
using Platform.Kernel.Modules;

namespace Platform.UnitTests.Api;

/// <summary>集中模块清单：新增/删除模块时最容易漏改的地方，这里兜住。</summary>
public sealed class PlatformModuleCatalogTests
{
    [Fact]
    public void 清单必须恰好覆盖全部模块键()
    {
        var declared = PlatformModuleCatalog.Modules.Select(static m => m.Key).ToList();

        Assert.Equal(PlatformModuleKeys.All.Count, declared.Count);
        Assert.Empty(PlatformModuleKeys.All.Except(declared));
        Assert.Empty(declared.Except(PlatformModuleKeys.All));
    }

    [Fact]
    public void 清单必须能通过启动期校验()
    {
        var registry = new PlatformModuleRegistry(PlatformModuleCatalog.Modules);

        registry.ValidatePermissions();
        Assert.Equal(PlatformModuleKeys.All.Count, registry.Describe().Count);
    }

    [Fact]
    public void 每个模块都必须声明显示名与权限()
    {
        foreach (var module in PlatformModuleCatalog.Modules)
        {
            Assert.StartsWith("module.", module.DisplayNameKey, StringComparison.Ordinal);
            Assert.NotEmpty(module.RequiredPermissions);
        }
    }

    [Fact]
    public void 模块声明的权限码必须归属本模块段位()
    {
        foreach (var module in PlatformModuleCatalog.Modules)
        {
            foreach (var code in module.RequiredPermissions)
            {
                var descriptor = PermissionCatalog.All.SingleOrDefault(p => p.Code == code);
                Assert.NotNull(descriptor);
                Assert.Equal(module.Key, descriptor.ModuleKey);
                Assert.StartsWith($"{PermissionDomainOf(module.Key)}.", descriptor.Code, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void 应用注册模块必须排在首位()
    {
        Assert.Equal(PlatformModuleKeys.Apps, PlatformModuleCatalog.Modules[0].Key);
    }

    /// <summary>
    /// 权限码域与模块键并非总是同形：授权域用 <c>license</c>（语义比 licensing 更贴合授权实例，
    /// 与现网 <c>license.issue</c> 口径一致），备份/文档域用单数。
    /// </summary>
    private static string PermissionDomainOf(string moduleKey) => moduleKey switch
    {
        PlatformModuleKeys.Licensing => "license",
        PlatformModuleKeys.Backups => "backup",
        PlatformModuleKeys.Documents => "document",
        PlatformModuleKeys.Webhooks => "webhook",
        _ => moduleKey,
    };
}
