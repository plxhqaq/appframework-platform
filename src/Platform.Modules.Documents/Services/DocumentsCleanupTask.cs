using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Modules.Documents.Options;

namespace Platform.Modules.Documents.Services;

/// <summary>
/// 文档软删除清理：宽限期读库内配置（无配置回落 Options），超期后先删修订文件、再删修订行与文档行。
/// </summary>
public sealed class DocumentsCleanupTask : IRetentionCleanupTask
{
    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly DocumentsOptions _options;
    private readonly IRetentionSettingsStore _settings;

    /// <summary>构造。</summary>
    public DocumentsCleanupTask(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IOptions<DocumentsOptions> options,
        IRetentionSettingsStore settings)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _options = options.Value;
        _settings = settings;
    }

    /// <inheritdoc />
    public string Name => "documents";

    /// <inheritdoc />
    public async Task<RetentionCleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var grace = await _settings.GetGraceMinutesAsync(PlatformModuleKeys.Documents, cancellationToken)
                    ?? _options.CleanupGraceMinutes;
        var cutoff = _clock.UtcNow.AddMinutes(-Math.Max(1, grace));
        return await DocumentPurgeExecutor.PurgeAsync(_db, _blobs, cutoff, appKey: null, cancellationToken);
    }
}
