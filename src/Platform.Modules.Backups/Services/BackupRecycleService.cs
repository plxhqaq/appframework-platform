using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Backups;
using Platform.Contracts.Retention;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Backups.Domain;
using Platform.Modules.Backups.Options;

namespace Platform.Modules.Backups.Services;

/// <summary>备份回收站：软删除行的列表、恢复与立即清理。</summary>
public interface IBackupRecycleService
{
    /// <summary>回收站分页列表；文件完好标记按当前页逐行 stat。</summary>
    Task<PageResult<DeletedBackupDto>> ListDeletedAsync(
        string appKey,
        PageQuery query,
        string? keyword,
        CancellationToken cancellationToken = default);

    /// <summary>恢复：先验文件存在；缺失抛 <see cref="ApiErrorCode.BackupsBlobReclaimed"/>。</summary>
    Task<BackupDto> RestoreAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>单条立即清理：仅接受已软删除的行，先删文件后删行。</summary>
    Task<PurgeResultDto> PurgeAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>全部清理：cutoff=now、限定当前 appKey，无视宽限期。</summary>
    Task<PurgeResultDto> PurgeAllAsync(string appKey, CancellationToken cancellationToken = default);
}

/// <summary>备份回收站实现。</summary>
public sealed class BackupRecycleService : IBackupRecycleService
{
    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly IRetentionSettingsStore _settings;
    private readonly BackupsOptions _options;

    /// <summary>构造。</summary>
    public BackupRecycleService(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IRetentionSettingsStore settings,
        IOptions<BackupsOptions> options)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _settings = settings;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<PageResult<DeletedBackupDto>> ListDeletedAsync(
        string appKey,
        PageQuery query,
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var grace = await ResolveGraceMinutesAsync(cancellationToken);
        var source = _db.Set<Backup>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey && x.DeletedAt != null);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            source = source.Where(x => EF.Functions.Like(x.Name, pattern)
                                       || (x.NodeName != null && EF.Functions.Like(x.NodeName, pattern)));
        }

        var rows = await source
            .OrderByDescending(static x => x.DeletedAt)
            .Select(static x => new { Backup = x, NamespaceKey = x.Namespace.Key })
            .ToPageResultAsync(query, cancellationToken);

        var now = _clock.UtcNow;
        var items = new List<DeletedBackupDto>(rows.Items.Count);
        foreach (var row in rows.Items)
        {
            var deletedAt = row.Backup.DeletedAt!.Value;
            var hasFile = !string.IsNullOrEmpty(row.Backup.BlobPath);
            var fileIntact = !hasFile || await _blobs.ExistsAsync(row.Backup.BlobPath!, cancellationToken);
            var remaining = (int)Math.Ceiling((deletedAt.AddMinutes(grace) - now).TotalMinutes);
            items.Add(new DeletedBackupDto(
                row.Backup.Id,
                row.NamespaceKey,
                row.Backup.Kind,
                row.Backup.Name,
                row.Backup.SizeBytes,
                hasFile,
                fileIntact,
                deletedAt,
                Math.Max(0, remaining)));
        }

        return PageResult<DeletedBackupDto>.Create(items, rows.Total, query);
    }

    /// <inheritdoc />
    public async Task<BackupDto> RestoreAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<Backup>()
                      .Include(static x => x.Namespace)
                      .FirstOrDefaultAsync(
                          x => x.Id == id && x.AppKey == appKey && x.DeletedAt != null,
                          cancellationToken)
                  ?? throw new BusinessException(
                      ApiErrorCode.BackupsNotFound,
                      $"备份 {id} 不在回收站中。");

        if (!string.IsNullOrEmpty(row.BlobPath)
            && !await _blobs.ExistsAsync(row.BlobPath, cancellationToken))
        {
            throw new BusinessException(
                ApiErrorCode.BackupsBlobReclaimed,
                "物理文件已被回收，无法恢复；请改用『立即清理』删除该记录。");
        }

        row.DeletedAt = null;
        await _db.SaveChangesAsync(cancellationToken);
        return BackupService.ToDto(row, row.Namespace.Key);
    }

    /// <inheritdoc />
    public async Task<PurgeResultDto> PurgeAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<Backup>()
                      .FirstOrDefaultAsync(
                          x => x.Id == id && x.AppKey == appKey && x.DeletedAt != null,
                          cancellationToken)
                  ?? throw new BusinessException(
                      ApiErrorCode.BackupsNotFound,
                      $"备份 {id} 不在回收站中，只有已软删除的备份可以立即清理。");

        var files = 0;
        if (!string.IsNullOrEmpty(row.BlobPath))
        {
            await _blobs.DeleteAsync(row.BlobPath, cancellationToken);
            files = 1;
        }

        _db.Set<Backup>().Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return new PurgeResultDto(1, files);
    }

    /// <inheritdoc />
    public async Task<PurgeResultDto> PurgeAllAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var result = await BackupPurgeExecutor.PurgeAsync(
            _db, _blobs, _clock.UtcNow, appKey, cancellationToken);
        return new PurgeResultDto(result.RowsDeleted, result.FilesDeleted);
    }

    private async Task<int> ResolveGraceMinutesAsync(CancellationToken cancellationToken) =>
        await _settings.GetGraceMinutesAsync(PlatformModuleKeys.Backups, cancellationToken)
        ?? _options.CleanupGraceMinutes;
}
