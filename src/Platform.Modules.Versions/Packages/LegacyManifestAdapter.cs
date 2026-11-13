using System.Text.Json;
using Platform.Contracts;
using Platform.Contracts.Versions;

namespace Platform.Modules.Versions.Packages;

/// <summary>
/// 老清单纯映射：不改变老构建脚本产物，把已知格式翻译成 <c>af-package/1</c> 强类型并记录 warning。
/// <para>
/// 支持：exe Comments 的 <c>AF-MANIFEST/1</c>、增量 zip 的 <c>AF-UPDATE-PACKAGE/1</c> + <c>manifest.json</c>、
/// 更新器 zip 的 <c>updater-info.json</c>。未知字段保留为 <see cref="PackageManifest.Labels"/>，不参与路由。
/// </para>
/// </summary>
public static class LegacyManifestAdapter
{
    /// <summary>老安装器 PE Comments 标记。</summary>
    public const string InstallerMarker = "AF-MANIFEST/1";

    /// <summary>老增量包 build-info.json 的 kind 标记。</summary>
    public const string UpdatePackageMarker = "AF-UPDATE-PACKAGE/1";

    /// <summary>更新器 updater-info.json 的 kind 标记。</summary>
    public const string UpdaterInfoMarker = "AF-UPDATER/1";

