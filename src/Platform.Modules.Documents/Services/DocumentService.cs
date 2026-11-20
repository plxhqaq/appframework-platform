using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Documents;
using Platform.Contracts.Webhooks;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Documents.Domain;
using Platform.Modules.Documents.Options;

namespace Platform.Modules.Documents.Services;

/// <summary>文档读写：上传/修订/元数据/软删除/下载，以及客户端可见性过滤。</summary>
public interface IDocumentService
{
    /// <summary>分页列出当前应用的文档（默认排除已软删除）。</summary>
    Task<PageResult<DocumentSummaryDto>> ListAsync(
        string appKey,
        PageQuery query,
        string? collectionKey,
        string? keyword,
        CancellationToken cancellationToken = default);

    /// <summary>文档详情（含修订历史）；不存在抛 <see cref="ApiErrorCode.DocumentsNotFound"/>。</summary>
    Task<DocumentDetailDto> GetAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>上传新文档（生成修订 1）；先落盘 pending，库提交后发布。</summary>
    Task<DocumentDetailDto> UploadAsync(
        string appKey,
        DocumentUpload upload,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>追加修订（修订号 +1）；旧修订保留可追溯。</summary>
    Task<DocumentDetailDto> AddRevisionAsync(
        string appKey,
        Guid id,
        DocumentFileUpload upload,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>更新标题/可见性（不产生新修订）。</summary>
    Task<DocumentDetailDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateDocumentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>软删除文档及其修订；物理文件由后台清理任务在宽限期后删除。</summary>
    Task<DocumentSummaryDto> DeleteAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>解析下载；<paramref name="clientOnly"/> 为真时服务端强制可见性过滤。</summary>
    Task<DocumentDownload> ResolveDownloadAsync(
        string appKey,
        Guid id,
        int? revision,
        bool clientOnly,
        CancellationToken cancellationToken = default);

    /// <summary>客户端清单：只返回可见性为 <c>client</c> 且集合启用的文档。</summary>
    Task<ClientDocumentManifest> GetClientManifestAsync(
        string appKey,
        CancellationToken cancellationToken = default);
}

/// <summary>新文档上传参数。</summary>
public sealed record DocumentUpload(
    string? CollectionKey,
    string? Title,
    string? Visibility,
    string? ApplicableVersion,
    string? FileName,
    string? ContentType,
    long SizeBytes,
    Stream Content);

/// <summary>文档文件上传参数（追加修订用）。</summary>
public sealed record DocumentFileUpload(
    string? FileName,
    string? ContentType,
    long SizeBytes,
    Stream Content);

/// <summary>下载解析结果。</summary>
public sealed record DocumentDownload(
    string BlobPath,
    string FileName,
    string? ContentType,
    string Sha256);

/// <summary>文档服务实现。</summary>
public sealed partial class DocumentService : IDocumentService
{
    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly IDocumentCollectionService _collections;
    private readonly DocumentsOptions _options;
    private readonly IPlatformEventPublisher _events;
    private readonly DocumentChangeNotifier _notifier;

    /// <summary>构造。</summary>
    public DocumentService(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IDocumentCollectionService collections,
        IOptions<DocumentsOptions> options,
        IPlatformEventPublisher events,
        DocumentChangeNotifier notifier)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _collections = collections;
        _options = options.Value;
        _events = events;
        _notifier = notifier;
    }

    [GeneratedRegex(@"[\x00-\x1f<>:""/\\|?*]", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidFileCharsRegex();

    /// <inheritdoc />
    public async Task<PageResult<DocumentSummaryDto>> ListAsync(
        string appKey,
        PageQuery query,
        string? collectionKey,
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = _db.Set<Document>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey && x.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(collectionKey))
        {
            var key = collectionKey.Trim();
            source = source.Where(x => x.Collection.Key == key);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            source = source.Where(x => EF.Functions.Like(x.Title, pattern)
                                       || EF.Functions.Like(x.FileName, pattern));
        }

        var rows = await source
            .OrderByDescending(static x => x.UpdatedAt)
            .Select(static x => new
            {
                Document = x,
                CollectionKey = x.Collection.Key,
            })
            .ToPageResultAsync(query, cancellationToken);

        return PageResult<DocumentSummaryDto>.Create(
            [.. rows.Items.Select(static row => ToSummary(row.Document, row.CollectionKey))],
            rows.Total,
            query);
    }

    /// <inheritdoc />
    public async Task<DocumentDetailDto> GetAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<Document>()
                      .AsNoTracking()
                      .Where(x => x.Id == id && x.AppKey == appKey && x.DeletedAt == null)
                      .Select(static x => new
                      {
                          Document = x,
                          CollectionKey = x.Collection.Key,
                          Revisions = x.Revisions
                              .Where(static r => r.DeletedAt == null)
                              .OrderByDescending(static r => r.Revision)
                              .ToList(),
                      })
                      .FirstOrDefaultAsync(cancellationToken)
                  ?? throw new BusinessException(ApiErrorCode.DocumentsNotFound, $"文档 {id} 不存在。");

        return ToDetail(row.Document, row.CollectionKey, row.Revisions);
    }

    /// <inheritdoc />
    public async Task<DocumentDetailDto> UploadAsync(
        string appKey,
        DocumentUpload upload,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var collectionKey = upload.CollectionKey?.Trim();
        if (string.IsNullOrEmpty(collectionKey))
        {
            throw BusinessException.Invalid("collectionKey 必填。");
        }

        var collection = await _collections.GetAsync(appKey, collectionKey, cancellationToken);
        var fileName = NormalizeFileName(upload.FileName);
        var title = NormalizeTitle(upload.Title, fileName);
        var visibility = NormalizeVisibility(upload.Visibility);
        var applicableVersion = NormalizeApplicableVersion(upload.ApplicableVersion);
        var contentType = NormalizeContentType(upload.ContentType);
        ValidateSize(upload.SizeBytes);

        var document = new Document
        {
            AppKey = appKey,
            CollectionId = collection.Id,
            Title = title,
            Visibility = visibility,
            ApplicableVersion = applicableVersion,
            Revision = 1,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = upload.SizeBytes,
            CreatedBy = actor,
        };

        var (sha256, writtenSize) = await PersistContentAsync(
            appKey, collection.Key, document.Id, 1, upload.Content, cancellationToken);

        document.Sha256 = sha256;
        document.SizeBytes = writtenSize;
        document.BlobPath = BlobPath(appKey, collection.Key, document.Id, 1);

        var revision = new DocumentRevision
        {
            DocumentId = document.Id,
            Revision = 1,
            FileName = fileName,
            ContentType = contentType,
            BlobPath = document.BlobPath,
            SizeBytes = writtenSize,
            Sha256 = sha256,
            CreatedBy = actor,
        };

        _db.Set<Document>().Add(document);
        _db.Set<DocumentRevision>().Add(revision);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await _blobs.DiscardPendingAsync(document.BlobPath, CancellationToken.None);
            throw;
        }

        try
        {
            await _blobs.PublishAsync(document.BlobPath, cancellationToken);
        }
        catch (Exception exception)
        {
            await _blobs.DiscardPendingAsync(document.BlobPath, CancellationToken.None);
            _db.Set<DocumentRevision>().Remove(revision);
            _db.Set<Document>().Remove(document);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw new BusinessException(
                ApiErrorCode.DependencyFailure,
                "文档发布失败，已回滚数据库记录。",
                exception);
        }

        await _events.PublishAsync(
            new PlatformEvent(
                WebhookConventions.EventDocumentUploaded,
                appKey,
                new
                {
                    documentId = document.Id,
                    collectionKey = collection.Key,
                    title = document.Title,
                    visibility = document.Visibility,
                    revision = document.Revision,
                },
                _clock.UtcNow),
            cancellationToken);

        if (string.Equals(document.Visibility, DocumentConventions.VisibilityClient, StringComparison.Ordinal))
        {
            await _notifier.NotifyClientChangedAsync(appKey, collection.Key, cancellationToken);
        }

        return ToDetail(document, collection.Key, [revision]);
    }

