using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Contracts.Webhooks;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Kernel.Metrics;
using Platform.Modules.Versions.Domain;
using Platform.Modules.Versions.Options;

namespace Platform.Modules.Versions.Services;

/// <summary>版本发布、下发与清单读取。</summary>
public interface IReleaseService
{
    /// <summary>按渠道/状态分页列出当前应用的版本。</summary>
    Task<PageResult<ReleaseSummaryDto>> ListAsync(
        string appKey,
        PageQuery page,
        string? channelKey,
        string? status,
        CancellationToken cancellationToken = default);

    /// <summary>版本详情（含全部产物与文件清单）。</summary>
    Task<ReleaseDetailDto> GetAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>编辑草稿版本的发布说明（包内说明可被管理员修正后再发布）。</summary>
    Task<ReleaseDetailDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateReleaseRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>按提交的清单创建版本；<c>Publish=true</c> 时创建即发布。</summary>
    Task<ReleaseDetailDto> CreateAsync(
        string appKey,
        CreateReleaseRequest request,
        string? actor,
        CancellationToken cancellationToken = default);

    /// <summary>发布草稿版本；已发布时幂等返回。</summary>
    Task<ReleaseDetailDto> PublishAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>归档版本（最新版本归档后，客户端 check 自动回退到上一个已发布版本）。</summary>
    Task<ReleaseDetailDto> ArchiveAsync(string appKey, Guid id, CancellationToken cancellationToken = default);

    /// <summary>客户端检查更新。</summary>
    Task<CheckUpdateResponse> CheckAsync(
        string appKey,
        CheckUpdateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>读取某个已发布版本的运行时清单。</summary>
    Task<ReleaseManifestDto> GetManifestAsync(
        string appKey,
        string channelKey,
        string version,
        string runtimeIdentifier,
        CancellationToken cancellationToken = default);
}

/// <summary>版本服务实现。</summary>
public sealed partial class ReleaseService : IReleaseService
{
    private readonly PlatformDbContext _db;
    private readonly IBlobStorage _blobs;
    private readonly IClock _clock;
    private readonly VersionsOptions _options;
    private readonly IPlatformEventPublisher _events;
    private readonly IUpdaterReleaseService _updaters;
    private readonly PlatformMetrics _metrics;

    /// <summary>构造。</summary>
    public ReleaseService(
        PlatformDbContext db,
        IBlobStorage blobs,
        IClock clock,
        IOptions<VersionsOptions> options,
        IPlatformEventPublisher events,
        IUpdaterReleaseService updaters,
        PlatformMetrics metrics)
    {
        _db = db;
        _blobs = blobs;
        _clock = clock;
        _options = options.Value;
        _events = events;
        _updaters = updaters;
        _metrics = metrics;
    }