    private static readonly HashSet<string> InstallerKnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "version", "branch", "publishTime", "gitVersion", "gitCommit", "modules", "hardware",
    };

    private static readonly HashSet<string> BuildInfoKnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "kind", "version", "branch", "publishTime", "gitVersion", "gitCommit", "modules", "hardware",
    };

    private static readonly HashSet<string> UpdaterInfoKnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "kind", "version", "entry", "gitCommit", "generatedAtUtc", "files",
    };

    /// <summary>映射 exe Comments 的 <c>AF-MANIFEST/1|version=…|branch=…</c> 管道串。</summary>
    public static PackageProbeResult TryMapInstallerManifest(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        if (!text.StartsWith(InstallerMarker + "|", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid($"清单前缀不是 {InstallerMarker}");
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = segment.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            fields[segment[..split].Trim()] = segment[(split + 1)..].Trim();
        }

        var version = fields.GetValueOrDefault("version", string.Empty).Trim();
        var branch = fields.GetValueOrDefault("branch", string.Empty).Trim();
        if (!PackageManifest.IsValidVersion(version) || branch.Length == 0)
        {
            return Invalid($"{InstallerMarker} 缺少合法的 version/branch 字段");
        }

        var warnings = new List<string> { "老清单 AF-MANIFEST/1：已按 kind=full、channelKey=branch 映射。" };
        var manifest = new PackageManifest
        {
            Kind = VersionConventions.PackageKindFull,
            ChannelKey = branch,
            Version = version,
            PublishTime = PackageManifest.ParsePublishTime(fields.GetValueOrDefault("publishTime"), warnings),
            GitVersion = NullIfEmpty(fields.GetValueOrDefault("gitVersion")),
            GitCommit = NullIfEmpty(fields.GetValueOrDefault("gitCommit")),
            Modules = PackageJson.SplitIds(fields.GetValueOrDefault("modules")),
            Hardware = PackageJson.SplitIds(fields.GetValueOrDefault("hardware")),
            Source = VersionConventions.ManifestSourceLegacy,
            Labels = CollectLabels(fields, InstallerKnownFields),
            Warnings = warnings,
        };
        return PackageProbeResult.Ok(manifest);
    }

    /// <summary>
    /// 映射老增量包：<c>build-info.json</c>（版本/分支/能力）+ <c>manifest.json</c>（entries 文件集）。
    /// 缺 build-info 时从 manifest.json 自身的 version/branch/productKey 兜底。
    /// </summary>
    public static PackageProbeResult TryMapUpdatePackage(string? buildInfoJson, string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return Invalid($"增量包缺少 {ZipPackageManifestProbe.LegacyManifestEntryName}");
        }

        JsonDocument? buildInfo = null;
        JsonDocument? manifest = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(buildInfoJson))
            {
                buildInfo = Parse(buildInfoJson, out var error);
                if (buildInfo is null)
                {
                    return Invalid(error!);
                }
            }

            manifest = Parse(manifestJson, out var manifestError);
            if (manifest is null)
            {
                return Invalid(manifestError!);
            }

            var warnings = new List<string> { "老增量包清单 AF-UPDATE-PACKAGE/1：已映射为 af-package/1。" };
            var buildRoot = buildInfo?.RootElement;
            var manifestRoot = manifest.RootElement;

            if (buildRoot is { ValueKind: JsonValueKind.Object })
            {
                var marker = PackageJson.GetString(buildRoot.Value, "kind");
                if (marker.Length > 0
                    && !string.Equals(marker, UpdatePackageMarker, StringComparison.OrdinalIgnoreCase))
                {
                    return Invalid($"{ZipPackageManifestProbe.LegacyBuildInfoEntryName} 的 kind 不识别（{marker}）");
                }
            }
            else
            {
                warnings.Add($"缺少 {ZipPackageManifestProbe.LegacyBuildInfoEntryName}，version/branch 取自 manifest.json。");
            }

            var version = FirstNonEmpty(
                buildRoot is { ValueKind: JsonValueKind.Object } ? PackageJson.GetString(buildRoot.Value, "version") : string.Empty,
                PackageJson.GetString(manifestRoot, "version"));
            var branch = FirstNonEmpty(
                buildRoot is { ValueKind: JsonValueKind.Object } ? PackageJson.GetString(buildRoot.Value, "branch") : string.Empty,
                PackageJson.GetString(manifestRoot, "branch"),
                PackageJson.GetString(manifestRoot, "productKey"));
            if (!PackageManifest.IsValidVersion(version) || branch.Length == 0)
            {
                return Invalid("老增量包清单缺少合法的 version/branch 字段");
            }

            if (!manifestRoot.TryGetProperty("entries", out var entries)
                && !manifestRoot.TryGetProperty("files", out entries))
            {
                return Invalid("manifest.json 不含 entries 文件清单");
            }

            if (!PackageJson.TryParseFiles(entries, out var files, out var filesError))
            {
                return Invalid(filesError!);
            }

            var modules = buildRoot is { ValueKind: JsonValueKind.Object }
                ? PackageJson.GetStringArray(buildRoot.Value, "modules")
                : [];
            var hardware = buildRoot is { ValueKind: JsonValueKind.Object }
                ? PackageJson.GetStringArray(buildRoot.Value, "hardware")
                : [];

            return PackageProbeResult.Ok(new PackageManifest
            {
                Kind = VersionConventions.PackageKindIncremental,
                ChannelKey = branch,
                Version = version,
                PublishTime = PackageManifest.ParsePublishTime(
                    FirstNonEmpty(
                        buildRoot is { ValueKind: JsonValueKind.Object } ? PackageJson.GetString(buildRoot.Value, "publishTime") : string.Empty,
                        PackageJson.GetString(manifestRoot, "generatedAt")),
                    warnings),
                GitVersion = NullIfEmpty(
                    buildRoot is { ValueKind: JsonValueKind.Object } ? PackageJson.GetString(buildRoot.Value, "gitVersion") : string.Empty),
                GitCommit = NullIfEmpty(FirstNonEmpty(
                    buildRoot is { ValueKind: JsonValueKind.Object } ? PackageJson.GetString(buildRoot.Value, "gitCommit") : string.Empty,
                    PackageJson.GetString(manifestRoot, "gitCommit"))),
                Modules = modules,
                Hardware = hardware,
                Files = files,
                Source = VersionConventions.ManifestSourceLegacy,
                Labels = buildRoot is { ValueKind: JsonValueKind.Object }
                    ? PackageJson.CollectLabels(buildRoot.Value, BuildInfoKnownFields)
                    : new Dictionary<string, string>(),
                Warnings = warnings,
            });
        }
        finally
        {
            manifest?.Dispose();
            buildInfo?.Dispose();
        }
    }

    /// <summary>映射更新器 zip 的 <c>updater-info.json</c>（kind/version/entry/files[]）。</summary>
    public static PackageProbeResult TryMapUpdaterInfo(string? json)
    {
        using var document = Parse(json, out var error);
        if (document is null)
        {
            return Invalid(error!);
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Invalid("updater-info.json 根必须是 JSON 对象");
        }

        var marker = PackageJson.GetString(root, "kind");
        if (marker.Length > 0 && !string.Equals(marker, UpdaterInfoMarker, StringComparison.OrdinalIgnoreCase))
        {
            return Invalid($"updater-info.json 的 kind 不识别（{marker}）");
        }

        var version = PackageJson.GetString(root, "version").Trim();
        if (!PackageManifest.IsValidVersion(version))
        {
            return Invalid("updater-info.json 缺少合法的 version 字段");
        }

        IReadOnlyList<ReleaseFileDto> files = [];
        if (root.TryGetProperty("files", out var filesElement)
            && !PackageJson.TryParseFiles(filesElement, out files, out var filesError))
        {
            return Invalid(filesError!);
        }

        var warnings = new List<string> { "老更新器清单 updater-info.json：已映射为 af-package/1。" };
        return PackageProbeResult.Ok(new PackageManifest
        {
            Kind = VersionConventions.PackageKindUpdater,
            Version = version,
            EntryPoint = NullIfEmpty(PackageJson.GetString(root, "entry")),
            PublishTime = PackageManifest.ParsePublishTime(PackageJson.GetString(root, "generatedAtUtc"), warnings),
            GitCommit = NullIfEmpty(PackageJson.GetString(root, "gitCommit")),
            Files = files,
            Source = VersionConventions.ManifestSourceLegacy,
            Labels = PackageJson.CollectLabels(root, UpdaterInfoKnownFields),
            Warnings = warnings,
        });
    }

    private static PackageProbeResult Invalid(string message) =>
        PackageProbeResult.Fail(ApiErrorCode.VersionsPackageManifestInvalid, message);

    private static JsonDocument? Parse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "清单为空";
            return null;
        }

        if (json.Length > PackageManifest.MaxBytes)
        {
            error = $"清单超过 {PackageManifest.MaxBytes} 字节上限";
            return null;
        }

        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            error = $"清单 JSON 解析失败：{exception.Message}";
            return null;
        }
    }

    private static Dictionary<string, string> CollectLabels(
        IReadOnlyDictionary<string, string> fields,
        HashSet<string> known)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
        {
            if (!known.Contains(key) && value.Length > 0)
            {
                labels[key] = value;
            }
        }

        return labels;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            var trimmed = value.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    private static string? NullIfEmpty(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
