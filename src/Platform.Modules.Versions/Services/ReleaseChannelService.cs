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
        var rows = await _db.Set<ReleaseChannel>()
            .AsNoTracking()
            .Where(x => x.AppKey == appKey)
            .OrderBy(static x => x.Key)
            .Select(static x => new
            {
                Channel = x,
                ReleaseCount = x.Releases.Count,
                LatestVersion = x.Releases
                    .Where(static r => r.Status == VersionConventions.StatusPublished)
                    .OrderByDescending(static r => r.PublishedAt)
                    .Select(static r => r.Version)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(static row => ToDto(
            row.Channel,
            row.ReleaseCount,
            row.LatestVersion))];
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

        await _db.SaveChangesAsync(cancellationToken);

        var releaseCount = await _db.Set<Release>()
            .CountAsync(x => x.ChannelId == channel.Id, cancellationToken);
        var latestVersion = await _db.Set<Release>()
            .AsNoTracking()
            .Where(x => x.ChannelId == channel.Id && x.Status == VersionConventions.StatusPublished)
            .OrderByDescending(static x => x.PublishedAt)
            .Select(static x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);

        return ToDto(channel, releaseCount, latestVersion);
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
            channel.UpdatedAt);
}
