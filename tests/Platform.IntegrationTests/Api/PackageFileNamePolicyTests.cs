using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>文件名模板严格模式下不匹配即拒绝；默认模式只告警（由其余用例覆盖）。</summary>
public sealed class PackageFileNamePolicyTests : IClassFixture<PackageFileNamePolicyTests.StrictFactory>
{
    private readonly StrictFactory _factory;

    public PackageFileNamePolicyTests(StrictFactory factory)
    {
        _factory = factory;
    }

    public sealed class StrictFactory : PlatformApiFactory
    {
        protected override void ConfigureTestSettings(IDictionary<string, string?> settings) =>
            settings["Versions:RequirePackageFileNameMatch"] = "true";
    }

    [Fact]
    public async Task 文件名不匹配模板被拒绝而匹配模板可登记()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        const string channelKey = "pkg-strict";

        _ = await IdentityTestHelpers.ReadDataAsync<ReleaseChannelDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/channels",
                token,
                new CreateReleaseChannelRequest(channelKey, "严格文件名", null))));

        var packageBytes = BuildZip(("af-package.json", FullManifest(channelKey, "1.0.0")));
        var sha = await UploadAsync(client, token, packageBytes);

        var mismatched = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Post,
            "/api/v1/admin/versions/packages",
            token,
            new RegisterReleasePackageRequest(
                sha,
                packageBytes.Length,
                "random-package.zip",
                VersionConventions.PackageKindFull,
                Target: "win-x64",
                Format: "zip")));
        Assert.Equal(
            (int)ApiErrorCode.VersionsPackageFileNameMismatch,
            await IdentityTestHelpers.ReadErrorCodeAsync(
                mismatched, HttpStatusCode.UnprocessableEntity));

        var registered = await IdentityTestHelpers.ReadDataAsync<ReleasePackageDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/packages",
                token,
                new RegisterReleasePackageRequest(
                    sha,
                    packageBytes.Length,
                    "Setup-pkg-strict-1.0.0.zip",
                    VersionConventions.PackageKindFull,
                    Target: "win-x64",
                    Format: "zip"))));
        Assert.Equal(VersionConventions.PackageStatusReady, registered.Status);
        Assert.Equal("Setup-pkg-strict-1.0.0.zip", registered.FileName);
    }

    private static string FullManifest(string channelKey, string version) => $$"""
        {
          "schema": "af-package/1",
          "kind": "full",
          "channelKey": "{{channelKey}}",
          "version": "{{version}}",
          "runtimeIdentifier": "win-x64",
          "entryPoint": "App.exe",
          "files": []
        }
        """;

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static async Task<string> UploadAsync(HttpClient client, string token, byte[] content)
    {
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var ticket = await IdentityTestHelpers.ReadDataAsync<UploadTicketDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/uploads",
                token,
                new CreateUploadTicketRequest(sha, content.Length))));

        using var request = new HttpRequestMessage(HttpMethod.Put, ticket.UploadUrl)
        {
            Content = new ByteArrayContent(content),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        _ = await IdentityTestHelpers.ReadDataAsync<UploadResultDto>(await client.SendAsync(request));
        return sha;
    }
}
