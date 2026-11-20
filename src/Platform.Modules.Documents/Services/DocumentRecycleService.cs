using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Documents;
using Platform.Contracts.Retention;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Documents.Domain;
using Platform.Modules.Documents.Options;

namespace Platform.Modules.Documents.Services;

/// <summary>文档回收站：软删除文档与全部修订的列表、恢复与立即清理。</summary>
public interface IDocumentRecycleService
{
    /// <summary>回收站分页列表；文件完好 = 文档当前文件与全部修订文件都存在。</summary>
    Task<PageResult<DeletedDocumentDto>> ListDeletedAsync(
        string appKey,
        PageQuery query,
        string? keyword,
        CancellationToken cancellationToken = default);

    /// <summary>恢复文档与全部修订；任一文件缺失抛 <see cref="ApiErrorCode.DocumentsFileReclaimed"/>。</summary>
    Task<DocumentSummaryDto> RestoreAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>单条立即清理：先删全部修订文件与文档文件，再删修订行与文档行。</summary>
    Task<PurgeResultDto> PurgeAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>全部清理：cutoff=now、限定当前 appKey，无视宽限期。</summary>
    Task<PurgeResultDto> PurgeAllAsync(string appKey, CancellationToken cancellationToken = default);
}

/// <summary>文档回收站实现。</summary>
public sealed class DocumentRecycleService : IDocumentRecycleService
{
    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly IRetentionSettingsStore _settings;
    private readonly DocumentsOptions _options;
    private readonly DocumentChangeNotifier _notifier;

    /// <summary>构造。</summary>
    public DocumentRecycleService(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IRetentionSettingsStore settings,
        IOptions<DocumentsOptions> options,
        DocumentChangeNotifier notifier)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _settings = settings;
        _options = options.Value;
        _notifier = notifier;
    }

    /// <inheritdoc />
    public async Task<PageResult<DeletedDocumentDto>> ListDeletedAsync(
        string appKey,
        PageQuery query,
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var grace = await ResolveGraceMinutesAsync(cancellationToken);
        var source = _db.Set<Document>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey && x.DeletedAt != null);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            source = source.Where(x => EF.Functions.Like(x.Title, pattern)
                                       || EF.Functions.Like(x.FileName, pattern));
        }

        var rows = await source
            .OrderByDescending(static x => x.DeletedAt)
            .Select(static x => new { Document = x, CollectionKey = x.Collection.Key })
            .ToPageResultAsync(query, cancellationToken);

        var ids = rows.Items.Select(static x => x.Document.Id).ToList();
        var revisionPaths = ids.Count == 0
            ? []
            : await _db.Set<DocumentRevision>()
                .AsNoTracking()
                .Where(x => ids.Contains(x.DocumentId))
                .Select(static x => new { x.DocumentId, x.BlobPath })
                .ToListAsync(cancellationToken);

        var now = _clock.UtcNow;
        var items = new List<DeletedDocumentDto>(rows.Items.Count);
        foreach (var row in rows.Items)
        {
            var deletedAt = row.Document.DeletedAt!.Value;
            var paths = revisionPaths
                .Where(x => x.DocumentId == row.Document.Id)
                .Select(static x => x.BlobPath)
                .Append(row.Document.BlobPath)
                .Where(static p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.Ordinal);

            var fileIntact = true;
            foreach (var path in paths)
            {
                if (!await _blobs.ExistsAsync(path, cancellationToken))
                {
                    fileIntact = false;
                    break;
                }
            }

            var remaining = (int)Math.Ceiling((deletedAt.AddMinutes(grace) - now).TotalMinutes);
            items.Add(new DeletedDocumentDto(
                row.Document.Id,
                row.CollectionKey,
                row.Document.Title,
                row.Document.Visibility,
                row.Document.Revision,
                row.Document.FileName,
                row.Document.SizeBytes,
                fileIntact,
                deletedAt,
                Math.Max(0, remaining)));
        }

        return PageResult<DeletedDocumentDto>.Create(items, rows.Total, query);
    }

    /// <inheritdoc />
    public async Task<DocumentSummaryDto> RestoreAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<Document>()
                      .Include(static x => x.Collection)
                      .Include(static x => x.Revisions)
                      .FirstOrDefaultAsync(
                          x => x.Id == id && x.AppKey == appKey && x.DeletedAt != null,
                          cancellationToken)
                  ?? throw new BusinessException(
                      ApiErrorCode.DocumentsNotFound,
                      $"文档 {id} 不在回收站中。");

        var paths = new List<string>();
        if (!string.IsNullOrEmpty(row.BlobPath))
        {
            paths.Add(row.BlobPath);
        }

        paths.AddRange(row.Revisions
            .Select(static x => x.BlobPath)
            .Where(static p => !string.IsNullOrEmpty(p)));

        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (!await _blobs.ExistsAsync(path, cancellationToken))
            {
                throw new BusinessException(
                    ApiErrorCode.DocumentsFileReclaimed,
                    "物理文件已被回收，无法恢复；请改用『立即清理』删除该记录。");
            }
        }

        row.DeletedAt = null;
        foreach (var revision in row.Revisions)
        {
            revision.DeletedAt = null;
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (string.Equals(row.Visibility, DocumentConventions.VisibilityClient, StringComparison.Ordinal))
        {
            await _notifier.NotifyClientChangedAsync(appKey, row.Collection.Key, cancellationToken);
        }

        return DocumentService.ToSummary(row, row.Collection.Key);
    }

    /// <inheritdoc />
    public async Task<PurgeResultDto> PurgeAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<Document>()
                      .Include(static x => x.Revisions)
                      .FirstOrDefaultAsync(
                          x => x.Id == id && x.AppKey == appKey && x.DeletedAt != null,
                          cancellationToken)
                  ?? throw new BusinessException(
                      ApiErrorCode.DocumentsNotFound,
                      $"文档 {id} 不在回收站中，只有已软删除的文档可以立即清理。");

        var paths = row.Revisions
            .Select(static x => x.BlobPath)
            .Append(row.BlobPath)
            .Where(static p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var path in paths)
        {
            await _blobs.DeleteAsync(path, cancellationToken);
        }

        _db.Set<DocumentRevision>().RemoveRange(row.Revisions);
        _db.Set<Document>().Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return new PurgeResultDto(row.Revisions.Count + 1, paths.Count);
    }

    /// <inheritdoc />
    public async Task<PurgeResultDto> PurgeAllAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var result = await DocumentPurgeExecutor.PurgeAsync(
            _db, _blobs, _clock.UtcNow, appKey, cancellationToken);
        return new PurgeResultDto(result.RowsDeleted, result.FilesDeleted);
    }

    private async Task<int> ResolveGraceMinutesAsync(CancellationToken cancellationToken) =>
        await _settings.GetGraceMinutesAsync(PlatformModuleKeys.Documents, cancellationToken)
        ?? _options.CleanupGraceMinutes;
}
