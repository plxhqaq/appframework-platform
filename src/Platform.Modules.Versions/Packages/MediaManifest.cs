using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Contracts.Versions;
using Platform.Modules.Versions.Services;

namespace Platform.Modules.Versions.Packages;

/// <summary>
/// 驱动媒体包清单（<c>af-media/1</c>，zip 根内 <c>af-media.json</c>）的规范化内容。
/// 由 <c>pack-media.ps1</c> 生成；字段口径见 <c>docs/平台驱动媒体外置管理_执行计划.md</c>。
/// 所有媒体文件统一落在 <c>media/</c> 前缀下，整包解压即可对上安装器期望的目录布局。
/// </summary>
public sealed partial class MediaManifest
{
    /// <summary>清单条目名（zip 根内）。</summary>
    public const string EntryName = VersionConventions.MediaManifestEntryName;

    /// <summary>清单 JSON 字节上限。</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>清单内文件条目数上限。</summary>
    public const int MaxFiles = 20_000;

    /// <summary>installOrder 条目数上限。</summary>
    public const int MaxInstallOrder = 1024;

    /// <summary>组件标识口径（对齐 components.manifest.json 的 componentId）。</summary>
    public const string ComponentIdPattern = "^[A-Za-z0-9_.:-]{1,64}$";

    /// <summary>协议版本。</summary>
    public string Schema { get; init; } = VersionConventions.MediaSchemaV1;

    /// <summary>组件标识（媒体键缺省取此值）。</summary>
    public required string ComponentId { get; init; }

    /// <summary>显示名。</summary>
    public required string Label { get; init; }

    /// <summary>媒体版本；空串表示未提供。</summary>
    public string Version { get; init; } = VersionConventions.MediaVersionUnspecified;

    /// <summary>统一目录前缀，恒为 <c>media/</c>。</summary>
    public string RootPrefix { get; init; } = VersionConventions.MediaRootPrefix;

    /// <summary>包内文件（path 已归一化并按序排列）。</summary>
    public required IReadOnlyList<ReleaseFileDto> Files { get; init; }

    /// <summary>建议安装顺序（组件标识序列，媒体文件集合变化才更新）。</summary>
    public IReadOnlyList<string> InstallOrder { get; init; } = [];

    /// <summary>离线包安装顺序（wheel 名前缀序列，供 PythonPackages 类组件消费；空 = 未声明）。</summary>
    public IReadOnlyList<string> PackageOrder { get; init; } = [];

    /// <summary>说明。</summary>
    public string? Description { get; init; }

    /// <summary>清单摘要：文件集内容指纹（path/sha256/sizeBytes 排序后 digest）。</summary>
    public string ManifestHash => ReleaseManifest.Compute(Files);

    private static readonly Regex ComponentIdRegex = ComponentPattern();
    private static readonly Regex VersionRegex = VersionPattern();

    /// <summary>是否为合法组件标识（af-media/1 与 af-package/1 mediaRequirements 共用口径）。</summary>
    public static bool IsComponentId(string? value) =>
        !string.IsNullOrEmpty(value) && ComponentIdRegex.IsMatch(value);

    /// <summary>严格解析 <c>af-media/1</c> 清单：未知协议、越出 media/ 前缀、条目缺失或超限都拒绝。</summary>
    public static bool TryParseJson(string json, out MediaManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = $"{EntryName} 为空";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            error = $"{EntryName} 超过 {MaxBytes} 字节上限";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            error = $"{EntryName} 不是合法 JSON：{exception.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = $"{EntryName} 根节点必须是对象";
                return false;
            }

            var schema = PackageJson.GetString(root, "schema");
            if (schema != VersionConventions.MediaSchemaV1)
            {
                error = $"{EntryName} 协议 {schema} 不支持，应为 {VersionConventions.MediaSchemaV1}";
                return false;
            }

            var componentId = PackageJson.GetString(root, "componentId");
            if (!ComponentIdRegex.IsMatch(componentId))
            {
                error = $"componentId 非法（应为 {ComponentIdPattern}）：{componentId}";
                return false;
            }

            var label = PackageJson.GetString(root, "label");
            if (label.Length == 0 || label.Length > 256)
            {
                error = "label 必填且长度不超过 256";
                return false;
            }

            var version = PackageJson.GetString(root, "version");
            if (version.Length > 0 && !VersionRegex.IsMatch(version))
            {
                error = $"version 不符合三段/四段版本口径：{version}";
                return false;
            }

            if (root.TryGetProperty("rootPrefix", out var prefixElement)
                && prefixElement.ValueKind == JsonValueKind.String)
            {
                var prefix = prefixElement.GetString() ?? string.Empty;
                if (prefix.Length > 0 && prefix != VersionConventions.MediaRootPrefix)
                {
                    error = $"rootPrefix 只允许 {VersionConventions.MediaRootPrefix}：{prefix}";
                    return false;
                }
            }

            if (!root.TryGetProperty("files", out var filesElement))
            {
                error = $"{EntryName} 缺少 files";
                return false;
            }

            if (!TryParseMediaFiles(filesElement, out var files, out error))
            {
                return false;
            }

            if (!TryParseIdList(root, "installOrder", out var installOrder, out error))
            {
                return false;
            }

            if (!TryParseIdList(root, "packageOrder", out var packageOrder, out error))
            {
                return false;
            }

