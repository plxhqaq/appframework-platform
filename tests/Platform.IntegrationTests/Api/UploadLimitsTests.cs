using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.IntegrationTests.Support;
using Platform.Kernel.Http;

namespace Platform.IntegrationTests.Api;

/// <summary>
/// 上传/备份大小上限对齐：<see cref="FormOptions"/>、端点 <c>RequestSizeLimit</c> 与版本票据阈值
/// 都必须跟随配置，不能在业务校验之前被宿主默认值（128MB / 30MB）提前拒绝。
/// </summary>
public sealed class UploadLimitsTests : IClassFixture<PlatformApiFactory>
{
    private const long OneMiB = 1024 * 1024;

    private readonly PlatformApiFactory _factory;

    public UploadLimitsTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public void 表单与端点上限随配置对齐()
    {
        const long documentLimit = 1 * OneMiB;
        const long backupLimit = 2 * OneMiB;

        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Documents:MaxFileSizeBytes"] = documentLimit.ToString(CultureInfo.InvariantCulture),
                    ["Backups:MaxPayloadBytes"] = backupLimit.ToString(CultureInfo.InvariantCulture),
                })));

        var formOptions = factory.Services.GetRequiredService<IOptions<FormOptions>>().Value;
        Assert.Equal(
            documentLimit + RequestLimits.MultipartOverheadBytes,
            formOptions.MultipartBodyLengthLimit);

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>();
        var documentRequestLimit = documentLimit + RequestLimits.MultipartOverheadBytes;
        Assert.Equal(documentRequestLimit, RequestLimitOf(endpoints, "documents.create"));
        Assert.Equal(documentRequestLimit, RequestLimitOf(endpoints, "documents.revisions.add"));
        Assert.Equal(
            backupLimit + RequestLimits.JsonOverheadBytes,
            RequestLimitOf(endpoints, "backups.create"));
    }

    [Fact]
    public async Task 版本票据按文件与包上限的较大值放行()
    {
        const long fileLimit = 1 * OneMiB;
        const long packageLimit = 4 * OneMiB;

        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Versions:MaxFileSizeBytes"] = fileLimit.ToString(CultureInfo.InvariantCulture),
                    ["Versions:MaxPackageSizeBytes"] = packageLimit.ToString(CultureInfo.InvariantCulture),
                })));

        using var client = factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var sha = new string('a', 64);

        // 介于单文件与整包上限之间：按整包口径允许，细粒度校验留给发布清单。
        var accepted = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/uploads",
            token,
            new CreateUploadTicketRequest(sha, packageLimit - 1)));
        var ticket = await IdentityTestHelpers.ReadDataAsync<UploadTicketDto>(accepted);
        Assert.NotEqual(Guid.Empty, ticket.Id);

        var rejected = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/uploads",
            token,
            new CreateUploadTicketRequest(sha, packageLimit + 1)));
        Assert.Equal(
            (int)ApiErrorCode.PayloadTooLarge,
            await IdentityTestHelpers.ReadErrorCodeAsync(rejected, HttpStatusCode.RequestEntityTooLarge));
    }

    private static long RequestLimitOf(EndpointDataSource endpoints, string endpointName) =>
        endpoints.Endpoints
            .Single(endpoint => string.Equals(
                endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
                endpointName,
                StringComparison.Ordinal))
            .Metadata.GetMetadata<IRequestSizeLimitMetadata>()!
            .MaxRequestBodySize!.Value;
}
