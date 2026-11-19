using System.IO.Compression;
using System.Security.Cryptography;
using Platform.Modules.Versions.Options;

namespace Platform.Modules.Versions.Packages;

/// <summary>媒体包探测结果。</summary>
/// <param name="Success">是否通过。</param>
/// <param name="Manifest">规范化清单（仅通过时有值）。</param>
/// <param name="ErrorMessage">失败原因。</param>
public sealed record MediaProbeResult(bool Success, MediaManifest? Manifest, string? ErrorMessage)
{
    /// <summary>探测成功。</summary>
    public static MediaProbeResult Ok(MediaManifest manifest) => new(true, manifest, null);

    /// <summary>探测失败。</summary>
    public static MediaProbeResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// 驱动媒体 zip 探测：先过 <see cref="PackageArchiveSafety"/>，再读根内 <c>af-media.json</c>，
/// 并逐文件核对「清单 ↔ zip 实际内容」双向一致（含每个文件内容 sha256 流式复核）。
/// 探测不通过的包一个字节都不落库。
/// </summary>
public static class ZipMediaManifestProbe
{
    /// <summary>从可随机读取的 zip 流探测并复核媒体清单。</summary>
    public static MediaProbeResult TryRead(Stream stream, VersionsOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        if (!stream.CanSeek)
        {
            return MediaProbeResult.Fail("zip 流不可随机读取，无法解析媒体清单。");
        }

        stream.Position = 0;
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var safety = PackageArchiveSafety.Validate(archive, options);
            if (!safety.IsSafe)
            {
                return MediaProbeResult.Fail(safety.ErrorMessage ?? "zip 安全校验未通过。");
            }

            var manifestEntry = ZipPackageManifestProbe.FindEntry(archive, MediaManifest.EntryName);
            if (manifestEntry is null)
            {
                return MediaProbeResult.Fail($"zip 内没有 {MediaManifest.EntryName}，不是媒体包。");
            }

            if (!ZipPackageManifestProbe.ReadText(manifestEntry, out var json, out var readError))
            {
                return MediaProbeResult.Fail(readError!);
            }

            if (!MediaManifest.TryParseJson(json!, out var manifest, out var parseError))
            {
                return MediaProbeResult.Fail(parseError!);
            }

            return VerifyAgainstArchive(archive, manifest!);
        }
        catch (InvalidDataException exception)
        {
            return MediaProbeResult.Fail($"zip 结构损坏：{exception.Message}");
        }
        catch (IOException exception)
        {
            return MediaProbeResult.Fail($"读取 zip 失败：{exception.Message}");
        }
    }

    /// <summary>双向核对清单与 zip 条目，并逐文件流式复核大小与 sha256。</summary>
    private static MediaProbeResult VerifyAgainstArchive(ZipArchive archive, MediaManifest manifest)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            // 目录条目（名为空）不承载内容，允许存在但不参与核对。
            if (entry.Name.Length == 0)
            {
                continue;
            }

            if (PackagePaths.Normalize(entry.FullName) is not { } normalized)
            {
                return MediaProbeResult.Fail($"zip 条目路径非法：{entry.FullName}");
            }

            if (string.Equals(normalized, MediaManifest.EntryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entries.ContainsKey(normalized))
            {
                return MediaProbeResult.Fail($"zip 条目路径重复（大小写碰撞）：{entry.FullName}");
            }

            entries[normalized] = entry;
        }

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            declared.Add(file.Path);
            if (!entries.ContainsKey(file.Path))
            {
                return MediaProbeResult.Fail($"清单声明的文件不在 zip 内：{file.Path}");
            }
        }

        if (entries.Count != declared.Count)
        {
            foreach (var path in entries.Keys)
            {
                if (!declared.Contains(path))
                {
                    return MediaProbeResult.Fail($"zip 内存在清单未声明的文件：{path}");
                }
            }
        }

        foreach (var file in manifest.Files)
        {
            var entry = entries[file.Path];
            if (entry.Length != file.SizeBytes)
            {
                return MediaProbeResult.Fail(
                    $"文件 {file.Path} 大小与清单不符（清单 {file.SizeBytes}，实际 {entry.Length}）。");
            }

            try
            {
                using var content = entry.Open();
                var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal))
                {
                    return MediaProbeResult.Fail(
                        $"文件 {file.Path} 内容 sha256 与清单不符（清单 {file.Sha256}，实际 {hash}）。");
                }
            }
            catch (InvalidDataException exception)
            {
                return MediaProbeResult.Fail($"文件 {file.Path} 解压失败：{exception.Message}");
            }
            catch (IOException exception)
            {
                return MediaProbeResult.Fail($"文件 {file.Path} 读取失败：{exception.Message}");
            }
        }

        return MediaProbeResult.Ok(manifest);
    }
}