    [GeneratedRegex(VersionConventions.ChannelKeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ChannelKeyRegex();

    [GeneratedRegex(VersionConventions.VersionPattern, RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    /// <inheritdoc />
    public async Task<PageResult<ReleaseSummaryDto>> ListAsync(
        string appKey,
        PageQuery page,
        string? channelKey,
        string? status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var query = _db.Set<Release>().AsNoTracking().Where(x => x.AppKey == appKey);

        if (!string.IsNullOrWhiteSpace(channelKey))
        {
            var key = channelKey.Trim();
            query = query.Where(x => x.Channel.Key == key);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim();
            if (!VersionConventions.IsValidStatus(normalized))
            {
                throw BusinessException.Invalid("status 必须是 draft / published / archived 之一。");
            }

            query = query.Where(x => x.Status == normalized);
        }

        var projection = query
            .OrderByDescending(static x => x.CreatedAt)
            .Select(static x => new ReleaseSummaryDto(
                x.Id,
                x.Channel.Key,
                x.Version,
                x.Status,
                x.Notes,
                x.Artifacts.Count,
                x.Artifacts.Count > 0
                    ? x.Artifacts.Sum(static a => a.FileCount)
                    : x.Packages.Sum(static p => p.FileCount ?? 1),
                x.Artifacts.Count > 0
                    ? x.Artifacts.Sum(static a => a.TotalSizeBytes)
                    : x.Packages.Sum(static p => p.SizeBytes),
                x.PublishedAt,
                x.CreatedAt,
                x.UpdatedAt,
                x.Packages.Count,
                x.Packages.Sum(static p => p.SizeBytes)));

        return await projection.ToPageResultAsync(page, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReleaseDetailDto> GetAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var release = await LoadDetailAsync(appKey, id, cancellationToken);
        return ToDetail(release);
    }

    /// <inheritdoc />
    public async Task<ReleaseDetailDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var release = await LoadDetailAsync(appKey, id, cancellationToken);
        if (release.Status != VersionConventions.StatusDraft)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsReleaseImmutable,
                $"版本 {release.Version} 已{(release.Status == VersionConventions.StatusPublished ? "发布" : "归档")}，"
                + "只有草稿可以编辑发布说明。");
        }

        string? notes = request.Notes?.Trim();
        if (notes is { Length: > VersionConventions.MaxDescriptionChars })
        {
            throw BusinessException.Invalid(
                $"发布说明不能超过 {VersionConventions.MaxDescriptionChars} 个字符。");
        }

        release.Notes = string.IsNullOrEmpty(notes) ? null : notes;
        await _db.SaveChangesAsync(cancellationToken);
        return ToDetail(release);
    }

    /// <inheritdoc />
    public async Task<ReleaseDetailDto> CreateAsync(
        string appKey,
        CreateReleaseRequest request,
        string? actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channelKey = request.ChannelKey?.Trim();
        if (string.IsNullOrEmpty(channelKey) || !ChannelKeyRegex().IsMatch(channelKey))
        {
            throw BusinessException.Invalid(
                $"渠道键必须匹配 {VersionConventions.ChannelKeyPattern}（小写字母开头，2–32 位小写字母/数字/连字符）。");
        }

        var version = request.Version?.Trim();
        if (string.IsNullOrEmpty(version) || !VersionRegex().IsMatch(version))
        {
            throw BusinessException.Invalid(
                $"版本号必须匹配 {VersionConventions.VersionPattern}，例如 1.2.3 或 1.2.3-rc.1。");
        }

        var channel = await _db.Set<ReleaseChannel>()
                          .FirstOrDefaultAsync(
                              x => x.AppKey == appKey && x.Key == channelKey,
                              cancellationToken)
                      ?? throw BusinessException.NotFound(
                          ApiErrorCode.VersionsChannelNotFound,
                          $"渠道 {channelKey} 不存在。");

        if (!channel.IsEnabled)
        {
            throw BusinessException.Invalid($"渠道 {channelKey} 已停用，不能发布新版本。");
        }

        var artifacts = ValidateArtifacts(request.Artifacts);
        await EnsureBlobsAsync(appKey, artifacts, cancellationToken);

        if (await _db.Set<Release>()
                .AnyAsync(x => x.ChannelId == channel.Id && x.Version == version, cancellationToken))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsReleaseVersionExists,
                $"渠道 {channelKey} 下版本 {version} 已存在。");
        }

        var now = _clock.UtcNow;
        var release = new Release
        {
            AppKey = appKey,
            ChannelId = channel.Id,
            Version = version,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            Status = request.Publish ? VersionConventions.StatusPublished : VersionConventions.StatusDraft,
            PublishedAt = request.Publish ? now : null,
            CreatedBy = actor,
        };

        foreach (var artifact in artifacts)
        {
            var files = artifact.Files
                .OrderBy(static x => x.Path, StringComparer.Ordinal)
                .Select(static x => new ReleaseFile
                {
                    Path = x.Path,
                    Sha256 = x.Sha256,
                    SizeBytes = x.SizeBytes,
                })
                .ToList();

            release.Artifacts.Add(new ReleaseArtifact
            {
                RuntimeIdentifier = artifact.RuntimeIdentifier,
                ManifestHash = ReleaseManifest.Compute(artifact.Files),
                FileCount = files.Count,
                TotalSizeBytes = files.Sum(static x => x.SizeBytes),
                Files = files,
            });
        }

        _db.Set<Release>().Add(release);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _db.ChangeTracker.Clear();
            throw new BusinessException(
                ApiErrorCode.VersionsReleaseVersionExists,
                $"渠道 {channelKey} 下版本 {version} 已存在。",
                exception);
        }

