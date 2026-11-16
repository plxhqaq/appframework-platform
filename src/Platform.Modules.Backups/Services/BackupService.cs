using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Backups;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Backups.Domain;
using Platform.Modules.Backups.Options;

namespace Platform.Modules.Backups.Services;

/// <summary>备份读写：内联/转存、配额、软删除与下载解析。</summary>
public interface IBackupService
{
    /// <summary>分页列出当前应用的备份（默认排除已软删除）。</summary>
    Task<PageResult<BackupDto>> ListAsync(
        string appKey,
        PageQuery query,
        string? namespaceKey,
        string? kind,
        string? keyword,
        Guid? deviceId = null,
        CancellationToken cancellationToken = default);

    /// <summary>读取备份详情；不存在/已删除抛 <see cref="ApiErrorCode.BackupsNotFound"/>。</summary>
    Task<BackupDto> GetAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>创建备份；≤64KB 内联，超过转存 Blob（先写 pending，库提交后发布）。</summary>
    Task<BackupDto> CreateAsync(
        string appKey,
        CreateBackupRequest request,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>解析下载：内联返回 JSON，转存返回 Blob 路径与建议文件名。</summary>
    Task<BackupDownload> ResolveDownloadAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>软删除；物理文件由后台清理任务在宽限期后删除。</summary>
    Task<BackupDto> DeleteAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>下发前快照：记录目标当前配置哈希，不保存负载。</summary>
    Task<BackupDto> SnapshotAsync(
        string appKey,
        Guid sourceBackupId,
        Guid? targetDeviceId,
        string? currentHash,
        string? actor,
        CancellationToken cancellationToken = default);
}

/// <summary>下载解析结果：<see cref="InlineJson"/> 与 <see cref="BlobPath"/> 恰有一个非空。</summary>
public sealed record BackupDownload(
    BackupDto Backup,
    string? InlineJson,
    string? BlobPath,
    string? FileName,
    string? Sha256);

/// <summary>备份服务实现。</summary>
public sealed partial class BackupService : IBackupService
{
    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly IBackupNamespaceService _namespaces;
    private readonly BackupsOptions _options;

    /// <summary>构造。</summary>
    public BackupService(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IBackupNamespaceService namespaces,
        IOptions<BackupsOptions> options)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _namespaces = namespaces;
        _options = options.Value;
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex(@"[\x00-\x1f<>:""/\\|?*]", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidFileCharsRegex();

    /// <inheritdoc />
    public async Task<PageResult<BackupDto>> ListAsync(
        string appKey,
        PageQuery query,
        string? namespaceKey,
        string? kind,
        string? keyword,
        Guid? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = _db.Set<Backup>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey && x.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(namespaceKey))
        {
            source = source.Where(x => x.Namespace.Key == namespaceKey);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            source = source.Where(x => x.Kind == kind);
        }

        if (deviceId is { } device)
        {
            source = source.Where(x => x.DeviceId == device);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            source = source.Where(x => EF.Functions.Like(x.Name, pattern)
                                       || (x.DeviceName != null && EF.Functions.Like(x.DeviceName, pattern)));
        }

        var rows = await source
            .OrderByDescending(static x => x.CreatedAt)
            .Select(static x => new
            {
                Backup = x,
                NamespaceKey = x.Namespace.Key,
            })
            .ToPageResultAsync(query, cancellationToken);

        return PageResult<BackupDto>.Create(
            [.. rows.Items.Select(static row => ToDto(row.Backup, row.NamespaceKey))],
            rows.Total,
            query);
    }

    /// <inheritdoc />
    public async Task<BackupDto> GetAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await FindAsync(appKey, id, cancellationToken);
        return ToDto(row.Backup, row.NamespaceKey);
    }

    /// <inheritdoc />
    public async Task<BackupDto> CreateAsync(
        string appKey,
        CreateBackupRequest request,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var namespaceKey = request.NamespaceKey?.Trim();
        if (string.IsNullOrEmpty(namespaceKey))
        {
            throw BusinessException.Invalid("namespaceKey 必填。");
        }

        var space = await _namespaces.GetEnabledAsync(appKey, namespaceKey, cancellationToken);

        var kind = request.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!BackupConventions.IsValidKind(kind))
        {
            throw new BusinessException(
                ApiErrorCode.BackupsPayloadInvalid,
                "kind 只能是 config 或 profile。");
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 256)
        {
            throw new BusinessException(ApiErrorCode.BackupsPayloadInvalid, "name 必填且不超过 256 个字符。");
        }

