using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Contracts;
using Platform.Kernel.Modules;

namespace Platform.UnitTests.Kernel;

/// <summary>模块注册表：启动期校验必须在错误配置下立刻失败。</summary>
public sealed class PlatformModuleRegistryTests
{
    [Fact]
    public void 模块键重复时构造失败()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new PlatformModuleRegistry(
            [new FakeModule("identity"), new FakeModule("identity")]));

        Assert.Contains("模块键重复", exception.Message);
    }

    [Fact]
    public void 模块键为空时构造失败()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new PlatformModuleRegistry([new FakeModule(" ")]));

        Assert.Contains("未声明 Key", exception.Message);
    }

    [Fact]
    public void 声明未登记权限码时校验失败()
    {
        var registry = new PlatformModuleRegistry([new FakeModule("identity", "no.such.permission")]);

        var exception = Assert.Throws<InvalidOperationException>(registry.ValidatePermissions);
        Assert.Contains("no.such.permission", exception.Message);
    }

    [Fact]
    public void 合法权限码通过校验()
    {
        var registry = new PlatformModuleRegistry([new FakeModule("identity", "identity.user.read")]);
        registry.ValidatePermissions();
    }

    [Fact]
    public void 按键查找模块()
    {
        var identity = new FakeModule("identity");
        var registry = new PlatformModuleRegistry([identity, new FakeModule("versions")]);

        Assert.Same(identity, registry.Find("identity"));
        Assert.Null(registry.Find("documents"));
        Assert.Equal(2, registry.Keys.Count);
    }

    [Fact]
    public void 自述信息包含键路由与版本()
    {
        var module = new FakeModule("versions", "versions.release.read");
        var info = PlatformModuleRegistry.Describe(module);

        Assert.Equal("versions", info.Key);
        Assert.Equal("module.versions", info.DisplayNameKey);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.Equal(["/api/v1/versions"], info.Routes);
        Assert.Equal(["versions.release.read"], info.RequiredPermissions);
    }

    [Fact]
    public void 装配时逐模块调用服务注册()
    {
        var first = new FakeModule("apps");
        var second = new FakeModule("identity");
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var registry = services.AddPlatformModules([first, second], configuration);

        Assert.True(first.RegisterServicesCalled);
        Assert.True(second.RegisterServicesCalled);
        Assert.Equal(2, registry.Modules.Count);
        Assert.NotNull(services.BuildServiceProvider().GetService<PlatformModuleRegistry>());
    }

    private sealed class FakeModule : IPlatformModule
    {
        public FakeModule(string key, params string[] permissions)
        {
            Key = key;
            RequiredPermissions = permissions;
        }

        public string Key { get; }

        public string DisplayNameKey => $"module.{Key.Trim()}";

        public IReadOnlyList<string> RequiredPermissions { get; }

        public bool RegisterServicesCalled { get; private set; }

        public void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
            RegisterServicesCalled = true;

        public void MapEndpoints(IEndpointRouteBuilder api)
        {
            // 测试不真正挂载端点
        }
    }
}
