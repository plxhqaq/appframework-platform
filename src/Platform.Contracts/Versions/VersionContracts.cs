namespace Platform.Contracts.Versions;

/// <summary>版本模块常量与校验口径（服务端与客户端共用）。</summary>
public static class VersionConventions
{
    /// <summary>渠道键：小写字母开头，2–32 位小写字母/数字/连字符。</summary>
    public const string ChannelKeyPattern = "^[a-z][a-z0-9-]{1,31}$";

    /// <summary>受众标签：小写字母/数字开头，最长 64，可含点/下划线/冒号/连字符。</summary>
    public const string AudienceTagPattern = "^[a-z0-9][a-z0-9._:-]{0,63}$";

    /// <summary>单次检查或单个渠道声明的受众标签数上限。</summary>
    public const int MaxAudienceTags = 16;

    /// <summary>自动创建渠道的键前缀（键为前缀 + 规范化标签摘要前 8 位）。</summary>
    public const string AutoChannelKeyPrefix = "auto-";

    /// <summary>
    /// 版本号：三段或四段数字，可选预发布后缀与构建元数据
    /// （如 <c>1.2.3</c>、<c>1.2.3.0</c>、<c>1.2.3-rc.1</c>、<c>1.2.3-rc.1+build.5</c>）。
    /// </summary>
    public const string VersionPattern =
        @"^\d{1,5}\.\d{1,5}\.\d{1,5}(\.\d{1,5})?(-[0-9A-Za-z.-]{1,32})?(\+[0-9A-Za-z.-]{1,32})?$";

    /// <summary>文件路径最大长度。</summary>
    public const int MaxPathLength = 512;

    /// <summary>单个产物允许的文件数上限。</summary>
    public const int MaxFilesPerArtifact = 50_000;

    /// <summary>单个文件大小上限（4 GiB）。</summary>
    public const long MaxFileSizeBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>单个增量包（zip）大小上限（4 GiB）；上传票据按文件与包上限的较大值放行。</summary>
    public const long MaxPackageSizeBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>上传票据默认有效期（分钟）。</summary>
    public const int UploadTicketMinutes = 30;

    /// <summary>版本草稿。</summary>
    public const string StatusDraft = "draft";

    /// <summary>已发布（客户端可见）。</summary>
    public const string StatusPublished = "published";

    /// <summary>已归档（不再下发；把最新版本归档即完成全量回退）。</summary>
    public const string StatusArchived = "archived";

    /// <summary>发布包类型：全量安装包。</summary>
    public const string PackageKindFull = "full";

    /// <summary>发布包类型：文件级增量包。</summary>
    public const string PackageKindIncremental = "incremental";

    /// <summary>发布包类型：更新器分发包（独立版本线）。</summary>
    public const string PackageKindUpdater = "updater";

    /// <summary>全量包允许不绑定具体运行时，用该值表示通用目标。</summary>
    public const string PackageTargetAny = "any";

    /// <summary>包等待摄取。</summary>
    public const string PackageStatusPending = "pending";

    /// <summary>包正在被 Worker 摄取。</summary>
    public const string PackageStatusProcessing = "processing";

    /// <summary>包可用。</summary>
    public const string PackageStatusReady = "ready";

    /// <summary>包摄取失败（可重试）。</summary>
    public const string PackageStatusFailed = "failed";

    /// <summary>清单来自包内嵌元数据（PE 资源或 zip 内 JSON）。</summary>
    public const string ManifestSourceEmbedded = "embedded";

    /// <summary>清单来自 zip 内清单文件。</summary>
    public const string ManifestSourceZip = "zip";

    /// <summary>清单由请求携带的 sidecar 提供。</summary>
    public const string ManifestSourceSidecar = "sidecar";

    /// <summary>无清单，由管理员人工登记。</summary>
    public const string ManifestSourceManual = "manual";

    /// <summary>清单来自老格式（AF-MANIFEST/1 等）。</summary>
    public const string ManifestSourceLegacy = "legacy";

    /// <summary>支持的包格式。</summary>
    public static IReadOnlySet<string> PackageFormats { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "exe",
        "msi",
        "msp",
        "zip",
        "7z",
        "bin",
    };

    /// <summary>支持的运行时标识（与服务端发布脚本、客户端更新器一致）。</summary>
    public static IReadOnlySet<string> RuntimeIdentifiers { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64",
    };