    /// <inheritdoc />
    public async Task<DocumentDetailDto> AddRevisionAsync(
        string appKey,
        Guid id,
        DocumentFileUpload upload,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var row = await _db.Set<Document>()
                      .Where(x => x.Id == id && x.AppKey == appKey && x.DeletedAt == null)
                      .Select(static x => new
                      {
                          Document = x,
                          CollectionKey = x.Collection.Key,
                          Revisions = x.Revisions
                              .Where(static r => r.DeletedAt == null)
                              .OrderByDescending(static r => r.Revision)
                              .ToList(),
                      })
                      .FirstOrDefaultAsync(cancellationToken)
                  ?? throw new BusinessException(ApiErrorCode.DocumentsNotFound, $"文档 {id} 不存在。");

        var document = row.Document;
        var fileName = NormalizeFileName(upload.FileName);
        var contentType = NormalizeContentType(upload.ContentType);
        ValidateSize(upload.SizeBytes);

        var revisionNumber = document.Revision + 1;
        var previous = new
        {
            document.Revision,
            document.FileName,
            document.ContentType,
            document.BlobPath,
            document.SizeBytes,
            document.Sha256,
        };

        var blobPath = BlobPath(appKey, row.CollectionKey, document.Id, revisionNumber);
        var (sha256, writtenSize) = await PersistContentAsync(
            appKey, row.CollectionKey, document.Id, revisionNumber, upload.Content, cancellationToken);

        var revision = new DocumentRevision
        {
            DocumentId = document.Id,
            Revision = revisionNumber,
            FileName = fileName,
            ContentType = contentType,
            BlobPath = blobPath,
            SizeBytes = writtenSize,
            Sha256 = sha256,
            CreatedBy = actor,
        };

        document.Revision = revisionNumber;
        document.FileName = fileName;
        document.ContentType = contentType;
        document.BlobPath = blobPath;
        document.SizeBytes = writtenSize;
        document.Sha256 = sha256;

        _db.Set<DocumentRevision>().Add(revision);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await _blobs.DiscardPendingAsync(blobPath, CancellationToken.None);
            throw;
        }

