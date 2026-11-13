using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Modules.Versions.Options;

namespace Platform.Modules.Versions.Packages;

/// <summary>清单探测输入：包 Blob 的打开方式（已落盘，可重复 seek）与可选 sidecar。</summary>
public sealed record PackageProbeRequest(
    string AppKey,
    string FileName,
    string Format,
    long SizeBytes,
    Func<Stream> OpenPackage,
    Func<Stream>? OpenSidecar = null);

/// <summary>
/// 清单探测结果。<see cref="ErrorCode"/> 取 <see cref="ApiErrorCode"/> 的版本段错误码：
/// 缺清单（<c>VersionsPackageManifestMissing</c>）时由登记服务决定是否允许人工登记（仅 full 包）。
/// </summary>
public sealed record PackageProbeResult(
    PackageManifest? Manifest,
    ApiErrorCode? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<string> Warnings)
{
    /// <summary>是否成功拿到清单。</summary>
    public bool Success => Manifest is not null;

    /// <summary>成功结果（warning 取自清单）。</summary>
    public static PackageProbeResult Ok(PackageManifest manifest) =>
        new(manifest, null, null, manifest.Warnings);

    /// <summary>失败结果。</summary>
    public static PackageProbeResult Fail(ApiErrorCode errorCode, string errorMessage) =>
        new(null, errorCode, errorMessage, []);

    /// <summary>包内无可用清单。</summary>
    public static PackageProbeResult Missing(string message) =>
        Fail(ApiErrorCode.VersionsPackageManifestMissing, message);
}

/// <summary>包清单探测器：sidecar → 包内嵌元数据（zip JSON / PE VERSIONINFO）。</summary>
public interface IPackageManifestProbe
{
    /// <summary>读取并规范化清单；不修改包内容。</summary>
    PackageProbeResult Probe(PackageProbeRequest request);
}

/// <summary>
/// 按 §2.3.4 的顺序探测：请求带 sidecar 时以 sidecar 为准；否则 zip 读内嵌 JSON，其他格式读 PE Comments。
/// <para>
/// 所有读取都有界：sidecar / PE 资源 / 清单 JSON 均不超过 <see cref="PackageManifest.MaxBytes"/>，
/// zip 先过 <see cref="PackageArchiveSafety"/> 再读条目。解析异常一律转成「无清单/清单非法」，不让坏包打断上传。
/// </para>
/// </summary>
public sealed class PackageManifestProbe : IPackageManifestProbe
{
    private readonly VersionsOptions _options;

    /// <summary>构造。</summary>
    public PackageManifestProbe(IOptions<VersionsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public PackageProbeResult Probe(PackageProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.OpenSidecar is not null)
        {
            using var sidecar = request.OpenSidecar();
            return SidecarPackageManifestProbe.TryRead(sidecar);
        }

        using var package = request.OpenPackage();
        return NormalizeFormat(request.Format) switch
        {
            "zip" => ZipPackageManifestProbe.TryRead(package, _options),
            "exe" or "msi" or "msp" or "bin" or "7z" => PePackageManifestProbe.TryRead(package),
            _ => PackageProbeResult.Missing($"格式 {request.Format} 不支持自动识别，可人工登记 full 包。"),
        };
    }

    private static string NormalizeFormat(string? format) =>
        format?.Trim().TrimStart('.').ToLowerInvariant() ?? string.Empty;
}