    /// <summary>是否受支持的运行时标识。</summary>
    public static bool IsSupportedRuntime(string? runtimeIdentifier) =>
        !string.IsNullOrWhiteSpace(runtimeIdentifier) && RuntimeIdentifiers.Contains(runtimeIdentifier);

    /// <summary>是否合法的版本状态。</summary>
    public static bool IsValidStatus(string? status) =>
        status is StatusDraft or StatusPublished or StatusArchived;

    /// <summary>是否受支持的包类型（更新器走独立版本线，不在此列）。</summary>
    public static bool IsSupportedPackageKind(string? kind) =>
        kind is PackageKindFull or PackageKindIncremental;

    /// <summary>是否受支持的包格式。</summary>
    public static bool IsSupportedPackageFormat(string? format) =>
        !string.IsNullOrWhiteSpace(format) && PackageFormats.Contains(format);

    /// <summary>是否合法的包状态。</summary>
    public static bool IsValidPackageStatus(string? status) =>
        status is PackageStatusPending or PackageStatusProcessing or PackageStatusReady or PackageStatusFailed;
}

/// <summary>版本渠道（同一应用下的 stable / beta / canary 等）。</summary>
public sealed record ReleaseChannelDto(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    bool IsEnabled,
    int ReleaseCount,
    string? LatestVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool AllowDowngrade = false,
    IReadOnlyList<string>? AudienceTags = null,
    bool AutoCreated = false);

/// <summary>创建渠道；<see cref="AudienceTags"/> 用于客户端省略渠道键时的标签匹配。</summary>
public sealed record CreateReleaseChannelRequest(
    string? Key,
    string? Name,
    string? Description,
    bool AllowDowngrade = false,
    IReadOnlyList<string>? AudienceTags = null);

/// <summary>更新渠道；字段缺省表示不修改（<see cref="AudienceTags"/> 传空数组表示清空）。</summary>
public sealed record UpdateReleaseChannelRequest(
    string? Name,
    string? Description,
    bool? IsEnabled,
    bool? AllowDowngrade = null,
    IReadOnlyList<string>? AudienceTags = null);

/// <summary>清单条目：相对路径 + 内容哈希 + 字节数。</summary>
public sealed record ReleaseFileDto(string Path, string Sha256, long SizeBytes);

/// <summary>版本摘要（列表用）。</summary>
public sealed record ReleaseSummaryDto(
    Guid Id,
    string ChannelKey,
    string Version,
    string Status,
    string? Notes,
    int ArtifactCount,
    int FileCount,
    long TotalSizeBytes,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int PackageCount = 0,
    long PackageSizeBytes = 0);

/// <summary>单个运行时产物（含文件清单）。</summary>
public sealed record ReleaseArtifactDto(
    string RuntimeIdentifier,
    string ManifestHash,
    int FileCount,
    long TotalSizeBytes,
    IReadOnlyList<ReleaseFileDto> Files);

/// <summary>版本详情。</summary>
public sealed record ReleaseDetailDto(
    Guid Id,
    string ChannelKey,
    string Version,
    string Status,
    string? Notes,
    int ArtifactCount,
    int FileCount,
    long TotalSizeBytes,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? ArchivedAt,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ReleaseArtifactDto> Artifacts,
    IReadOnlyList<ReleasePackageDto>? Packages = null);

/// <summary>发布时提交的单个文件条目。</summary>
public sealed record ReleaseFileInput(string? Path, string? Sha256, long SizeBytes);

/// <summary>发布时提交的单个运行时产物。</summary>
public sealed record CreateReleaseArtifactRequest(
    string? RuntimeIdentifier,
    IReadOnlyList<ReleaseFileInput>? Files);

/// <summary>创建版本；<see cref="Publish"/> 为 true 时创建即发布。</summary>
public sealed record CreateReleaseRequest(
    string? ChannelKey,
    string? Version,
    string? Notes,
    bool Publish,
    IReadOnlyList<CreateReleaseArtifactRequest>? Artifacts);

