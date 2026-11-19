using System.IO.Compression;
using System.Text;
using Platform.Contracts;
using Platform.Modules.Versions.Options;

namespace Platform.Modules.Versions.Packages;

/// <summary>
/// zip 包清单探测：优先 <c>af-package.json</c>；回退老格式 <c>build-info.json</c> + <c>manifest.json</c>，
/// 最后 <c>updater-info.json</c>。读清单前先过 <see cref="PackageArchiveSafety"/>，坏包一个文件都不落。
/// </summary>
public static class ZipPackageManifestProbe
{
    /// <summary>新清单文件（zip 根或 <c>app/</c> 前缀下，大小写不敏感）。</summary>
    public const string ManifestEntryName = "af-package.json";

    /// <summary>老增量包的构建信息。</summary>
    public const string LegacyBuildInfoEntryName = "build-info.json";

    /// <summary>老增量包的全文件清单。</summary>
    public const string LegacyManifestEntryName = "manifest.json";

    /// <summary>更新器分发包的元数据。</summary>
    public const string UpdaterInfoEntryName = "updater-info.json";

    /// <summary>从可随机读取的 zip 流探测清单。</summary>
    public static PackageProbeResult TryRead(Stream stream, VersionsOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        if (!stream.CanSeek)
        {
            return PackageProbeResult.Fail(
                ApiErrorCode.VersionsPackageManifestInvalid,
                "zip 流不可随机读取，无法解析清单。");
        }

        stream.Position = 0;
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var safety = PackageArchiveSafety.Validate(archive, options);
            if (!safety.IsSafe)
            {
                return PackageProbeResult.Fail(
                    safety.ErrorCode ?? ApiErrorCode.VersionsPackageUnsafeArchive,
                    safety.ErrorMessage ?? "zip 安全校验未通过。");
            }

            var manifestEntry = FindEntry(archive, ManifestEntryName);
            if (manifestEntry is not null)
            {
                return ReadNewManifest(manifestEntry);
            }

            var buildInfoEntry = FindEntry(archive, LegacyBuildInfoEntryName);
            var legacyManifestEntry = FindEntry(archive, LegacyManifestEntryName);
            if (buildInfoEntry is not null || legacyManifestEntry is not null)
            {
                return ReadLegacyUpdatePackage(buildInfoEntry, legacyManifestEntry);
            }

            var updaterInfoEntry = FindEntry(archive, UpdaterInfoEntryName);
            if (updaterInfoEntry is not null)
            {
                return ReadText(updaterInfoEntry, out var json, out var error)
                    ? LegacyManifestAdapter.TryMapUpdaterInfo(json)
                    : PackageProbeResult.Fail(ApiErrorCode.VersionsPackageManifestInvalid, error!);
            }

            return PackageProbeResult.Missing(
                $"zip 内没有 {ManifestEntryName}（也未见老格式 {LegacyBuildInfoEntryName}/{LegacyManifestEntryName}/{UpdaterInfoEntryName}）。");
        }
        catch (InvalidDataException exception)
        {
            return PackageProbeResult.Fail(
                ApiErrorCode.VersionsPackageManifestInvalid,
                $"zip 结构损坏：{exception.Message}");
        }
        catch (IOException exception)
        {
            return PackageProbeResult.Fail(
                ApiErrorCode.VersionsPackageManifestInvalid,
                $"读取 zip 失败：{exception.Message}");
        }
    }

    /// <summary>按逻辑名定位 zip 条目（归一化后比较，兼容 <c>app/</c> 前缀与大小写）。</summary>
    public static ZipArchiveEntry? FindEntry(ZipArchive archive, string logicalName)
    {
        var wanted = PackagePaths.Normalize(logicalName);
        if (wanted is null)
        {
            return null;
        }

        foreach (var entry in archive.Entries)
        {
            if (PackagePaths.Normalize(entry.FullName) is { } normalized
                && string.Equals(normalized, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static PackageProbeResult ReadNewManifest(ZipArchiveEntry entry)
    {
        if (!ReadText(entry, out var json, out var error))
        {
            return PackageProbeResult.Fail(ApiErrorCode.VersionsPackageManifestInvalid, error!);
        }

        return PackageManifest.TryParseJson(
            json,
            Platform.Contracts.Versions.VersionConventions.ManifestSourceZip,
            out var manifest,
            out error)
            ? PackageProbeResult.Ok(manifest!)
            : PackageProbeResult.Fail(ApiErrorCode.VersionsPackageManifestInvalid, error!);
    }

    private static PackageProbeResult ReadLegacyUpdatePackage(
        ZipArchiveEntry? buildInfoEntry,
        ZipArchiveEntry? manifestEntry)
    {
        string? buildInfoJson = null;
        if (buildInfoEntry is not null
            && !ReadText(buildInfoEntry, out buildInfoJson, out var buildInfoError))
        {
            return PackageProbeResult.Fail(ApiErrorCode.VersionsPackageManifestInvalid, buildInfoError!);
        }

        string? manifestJson = null;
        if (manifestEntry is not null
            && !ReadText(manifestEntry, out manifestJson, out var manifestError))
        {
            return PackageProbeResult.Fail(ApiErrorCode.VersionsPackageManifestInvalid, manifestError!);
        }

        return LegacyManifestAdapter.TryMapUpdatePackage(buildInfoJson, manifestJson);
    }

    internal static bool ReadText(ZipArchiveEntry entry, out string? text, out string? error)
    {
        text = null;
        error = null;
        if (entry.Length > PackageManifest.MaxBytes)
        {
            error = $"{entry.FullName} 超过 {PackageManifest.MaxBytes} 字节上限";
            return false;
        }

        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var builder = new StringBuilder((int)Math.Min(entry.Length, PackageManifest.MaxBytes));
            var buffer = new char[4096];
            int read;
            long total = 0;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > PackageManifest.MaxBytes)
                {
                    error = $"{entry.FullName} 超过 {PackageManifest.MaxBytes} 字节上限";
                    return false;
                }

                builder.Append(buffer, 0, read);
            }

            text = builder.ToString();
            return true;
        }
        catch (InvalidDataException exception)
        {
            error = $"{entry.FullName} 解压失败：{exception.Message}";
            return false;
        }
        catch (IOException exception)
        {
            error = $"{entry.FullName} 读取失败：{exception.Message}";
            return false;
        }
    }
}
