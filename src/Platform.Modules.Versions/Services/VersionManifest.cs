using System.Security.Cryptography;
using System.Text;
using Platform.Contracts.Versions;

namespace Platform.Modules.Versions.Services;

/// <summary>版本内容的 Blob 路径约定：按应用 + 内容哈希寻址，同内容只存一份。</summary>
internal static class VersionBlobPaths
{
    public static string Blob(string appKey, string sha256) => $"versions/{appKey}/{sha256}";
}

/// <summary>清单哈希：文件条目按路径排序后逐行规范化，路径/哈希/长度任一变化都会改变摘要。</summary>
internal static class ReleaseManifest
{
    public static string Compute(IEnumerable<ReleaseFileDto> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(static x => x.Path, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{file.Path}\n{file.Sha256}\n{file.SizeBytes}\n"));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
