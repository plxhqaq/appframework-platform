using System.Security.Cryptography;
using System.Text;
using Platform.Contracts.Versions;

namespace Platform.Modules.Versions.Services;

/// <summary>版本内容的 Blob 路径约定：按应用 + 内容哈希寻址，同内容只存一份。</summary>
internal static class VersionBlobPaths
{
    public static string Blob(string appKey, string sha256) => $"versions/{appKey}/{sha256}";

    /// <summary>
    /// 管理台下载用的 zip 缓存：同目录、以 <c>{sha}.zip</c> 命名，孤儿扫描按同一 sha 判定引用，
    /// 源内容不再被引用时与源文件一起回收。
    /// </summary>
    public static string ArchiveZip(string appKey, string sha256) =>
        $"versions/{appKey}/{sha256}.zip";

    /// <summary>
    /// 完整包组合缓存：独立 <c>bundles/</c> 子目录，按组合哈希（包 sha + 排序媒体 sha）命名。
    /// 纯派生、可重建：孤儿扫描只按宽限期回收，不看引用（删掉下次下载会原样重建）。
    /// </summary>
    public static string Bundle(string appKey, string compositionHash) =>
        $"versions/{appKey}/bundles/{compositionHash}.zip";
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
