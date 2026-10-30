namespace Platform.Contracts.Documents;

/// <summary>文档模块常量与校验口径（服务端与客户端共用）。</summary>
public static class DocumentConventions
{
    /// <summary>集合键：小写字母开头，2–32 位小写字母/数字/连字符。</summary>
    public const string CollectionKeyPattern = "^[a-z][a-z0-9-]{1,31}$";

    /// <summary>客户端可见：出现在客户端清单，可下载。</summary>
    public const string VisibilityClient = "client";

    /// <summary>仅内部：管理端可见，客户端清单与下载一律拒绝。</summary>
    public const string VisibilityInternal = "internal";

    /// <summary>默认单文件上限（200 MB），与旧实现一致。</summary>
    public const long MaxFileSizeBytes = 200L * 1024 * 1024;

    /// <summary>文件名最大长度。</summary>
    public const int MaxFileNameLength = 256;

    /// <summary>是否合法的可见性。</summary>
    public static bool IsValidVisibility(string? visibility) =>
        visibility is VisibilityClient or VisibilityInternal;

    /// <summary>默认可见性：最小权限，需显式放开给客户端。</summary>
    public const string DefaultVisibility = VisibilityInternal;
}

/// <summary>文档集合：一类文档的分组（手册、API 文档、客户端资料 …）。</summary>
public sealed record DocumentCollectionDto(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    bool IsEnabled,
    int DocumentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>创建集合；键创建后不可修改。</summary>
public sealed record CreateDocumentCollectionRequest(string? Key, string? Name, string? Description);

/// <summary>更新集合；缺省字段表示不修改。</summary>
public sealed record UpdateDocumentCollectionRequest(string? Name, string? Description, bool? IsEnabled);

/// <summary>文档修订：每次上传生成一条，旧修订保留可追溯。</summary>
public sealed record DocumentRevisionDto(
    Guid Id,
    int Revision,
    string FileName,
    string? ContentType,
    long SizeBytes,
    string Sha256,
    string? CreatedBy,
    DateTimeOffset CreatedAt,
    string DownloadUrl);

/// <summary>文档摘要（列表用）。</summary>
public sealed record DocumentSummaryDto(
    Guid Id,
    string CollectionKey,
    string Title,
    string Visibility,
    int Revision,
    string FileName,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>文档详情：最新修订 + 全部修订历史。</summary>
public sealed record DocumentDetailDto(
    Guid Id,
    string CollectionKey,
    string Title,
    string Visibility,
    int Revision,
    string FileName,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DocumentRevisionDto> Revisions);

/// <summary>更新文档元数据（不产生新修订）。</summary>
public sealed record UpdateDocumentRequest(string? Title, string? Visibility);

/// <summary>客户端文档清单条目；下载地址由服务端生成，客户端不得自行拼接。</summary>
public sealed record ClientDocumentDto(
    Guid DocumentId,
    string CollectionKey,
    string Title,
    int Revision,
    string FileName,
    string? ContentType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset UpdatedAt,
    string DownloadUrl);

/// <summary>客户端文档清单：只包含可见性为 <c>client</c> 且集合启用的文档。</summary>
public sealed record ClientDocumentManifest(IReadOnlyList<ClientDocumentDto> Items, DateTimeOffset GeneratedAt);
