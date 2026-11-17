using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Contracts.Versions;
using Platform.Modules.Versions.Services;

namespace Platform.Modules.Versions.Packages;

/// <summary>包内文件路径与哈希的规范化口径（清单、zip 条目、摄取共用）。</summary>
public static partial class PackagePaths
{
    /// <summary>zip 条目可能带的顶层应用前缀。</summary>
    public const string AppPrefix = "app/";

    /// <summary>是否 64 位小写十六进制 SHA-256。</summary>
    public static bool IsSha256(string? value) => !string.IsNullOrEmpty(value) && Sha256Regex().IsMatch(value);

    /// <summary>哈希归一：去空白、转小写。</summary>
    public static string NormalizeSha256(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>
    /// 归一化相对路径：反斜杠转正斜杠、去 <c>./</c> 与顶层 <c>app/</c> 前缀。
    /// 绝对路径、盘符/冒号、NUL、空段、<c>.</c>/<c>..</c>、超长一律返回 null。
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var path = raw.Trim().Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (path.Length == 0 || path[0] == '/' || path.Contains('\0') || path.Contains(':'))
        {
            return null;
        }

        if (path.StartsWith(AppPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[AppPrefix.Length..];
        }

        if (path.Length == 0 || path.Length > VersionConventions.MaxPathLength)
        {
            return null;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return null;
            }
        }

        return path;
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}

/// <summary>
/// 统一包清单（<c>af-package/1</c>）：zip 内 JSON 与 exe 的 PE Comments 管道串共用同一强类型。
/// <para>
/// 解析只做结构与安全校验，不与请求参数比对（appKey/channel/version 一致性由登记服务判断）。
/// 文件条目路径归一后再按路径排序；<see cref="ManifestHash"/> 与 <c>ReleaseManifest.Compute</c> 同算法，
/// 保证「包清单哈希」与「ReleaseArtifact 清单哈希」可直接比对。
/// </para>
/// </summary>
public sealed class PackageManifest
{
    /// <summary>清单协议标记（JSON 的 schema 与管道串前缀同值）。</summary>
    public const string SchemaId = "af-package/1";

    /// <summary>清单 JSON 大小上限（1 MB）。</summary>
    public const long MaxBytes = 1024 * 1024;

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 16 };

    private static readonly IReadOnlyDictionary<string, string> EmptyLabels =
        new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

    /// <summary>协议版本（恒为 <see cref="SchemaId"/>）。</summary>
    public string Schema { get; init; } = SchemaId;

    /// <summary>包类型：full / incremental / updater。</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>清单声明的应用键（可选；出现即校验）。</summary>
    public string? AppKey { get; init; }

    /// <summary>清单声明的渠道键（可选；老格式的 branch 映射到此）。</summary>
    public string? ChannelKey { get; init; }

    /// <summary>目标版本（必填）。</summary>
    public string? Version { get; init; }

    /// <summary>目标运行时（可选；full 包可为空或 any）。</summary>
    public string? RuntimeIdentifier { get; init; }

    /// <summary>发布说明（可选；登记草稿版本时作为初始 notes）。</summary>
    public string? Description { get; init; }

    /// <summary>构建时间（可选）。</summary>
    public DateTimeOffset? PublishTime { get; init; }

    /// <summary>GitVersion 原文（可选，展示用）。</summary>
    public string? GitVersion { get; init; }

    /// <summary>Git 提交（可选，展示用）。</summary>
    public string? GitCommit { get; init; }

    /// <summary>入口文件（更新器 / 安装器可选）。</summary>
    public string? EntryPoint { get; init; }

    /// <summary>携带的测试模块 Id（展示用，不参与路由）。</summary>
    public IReadOnlyList<string> Modules { get; init; } = [];

    /// <summary>携带的硬件类型 Id（展示用，不参与路由）。</summary>
    public IReadOnlyList<string> Hardware { get; init; } = [];

    /// <summary>完整目标文件集（增量包必填；full/updater 可为空）。</summary>
    public IReadOnlyList<ReleaseFileDto> Files { get; init; } = [];

    /// <summary>清单来源：embedded / zip / sidecar / legacy / manual。</summary>
    public string Source { get; init; } = VersionConventions.ManifestSourceEmbedded;

    /// <summary>老格式或未知字段（保留展示，不参与路由）。</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = EmptyLabels;