/// <summary>
/// 客户端检查更新。<see cref="ChannelKey"/> 与 <see cref="AudienceTags"/> 至少给一个：
/// 给了渠道键以渠道键为准；只有标签时，客户端声明的标签须包含渠道的全部受众标签
/// （即客户端具备该渠道所需能力），多个命中时取标签最多的渠道。
/// </summary>
public sealed record CheckUpdateRequest(
    string? ChannelKey,
    string? CurrentVersion,
    string? RuntimeIdentifier,
    string? UpdaterVersion = null,
    bool AllowDowngrade = false,
    IReadOnlyList<string>? AudienceTags = null);

/// <summary>
/// 检查更新结果；无更新时 <see cref="Release"/> 仍带清单，供客户端做完整性修复。
/// <see cref="LatestVersion"/> 是渠道内语义最高版本；<see cref="IsDowngrade"/> 表示最新版本低于
/// 客户端当前版本（默认不再下发，除非请求或渠道允许降级）。
/// </summary>
public sealed record CheckUpdateResponse(
    bool HasUpdate,
    string ChannelKey,
    string? CurrentVersion,
    ReleaseManifestDto? Release,
    string? LatestVersion = null,
    bool IsDowngrade = false,
    bool HasUpdaterUpdate = false,
    UpdaterInfoDto? Updater = null,
    ReleasePackageDto? Installer = null,
    ReleasePackageDto? IncrementalPackage = null,
    string? Notes = null,
    DateTimeOffset? PublishedAt = null);

/// <summary>单个运行时产物的清单（客户端据此做文件级差异）。</summary>
public sealed record ReleaseManifestDto(
    Guid ReleaseId,
    string ChannelKey,
    string Version,
    string RuntimeIdentifier,
    string ManifestHash,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseFileDto> Files);

/// <summary>申请上传票据（先声明内容哈希与长度，再直传字节）。</summary>
public sealed record CreateUploadTicketRequest(string? Sha256, long SizeBytes);

/// <summary>上传票据；<see cref="UploadUrl"/> 为相对本服务的 PUT 地址。</summary>
public sealed record UploadTicketDto(Guid Id, string UploadUrl, DateTimeOffset ExpiresAt);

/// <summary>上传结果。<see cref="Reused"/> 表示内容已存在（内容寻址去重）。</summary>
public sealed record UploadResultDto(string Sha256, long SizeBytes, bool Reused);

/// <summary>发布包（全量安装包或文件级增量包）。</summary>
public sealed record ReleasePackageDto(
    Guid Id,
    Guid ReleaseId,
    string ChannelKey,
    string Version,
    string Kind,
    string Target,
    string Format,
    string FileName,
    string Sha256,
    long SizeBytes,
    string Status,
    string ManifestSource,
    string? ManifestHash,
    bool Reused,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? FileCount = null);

/// <summary>
/// 登记发布包：内容必须先经上传票据直传。清单优先取请求的 <see cref="ManifestSha256"/>（sidecar），
/// 否则从包内探测；<see cref="Kind"/> 为 <c>incremental</c> 时缺清单直接拒绝。
/// </summary>
public sealed record RegisterReleasePackageRequest(
    string? Sha256,
    long SizeBytes,
    string? FileName,
    string? Kind,
    string? Target = null,
    string? Format = null,
    string? ChannelKey = null,
    string? Version = null,
    string? ManifestSha256 = null,
    bool Publish = false,
    string? Notes = null);

/// <summary>规范化后的包清单（探测结果，管理台展示与审计用）。</summary>
public sealed record PackageManifestDto(
    string Schema,
    string Kind,
    string? AppKey,
    string? ChannelKey,
    string? Version,
    string? RuntimeIdentifier,
    DateTimeOffset? PublishTime,
    string? GitVersion,
    string? GitCommit,
    string? EntryPoint,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> Hardware,
    string? ManifestHash,
    IReadOnlyList<ReleaseFileDto> Files);

/// <summary>更新器版本线（独立于应用版本，客户端先更新更新器再应用增量）。</summary>
public sealed record UpdaterInfoDto(
    Guid Id,
    string Target,
    string Version,
    string FileName,
    string Sha256,
    long SizeBytes,
    string? ManifestHash,
    string Status,
    string? Notes,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>登记更新器分发包；内容同样先进上传票据。</summary>
public sealed record RegisterUpdaterRequest(
    string? Target,
    string? Version,
    string? FileName,
    string? Sha256,
    long SizeBytes,
    string? ManifestSha256 = null,
    string? Notes = null);
