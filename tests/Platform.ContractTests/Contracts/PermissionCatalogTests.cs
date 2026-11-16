using System.Text.RegularExpressions;
using Platform.Contracts;
using Platform.Contracts.Permissions;

namespace Platform.ContractTests.Contracts;

/// <summary>权限码契约：唯一、格式稳定、映射完整。权限码是跨端真相，改名等于破坏性变更。</summary>
public sealed partial class PermissionCatalogTests
{
    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9]+)+$")]
    private static partial Regex CodePattern();

    [Fact]
    public void 权限码不得重复()
    {
        var duplicated = PermissionCatalog.All
            .GroupBy(static p => p.Code, StringComparer.Ordinal)
            .Where(static g => g.Count() > 1)
            .Select(static g => g.Key)
            .ToList();

        Assert.Empty(duplicated);
    }

    [Fact]
    public void 权限码必须是小写点分格式()
    {
        foreach (var descriptor in PermissionCatalog.All)
        {
            Assert.True(
                CodePattern().IsMatch(descriptor.Code),
                $"权限码 '{descriptor.Code}' 不符合 {{域}}.{{资源}}.{{动作}} 小写点分格式。");
        }
    }

    [Fact]
    public void 权限码的显示名键不得重复()
    {
        var duplicated = PermissionCatalog.All
            .GroupBy(static p => p.DisplayNameKey, StringComparer.Ordinal)
            .Where(static g => g.Count() > 1)
            .Select(static g => g.Key)
            .ToList();

        Assert.Empty(duplicated);
    }

    [Fact]
    public void 权限必须归属已知模块()
    {
        var known = PlatformModuleKeys.All
            .Append("platform")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var descriptor in PermissionCatalog.All)
        {
            Assert.Contains(descriptor.ModuleKey, known);
        }
    }

    [Fact]
    public void 每个模块都至少声明一个权限()
    {
        foreach (var moduleKey in PlatformModuleKeys.All)
        {
            Assert.NotEmpty(PermissionCatalog.ForModule(moduleKey));
        }
    }

    [Fact]
    public void 通配符视为已知且能展开为全部权限()
    {
        Assert.True(PermissionCatalog.IsKnown(PermissionCatalog.Wildcard));

        var expanded = PermissionCatalog.Expand([PermissionCatalog.Wildcard]);
        Assert.Equal(PermissionCatalog.Codes.Count, expanded.Count);
    }

    [Fact]
    public void 未知权限码在展开时被丢弃()
    {
        var expanded = PermissionCatalog.Expand(["identity.user.read", "no.such.code"]);
        Assert.Equal(["identity.user.read"], expanded);
    }

    [Fact]
    public void 按模块查询结果按排序值升序()
    {
        var versions = PermissionCatalog.ForModule(PlatformModuleKeys.Versions);
        var sorts = versions.Select(static p => p.Sort).ToList();
        Assert.Equal(sorts.OrderBy(static s => s), sorts);
    }

    [Fact]
    public void 平台目录不包含任何客户端专属权限()
    {
        Assert.DoesNotContain(PermissionCatalog.All, static p => p.ModuleKey == "client");
        Assert.All(PermissionCatalog.All, static p => Assert.False(p.Code.StartsWith("client.", StringComparison.Ordinal)));
    }
}
