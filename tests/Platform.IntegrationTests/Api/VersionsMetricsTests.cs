using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.IntegrationTests.Support;

namespace Platform.IntegrationTests.Api;

/// <summary>版本域关键动作（探测、登记、检查、下载）都要留下可告警的低基数指标。</summary>
public sealed class VersionsMetricsTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public VersionsMetricsTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task 探测登记检查与下载都会写出指标()
    {
        using var client = _factory.CreateClient();
        var token = await IdentityTestHelpers.LoginAsAdminAsync(client);
        var channelKey = "mt" + Guid.NewGuid().ToString("N")[..8];

        _ = await IdentityTestHelpers.ReadDataAsync<ReleaseChannelDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/channels",
                token,
                new CreateReleaseChannelRequest(channelKey, "指标用例", null))));

        var packageBytes = BuildZip(("af-package.json", FullManifest(channelKey, "1.0.0")));
        var sha = await UploadAsync(client, token, packageBytes);

        var registered = await IdentityTestHelpers.ReadDataAsync<ReleasePackageDto>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/admin/versions/packages",
                token,
                new RegisterReleasePackageRequest(
                    sha,
                    packageBytes.Length,
                    $"Setup-{channelKey}-1.0.0.zip",
                    VersionConventions.PackageKindFull,
                    Target: "win-x64",
                    Format: "zip",
                    Publish: true))));
        Assert.Equal(VersionConventions.PackageStatusReady, registered.Status);

        var check = await IdentityTestHelpers.ReadDataAsync<CheckUpdateResponse>(
            await client.SendAsync(IdentityTestHelpers.Authorized(
                HttpMethod.Post,
                "/api/v1/client/versions/check",
                token,
                new CheckUpdateRequest(channelKey, "0.9.0", "win-x64"))));
        Assert.True(check.HasUpdate);

        using var download = await client.SendAsync(IdentityTestHelpers.Authorized(
            HttpMethod.Get,
            $"/api/v1/client/versions/packages/{registered.Id}/download",
            token));
        Assert.True(download.IsSuccessStatusCode);

        var metrics = await (await client.GetAsync("/metrics")).Content.ReadAsStringAsync();
        Assert.Contains("versions_package_probe_total{format=\"zip\",result=\"success\"}", metrics, StringComparison.Ordinal);
        Assert.Contains("versions_package_register_total{kind=\"full\",result=\"created\"}", metrics, StringComparison.Ordinal);
        Assert.Contains("versions_package_bytes_total{kind=\"full\"}", metrics, StringComparison.Ordinal);
        Assert.Contains("versions_check_total{result=\"update\"}", metrics, StringComparison.Ordinal);
        Assert.Contains("versions_package_download_bytes_total{kind=\"package\"}", metrics, StringComparison.Ordinal);
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