        try
        {
            await _blobs.PublishAsync(blobPath, cancellationToken);
        }
        catch (Exception exception)
        {
            await _blobs.DiscardPendingAsync(blobPath, CancellationToken.None);
            _db.Set<DocumentRevision>().Remove(revision);
            document.Revision = previous.Revision;
            document.FileName = previous.FileName;
            document.ContentType = previous.ContentType;
            document.BlobPath = previous.BlobPath;
            document.SizeBytes = previous.SizeBytes;
            document.Sha256 = previous.Sha256;
            await _db.SaveChangesAsync(CancellationToken.None);
            throw new BusinessException(
                ApiErrorCode.DependencyFailure,
                "文档发布失败，已回滚数据库记录。",
                exception);
        }

        var revisions = row.Revisions.Append(revision).ToList();
        if (string.Equals(document.Visibility, DocumentConventions.VisibilityClient, StringComparison.Ordinal))
        {
            await _notifier.NotifyClientChangedAsync(appKey, row.CollectionKey, cancellationToken);
        }

        return ToDetail(document, row.CollectionKey, revisions);
    }

    /// <inheritdoc />
    public async Task<DocumentDetailDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var document = await FindTrackedAsync(appKey, id, cancellationToken);
        var previousVisibility = document.Visibility;

        if (request.Title is not null)
        {
            document.Title = NormalizeTitle(request.Title, document.FileName);
        }

        if (request.Visibility is not null)
        {
            document.Visibility = NormalizeVisibility(request.Visibility);
        }

        if (request.ApplicableVersion is not null)
        {
            document.ApplicableVersion = NormalizeApplicableVersion(request.ApplicableVersion);
        }

        await _db.SaveChangesAsync(cancellationToken);
        var detail = await GetAsync(appKey, id, cancellationToken);
        // 新旧可见性任一为 client：客户端清单集合都可能变化，需要提示回拉。
        if (string.Equals(previousVisibility, DocumentConventions.VisibilityClient, StringComparison.Ordinal)
            || string.Equals(detail.Visibility, DocumentConventions.VisibilityClient, StringComparison.Ordinal))
        {
            await _notifier.NotifyClientChangedAsync(appKey, detail.CollectionKey, cancellationToken);
        }

        return detail;
    }

    /// <inheritdoc />
    public async Task<DocumentSummaryDto> DeleteAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<Document>()
                      .Where(x => x.Id == id && x.AppKey == appKey && x.DeletedAt == null)
                      .Select(static x => new
                      {
                          Document = x,
                          CollectionKey = x.Collection.Key,
                          Revisions = x.Revisions
                              .Where(static r => r.DeletedAt == null)
                              .ToList(),
                      })
                      .FirstOrDefaultAsync(cancellationToken)
                  ?? throw new BusinessException(ApiErrorCode.DocumentsNotFound, $"文档 {id} 不存在。");

        var deletedAt = _clock.UtcNow;
        row.Document.DeletedAt = deletedAt;
        foreach (var revision in row.Revisions)
        {
            revision.DeletedAt = deletedAt;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await _events.PublishAsync(
            new PlatformEvent(
                WebhookConventions.EventDocumentDeleted,
                appKey,
                new { documentId = row.Document.Id, collectionKey = row.CollectionKey },
                deletedAt),
            cancellationToken);

        if (string.Equals(row.Document.Visibility, DocumentConventions.VisibilityClient, StringComparison.Ordinal))
        {
            await _notifier.NotifyClientChangedAsync(appKey, row.CollectionKey, cancellationToken);
        }

        return ToSummary(row.Document, row.CollectionKey);
    }

    /// <inheritdoc />
    public async Task<DocumentDownload> ResolveDownloadAsync(
        string appKey,
        Guid id,
        int? revision,
        bool clientOnly,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<Document>()
                      .AsNoTracking()
                      .Where(x => x.Id == id && x.AppKey == appKey && x.DeletedAt == null)
                      .Select(static x => new
                      {
                          Document = x,
                          Collection = x.Collection,
                      })
                      .FirstOrDefaultAsync(cancellationToken)
                  ?? throw new BusinessException(ApiErrorCode.DocumentsNotFound, $"文档 {id} 不存在。");

        if (clientOnly
            && (!row.Collection.IsEnabled
                || !string.Equals(
                    row.Document.Visibility,
                    DocumentConventions.VisibilityClient,
                    StringComparison.Ordinal)))
        {
            throw new BusinessException(ApiErrorCode.DocumentsVisibilityDenied, "该文档不可对客户端下发。");
        }

        string blobPath;
        string fileName;
        string? contentType;
        string sha256;

        if (revision is null || revision == row.Document.Revision)
        {
            blobPath = row.Document.BlobPath;
            fileName = row.Document.FileName;
            contentType = row.Document.ContentType;
            sha256 = row.Document.Sha256;
        }
        else
        {
            var revisionRow = await _db.Set<DocumentRevision>()
                                  .AsNoTracking()
                                  .FirstOrDefaultAsync(
                                      x => x.DocumentId == id
                                           && x.Revision == revision
                                           && x.DeletedAt == null,
                                      cancellationToken)
                              ?? throw new BusinessException(
                                  ApiErrorCode.DocumentsRevisionNotFound,
                                  $"文档 {id} 不存在修订 {revision}。");

            blobPath = revisionRow.BlobPath;
            fileName = revisionRow.FileName;
            contentType = revisionRow.ContentType;
            sha256 = revisionRow.Sha256;
        }

        if (!await _blobs.ExistsAsync(blobPath, cancellationToken))
        {
            throw new BusinessException(ApiErrorCode.DocumentsFileMissing, "文档文件缺失。");
        }

        return new DocumentDownload(blobPath, fileName, contentType, sha256);
    }

    /// <inheritdoc />
    public async Task<ClientDocumentManifest> GetClientManifestAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<Document>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey
                        && x.DeletedAt == null
                        && x.Collection.IsEnabled
                        && x.Visibility == DocumentConventions.VisibilityClient)
            .OrderBy(static x => x.Collection.Key)
            .ThenBy(static x => x.Title)
            .Select(static x => new
            {
                x.Id,
                CollectionKey = x.Collection.Key,
                x.Title,
                x.ApplicableVersion,
                x.Revision,
                x.FileName,
                x.ContentType,
                x.SizeBytes,
                x.Sha256,
                x.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(static x => new ClientDocumentDto(
                x.Id,
                x.CollectionKey,
                x.Title,
                x.ApplicableVersion,
                x.Revision,
                x.FileName,
                x.ContentType,
                x.SizeBytes,
                x.Sha256,
                x.UpdatedAt,
                $"/api/v1/client/documents/{x.Id:D}/download"))
            .ToList();

        return new ClientDocumentManifest(items, _clock.UtcNow);
    }

    private async Task<Document> FindTrackedAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken) =>
        await _db.Set<Document>()
            .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey && x.DeletedAt == null, cancellationToken)
        ?? throw new BusinessException(ApiErrorCode.DocumentsNotFound, $"文档 {id} 不存在。");

    private async Task<(string Sha256, long SizeBytes)> PersistContentAsync(
        string appKey,
        string collectionKey,
        Guid documentId,
        int revision,
        Stream content,
        CancellationToken cancellationToken)
    {
        var blobPath = BlobPath(appKey, collectionKey, documentId, revision);
        var written = await _blobs.WritePendingAsync(blobPath, content, cancellationToken);
        if (written.SizeBytes <= 0 || written.SizeBytes > _options.MaxFileSizeBytes)
        {
            await _blobs.DiscardPendingAsync(blobPath, CancellationToken.None);
            throw new BusinessException(
                ApiErrorCode.PayloadTooLarge,
                $"文档大小必须在 1–{_options.MaxFileSizeBytes} 字节之间。");
        }

        return (written.Sha256, written.SizeBytes);
    }

    private void ValidateSize(long sizeBytes)
    {
        if (sizeBytes <= 0)
        {
            throw BusinessException.Invalid("文件不能为空。");
        }

        if (sizeBytes > _options.MaxFileSizeBytes)
        {
            throw new BusinessException(
                ApiErrorCode.PayloadTooLarge,
                $"文档不能超过 {_options.MaxFileSizeBytes} 字节。");
        }
    }

    private static string NormalizeTitle(string? title, string fileName)
    {
        var value = title?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            value = Path.GetFileNameWithoutExtension(fileName);
        }

        if (string.IsNullOrEmpty(value) || value.Length > 256)
        {
            throw BusinessException.Invalid("标题必填且不超过 256 个字符。");
        }

        return value;
    }

    private static string NormalizeVisibility(string? visibility)
    {
        var value = visibility?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return DocumentConventions.DefaultVisibility;
        }

        return DocumentConventions.IsValidVisibility(value)
            ? value
            : throw BusinessException.Invalid("visibility 只能是 client 或 internal。");
    }

    private static string? NormalizeApplicableVersion(string? applicableVersion)
    {
        var value = applicableVersion?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length > DocumentConventions.MaxApplicableVersionLength
            ? throw BusinessException.Invalid(
                $"适用版本不超过 {DocumentConventions.MaxApplicableVersionLength} 个字符。")
            : value;
    }

    private static string NormalizeFileName(string? fileName)
    {
        var value = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        value = InvalidFileCharsRegex().Replace(value, "_").Trim();
        if (string.IsNullOrEmpty(value) || value.Length > DocumentConventions.MaxFileNameLength)
        {
            throw BusinessException.Invalid(
                $"文件名必填且不超过 {DocumentConventions.MaxFileNameLength} 个字符。");
        }

        return value;
    }

    private static string? NormalizeContentType(string? contentType)
    {
        var value = contentType?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return "application/octet-stream";
        }

        return value.Length > 128 ? value[..128] : value;
    }

    private static string BlobPath(string appKey, string collectionKey, Guid documentId, int revision) =>
        $"documents/{appKey}/{collectionKey}/{documentId:N}/r{revision:D}.bin";

    internal static DocumentSummaryDto ToSummary(Document entity, string collectionKey) =>
        new(
            entity.Id,
            collectionKey,
            entity.Title,
            entity.Visibility,
            entity.ApplicableVersion,
            entity.Revision,
            entity.FileName,
            entity.SizeBytes,
            entity.Sha256,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static DocumentDetailDto ToDetail(
        Document entity,
        string collectionKey,
        IReadOnlyList<DocumentRevision> revisions) =>
        new(
            entity.Id,
            collectionKey,
            entity.Title,
            entity.Visibility,
            entity.ApplicableVersion,
            entity.Revision,
            entity.FileName,
            entity.SizeBytes,
            entity.Sha256,
            entity.CreatedAt,
            entity.UpdatedAt,
            [.. revisions
                .OrderByDescending(static r => r.Revision)
                .Select(static r => new DocumentRevisionDto(
                    r.Id,
                    r.Revision,
                    r.FileName,
                    r.ContentType,
                    r.SizeBytes,
                    r.Sha256,
                    r.CreatedBy,
                    r.CreatedAt,
                    $"/api/v1/admin/documents/{r.DocumentId:D}/revisions/{r.Revision}/download"))]);
}