    /// <summary>解析期间产生的兼容性提示（非致命）。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>是否携带文件清单。</summary>
    public bool HasFiles => Files.Count > 0;

    /// <summary>文件条目按路径排序后的清单哈希（与 ReleaseArtifact 同算法）。</summary>
    public string ManifestHash => ReleaseManifest.Compute(Files);

    /// <summary>是否为已知包类型。</summary>
    public static bool IsKnownKind(string? kind) =>
        kind is VersionConventions.PackageKindFull
            or VersionConventions.PackageKindIncremental
            or VersionConventions.PackageKindUpdater;

    /// <summary>版本号是否合法（三段/四段 + 可选预发布与构建元数据）。</summary>
    public static bool IsValidVersion(string? version) => VersionComparer.TryParse(version, out _);

    /// <summary>
    /// 解析 <c>af-package/1</c> JSON。缺少 schema 时按本协议解析并给出 warning；
    /// 结构与安全校验失败时 <paramref name="error"/> 可直接展示给管理员。
    /// </summary>
    public static bool TryParseJson(
        string? raw,
        string source,
        out PackageManifest? manifest,
        out string? error)
    {
        manifest = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "清单为空";
            return false;
        }

        if (raw.Length > MaxBytes)
        {
            error = $"清单超过 {MaxBytes} 字节上限";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw, DocumentOptions);
        }
        catch (JsonException exception)
        {
            error = $"清单 JSON 解析失败：{exception.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "清单根必须是 JSON 对象";
                return false;
            }

            var warnings = new List<string>();
            var schema = PackageJson.GetString(root, "schema");
            if (schema.Length == 0)
            {
                schema = PackageJson.GetString(root, "schemaId");
            }

            if (schema.Length > 0 && !string.Equals(schema, SchemaId, StringComparison.OrdinalIgnoreCase))
            {
                error = $"清单 schema 不识别（{schema}），应为 {SchemaId}";
                return false;
            }

            if (schema.Length == 0)
            {
                warnings.Add($"清单未声明 schema，按 {SchemaId} 解析。");
            }

            var kind = PackageJson.GetString(root, "kind").ToLowerInvariant();
            if (!IsKnownKind(kind))
            {
                error = "清单 kind 必须是 full / incremental / updater 之一";
                return false;
            }

            var version = PackageJson.GetString(root, "version").Trim();
            if (!IsValidVersion(version))
            {
                error = "清单缺少合法的 version 字段";
                return false;
            }

            var runtimeIdentifier = FirstNonEmpty(
                PackageJson.GetString(root, "runtimeIdentifier"),
                PackageJson.GetString(root, "rid"));
            if (runtimeIdentifier.Length > 0
                && !VersionConventions.IsSupportedRuntime(runtimeIdentifier)
                && !string.Equals(runtimeIdentifier, VersionConventions.PackageTargetAny, StringComparison.OrdinalIgnoreCase))
            {
                error = $"清单 runtimeIdentifier 不支持：{runtimeIdentifier}";
                return false;
            }

            var publishTime = ParsePublishTime(PackageJson.GetString(root, "publishTime"), warnings);
            var description = PackageJson.GetString(root, "description").Trim();
            if (description.Length > VersionConventions.MaxDescriptionChars)
            {
                error = $"清单 description 超过 {VersionConventions.MaxDescriptionChars} 个字符";
                return false;
            }
            var modules = PackageJson.GetStringArray(root, "modules");
            var hardware = PackageJson.GetStringArray(root, "hardware");
            if (root.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object)
            {
                if (modules.Length == 0)
                {
                    modules = PackageJson.GetStringArray(capabilities, "modules");
                }

                if (hardware.Length == 0)
                {
                    hardware = PackageJson.GetStringArray(capabilities, "hardware");
                }
            }

            IReadOnlyList<ReleaseFileDto> files = [];
            if (root.TryGetProperty("files", out var filesElement))
            {
                if (!PackageJson.TryParseFiles(filesElement, out var parsedFiles, out error))
                {
                    return false;
                }

                files = parsedFiles;
            }

            if (kind == VersionConventions.PackageKindIncremental && files.Count == 0)
            {
                error = "kind=incremental 必须带非空 files";
                return false;
            }

            manifest = new PackageManifest
            {
                Schema = SchemaId,
                Kind = kind,
                AppKey = NullIfEmpty(PackageJson.GetString(root, "appKey")),
                ChannelKey = NullIfEmpty(FirstNonEmpty(
                    PackageJson.GetString(root, "channelKey"),
                    PackageJson.GetString(root, "channel"))),
                Version = version,
                RuntimeIdentifier = NullIfEmpty(runtimeIdentifier),
                Description = NullIfEmpty(description),
                PublishTime = publishTime,
                GitVersion = NullIfEmpty(PackageJson.GetString(root, "gitVersion")),
                GitCommit = NullIfEmpty(PackageJson.GetString(root, "gitCommit")),
                EntryPoint = NullIfEmpty(PackageJson.GetString(root, "entryPoint")),
                Modules = modules,
                Hardware = hardware,
                Files = files,
                Source = source,
                Warnings = warnings,
            };
            return true;
        }
    }

    /// <summary>解析 exe 的 <c>AF-PACKAGE/1|k=v|…</c> 管道串（不支持文件清单）。</summary>
    public static bool TryParsePipe(
        string? raw,
        string source,
        out PackageManifest? manifest,
        out string? error)
    {
        manifest = null;
        error = null;
        var text = raw?.Trim() ?? string.Empty;
        if (!text.StartsWith(SchemaId + "|", StringComparison.OrdinalIgnoreCase))
        {
            error = $"清单前缀不是 {SchemaId}";
            return false;
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

        var kind = fields.GetValueOrDefault("kind", string.Empty).ToLowerInvariant();
        if (!IsKnownKind(kind))
        {
            error = "清单 kind 必须是 full / incremental / updater 之一";
            return false;
        }

        if (kind == VersionConventions.PackageKindIncremental)
        {
            error = "exe 管道清单无法携带 files，增量包请用 zip + af-package.json";
            return false;
        }

        var version = fields.GetValueOrDefault("version", string.Empty).Trim();
        if (!IsValidVersion(version))
        {
            error = "清单缺少合法的 version 字段";
            return false;
        }

        var runtimeIdentifier = FirstNonEmpty(
            fields.GetValueOrDefault("runtimeIdentifier", string.Empty),
            fields.GetValueOrDefault("rid", string.Empty));
        if (runtimeIdentifier.Length > 0
            && !VersionConventions.IsSupportedRuntime(runtimeIdentifier)
            && !string.Equals(runtimeIdentifier, VersionConventions.PackageTargetAny, StringComparison.OrdinalIgnoreCase))
        {
            error = $"清单 runtimeIdentifier 不支持：{runtimeIdentifier}";
            return false;
        }

        var warnings = new List<string>();
        var description = fields.GetValueOrDefault("description", string.Empty).Trim();
        if (description.Length > VersionConventions.MaxDescriptionChars)
        {
            error = $"清单 description 超过 {VersionConventions.MaxDescriptionChars} 个字符";
            return false;
        }

        var publishTime = ParsePublishTime(fields.GetValueOrDefault("publishTime", string.Empty), warnings);
        manifest = new PackageManifest
        {
            Schema = SchemaId,
            Kind = kind,
            AppKey = NullIfEmpty(fields.GetValueOrDefault("appKey", string.Empty)),
            ChannelKey = NullIfEmpty(FirstNonEmpty(
                fields.GetValueOrDefault("channelKey", string.Empty),
                fields.GetValueOrDefault("channel", string.Empty))),
            Version = version,
            RuntimeIdentifier = NullIfEmpty(runtimeIdentifier),
            Description = NullIfEmpty(description),
            PublishTime = publishTime,
            GitVersion = NullIfEmpty(fields.GetValueOrDefault("gitVersion", string.Empty)),
            GitCommit = NullIfEmpty(fields.GetValueOrDefault("gitCommit", string.Empty)),
            EntryPoint = NullIfEmpty(fields.GetValueOrDefault("entryPoint", string.Empty)),
            Modules = PackageJson.SplitIds(fields.GetValueOrDefault("modules", string.Empty)),
            Hardware = PackageJson.SplitIds(fields.GetValueOrDefault("hardware", string.Empty)),
            Source = source,
            Warnings = warnings,
        };
        return true;
    }

    /// <summary>转为管理台/审计用的 DTO。</summary>
    public PackageManifestDto ToDto() => new(
        Schema,
        Kind,
        AppKey,
        ChannelKey,
        Version,
        RuntimeIdentifier,
        PublishTime,
        GitVersion,
        GitCommit,
        EntryPoint,
        Modules,
        Hardware,
        ManifestHash,
        Files,
        Description);

    /// <summary>规范化为存储用 JSON（可再次解析，<see cref="ManifestHash"/> 不变）。</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("kind", Kind);
            WriteOptional(writer, "appKey", AppKey);
            WriteOptional(writer, "channelKey", ChannelKey);
            WriteOptional(writer, "version", Version);
            WriteOptional(writer, "runtimeIdentifier", RuntimeIdentifier);
            WriteOptional(writer, "description", Description);
            if (PublishTime is { } publishTime)
            {
                writer.WriteString("publishTime", publishTime.ToString("O", CultureInfo.InvariantCulture));
            }

            WriteOptional(writer, "gitVersion", GitVersion);
            WriteOptional(writer, "gitCommit", GitCommit);
            WriteOptional(writer, "entryPoint", EntryPoint);
            writer.WriteStartObject("capabilities");
            WriteStringArray(writer, "modules", Modules);
            WriteStringArray(writer, "hardware", Hardware);
            writer.WriteEndObject();
            writer.WriteStartArray("files");
            foreach (var file in Files.OrderBy(static x => x.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteNumber("sizeBytes", file.SizeBytes);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("source", Source);
            if (Labels.Count > 0)
            {
                writer.WriteStartObject("labels");
                foreach (var (key, value) in Labels.OrderBy(static x => x.Key, StringComparer.Ordinal))
                {
                    writer.WriteString(key, value);
                }

                writer.WriteEndObject();
            }

            WriteStringArray(writer, "warnings", Warnings);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static DateTimeOffset? ParsePublishTime(string? raw, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                raw.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value))
        {
            return value;
        }

        warnings.Add($"publishTime 无法解析（{raw}），已忽略。");
        return null;
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string FirstNonEmpty(string first, string second) =>
        first.Length > 0 ? first.Trim() : second.Trim();

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

/// <summary>清单 JSON 的字段读取与文件条目解析（新旧格式共用）。</summary>
internal static class PackageJson
{
    public static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    public static string[] GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    items.Add(text);
                }
            }
        }

        return [.. items];
    }

    public static string[] SplitIds(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ';', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static Dictionary<string, string> CollectLabels(JsonElement root, HashSet<string> known)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (known.Contains(property.Name))
            {
                continue;
            }

            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty,
            };
            if (value.Length > 0)
            {
                labels[property.Name] = value.Trim();
            }
        }

        return labels;
    }

    /// <summary>解析文件条目数组：path/sha256/sizeBytes（兼容老格式的 size），归一化并检测大小写不敏感重复。</summary>
    public static bool TryParseFiles(JsonElement element, out IReadOnlyList<ReleaseFileDto> files, out string? error)
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

            var rawPath = GetString(item, "path");
            var path = PackagePaths.Normalize(rawPath);
            if (path is null)
            {
                files = [];
                error = $"文件路径非法：{rawPath}";
                return false;
            }

            if (!paths.Add(path))
            {
                files = [];
                error = $"文件路径重复：{path}";
                return false;
            }

            var sha256 = PackagePaths.NormalizeSha256(GetString(item, "sha256"));
            if (!PackagePaths.IsSha256(sha256))
            {
                files = [];
                error = $"文件 {path} 的 sha256 非法";
                return false;
            }

            var sizeBytes = GetInt64(item, "sizeBytes") ?? GetInt64(item, "size") ?? 0;
            if (sizeBytes <= 0 || sizeBytes > VersionConventions.MaxFileSizeBytes)
            {
                files = [];
                error = $"文件 {path} 的大小 {sizeBytes} 超出允许范围";
                return false;
            }

            result.Add(new ReleaseFileDto(path, sha256, sizeBytes));
        }

        if (result.Count > VersionConventions.MaxFilesPerArtifact)
        {
            files = [];
            error = $"文件数超过上限 {VersionConventions.MaxFilesPerArtifact}";
            return false;
        }

        result.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        files = result;
        return true;
    }

    private static long? GetInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
