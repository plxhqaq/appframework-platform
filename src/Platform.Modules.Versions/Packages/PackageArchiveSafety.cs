using System.IO.Compression;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Modules.Versions.Options;

namespace Platform.Modules.Versions.Packages;

/// <summary>zip 安全校验结果；不安全时带可直接返回给管理员的错误码与原因。</summary>
public sealed record PackageArchiveSafetyResult(bool IsSafe, ApiErrorCode? ErrorCode, string? ErrorMessage)
{
    /// <summary>安全。</summary>
    public static PackageArchiveSafetyResult Safe { get; } = new(true, null, null);

    /// <summary>不安全（<c>VersionsPackageUnsafeArchive</c>）。</summary>
    public static PackageArchiveSafetyResult Unsafe(string message) =>
        new(false, ApiErrorCode.VersionsPackageUnsafeArchive, message);
}

/// <summary>
/// zip 归档安全边界（§2.3.4）：条目数、单条目/解压总量、压缩比、加密、符号链接、绝对路径/穿越/重复路径。
/// 只读中央目录，不解压内容；任何越界都拒绝，不落任何文件。
/// </summary>
public static class PackageArchiveSafety
{
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymbolicLink = 0xA000;

    /// <summary>校验 zip 归档；<paramref name="options"/> 提供条目数/总量/压缩比上限。</summary>
    public static PackageArchiveSafetyResult Validate(ZipArchive archive, VersionsOptions options)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(options);

        var entries = archive.Entries;
        if (entries.Count > options.MaxZipEntries)
        {
            return PackageArchiveSafetyResult.Unsafe(
                $"zip 条目数 {entries.Count} 超过上限 {options.MaxZipEntries}。");
        }

        long totalUncompressed = 0;
        long totalCompressed = 0;
        var fileCount = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry.IsEncrypted)
            {
                return PackageArchiveSafetyResult.Unsafe($"zip 含加密条目：{entry.FullName}。");
            }

            if (IsSymbolicLink(entry))
            {
                return PackageArchiveSafetyResult.Unsafe($"zip 含符号链接条目：{entry.FullName}。");
            }

            if (IsDirectory(entry.FullName))
            {
                continue;
            }

            var path = PackagePaths.Normalize(entry.FullName);
            if (path is null)
            {
                return PackageArchiveSafetyResult.Unsafe($"zip 条目路径非法：{entry.FullName}。");
            }

            if (!paths.Add(path))
            {
                return PackageArchiveSafetyResult.Unsafe($"zip 条目路径重复（大小写不敏感）：{path}。");
            }

            if (entry.Length < 0 || entry.Length > VersionConventions.MaxFileSizeBytes)
            {
                return PackageArchiveSafetyResult.Unsafe(
                    $"zip 条目 {path} 大小 {entry.Length} 超出单文件上限 {VersionConventions.MaxFileSizeBytes}。");
            }

            if (totalUncompressed > long.MaxValue - entry.Length)
            {
                return PackageArchiveSafetyResult.Unsafe("zip 解压总量溢出。");
            }

            totalUncompressed += entry.Length;
            totalCompressed += Math.Max(entry.CompressedLength, 0);
            fileCount++;
        }

        if (fileCount == 0)
        {
            return PackageArchiveSafetyResult.Unsafe("zip 不含任何文件条目。");
        }

        if (totalUncompressed > options.MaxExtractedBytesPerPackage)
        {
            return PackageArchiveSafetyResult.Unsafe(
                $"zip 解压总量 {totalUncompressed} 超过上限 {options.MaxExtractedBytesPerPackage}。");
        }

        if (totalCompressed > 0
            && (double)totalUncompressed / totalCompressed > options.MaxCompressionRatio)
        {
            return PackageArchiveSafetyResult.Unsafe(
                $"zip 压缩比 {(double)totalUncompressed / totalCompressed:F1} 超过上限 {options.MaxCompressionRatio}。");
        }

        return PackageArchiveSafetyResult.Safe;
    }

    private static bool IsDirectory(string name) =>
        name.EndsWith('/') || name.EndsWith('\\');

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        var mode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        return mode == UnixSymbolicLink;
    }
}
