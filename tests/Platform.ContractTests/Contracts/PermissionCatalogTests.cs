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
            .Append("client")
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
    public void 客户端权限映射必须覆盖全部PermissionType成员()
    {
        var expected = ClientPermissionCatalog.ExpectedPermissionTypeNames.ToHashSet(StringComparer.Ordinal);
        var mapped = ClientPermissionCatalog.TypeToCode.Keys.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected.Count, ClientPermissionCatalog.ExpectedPermissionTypeNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(expected.Except(mapped));
        Assert.Empty(mapped.Except(expected));
    }

    [Fact]
    public void 客户端映射的目标权限码必须在目录中登记()
    {
        foreach (var (typeName, code) in ClientPermissionCatalog.TypeToCode)
        {
            Assert.True(PermissionCatalog.IsKnown(code), $"{typeName} 映射到未登记权限码 {code}。");
        }
    }

    [Fact]
    public void 反向索引与正向映射一致()
    {
        var totalFromReverse = ClientPermissionCatalog.CodeToTypes.Sum(static kv => kv.Value.Count);
        Assert.Equal(ClientPermissionCatalog.TypeToCode.Count, totalFromReverse);

        foreach (var (typeName, code) in ClientPermissionCatalog.TypeToCode)
        {
            Assert.Contains(typeName, ClientPermissionCatalog.CodeToTypes[code]);
        }
    }

    [Fact]
    public void 客户端权限判定识别通配符与具体码()
    {
        IReadOnlySet<string> wildcard = new HashSet<string>([PermissionCatalog.Wildcard], StringComparer.Ordinal);
        IReadOnlySet<string> one = new HashSet<string>([ClientPermissionCatalog.PageDocuments], StringComparer.Ordinal);
        IReadOnlySet<string> none = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(ClientPermissionCatalog.Allows(wildcard, "SaveButton"));
        Assert.True(ClientPermissionCatalog.Allows(one, "ViewDocuments"));
        Assert.False(ClientPermissionCatalog.Allows(one, "ViewAbout"));
        Assert.False(ClientPermissionCatalog.Allows(none, "ViewDocuments"));
        Assert.False(ClientPermissionCatalog.Allows(one, "NotARealPermission"));
    }
}
