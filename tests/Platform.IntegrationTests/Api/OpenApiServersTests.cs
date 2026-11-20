using System.Text.Json;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// OpenAPI 服务器地址必须是相对值：绝对地址取自取文档那次请求的 Host，
/// 反向代理/changeOrigin 下会把上游端口（如 8080）泄给浏览器，Scalar「Try it」随之打错端口。
/// </summary>
public sealed class OpenApiServersTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public OpenApiServersTests(PlatformApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost:18888/")]
    [InlineData("https://platform.example.com/")]
    public async Task 开放文档的服务器地址为相对值不随上游Host漂移(string origin)
    {
        using var client = _factory.CreateClient();
        client.BaseAddress = new Uri(origin);

        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var servers = document.RootElement.GetProperty("servers").EnumerateArray().ToList();
        var server = Assert.Single(servers);
        Assert.Equal("/", server.GetProperty("url").GetString());
    }
}