using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Versions;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Errors;
using Platform.Modules.Versions.Domain;

namespace Platform.Modules.Versions.Services;

/// <summary>版本渠道读写。</summary>
public interface IReleaseChannelService
{
    /// <summary>列出当前应用的全部渠道（含发布计数与最新已发布版本）。</summary>
    Task<IReadOnlyList<ReleaseChannelDto>> ListAsync(string appKey, CancellationToken cancellationToken = default);

    /// <summary>创建渠道；键重复抛 <see cref="ApiErrorCode.VersionsChannelKeyExists"/>。</summary>
    Task<ReleaseChannelDto> CreateAsync(
        string appKey,
        CreateReleaseChannelRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>更新名称/说明/启用状态。</summary>
    Task<ReleaseChannelDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateReleaseChannelRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>删除渠道；仍有任何版本登记时拒绝（FK 级联会连带删除版本，必须清空后再删）。</summary>
    Task DeleteAsync(string appKey, Guid id, CancellationToken cancellationToken = default);
}

/// <summary>渠道服务实现。</summary>
public sealed partial class ReleaseChannelService : IReleaseChannelService
{
    private readonly PlatformDbContext _db;

    /// <summary>构造。</summary>
    public ReleaseChannelService(PlatformDbContext db)
    {
        _db = db;
    }

    [GeneratedRegex(VersionConventions.ChannelKeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ChannelKeyRegex();

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReleaseChannelDto>> ListAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var channels = await _db.Set<ReleaseChannel>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey)
            .OrderBy(static x => x.Key)
            .ToListAsync(cancellationToken);

        if (channels.Count == 0)
        {
            return [];
        }

        var releaseCounts = await _db.Set<Release>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey)
            .GroupBy(static x => x.ChannelId)
            .Select(static group => new { ChannelId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(static x => x.ChannelId, static x => x.Count, cancellationToken);

        var published = await _db.Set<Release>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey && x.Status == VersionConventions.StatusPublished)
            .Select(static x => new { x.ChannelId, x.Version, x.PublishedAt })
            .ToListAsync(cancellationToken);

        var latestVersions = new Dictionary<Guid, string>();
        foreach (var group in published.GroupBy(static x => x.ChannelId))
        {
            var best = group.First();
            foreach (var candidate in group.Skip(1))
            {
                if (ReleaseVersionOrdering.Compare(
                        candidate.Version, candidate.PublishedAt,
                        best.Version, best.PublishedAt) > 0)
                {
                    best = candidate;
                }
            }

            latestVersions[group.Key] = best.Version;
        }

        return
        [
            .. channels.Select(channel => ToDto(
                channel,
                releaseCounts.GetValueOrDefault(channel.Id),
                latestVersions.GetValueOrDefault(channel.Id))),
        ];
    }

    /// <inheritdoc />
    public async Task<ReleaseChannelDto> CreateAsync(
        string appKey,
        CreateReleaseChannelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = request.Key?.Trim();
        if (string.IsNullOrEmpty(key) || !ChannelKeyRegex().IsMatch(key))
        {
            throw BusinessException.Invalid(
                $"渠道键必须匹配 {VersionConventions.ChannelKeyPattern}（小写字母开头，2–32 位小写字母/数字/连字符）。");
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw BusinessException.Invalid("渠道名称不能为空。");
        }

        if (await _db.Set<ReleaseChannel>()
                .AnyAsync(x => x.AppKey == appKey && x.Key == key, cancellationToken))
        {
            throw new BusinessException(
                ApiErrorCode.VersionsChannelKeyExists,
                $"渠道键 {key} 已存在。");
        }

        var channel = new ReleaseChannel
        {
            AppKey = appKey,
            Key = key,
            Name = name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            AllowDowngrade = request.AllowDowngrade,
            AudienceTags = [.. AudienceTagRules.Normalize(request.AudienceTags)],
        };

        _db.Set<ReleaseChannel>().Add(channel);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _db.ChangeTracker.Clear();
            throw new BusinessException(
                ApiErrorCode.VersionsChannelKeyExists,
                $"渠道键 {key} 已存在。",
                exception);
        }

        return ToDto(channel, 0, null);
    }

    /// <inheritdoc />
    public async Task<ReleaseChannelDto> UpdateAsync(
        string appKey,
        Guid id,
        UpdateReleaseChannelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channel = await _db.Set<ReleaseChannel>()
                          .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                      ?? throw BusinessException.NotFound(
                          ApiErrorCode.VersionsChannelNotFound,
                          $"渠道 {id} 不存在。");

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length == 0)
            {
                throw BusinessException.Invalid("渠道名称不能为空。");
            }

            channel.Name = name;
        }

        if (request.Description is not null)
        {
            var description = request.Description.Trim();
            channel.Description = description.Length == 0 ? null : description;
        }

        if (request.IsEnabled is { } enabled)
        {
            channel.IsEnabled = enabled;
        }

        if (request.AllowDowngrade is { } allowDowngrade)
        {
            channel.AllowDowngrade = allowDowngrade;
        }

        if (request.AudienceTags is not null)
        {
            channel.AudienceTags = [.. AudienceTagRules.Normalize(request.AudienceTags)];
        }

        await _db.SaveChangesAsync(cancellationToken);

        var releaseCount = await _db.Set<Release>()
            .CountAsync(x => x.ChannelId == channel.Id, cancellationToken);
        var published = await _db.Set<Release>()
            .AsNoTracking()
            .Where(x => x.ChannelId == channel.Id && x.Status == VersionConventions.StatusPublished)
            .Select(static x => new { x.Version, x.PublishedAt })
            .ToListAsync(cancellationToken);

        string? latestVersion = null;
        if (published.Count > 0)
        {
            var best = published[0];
            foreach (var candidate in published.Skip(1))
            {
                if (ReleaseVersionOrdering.Compare(
                        candidate.Version, candidate.PublishedAt,
                        best.Version, best.PublishedAt) > 0)
                {
                    best = candidate;
                }
            }

            latestVersion = best.Version;
        }

        return ToDto(channel, releaseCount, latestVersion);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        string appKey,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var channel = await _db.Set<ReleaseChannel>()
                          .FirstOrDefaultAsync(x => x.Id == id && x.AppKey == appKey, cancellationToken)
                      ?? throw BusinessException.NotFound(
                          ApiErrorCode.VersionsChannelNotFound,
                          $"渠道 {id} 不存在。");

        var releaseCount = await _db.Set<Release>()
            .CountAsync(x => x.ChannelId == id, cancellationToken);
        if (releaseCount > 0)
        {
            throw new BusinessException(
                ApiErrorCode.VersionsReleaseImmutable,
                $"渠道 {channel.Key} 仍登记 {releaseCount} 个版本，请先删除全部版本再删除渠道。");
        }

        _db.Set<ReleaseChannel>().Remove(channel);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static ReleaseChannelDto ToDto(ReleaseChannel channel, int releaseCount, string? latestVersion) =>
        new(
            channel.Id,
            channel.Key,
            channel.Name,
            channel.Description,
            channel.IsEnabled,
            releaseCount,
            latestVersion,
            channel.CreatedAt,
            channel.UpdatedAt,
            channel.AllowDowngrade,
            channel.AudienceTags,
            channel.AutoCreated);
}