        var frameworkVersion = request.FrameworkVersion?.Trim();
        if (string.IsNullOrEmpty(frameworkVersion) || frameworkVersion.Length > 64)
        {
            throw new BusinessException(
                ApiErrorCode.BackupsPayloadInvalid,
                "frameworkVersion 必填且不超过 64 个字符。");
        }

        if (request.SchemaVersion < 0)
        {
            throw new BusinessException(ApiErrorCode.BackupsPayloadInvalid, "schemaVersion 不能为负数。");
        }

        var payload = request.PayloadJson;
        if (string.IsNullOrEmpty(payload))
        {
            throw new BusinessException(ApiErrorCode.BackupsPayloadInvalid, "payloadJson 不能为空。");
        }

        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        if (payloadBytes.Length > _options.MaxPayloadBytes)
        {
            throw new BusinessException(
                ApiErrorCode.PayloadTooLarge,
                $"备份负载不能超过 {_options.MaxPayloadBytes} 字节。");
        }

        var channel = ResolveChannel(space, request.Channel);

        var count = await _db.Set<Backup>()
            .CountAsync(x => x.NamespaceId == space.Id && x.DeletedAt == null, cancellationToken);
        if (count >= space.MaxVersions)
        {
            throw new BusinessException(
                ApiErrorCode.BackupsVersionQuotaExceeded,
                $"命名空间 {space.Key} 已达到保留上限 {space.MaxVersions} 条，请先删除旧备份。");
        }

        var computedSha = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
        var declaredSha = NormalizeSha(request.Sha256);
        if (declaredSha is not null && !string.Equals(declaredSha, computedSha, StringComparison.Ordinal))
        {
            throw new BusinessException(ApiErrorCode.BackupsPayloadInvalid, "声明的 sha256 与负载不一致。");
        }

        var entity = new Backup
        {
            AppKey = appKey,
            NamespaceId = space.Id,
            Kind = kind,
            Name = name,
            DeviceId = request.DeviceId,
            DeviceName = TrimTo(request.DeviceName, 256),
            ModuleId = TrimTo(request.ModuleId, 64),
            SchemaVersion = request.SchemaVersion,
            FrameworkVersion = frameworkVersion,
            Channel = channel,
            CreatedBy = actor,
        };

        if (payloadBytes.Length <= _options.InlineMaxBytes)
        {
            entity.PayloadJson = payload;
            entity.Sha256 = computedSha;
            entity.SizeBytes = payloadBytes.Length;
            _db.Set<Backup>().Add(entity);
            await _db.SaveChangesAsync(cancellationToken);
            return ToDto(entity, space.Key);
        }

        // 超阈值：先写 pending → 库提交 → rename 发布；任一步失败都不留下"有行无文件"。
        entity.FileName = BuildFileName(space.Key, name, entity.Id);
        entity.BlobPath = BlobPath(appKey, space.Key, entity.Id);
        entity.SizeBytes = payloadBytes.Length;

        await using var content = new MemoryStream(payloadBytes, writable: false);
        var written = await _blobs.WritePendingAsync(entity.BlobPath, content, cancellationToken);
        if (written.SizeBytes != payloadBytes.Length
            || !string.Equals(written.Sha256, computedSha, StringComparison.Ordinal))
        {
            await _blobs.DiscardPendingAsync(entity.BlobPath, cancellationToken);
            throw new BusinessException(ApiErrorCode.BackupsPayloadInvalid, "负载落盘校验失败。");
        }

