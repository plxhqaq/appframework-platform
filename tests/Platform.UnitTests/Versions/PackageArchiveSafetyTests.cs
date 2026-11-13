using System.IO.Compression;
using System.Text;
using Platform.Modules.Versions.Options;
using Platform.Modules.Versions.Packages;
using Xunit;

namespace Platform.UnitTests.Versions;

/// <summary>zip 安全边界都在比较符上：等于上限必须放行，超过一个字节/条目才拒绝。</summary>
public sealed class PackageArchiveSafetyTests
{
    [Fact]
    public void 条目数等于上限通过超过上限拒绝()
    {
        var options = new VersionsOptions { MaxZipEntries = 2 };
        using var atLimit = BuildZip(("a.txt", "a"), ("b.txt", "b"));
        using var overLimit = BuildZip(("a.txt", "a"), ("b.txt", "b"), ("c.txt", "c"));

        Assert.True(PackageArchiveSafety.Validate(atLimit, options).IsSafe);

        var rejected = PackageArchiveSafety.Validate(overLimit, options);
        Assert.False(rejected.IsSafe);
        Assert.Contains("条目数", rejected.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void 解压总量等于上限通过超过上限拒绝()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 100));
        var options = new VersionsOptions { MaxExtractedBytesPerPackage = bytes.Length };
        using var atLimit = BuildZipBytes(("payload.bin", bytes));
        Assert.True(PackageArchiveSafety.Validate(atLimit, options).IsSafe);

        var over = new VersionsOptions { MaxExtractedBytesPerPackage = bytes.Length - 1 };
        using var overLimit = BuildZipBytes(("payload.bin", bytes));
        var rejected = PackageArchiveSafety.Validate(overLimit, over);
        Assert.False(rejected.IsSafe);
        Assert.Contains("解压总量", rejected.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void 高压缩比被拒绝低压缩比放行()
    {
        var options = new VersionsOptions { MaxCompressionRatio = 3 };

        // 4 KiB 全零：压缩后只有几十字节，比值远超阈值
        using var bomb = BuildZipBytes(("zeros.bin", new byte[4096]));
        var rejected = PackageArchiveSafety.Validate(bomb, options);
        Assert.False(rejected.IsSafe);
        Assert.Contains("压缩比", rejected.ErrorMessage, StringComparison.Ordinal);

        // 伪随机内容不可压缩，比值接近 1
        var noise = new byte[4096];
        new Random(42).NextBytes(noise);
        using var normal = BuildZipBytes(("noise.bin", noise));
        Assert.True(PackageArchiveSafety.Validate(normal, options).IsSafe);
    }

    private static ZipArchive BuildZip(params (string Name, string Content)[] entries) =>
        BuildZipBytes([.. entries.Select(static entry => (entry.Name, Encoding.UTF8.GetBytes(entry.Content)))]);

    private static ZipArchive BuildZipBytes(params (string Name, byte[] Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }
}
