using Platform.Kernel.Abstractions;

namespace Platform.Modules.Versions.Domain;

/// <summary>版本渠道：同一应用下的 stable / beta 等发布线，客户端按渠道检查更新。</summary>
public sealed class ReleaseChannel : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用键（隔离轴，来自令牌而不是请求参数）。</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>渠道键（应用内唯一）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>说明。</summary>
    public string? Description { get; set; }

    /// <summary>是否启用；停用后不再允许发布。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>是否允许客户端降级到本渠道的最新版本（默认否，避免归档操作意外回退现场）。</summary>
    public bool AllowDowngrade { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>该渠道下的版本。</summary>
    public List<Release> Releases { get; set; } = [];
}

/// <summary>
/// 版本。发布后客户端可见；归档即从下发序列中移除（把最新版本归档就是全量回退）。
/// </summary>
public sealed class Release : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用键。</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>所属渠道。</summary>
    public Guid ChannelId { get; set; }

    /// <summary>所属渠道导航。</summary>
    public ReleaseChannel Channel { get; set; } = null!;

    /// <summary>版本号（渠道内唯一）。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>发布说明。</summary>
    public string? Notes { get; set; }

    /// <summary>状态：draft / published / archived。</summary>
    public string Status { get; set; } = "draft";

    /// <summary>发布时间（排序与回退的依据）。</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>归档时间。</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>创建人（用户名或 API Key 主体）。</summary>
    public string? CreatedBy { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>该版本的多平台产物。</summary>
    public List<ReleaseArtifact> Artifacts { get; set; } = [];

    /// <summary>该版本的全量安装包与增量包。</summary>
    public List<ReleasePackage> Packages { get; set; } = [];
}

/// <summary>某个版本在单个运行时（win-x64 等）下的产物与清单摘要。</summary>
public sealed class ReleaseArtifact : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属版本。</summary>
    public Guid ReleaseId { get; set; }

    /// <summary>所属版本导航。</summary>
    public Release Release { get; set; } = null!;

    /// <summary>运行时标识（win-x64 / linux-arm64 …）。</summary>
    public string RuntimeIdentifier { get; set; } = string.Empty;

    /// <summary>清单哈希：文件条目按路径排序后的规范化摘要，客户端可先比对再决定是否拉清单。</summary>
    public string ManifestHash { get; set; } = string.Empty;

    /// <summary>文件数。</summary>
    public int FileCount { get; set; }

    /// <summary>所有文件字节数之和。</summary>
    public long TotalSizeBytes { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>文件条目。</summary>
    public List<ReleaseFile> Files { get; set; } = [];
}

/// <summary>文件条目：路径、内容哈希与长度。内容本体按哈希存放在 Blob 存储里。</summary>
public sealed class ReleaseFile : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属产物。</summary>
    public Guid ArtifactId { get; set; }

    /// <summary>所属产物导航。</summary>
    public ReleaseArtifact Artifact { get; set; } = null!;

    /// <summary>相对路径（正斜杠，不含 .. ）。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>内容 SHA-256（小写十六进制）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>字节数。</summary>
    public long SizeBytes { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 发布包：全量安装包或文件级增量包。整包字节仍按内容寻址存放；增量包经异步摄取后
/// 把文件清单落到 <see cref="ReleaseArtifact"/> 与 <see cref="ReleaseFile"/>。
/// </summary>
public sealed class ReleasePackage : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用键。</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>所属版本。</summary>
    public Guid ReleaseId { get; set; }

    /// <summary>所属版本导航。</summary>
    public Release Release { get; set; } = null!;

    /// <summary>包类型：full / incremental。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>目标运行时；全量包可为 any。</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>包格式：exe / msi / msp / zip / 7z / bin。</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>上传原文件名（下载时 Content-Disposition 用）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>整包内容 SHA-256（小写十六进制）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>整包字节数。</summary>
    public long SizeBytes { get; set; }

    /// <summary>规范化清单的 SHA-256；无清单的人工登记为 null。</summary>
    public string? ManifestHash { get; set; }

    /// <summary>规范化清单原文（JSON）。</summary>
    public string? ManifestJson { get; set; }

    /// <summary>清单来源：embedded / zip / sidecar / manual / legacy。</summary>
    public string ManifestSource { get; set; } = string.Empty;

    /// <summary>摄取状态：pending / processing / ready / failed。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>登记时是否请求摄取完成后自动发布（仅增量包有意义）。</summary>
    public bool PublishRequested { get; set; }

    /// <summary>清单文件数；无清单（人工登记的全量包）为 null。</summary>
    public int? FileCount { get; set; }

    /// <summary>失败错误码（<see cref="Platform.Contracts.ApiErrorCode"/> 的字符串形式）。</summary>
    public string? ErrorCode { get; set; }

    /// <summary>失败原因。</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>摄取尝试次数。</summary>
    public int Attempts { get; set; }

    /// <summary>Worker 认领租约到期时间。</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>认领实例标识。</summary>
    public string? LockedBy { get; set; }

    /// <summary>登记主体。</summary>
    public string? CreatedBy { get; set; }

    /// <summary>摄取完成时间。</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>更新器版本线：客户端先更新更新器，再应用增量包。与应用版本相互独立。</summary>
public sealed class AppUpdaterRelease : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用键。</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>目标运行时（RID 或 any）。</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>更新器版本号（按语义版本比较）。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>分发包文件名。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>zip 内容 SHA-256。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>zip 字节数。</summary>
    public long SizeBytes { get; set; }

    /// <summary>规范化清单摘要（可选）。</summary>
    public string? ManifestHash { get; set; }

    /// <summary>规范化清单原文（可选）。</summary>
    public string? ManifestJson { get; set; }

    /// <summary>状态：published / archived。</summary>
    public string Status { get; set; } = "published";

    /// <summary>发布说明。</summary>
    public string? Notes { get; set; }

    /// <summary>发布时间。</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>归档时间。</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>登记主体。</summary>
    public string? CreatedBy { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>上传票据：客户端先声明哈希与长度，再凭票据一次性直传内容。</summary>
public sealed class VersionUploadTicket : IAuditableEntity
{
    /// <summary>主键（即票据号）。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用键。</summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>声明的 SHA-256。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>声明的字节数。</summary>
    public long SizeBytes { get; set; }

    /// <summary>过期时间。</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>消费时间；非空即不可再用。</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>申请主体。</summary>
    public string? CreatedBy { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}