            string? description = null;
            if (root.TryGetProperty("description", out var descriptionElement)
                && descriptionElement.ValueKind == JsonValueKind.String)
            {
                description = descriptionElement.GetString();
                if (description is { Length: > VersionConventions.MaxDescriptionChars })
                {
                    error = $"description 超过 {VersionConventions.MaxDescriptionChars} 字符上限";
                    return false;
                }
            }

            manifest = new MediaManifest
            {
                Schema = schema,
                ComponentId = componentId,
                Label = label,
                Version = version,
                RootPrefix = VersionConventions.MediaRootPrefix,
                Files = files,
                InstallOrder = installOrder,
                PackageOrder = packageOrder,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            };
            return true;
        }
    }

    /// <summary>把清单序列化为规范化 JSON（键序固定、缩进）；落库存这份原文。</summary>
    public string ToJson()
    {
        var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("componentId", ComponentId);
            writer.WriteString("label", Label);
            if (Version.Length > 0)
            {
                writer.WriteString("version", Version);
            }

            writer.WriteString("rootPrefix", RootPrefix);
            writer.WriteStartArray("files");
            foreach (var file in Files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteNumber("sizeBytes", file.SizeBytes);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (InstallOrder.Count > 0)
            {
                WriteIdArray(writer, "installOrder", InstallOrder);
            }

            if (PackageOrder.Count > 0)
            {
                WriteIdArray(writer, "packageOrder", PackageOrder);
            }

            if (!string.IsNullOrEmpty(Description))
            {
                writer.WriteString("description", Description);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>媒体文件条目：path/sha256/sizeBytes 全必填；大小允许 0（空文件合法），上限按单文件口径。</summary>
    private static bool TryParseMediaFiles(
        JsonElement element,
        out IReadOnlyList<ReleaseFileDto> files,
        out string? error)
    {
        error = null;
        if (element.ValueKind != JsonValueKind.Array)
        {
            files = [];
            error = "files 必须是数组";
            return false;
        }

        var result = new List<ReleaseFileDto>(element.GetArrayLength());
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                files = [];
                error = "文件条目必须是对象";
                return false;
            }

            var rawPath = PackageJson.GetString(item, "path");
            var path = PackagePaths.Normalize(rawPath);
            if (path is null)
            {
                files = [];
                error = $"文件路径非法：{rawPath}";
                return false;
            }

            if (!path.StartsWith(VersionConventions.MediaRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                files = [];
                error = $"文件必须位于 {VersionConventions.MediaRootPrefix} 前缀下：{path}";
                return false;
            }

            if (string.Equals(path, EntryName, StringComparison.OrdinalIgnoreCase))
            {
                files = [];
                error = $"files 不应包含清单自身 {EntryName}";
                return false;
            }

            if (!paths.Add(path))
            {
                files = [];
                error = $"文件路径重复：{path}";
                return false;
            }

            var sha256 = PackagePaths.NormalizeSha256(PackageJson.GetString(item, "sha256"));
            if (!PackagePaths.IsSha256(sha256))
            {
                files = [];
                error = $"文件 {path} 的 sha256 非法";
                return false;
            }

            if (!item.TryGetProperty("sizeBytes", out var sizeElement)
                || sizeElement.ValueKind != JsonValueKind.Number
                || !sizeElement.TryGetInt64(out var sizeBytes)
                || sizeBytes < 0
                || sizeBytes > VersionConventions.MaxFileSizeBytes)
            {
                files = [];
                error = $"文件 {path} 缺少合法 sizeBytes（0 ~ {VersionConventions.MaxFileSizeBytes}）";
                return false;
            }

            result.Add(new ReleaseFileDto(path, sha256, sizeBytes));
        }

        if (result.Count == 0)
        {
            files = [];
            error = "files 不能为空";
            return false;
        }

        if (result.Count > MaxFiles)
        {
            files = [];
            error = $"文件数超过上限 {MaxFiles}";
            return false;
        }

        result.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        files = result;
        return true;
    }

    /// <summary>解析 id 序列字段（installOrder / packageOrder 共用口径）：缺省或非法类型视为空；元素须为合法标识且去重保序。</summary>
    private static bool TryParseIdList(
        JsonElement root,
        string propertyName,
        out IReadOnlyList<string> ids,
        out string? error)
    {
        error = null;
        ids = [];
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                ids = [];
                error = $"{propertyName} 元素必须是字符串";
                return false;
            }

            var id = item.GetString() ?? string.Empty;
            if (!ComponentIdRegex.IsMatch(id))
            {
                ids = [];
                error = $"{propertyName} 元素非法（应为 {ComponentIdPattern}）：{id}";
                return false;
            }

            if (seen.Add(id) == false)
            {
                continue;
            }

            result.Add(id);
            if (result.Count > MaxInstallOrder)
            {
                ids = [];
                error = $"{propertyName} 超过 {MaxInstallOrder} 条上限";
                return false;
            }
        }

        ids = result;
        return true;
    }

    private static void WriteIdArray(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> ids)
    {
        writer.WriteStartArray(propertyName);
        foreach (var id in ids)
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
    }

    [GeneratedRegex(ComponentIdPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ComponentPattern();

    [GeneratedRegex(VersionConventions.VersionPattern, RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
