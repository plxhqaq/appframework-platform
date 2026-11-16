using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Backups;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Errors;
using Platform.Modules.Backups.Domain;
using Platform.Modules.Backups.Options;

namespace Platform.Modules.Backups.Services;

/// <summary>命名空间读写：键不可变，渠道与配额可调整。</summary>
public interface IBackupNamespaceService
{
    /// <summary>列出当前应用的命名空间（含未删除备份数）。</summary>
    Task<IReadOnlyList<BackupNamespaceDto>> ListAsync(string appKey, CancellationToken cancellationToken = default);

    /// <summary>创建命名空间；键重复抛 <see cref="ApiErrorCode.BackupsNamespaceKeyExists"/>。</summary>
    Task<BackupNamespaceDto> CreateAsync(
        string appKey,
        CreateBackupNamespaceRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>更新名称/说明/渠道/配额/启用状态。</summary>
    Task<BackupNamespaceDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateBackupNamespaceRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>按应用 + 键读取启用中的命名空间；不存在抛 <see cref="ApiErrorCode.BackupsNamespaceNotFound"/>。</summary>
    Task<BackupNamespace> GetEnabledAsync(string appKey, string key, CancellationToken cancellationToken = default);

    /// <summary>幂等确保命名空间存在（已存在不改动）；用于客户端首次按设备上传。</summary>
    Task EnsureAsync(
        string appKey,
        string key,
        string name,
        CancellationToken cancellationToken = default);
}

/// <summary>命名空间服务实现。</summary>
public sealed partial class BackupNamespaceService : IBackupNamespaceService
{
    private readonly PlatformDbContext _db;
    private readonly BackupsOptions _options;

    /// <summary>构造。</summary>
    public BackupNamespaceService(PlatformDbContext db, IOptions<BackupsOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    [GeneratedRegex(BackupConventions.KeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();

    /// <inheritdoc />
    public async Task<IReadOnlyList<BackupNamespaceDto>> ListAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.Set<BackupNamespace>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey)
            .OrderBy(static x => x.Key)
            .Select(static x => new
            {
                Namespace = x,
                Count = x.Backups.Count(static b => b.DeletedAt == null),
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(static row => ToDto(row.Namespace, row.Count))];
    }

    /// <inheritdoc />
    public async Task<BackupNamespaceDto> CreateAsync(
        string appKey,
        CreateBackupNamespaceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = request.Key?.Trim();
        if (string.IsNullOrEmpty(key) || !KeyRegex().IsMatch(key))
        {
            throw BusinessException.Invalid("命名空间键格式：小写字母开头，2–32 位小写字母/数字/连字符。");
        }

        var name = RequireName(request.Name);
        var channel = NormalizeChannel(request.Channel);
        var maxVersions = NormalizeMaxVersions(request.MaxVersions);

        if (await _db.Set<BackupNamespace>()
                .AnyAsync(x => x.AppKey == appKey && x.Key == key, cancellationToken))
        {
            throw new BusinessException(ApiErrorCode.BackupsNamespaceKeyExists, $"命名空间 {key} 已存在。");
        }

        var entity = new BackupNamespace
        {
            AppKey = appKey,
            Key = key,
            Name = name,
            Description = TrimTo(request.Description, 1024),
            Channel = channel,
            MaxVersions = maxVersions,
        };

        _db.Set<BackupNamespace>().Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(entity, 0);
    }

    /// <inheritdoc />
    public async Task<BackupNamespaceDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateBackupNamespaceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entity = await _db.Set<BackupNamespace>()
                         .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                     ?? throw new BusinessException(
                         ApiErrorCode.BackupsNamespaceNotFound,
                         $"命名空间 {id} 不存在。");

        if (request.Name is not null)
        {
            entity.Name = RequireName(request.Name);
        }

        if (request.Description is not null)
        {
            entity.Description = TrimTo(request.Description, 1024);
        }

        if (request.Channel is not null)
        {
            entity.Channel = NormalizeChannel(request.Channel);
        }

        if (request.MaxVersions is not null)
        {
            entity.MaxVersions = NormalizeMaxVersions(request.MaxVersions);
        }

        if (request.IsEnabled is not null)
        {
            entity.IsEnabled = request.IsEnabled.Value;
        }

        await _db.SaveChangesAsync(cancellationToken);

        var count = await _db.Set<Backup>()
            .CountAsync(x => x.NamespaceId == id && x.DeletedAt == null, cancellationToken);
        return ToDto(entity, count);
    }

    /// <inheritdoc />
    public async Task<BackupNamespace> GetEnabledAsync(
        string appKey,
        string key,
        CancellationToken cancellationToken = default)
    {
        var entity = await _db.Set<BackupNamespace>()
                         .FirstOrDefaultAsync(x => x.AppKey == appKey && x.Key == key, cancellationToken)
                     ?? throw new BusinessException(
                         ApiErrorCode.BackupsNamespaceNotFound,
                         $"命名空间 {key} 不存在。");

        if (!entity.IsEnabled)
        {
            throw new BusinessException(ApiErrorCode.BackupsNamespaceNotFound, $"命名空间 {key} 已停用。");
        }

        return entity;
    }

    /// <inheritdoc />
    public async Task EnsureAsync(
        string appKey,
        string key,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (await _db.Set<BackupNamespace>()
                .AnyAsync(x => x.AppKey == appKey && x.Key == key, cancellationToken))
        {
            return;
        }

        var entity = new BackupNamespace
        {
            AppKey = appKey,
            Key = key,
            Name = name,
            Channel = BackupConventions.DefaultChannel,
            MaxVersions = _options.DefaultMaxVersions,
        };

        _db.Set<BackupNamespace>().Add(entity);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 并发首次上传：唯一索引兜底，另一个请求已创建，视为成功。
            _db.ChangeTracker.Clear();
        }
    }

    private static string RequireName(string? name)
    {
        var value = name?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 256)
        {
            throw BusinessException.Invalid("命名空间名称必填且不超过 256 个字符。");
        }

        return value;
    }

    private static string NormalizeChannel(string? channel)
    {
        var value = BackupConventions.NormalizeChannel(channel);
        return value.Length > 64
            ? throw BusinessException.Invalid("渠道名不超过 64 个字符。")
            : value;
    }

    private int NormalizeMaxVersions(int? maxVersions)
    {
        var value = maxVersions ?? _options.DefaultMaxVersions;
        if (value < 1 || value > BackupConventions.MaxVersionsLimit)
        {
            throw BusinessException.Invalid(
                $"保留版本数必须在 1–{BackupConventions.MaxVersionsLimit} 之间。");
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

    private static BackupNamespaceDto ToDto(BackupNamespace entity, int backupCount) =>
        new(
            entity.Id,
            entity.Key,
            entity.Name,
            entity.Description,
            entity.Channel,
            entity.MaxVersions,
            entity.IsEnabled,
            backupCount,
            entity.CreatedAt,
            entity.UpdatedAt);
}
