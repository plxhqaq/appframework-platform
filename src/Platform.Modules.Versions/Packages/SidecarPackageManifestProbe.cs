using System.Text;
using Platform.Contracts;
using Platform.Contracts.Versions;

namespace Platform.Modules.Versions.Packages;

/// <summary>sidecar 清单探测：请求带 <c>manifestSha256</c> 时，以该 Blob 的 JSON 为准（优先于包内清单）。</summary>
public static class SidecarPackageManifestProbe
{
    /// <summary>从可随机读取的 sidecar 流解析 <c>af-package/1</c> JSON。</summary>
    public static PackageProbeResult TryRead(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            return Invalid("sidecar 清单不可随机读取。");
        }

        if (stream.Length > PackageManifest.MaxBytes)
        {
            return Invalid($"sidecar 清单超过 {PackageManifest.MaxBytes} 字节上限。");
        }

        try
        {
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var json = reader.ReadToEnd();
            return PackageManifest.TryParseJson(
                json,
                VersionConventions.ManifestSourceSidecar,
                out var manifest,
                out var error)
                ? PackageProbeResult.Ok(manifest!)
                : Invalid(error!);
        }
        catch (IOException exception)
        {
            return Invalid($"读取 sidecar 清单失败：{exception.Message}");
        }
    }

    private static PackageProbeResult Invalid(string message) =>
        PackageProbeResult.Fail(ApiErrorCode.VersionsPackageManifestInvalid, message);
}
