using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Backups.Options;

namespace Platform.Modules.Backups.Services;

/// <summary>
/// 备份软删除清理：宽限期读库内配置（无配置回落 Options），超期后先删 Blob、再删行。
/// </summary>
public sealed class BackupCleanupTask : IRetentionCleanupTask
{
    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly BackupsOptions _options;
    private readonly IRetentionSettingsStore _settings;

    /// <summary>构造。</summary>
    public BackupCleanupTask(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IOptions<BackupsOptions> options,
        IRetentionSettingsStore settings)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _options = options.Value;
        _settings = settings;
    }

    /// <inheritdoc />
    public string Name => "backups";

    /// <inheritdoc />
    public async Task<RetentionCleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var grace = await _settings.GetGraceMinutesAsync(PlatformModuleKeys.Backups, cancellationToken)
                    ?? _options.CleanupGraceMinutes;
        var cutoff = _clock.UtcNow.AddMinutes(-Math.Max(1, grace));
        return await BackupPurgeExecutor.PurgeAsync(_db, _blobs, cutoff, appKey: null, cancellationToken);
    }
}
