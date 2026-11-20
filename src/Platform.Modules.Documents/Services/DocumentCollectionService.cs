using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Documents;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Errors;
using Platform.Modules.Documents.Domain;

namespace Platform.Modules.Documents.Services;

/// <summary>文档集合读写：键不可变，名称/说明/启用状态可调整。</summary>
public interface IDocumentCollectionService
{
    /// <summary>列出当前应用的集合（含未删除文档数）。</summary>
    Task<IReadOnlyList<DocumentCollectionDto>> ListAsync(
        string appKey,
        CancellationToken cancellationToken = default);

    /// <summary>创建集合；键重复抛 <see cref="ApiErrorCode.DocumentsCollectionKeyExists"/>。</summary>
    Task<DocumentCollectionDto> CreateAsync(
        string appKey,
        CreateDocumentCollectionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>更新集合。</summary>
    Task<DocumentCollectionDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateDocumentCollectionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>删除集合；集合内仍有文档（含软删除）时拒绝，避免留痕连带消失。</summary>
    Task DeleteAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>按应用 + 键读取集合；不存在抛 <see cref="ApiErrorCode.DocumentsCollectionNotFound"/>。</summary>
    Task<DocumentCollection> GetAsync(
        string appKey,
        string key,
        CancellationToken cancellationToken = default);
}

/// <summary>文档集合服务实现。</summary>
public sealed partial class DocumentCollectionService : IDocumentCollectionService
{
    private readonly PlatformDbContext _db;

    /// <summary>构造。</summary>
    public DocumentCollectionService(PlatformDbContext db) => _db = db;

    [GeneratedRegex(DocumentConventions.CollectionKeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentCollectionDto>> ListAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<DocumentCollection>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey)
            .OrderBy(static x => x.Key)
            .Select(static x => new
            {
                Collection = x,
                Count = x.Documents.Count(static d => d.DeletedAt == null),
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(static row => ToDto(row.Collection, row.Count))];
    }

    /// <inheritdoc />
    public async Task<DocumentCollectionDto> CreateAsync(
        string appKey,
        CreateDocumentCollectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = request.Key?.Trim();
        if (string.IsNullOrEmpty(key) || !KeyRegex().IsMatch(key))
        {
            throw BusinessException.Invalid("集合键格式：小写字母开头，2–32 位小写字母/数字/连字符。");
        }

        var name = RequireName(request.Name);

        if (await _db.Set<DocumentCollection>()
                .AnyAsync(x => x.AppKey == appKey && x.Key == key, cancellationToken))
        {
            throw new BusinessException(
                ApiErrorCode.DocumentsCollectionKeyExists,
                $"集合 {key} 已存在。");
        }

        var entity = new DocumentCollection
        {
            AppKey = appKey,
            Key = key,
            Name = name,
            Description = TrimTo(request.Description, 1024),
        };

        _db.Set<DocumentCollection>().Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(entity, 0);
    }

    /// <inheritdoc />
    public async Task<DocumentCollectionDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateDocumentCollectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entity = await _db.Set<DocumentCollection>()
                         .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                     ?? throw new BusinessException(
                         ApiErrorCode.DocumentsCollectionNotFound,
                         $"集合 {id} 不存在。");

        if (request.Name is not null)
        {
            entity.Name = RequireName(request.Name);
        }

        if (request.Description is not null)
        {
            entity.Description = TrimTo(request.Description, 1024);
        }

        if (request.IsEnabled is not null)
        {
            entity.IsEnabled = request.IsEnabled.Value;
        }

        await _db.SaveChangesAsync(cancellationToken);

        var count = await _db.Set<Document>()
            .CountAsync(x => x.CollectionId == id && x.DeletedAt == null, cancellationToken);
        return ToDto(entity, count);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var entity = await _db.Set<DocumentCollection>()
                         .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                     ?? throw new BusinessException(
                         ApiErrorCode.DocumentsCollectionNotFound,
                         $"集合 {id} 不存在。");

        // 软删除的文档也算数：先彻底清理文档，集合才可删。
        var remaining = await _db.Set<Document>()
            .CountAsync(x => x.CollectionId == id, cancellationToken);
        if (remaining > 0)
        {
            throw new BusinessException(
                ApiErrorCode.Conflict,
                $"集合 {entity.Key} 仍有 {remaining} 篇文档（含已删除待清理），请先清空再删除集合。");
        }

        _db.Set<DocumentCollection>().Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DocumentCollection> GetAsync(
        string appKey,
        string key,
        CancellationToken cancellationToken = default)
    {
        return await _db.Set<DocumentCollection>()
                   .FirstOrDefaultAsync(x => x.AppKey == appKey && x.Key == key, cancellationToken)
               ?? throw new BusinessException(
                   ApiErrorCode.DocumentsCollectionNotFound,
                   $"集合 {key} 不存在。");
    }

    private static string RequireName(string? name)
    {
        var value = name?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 256)
        {
            throw BusinessException.Invalid("集合名称必填且不超过 256 个字符。");
        }

        return value;
    }

    private static string? TrimTo(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    private static DocumentCollectionDto ToDto(DocumentCollection entity, int documentCount) =>
        new(
            entity.Id,
            entity.Key,
            entity.Name,
            entity.Description,
            entity.IsEnabled,
            documentCount,
            entity.CreatedAt,
            entity.UpdatedAt);
}