        if (request.Publish)
        {
            await PublishReleasedAsync(appKey, release, channelKey, cancellationToken);
        }

        return await GetAsync(appKey, release.Id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReleaseDetailDto> PublishAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var release = await LoadDetailAsync(appKey, id, cancellationToken);
        if (release.Status == VersionConventions.StatusPublished)
        {
            return ToDetail(release);
        }

        if (release.Status == VersionConventions.StatusArchived)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsReleaseNotPublished,
                $"版本 {release.Version} 已归档，不能重新发布。");
        }

        if (release.Artifacts.Count == 0 && !HasReadyInstaller(release))
        {
            throw BusinessException.Invalid("版本既没有产物也没有可用的全量安装包，不能发布。");
        }

        if (release.Artifacts.Count > 0)
        {
            var files = release.Artifacts
                .SelectMany(static artifact => artifact.Files)
                .Select(static file => new ReleaseFileDto(file.Path, file.Sha256, file.SizeBytes))
                .ToList();
            await EnsureBlobsAsync(appKey, [new ArtifactInput("all", files)], cancellationToken);
        }

        release.Status = VersionConventions.StatusPublished;
        release.PublishedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await PublishReleasedAsync(appKey, release, release.Channel.Key, cancellationToken);

        return ToDetail(release);
    }

    /// <inheritdoc />
    public async Task<ReleaseDetailDto> ArchiveAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var release = await LoadDetailAsync(appKey, id, cancellationToken);
        if (release.Status == VersionConventions.StatusArchived)
        {
            return ToDetail(release);
        }

        release.Status = VersionConventions.StatusArchived;
        release.ArchivedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return ToDetail(release);
    }

    /// <inheritdoc />
    public async Task<CheckUpdateResponse> CheckAsync(
        string appKey,
        CheckUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        CheckUpdateResponse response;
        try
        {
            response = await CheckCoreAsync(appKey, request, cancellationToken);
        }
        catch
        {
            IncrementCheck("error");
            throw;
        }

        IncrementCheck(response.HasUpdate ? "update" : "none");
        return response;
    }

    private void IncrementCheck(string result) =>
        _metrics.Increment(
            PlatformMetricNames.VersionsCheck,
            PlatformMetricNames.VersionsCheckHelp,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["result"] = result });

    private async Task<CheckUpdateResponse> CheckCoreAsync(
        string appKey,
        CheckUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channelKey = request.ChannelKey?.Trim();
        var audienceTags = AudienceTagRules.Normalize(request.AudienceTags);
        if (string.IsNullOrEmpty(channelKey) && audienceTags.Count == 0)
        {
            throw BusinessException.Invalid("channelKey 与 audienceTags 至少提供一个。");
        }

        if (!string.IsNullOrEmpty(channelKey) && !ChannelKeyRegex().IsMatch(channelKey))
        {
            throw BusinessException.Invalid("channelKey 非法。");
        }

        var runtimeIdentifier = request.RuntimeIdentifier?.Trim();
        if (!VersionConventions.IsSupportedRuntime(runtimeIdentifier))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsRuntimeIdentifierUnsupported,
                $"不支持的 runtimeIdentifier：{request.RuntimeIdentifier}。");
        }

        ReleaseChannel channel;
        if (!string.IsNullOrEmpty(channelKey))
        {
            channel = await _db.Set<ReleaseChannel>()
                          .AsNoTracking()
                          .FirstOrDefaultAsync(
                              x => x.AppKey == appKey && x.Key == channelKey,
                              cancellationToken)
                      ?? throw BusinessException.NotFound(
                          ApiErrorCode.VersionsChannelNotFound,
                          $"渠道 {channelKey} 不存在。");
        }
        else
        {
            channel = await ResolveChannelByAudienceAsync(appKey, audienceTags, cancellationToken);
        }

        // 自动解析出来的渠道键要回给客户端，后续下载/清单都按它寻址。
        channelKey = channel.Key;

        var published = await _db.Set<Release>()
            .AsNoTracking()
            .Include(static x => x.Artifacts)
            .ThenInclude(static x => x.Files)
            .Include(static x => x.Packages)
            .Where(x => x.ChannelId == channel.Id && x.Status == VersionConventions.StatusPublished)
            .ToListAsync(cancellationToken);

        var currentVersion = request.CurrentVersion?.Trim();
        var updater = await _updaters.GetLatestAsync(appKey, runtimeIdentifier!, cancellationToken);
        var hasUpdaterUpdate = HasUpdaterUpdate(request.UpdaterVersion, updater);

        if (published.Count == 0)
        {
            if (updater is not null)
            {
                // 本体还没有已发布版本，但更新器可以先补齐；不要用 212 阻断更新器下发。
                return new CheckUpdateResponse(
                    false,
                    channelKey,
                    currentVersion,
                    null,
                    HasUpdaterUpdate: hasUpdaterUpdate,
                    Updater: updater);
            }

            throw BusinessException.NotFound(
                ApiErrorCode.VersionsNoPublishedRelease,
                $"渠道 {channelKey} 没有已发布版本。");
        }

        var latest = published[0];
        foreach (var candidate in published.Skip(1))
        {
            // 语义版本优先，同版本再取更晚发布的；避免「先发 1.10 后发 1.9」时 1.9 被当成最新。
            if (ReleaseVersionOrdering.Compare(
                    candidate.Version, candidate.PublishedAt,
                    latest.Version, latest.PublishedAt) > 0)
            {
                latest = candidate;
            }
        }

        var latestVersion = latest.Version;
        var currentKnown = VersionComparer.TryCompare(currentVersion, latestVersion, out var comparison);
        var isDowngrade = currentKnown && comparison > 0;
        var allowDowngrade = request.AllowDowngrade || channel.AllowDowngrade;
        var hasUpdate = !currentKnown || comparison < 0 || (isDowngrade && allowDowngrade);

        var artifact = latest.Artifacts.FirstOrDefault(x => x.RuntimeIdentifier == runtimeIdentifier);
        var installer = ReleasePackageService.SelectInstaller(latest.Packages, runtimeIdentifier!);
        var incremental = ReleasePackageService.SelectIncremental(latest.Packages, runtimeIdentifier!);
        if (artifact is null && installer is null)
        {
            // 最新版本对该运行时没有可下载内容：不冒充「有更新」，也不回退到更老的版本。
            return new CheckUpdateResponse(
                false,
                channelKey,
                currentVersion,
                null,
                latestVersion,
                isDowngrade,
                HasUpdaterUpdate: hasUpdaterUpdate,
                Updater: updater,
                Notes: latest.Notes,
                PublishedAt: latest.PublishedAt);
        }

        var manifest = artifact is null ? null : ToManifest(latest, channelKey, artifact);
        return new CheckUpdateResponse(
            hasUpdate,
            channelKey,
            currentVersion,
            manifest,
            latestVersion,
            isDowngrade,
            Installer: installer is null
                ? null
                : ReleasePackageService.ToDto(installer, channelKey, latest.Version, reused: false),
            IncrementalPackage: incremental is null || !hasUpdate
                ? null
                : ReleasePackageService.ToDto(incremental, channelKey, latest.Version, reused: false),
            HasUpdaterUpdate: hasUpdaterUpdate,
            Updater: updater,
            Notes: latest.Notes,
            PublishedAt: latest.PublishedAt);
    }

    /// <summary>
    /// 只给受众标签时：在启用渠道里找「渠道受众是请求标签子集」的渠道（客户端具备渠道所需能力），
    /// 取标签最多者，其次按键序；没有命中且开启自动建渠道时按标签摘要建一个。
    /// </summary>
    private async Task<ReleaseChannel> ResolveChannelByAudienceAsync(
        string appKey,
        IReadOnlyList<string> audienceTags,
        CancellationToken cancellationToken)
    {
        var channels = await _db.Set<ReleaseChannel>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey && x.IsEnabled)
            .ToListAsync(cancellationToken);

        var match = channels
            .Where(channel => channel.AudienceTags.Count > 0
                              && channel.AudienceTags.All(audienceTags.Contains))
            .OrderByDescending(static channel => channel.AudienceTags.Count)
            .ThenBy(static channel => channel.Key, StringComparer.Ordinal)
            .FirstOrDefault();

        if (match is not null)
        {
            return match;
        }

        if (!_options.AudienceAutoCreateChannels)
        {
            throw BusinessException.NotFound(
                ApiErrorCode.VersionsChannelNotFound,
                $"没有匹配受众标签 [{string.Join(", ", audienceTags)}] 的启用渠道。");
        }

        return await GetOrCreateAudienceChannelAsync(appKey, audienceTags, cancellationToken);
    }

    private async Task<ReleaseChannel> GetOrCreateAudienceChannelAsync(
        string appKey,
        IReadOnlyList<string> audienceTags,
        CancellationToken cancellationToken)
    {
        var key = AudienceTagRules.DeriveKey(audienceTags);
        var existing = await _db.Set<ReleaseChannel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AppKey == appKey && x.Key == key, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var channel = new ReleaseChannel
        {
            AppKey = appKey,
            Key = key,
            Name = AudienceTagRules.DeriveName(audienceTags),
            Description = "由客户端受众标签自动创建",
            AudienceTags = [.. audienceTags],
            AutoCreated = true,
        };

        _db.Set<ReleaseChannel>().Add(channel);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 并发检查同时创建：另一条已插入，沿用它的行。
            _db.ChangeTracker.Clear();
            return await _db.Set<ReleaseChannel>()
                .AsNoTracking()
                .FirstAsync(x => x.AppKey == appKey && x.Key == key, cancellationToken);
        }

        return channel;
    }

    /// <inheritdoc />
    public async Task<ReleaseManifestDto> GetManifestAsync(
        string appKey,
        string channelKey,
        string version,
        string runtimeIdentifier,
        CancellationToken cancellationToken = default)
    {
        var key = channelKey?.Trim();
        if (string.IsNullOrEmpty(key) || !ChannelKeyRegex().IsMatch(key))
        {
            throw BusinessException.Invalid("channelKey 非法。");
        }

        var normalizedVersion = version?.Trim();
        if (string.IsNullOrEmpty(normalizedVersion))
        {
            throw BusinessException.Invalid("version 不能为空。");
        }

        var rid = runtimeIdentifier?.Trim();
        if (!VersionConventions.IsSupportedRuntime(rid))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsRuntimeIdentifierUnsupported,
                $"不支持的 runtimeIdentifier：{runtimeIdentifier}。");
        }

        var release = await _db.Set<Release>()
            .AsNoTracking()
            .Include(static x => x.Artifacts)
            .ThenInclude(static x => x.Files)
            .Include(static x => x.Channel)
            .FirstOrDefaultAsync(
                x => x.AppKey == appKey && x.Channel.Key == key && x.Version == normalizedVersion,
                cancellationToken)
            ?? throw BusinessException.NotFound(
                ApiErrorCode.VersionsReleaseNotFound,
                $"渠道 {key} 下版本 {normalizedVersion} 不存在。");

        if (release.Status != VersionConventions.StatusPublished)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsReleaseNotPublished,
                $"版本 {normalizedVersion} 尚未发布。");
        }

        var artifact = release.Artifacts.FirstOrDefault(x => x.RuntimeIdentifier == rid)
            ?? throw BusinessException.NotFound(
                ApiErrorCode.VersionsArtifactNotFound,
                $"版本 {normalizedVersion} 没有 {rid} 产物。");

        return ToManifest(release, key, artifact);
    }

    private async Task<Release> LoadDetailAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken)
    {
        return await _db.Set<Release>()
                   .Include(static x => x.Channel)
                   .Include(static x => x.Artifacts)
                   .ThenInclude(static x => x.Files)
                   .Include(static x => x.Packages)
                   .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
               ?? throw BusinessException.NotFound(
                   ApiErrorCode.VersionsReleaseNotFound,
                   $"版本 {id} 不存在。");
    }

    private static bool HasReadyInstaller(Release release) =>
        release.Packages.Any(static x => x.Kind == VersionConventions.PackageKindFull
                                         && x.Status == VersionConventions.PackageStatusReady);

    /// <summary>
    /// 未上报更新器版本（首次安装）或版本号无法解析时都按下发处理；
    /// 只有明确不旧于最新版才判否。
    /// </summary>
    private static bool HasUpdaterUpdate(string? currentVersion, UpdaterInfoDto? latest)
    {
        if (latest is null)
        {
            return false;
        }

        return !VersionComparer.TryCompare(currentVersion, latest.Version, out var comparison)
               || comparison < 0;
    }

    private Task PublishReleasedAsync(
        string appKey,
        Release release,
        string channelKey,
        CancellationToken cancellationToken) =>
        _events.PublishAsync(
            new PlatformEvent(
                WebhookConventions.EventVersionReleased,
                appKey,
                new
                {
                    releaseId = release.Id,
                    channelKey,
                    version = release.Version,
                    publishedAt = release.PublishedAt,
                },
                _clock.UtcNow),
            cancellationToken);

    /// <summary>校验并规范化客户端提交的清单；任何结构问题都先于落库被拒绝。</summary>
    private List<ArtifactInput> ValidateArtifacts(IReadOnlyList<CreateReleaseArtifactRequest>? artifacts)
    {
        if (artifacts is null || artifacts.Count == 0)
        {
            throw BusinessException.Invalid("至少需要一个运行时产物。");
        }

        var result = new List<ArtifactInput>(artifacts.Count);
        var runtimes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var artifact in artifacts)
        {
            var runtimeIdentifier = artifact.RuntimeIdentifier?.Trim();
            if (!VersionConventions.IsSupportedRuntime(runtimeIdentifier))
            {
                throw new BusinessException(
                    ApiErrorCode.VersionsRuntimeIdentifierUnsupported,
                    $"不支持的 runtimeIdentifier：{artifact.RuntimeIdentifier}。");
            }

            if (!runtimes.Add(runtimeIdentifier!))
            {
                throw BusinessException.Invalid($"runtimeIdentifier {runtimeIdentifier} 重复。");
            }

            if (artifact.Files is null || artifact.Files.Count == 0)
            {
                throw BusinessException.Invalid($"产物 {runtimeIdentifier} 没有文件条目。");
            }

            if (artifact.Files.Count > _options.MaxFilesPerArtifact)
            {
                throw new BusinessException(
                    ApiErrorCode.PayloadTooLarge,
                    $"产物 {runtimeIdentifier} 文件数超过上限 {_options.MaxFilesPerArtifact}。");
            }

            var files = new List<ReleaseFileDto>(artifact.Files.Count);
            var paths = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in artifact.Files)
            {
                var path = NormalizePath(file.Path)
                           ?? throw BusinessException.Invalid($"文件路径非法：{file.Path}。");
                if (!paths.Add(path))
                {
                    throw BusinessException.Invalid($"文件路径重复：{path}。");
                }

                var sha256 = file.Sha256?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(sha256) || !Sha256Regex().IsMatch(sha256))
                {
                    throw BusinessException.Invalid($"文件 {path} 的 sha256 非法。");
                }

                if (file.SizeBytes <= 0 || file.SizeBytes > _options.MaxFileSizeBytes)
                {
                    throw new BusinessException(
                        ApiErrorCode.PayloadTooLarge,
                        $"文件 {path} 的大小 {file.SizeBytes} 超出允许范围。");
                }

                files.Add(new ReleaseFileDto(path, sha256, file.SizeBytes));
            }

            result.Add(new ArtifactInput(runtimeIdentifier!, files));
        }

        return result;
    }

    /// <summary>确认每个声明的内容都已上传且长度一致；缺失时一次性列出，便于发布脚本修正。</summary>
    private async Task EnsureBlobsAsync(
        string appKey,
        IReadOnlyList<ArtifactInput> artifacts,
        CancellationToken cancellationToken)
    {
        var expected = artifacts
            .SelectMany(static x => x.Files)
            .GroupBy(static x => x.Sha256, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().SizeBytes, StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var (sha256, sizeBytes) in expected)
        {
            var blobPath = VersionBlobPaths.Blob(appKey, sha256);
            if (!await _blobs.ExistsAsync(blobPath, cancellationToken))
            {
                missing.Add($"{sha256}（{sizeBytes} 字节）");
                continue;
            }

            var actual = await _blobs.GetLengthAsync(blobPath, cancellationToken);
            if (actual != sizeBytes)
            {
                missing.Add($"{sha256}（声明 {sizeBytes}，实际 {actual}）");
            }
        }

        if (missing.Count > 0)
        {
            var shown = string.Join("、", missing.Take(5));
            var suffix = missing.Count > 5 ? $" 等 {missing.Count} 项" : string.Empty;
            throw new BusinessException(
                ApiErrorCode.VersionsManifestMismatch,
                $"以下文件未上传或长度不符：{shown}{suffix}。");
        }
    }

    private static string? NormalizePath(string? raw)
    {
        var path = raw?.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(path) || path.Length > VersionConventions.MaxPathLength)
        {
            return null;
        }

        if (path[0] == '/' || path.Contains('\0'))
        {
            return null;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return null;
            }
        }

        return path;
    }

    private static ReleaseDetailDto ToDetail(Release release)
    {
        var artifacts = release.Artifacts
            .OrderBy(static x => x.RuntimeIdentifier, StringComparer.Ordinal)
            .Select(static artifact => new ReleaseArtifactDto(
                artifact.RuntimeIdentifier,
                artifact.ManifestHash,
                artifact.FileCount,
                artifact.TotalSizeBytes,
                [.. artifact.Files
                    .OrderBy(static x => x.Path, StringComparer.Ordinal)
                    .Select(static file => new ReleaseFileDto(file.Path, file.Sha256, file.SizeBytes))]))
            .ToList();

        var packages = release.Packages
            .OrderBy(static x => x.Kind, StringComparer.Ordinal)
            .ThenBy(static x => x.Target, StringComparer.Ordinal)
            .ThenBy(static x => x.Format, StringComparer.Ordinal)
            .Select(x => ReleasePackageService.ToDto(x, release.Channel.Key, release.Version, reused: false))
            .ToList();

        return new ReleaseDetailDto(
            release.Id,
            release.Channel.Key,
            release.Version,
            release.Status,
            release.Notes,
            artifacts.Count,
            artifacts.Count > 0
                ? artifacts.Sum(static x => x.FileCount)
                : release.Packages.Sum(static p => p.FileCount ?? 1),
            artifacts.Count > 0
                ? artifacts.Sum(static x => x.TotalSizeBytes)
                : release.Packages.Sum(static p => p.SizeBytes),
            release.PublishedAt,
            release.ArchivedAt,
            release.CreatedBy,
            release.CreatedAt,
            release.UpdatedAt,
            artifacts,
            packages);
    }

    private static ReleaseManifestDto ToManifest(Release release, string channelKey, ReleaseArtifact artifact)
    {
        var files = artifact.Files
            .OrderBy(static x => x.Path, StringComparer.Ordinal)
            .Select(static x => new ReleaseFileDto(x.Path, x.Sha256, x.SizeBytes))
            .ToList();

        return new ReleaseManifestDto(
            release.Id,
            channelKey,
            release.Version,
            artifact.RuntimeIdentifier,
            artifact.ManifestHash,
            release.PublishedAt,
            files);
    }

    private sealed record ArtifactInput(string RuntimeIdentifier, IReadOnlyList<ReleaseFileDto> Files);
}