        entity.Sha256 = written.Sha256;
        _db.Set<Backup>().Add(entity);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await _blobs.DiscardPendingAsync(entity.BlobPath, cancellationToken);
            throw;
        }

        try
        {
            await _blobs.PublishAsync(entity.BlobPath, cancellationToken);
        }
        catch (Exception exception)
        {
            _db.Set<Backup>().Remove(entity);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw new BusinessException(
                ApiErrorCode.DependencyFailure,
                "备份文件发布失败，已回滚数据库记录。",
                exception);
        }

        return ToDto(entity, space.Key);
    }

    /// <inheritdoc />
    public async Task<BackupDownload> ResolveDownloadAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await FindAsync(appKey, id, cancellationToken);
        var dto = ToDto(row.Backup, row.NamespaceKey);

        if (!string.IsNullOrEmpty(row.Backup.PayloadJson))
        {
            return new BackupDownload(dto, row.Backup.PayloadJson, null, null, row.Backup.Sha256);
        }

        if (string.IsNullOrEmpty(row.Backup.BlobPath)
            || !await _blobs.ExistsAsync(row.Backup.BlobPath, cancellationToken))
        {
            throw new BusinessException(ApiErrorCode.BackupsNotFound, "备份文件缺失。");
        }

        return new BackupDownload(
            dto,
            null,
            row.Backup.BlobPath,
            row.Backup.FileName,
            row.Backup.Sha256);
    }

    /// <inheritdoc />
    public async Task<BackupDto> DeleteAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var backup = await _db.Set<Backup>()
                          .FirstOrDefaultAsync(
                              x => x.Id == id && x.AppKey == appKey && x.DeletedAt == null,
                              cancellationToken)
                      ?? throw new BusinessException(ApiErrorCode.BackupsNotFound, $"备份 {id} 不存在。");
        var namespaceKey = await _db.Set<BackupNamespace>()
            .Where(x => x.Id == backup.NamespaceId)
            .Select(static x => x.Key)
            .FirstAsync(cancellationToken);

        backup.DeletedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(backup, namespaceKey);
    }

    /// <inheritdoc />
    public async Task<BackupDto> SnapshotAsync(
        string appKey,
        Guid sourceBackupId,
        Guid? targetDeviceId,
        string? currentHash,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        var row = await FindAsync(appKey, sourceBackupId, cancellationToken);
        var source = row.Backup;

        var snapshot = new Backup
        {
            AppKey = appKey,
            NamespaceId = source.NamespaceId,
            Kind = source.Kind,
            Name = "pre-push-snapshot",
            DeviceId = targetDeviceId,
            SchemaVersion = source.SchemaVersion,
            FrameworkVersion = source.FrameworkVersion,
            Channel = source.Channel,
            Sha256 = NormalizeSha(currentHash),
            SizeBytes = 0,
            CreatedBy = actor,
        };

        _db.Set<Backup>().Add(snapshot);
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(snapshot, row.NamespaceKey);
    }

    private async Task<(Backup Backup, string NamespaceKey)> FindAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken)
    {
        var row = await _db.Set<Backup>()
            .AsNoTracking()
            .Where(x => x.Id == id && x.AppKey == appKey && x.DeletedAt == null)
            .Select(static x => new
            {
                Backup = x,
                NamespaceKey = x.Namespace.Key,
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BusinessException(ApiErrorCode.BackupsNotFound, $"备份 {id} 不存在。");

        return (row.Backup, row.NamespaceKey);
    }

    private static string ResolveChannel(BackupNamespace space, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return space.Channel;
        }

        var channel = BackupConventions.NormalizeChannel(requested);
        if (!string.Equals(channel, space.Channel, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                ApiErrorCode.BackupsChannelMismatch,
                $"命名空间渠道为 {space.Channel}，不接受 {channel} 渠道的备份。");
        }

        return channel;
    }

    private static string? NormalizeSha(string? value)
    {
        var sha = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(sha))
        {
            return null;
        }

        return Sha256Regex().IsMatch(sha)
            ? sha
            : throw new BusinessException(ApiErrorCode.BackupsPayloadInvalid, "sha256 必须是 64 位小写十六进制。");
    }

    private static string BlobPath(string appKey, string namespaceKey, Guid id) =>
        $"backups/{appKey}/{namespaceKey}/{id:N}.json";

    private static string BuildFileName(string namespaceKey, string name, Guid id)
    {
        var sanitized = InvalidFileCharsRegex().Replace(name, "_").Trim();
        var fileName = $"{namespaceKey}-{sanitized}-{id.ToString("N")[..8]}.json";
        return fileName.Length <= 256 ? fileName : fileName[..256];
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

    private static BackupDto ToDto(Backup entity, string namespaceKey) =>
        new(
            entity.Id,
            namespaceKey,
            entity.Kind,
            entity.Name,
            entity.DeviceId,
            entity.DeviceName,
            entity.ModuleId,
            entity.SchemaVersion,
            entity.FrameworkVersion,
            entity.Channel,
            entity.SizeBytes,
            entity.Sha256,
            entity.PayloadJson is not null,
            entity.BlobPath is not null,
            entity.FileName,
            $"/api/v1/admin/backups/{entity.Id:D}/download",
            entity.CreatedAt,
            entity.CreatedBy);
}
