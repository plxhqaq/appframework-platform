using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Modules.Versions.Options;
using Platform.Modules.Versions.Packages;

namespace Platform.UnitTests.Versions;

/// <summary>统一清单协议：af-package/1 解析、老格式映射、PE/zip/sidecar 探测与 zip 安全边界。</summary>
public sealed class PackageManifestTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Sha2 = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private static readonly string[] ExpectedPaths = ["A.exe", "Bin/B.dll"];
    private static readonly string[] ExpectedModules = ["HipotTest"];
    private static readonly string[] ExpectedHardware = ["TH9110"];
    private static readonly string[] ExpectedLegacyHardware = ["TH9110", "ScanUsb"];

    [Fact]
    public void 解析新清单并归一化路径与排序()
    {
        var json = $$"""
        {
          "schema": "af-package/1",
          "kind": "incremental",
          "appKey": "demo",
          "channelKey": "stable",
          "version": "1.2.3.0",
          "runtimeIdentifier": "win-x64",
          "publishTime": "2026-11-13T10:00:00Z",
          "capabilities": { "modules": ["HipotTest"], "hardware": ["TH9110"] },
          "files": [
            { "path": "app\\Bin\\B.dll", "sha256": "{{Sha2}}", "sizeBytes": 20 },
            { "path": "./A.exe", "sha256": "{{Sha}}", "sizeBytes": 10 }
          ]
        }
        """;

        Assert.True(PackageManifest.TryParseJson(
            json,
            VersionConventions.ManifestSourceZip,
            out var manifest,
            out var error));
        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal("incremental", manifest.Kind);
        Assert.Equal("stable", manifest.ChannelKey);
        Assert.Equal("win-x64", manifest.RuntimeIdentifier);
        Assert.Equal(ExpectedPaths, manifest.Files.Select(static x => x.Path).ToArray());
        Assert.Equal(ExpectedModules, manifest.Modules);
        Assert.Equal(ExpectedHardware, manifest.Hardware);

        var reversed = $$"""
        {
          "schema": "af-package/1",
          "kind": "incremental",
          "version": "1.2.3.0",
          "files": [
            { "path": "./A.exe", "sha256": "{{Sha}}", "sizeBytes": 10 },
            { "path": "app\\Bin\\B.dll", "sha256": "{{Sha2}}", "sizeBytes": 20 }
          ]
        }
        """;
        Assert.True(PackageManifest.TryParseJson(reversed, "zip", out var manifest2, out _));
        Assert.NotNull(manifest2);
        Assert.Equal(manifest.ManifestHash, manifest2.ManifestHash);
    }

    [Fact]
    public void 增量包缺文件清单被拒绝()
    {
        const string json = """{ "schema": "af-package/1", "kind": "incremental", "version": "1.0.0" }""";
        Assert.False(PackageManifest.TryParseJson(json, "zip", out var manifest, out var error));
        Assert.Null(manifest);
        Assert.Contains("files", error);
    }

    [Theory]
    [InlineData("../evil.dll", "路径非法")]
    [InlineData("/etc/passwd", "路径非法")]
    [InlineData("C:/Windows/evil.dll", "路径非法")]
    [InlineData("a//b.dll", "路径非法")]
    [InlineData("a/./b.dll", "路径非法")]
    public void 非法文件路径被拒绝(string path, string expected)
    {
        var json = $$"""
        {
          "kind": "incremental",
          "version": "1.0.0",
          "files": [ { "path": "{{path}}", "sha256": "{{Sha}}", "sizeBytes": 1 } ]
        }
        """;
        Assert.False(PackageManifest.TryParseJson(json, "zip", out _, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void 大小写不敏感重复路径被拒绝()
    {
        var json = $$"""
        {
          "kind": "incremental",
          "version": "1.0.0",
          "files": [
            { "path": "App.exe", "sha256": "{{Sha}}", "sizeBytes": 1 },
            { "path": "app.exe", "sha256": "{{Sha2}}", "sizeBytes": 1 }
          ]
        }
        """;
        Assert.False(PackageManifest.TryParseJson(json, "zip", out _, out var error));
        Assert.Contains("重复", error);
    }

    [Fact]
    public void 非法哈希被拒绝()
    {
        const string json = """{ "kind": "incremental", "version": "1.0.0", "files": [ { "path": "A.exe", "sha256": "xyz", "sizeBytes": 1 } ] }""";
        Assert.False(PackageManifest.TryParseJson(json, "zip", out _, out var error));
        Assert.Contains("sha256", error);
    }

    [Fact]
    public void 管道串解析支持exe清单()
    {
        const string raw = "AF-PACKAGE/1|kind=full|version=1.2.3.0|channel=stable|rid=win-x64|modules=HipotTest|hardware=TH9110|gitVersion=1.2.3.0|gitCommit=abc|publishTime=2026-11-13|entryPoint=Setup.exe";
        Assert.True(PackageManifest.TryParsePipe(raw, "embedded", out var manifest, out var error));
        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal("full", manifest.Kind);
        Assert.Equal("stable", manifest.ChannelKey);
        Assert.Equal("Setup.exe", manifest.EntryPoint);
        Assert.Equal(ExpectedModules, manifest.Modules);
        Assert.Equal(ExpectedHardware, manifest.Hardware);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero), manifest.PublishTime);
    }

    [Fact]
    public void 老安装器清单映射为全量包()
    {
        const string raw = "AF-MANIFEST/1|version=1.0.0.225|branch=hipot|publishTime=2026-10-19|gitVersion=1.0.0.225|gitCommit=abc|modules=HipotTest|hardware=TH9110,ScanUsb|productKey=hipot-lic";
        var result = LegacyManifestAdapter.TryMapInstallerManifest(raw);
        Assert.True(result.Success);
        Assert.NotNull(result.Manifest);
        Assert.Equal("full", result.Manifest.Kind);
        Assert.Equal("hipot", result.Manifest.ChannelKey);
        Assert.Equal("1.0.0.225", result.Manifest.Version);
        Assert.Equal(ExpectedLegacyHardware, result.Manifest.Hardware);
        Assert.Equal("hipot-lic", result.Manifest.Labels["productKey"]);
        Assert.Equal(VersionConventions.ManifestSourceLegacy, result.Manifest.Source);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void 老安装器清单缺分支被拒绝()
    {
        var result = LegacyManifestAdapter.TryMapInstallerManifest("AF-MANIFEST/1|version=1.0.0.1");
        Assert.False(result.Success);
        Assert.Equal(ApiErrorCode.VersionsPackageManifestInvalid, result.ErrorCode);
    }

    [Fact]
    public void 老安装器清单带运行时与说明()
    {
        const string raw = "AF-MANIFEST/1|version=1.0.0.225|branch=hipot|productKey=hipot|runtimeIdentifier=win-x64|description=涂布机 1.0.0.225 安装包";
        var result = LegacyManifestAdapter.TryMapInstallerManifest(raw);
        Assert.True(result.Success);
        Assert.NotNull(result.Manifest);
        Assert.Equal("win-x64", result.Manifest.RuntimeIdentifier);
        Assert.Equal("涂布机 1.0.0.225 安装包", result.Manifest.Description);
    }

    [Fact]
    public void 老安装器清单运行时非法被拒绝()
    {
        var result = LegacyManifestAdapter.TryMapInstallerManifest(
            "AF-MANIFEST/1|version=1.0.0.1|branch=hipot|runtimeIdentifier=freebsd-x64");
        Assert.False(result.Success);
        Assert.Equal(ApiErrorCode.VersionsPackageManifestInvalid, result.ErrorCode);
    }

    [Fact]
    public void 老增量包摘要读取运行时与说明()
    {
        const string buildInfo = """
        {
          "kind": "AF-UPDATE-PACKAGE/1",
          "version": "1.2.3.0",
          "branch": "hipot",
          "runtimeIdentifier": "win-x64",
          "description": "涂布机 1.2.3.0 增量包"
        }
        """;
        var manifestJson = $$"""
        {
          "entries": [ { "path": "App.exe", "sha256": "{{Sha}}", "size": 123 } ]
        }
        """;
        var result = LegacyManifestAdapter.TryMapUpdatePackage(buildInfo, manifestJson);
        Assert.True(result.Success);
        Assert.NotNull(result.Manifest);
        Assert.Equal("win-x64", result.Manifest.RuntimeIdentifier);
        Assert.Equal("涂布机 1.2.3.0 增量包", result.Manifest.Description);
    }

    [Fact]
    public void 新清单解析说明字段并原样回写()
    {
        var json = $$"""
        {
          "schema": "af-package/1",
          "kind": "incremental",
          "version": "1.2.3.0",
          "description": "说明文本",
          "files": [ { "path": "A.exe", "sha256": "{{Sha}}", "sizeBytes": 10 } ]
        }
        """;
        Assert.True(PackageManifest.TryParseJson(json, "zip", out var manifest, out _));
        Assert.NotNull(manifest);
        Assert.Equal("说明文本", manifest.Description);
        Assert.True(PackageManifest.TryParseJson(manifest.ToJson(), "zip", out var roundTrip, out _));
        Assert.NotNull(roundTrip);
        Assert.Equal("说明文本", roundTrip.Description);
    }

    [Fact]
    public void 老增量包映射为增量清单()
    {
        const string buildInfo = """
        {
          "kind": "AF-UPDATE-PACKAGE/1",
          "version": "1.2.3.0",
          "branch": "hipot",
          "productKey": "hipot",
          "family": "HipotTest",
          "licenseGuard": "none",
          "publishTime": "2026-10-19",
          "gitCommit": "abc",
          "modules": ["HipotTest"],
          "hardware": ["TH9110"]
        }
        """;
        var manifestJson = $$"""
        {
          "schemaVersion": 1,
          "branch": "hipot",
          "version": "1.2.3.0",
          "generatedAt": "2026-10-19T10:00:00Z",
          "entries": [ { "path": "app/App.exe", "sha256": "{{Sha}}", "size": 123 } ]
        }
        """;

        var result = LegacyManifestAdapter.TryMapUpdatePackage(buildInfo, manifestJson);
        Assert.True(result.Success);
        Assert.NotNull(result.Manifest);
        Assert.Equal("incremental", result.Manifest.Kind);
        Assert.Equal("hipot", result.Manifest.ChannelKey);
        Assert.Single(result.Manifest.Files);
        Assert.Equal("App.exe", result.Manifest.Files[0].Path);
        Assert.Equal(123, result.Manifest.Files[0].SizeBytes);
        Assert.Equal("hipot", result.Manifest.Labels["productKey"]);
        Assert.Equal(VersionConventions.ManifestSourceLegacy, result.Manifest.Source);
    }

    [Fact]
    public void 老增量包缺清单被拒绝()
    {
        const string buildInfo = """{ "kind": "AF-UPDATE-PACKAGE/1", "version": "1.2.3", "branch": "hipot" }""";
        var result = LegacyManifestAdapter.TryMapUpdatePackage(buildInfo, null);
        Assert.False(result.Success);
        Assert.Contains("manifest.json", result.ErrorMessage);
    }

    [Fact]
    public void 更新器清单映射为updater()
    {
        var json = $$"""
        {
          "kind": "AF-UPDATER/1",
          "version": "1.0.0.7",
          "entry": "AppFramework.Updater.exe",
          "gitCommit": "abc",
          "generatedAtUtc": "2026-10-19T10:00:00Z",
          "files": [ { "path": "AppFramework.Updater.exe", "sha256": "{{Sha}}", "size": 456 } ]
        }
        """;
        var result = LegacyManifestAdapter.TryMapUpdaterInfo(json);
        Assert.True(result.Success);
        Assert.NotNull(result.Manifest);
        Assert.Equal("updater", result.Manifest.Kind);
        Assert.Equal("AppFramework.Updater.exe", result.Manifest.EntryPoint);
        Assert.Single(result.Manifest.Files);
        Assert.Null(result.Manifest.ChannelKey);
    }

    [Fact]
    public void zip探测读取app前缀下的新清单()
    {
        var json = $$"""
        {
          "schema": "af-package/1",
          "kind": "incremental",
          "version": "2.0.0",
          "files": [ { "path": "App.exe", "sha256": "{{Sha}}", "sizeBytes": 1 } ]
        }
        """;
        using var zip = BuildZip(("app/af-package.json", json), ("app/App.exe", "x"));

        var result = ZipPackageManifestProbe.TryRead(zip, new VersionsOptions());
        Assert.True(result.Success);
        Assert.NotNull(result.Manifest);
        Assert.Equal("2.0.0", result.Manifest.Version);
        Assert.Equal(VersionConventions.ManifestSourceZip, result.Manifest.Source);
    }

    [Fact]
    public void zip探测回退老增量包()
    {
        using var zip = BuildZip(
            ("build-info.json", """{ "kind": "AF-UPDATE-PACKAGE/1", "version": "3.0.0", "branch": "stable" }"""),
            ("manifest.json", $$"""{ "entries": [ { "path": "App.exe", "sha256": "{{Sha}}", "size": 1 } ] }"""),
            ("app/App.exe", "x"));

        var result = ZipPackageManifestProbe.TryRead(zip, new VersionsOptions());
        Assert.True(result.Success);
        Assert.NotNull(result.Manifest);
        Assert.Equal("incremental", result.Manifest.Kind);
        Assert.Equal("stable", result.Manifest.ChannelKey);
    }

    [Fact]
    public void zip探测缺清单返回缺清单错误码()
    {
        using var zip = BuildZip(("app/readme.txt", "hello"));
        var result = ZipPackageManifestProbe.TryRead(zip, new VersionsOptions());
        Assert.False(result.Success);
        Assert.Equal(ApiErrorCode.VersionsPackageManifestMissing, result.ErrorCode);
    }

    [Fact]
    public void zip路径穿越被拒绝()
    {
        using var zip = BuildZip(("../evil.txt", "x"), ("app/App.exe", "x"));
        var result = ZipPackageManifestProbe.TryRead(zip, new VersionsOptions());
        Assert.False(result.Success);
        Assert.Equal(ApiErrorCode.VersionsPackageUnsafeArchive, result.ErrorCode);
    }

    [Fact]
    public void zip大小写重复路径被拒绝()
    {
        using var zip = BuildZip(("app/App.exe", "x"), ("APP/App.exe", "y"));
        var result = ZipPackageManifestProbe.TryRead(zip, new VersionsOptions());
        Assert.False(result.Success);
        Assert.Equal(ApiErrorCode.VersionsPackageUnsafeArchive, result.ErrorCode);
    }

    [Fact]
    public void zip条目数超限被拒绝()
    {
        using var zip = BuildZip(("a.txt", "a"), ("b.txt", "b"));
        var options = new VersionsOptions { MaxZipEntries = 1 };
        var result = ZipPackageManifestProbe.TryRead(zip, options);
        Assert.False(result.Success);
        Assert.Equal(ApiErrorCode.VersionsPackageUnsafeArchive, result.ErrorCode);
    }

    [Fact]
    public void zip压缩比超限被拒绝()
    {
        using var zip = BuildZip(("app/big.bin", new byte[256 * 1024]));
        var options = new VersionsOptions { MaxCompressionRatio = 2 };
        var result = ZipPackageManifestProbe.TryRead(zip, options);
        Assert.False(result.Success);
        Assert.Equal(ApiErrorCode.VersionsPackageUnsafeArchive, result.ErrorCode);
    }

    [Fact]
    public void sidecar优先于包内清单()
    {
        var embedded = $$"""
        {
          "schema": "af-package/1",
          "kind": "full",
          "version": "1.0.0",
          "files": [ { "path": "App.exe", "sha256": "{{Sha}}", "sizeBytes": 1 } ]
        }
        """;
        using var zip = BuildZip(("af-package.json", embedded), ("App.exe", "x"));
        const string sidecar = """{ "schema": "af-package/1", "kind": "full", "version": "9.9.9", "channelKey": "beta" }""";

        var probe = new PackageManifestProbe(Options.Create(new VersionsOptions()));
        var result = probe.Probe(new PackageProbeRequest(
            "demo",
            "Setup.exe",
            "zip",
            zip.Length,
            () => zip,
            () => new MemoryStream(Encoding.UTF8.GetBytes(sidecar))));

        Assert.True(result.Success);
        Assert.NotNull(result.Manifest);
        Assert.Equal("9.9.9", result.Manifest.Version);
        Assert.Equal("beta", result.Manifest.ChannelKey);
        Assert.Equal(VersionConventions.ManifestSourceSidecar, result.Manifest.Source);
    }

    [Fact]
    public void PE读取测试程序集的Comments()
    {
        var assembly = typeof(PackageManifestTests).Assembly;
        Assert.Equal(
            "AF-PACKAGE/1|kind=full|version=0.0.0.1|channel=test|rid=win-x64|modules=HipotTest|hardware=TH9110|publishTime=2026-01-01|gitVersion=0.0.0-1|gitCommit=test",
            assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description);

        using var stream = File.OpenRead(assembly.Location);
        var result = PePackageManifestProbe.TryRead(stream);

        Assert.True(result.Success);
        Assert.NotNull(result.Manifest);
        Assert.Equal("full", result.Manifest.Kind);
        Assert.Equal("0.0.0.1", result.Manifest.Version);
        Assert.Equal("test", result.Manifest.ChannelKey);
        Assert.Equal("win-x64", result.Manifest.RuntimeIdentifier);
        Assert.Equal(VersionConventions.ManifestSourceEmbedded, result.Manifest.Source);
    }

    [Fact]
    public void 非PE文件返回缺清单()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5]);
        var result = PePackageManifestProbe.TryRead(stream);
        Assert.False(result.Success);
        Assert.Equal(ApiErrorCode.VersionsPackageManifestMissing, result.ErrorCode);
    }

    [Fact]
    public void 规范化JSON可再次解析且哈希不变()
    {
        var json = $$"""
        {
          "kind": "incremental",
          "version": "1.0.0",
          "appKey": "demo",
          "files": [
            { "path": "B.dll", "sha256": "{{Sha2}}", "sizeBytes": 2 },
            { "path": "A.exe", "sha256": "{{Sha}}", "sizeBytes": 1 }
          ]
        }
        """;
        Assert.True(PackageManifest.TryParseJson(json, "zip", out var first, out _));
        Assert.NotNull(first);

        Assert.True(PackageManifest.TryParseJson(first.ToJson(), "zip", out var second, out var error));
        Assert.Null(error);
        Assert.NotNull(second);
        Assert.Equal(first.ManifestHash, second.ManifestHash);
        Assert.Equal(first.Files, second.Files);
    }

    private static MemoryStream BuildZip(params (string Name, string Content)[] entries) =>
        BuildZip(entries.Select(static x => (x.Name, Encoding.UTF8.GetBytes(x.Content))).ToArray());

    private static MemoryStream BuildZip(params (string Name, byte[] Content)[] entries)
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
        return buffer;
    }
}
