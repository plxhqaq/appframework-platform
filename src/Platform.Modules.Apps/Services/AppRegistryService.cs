using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Infrastructure.Persistence;
using Platform.Modules.Apps.Domain;

namespace Platform.Modules.Apps.Services;

/// <summary>应用注册读写。应用键创建后不可修改——它是历史数据与令牌声明的锚点。</summary>
public interface IAppRegistryService
{
    /// <summary>按应用键排序列出全部应用。</summary>
    Task<IReadOnlyList<AppSummaryDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>取应用详情；不存在抛 <see cref="ApiErrorCode.AppsNotFound"/>。</summary>
    Task<AppDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>创建应用。</summary>
    Task<AppDetailDto> CreateAsync(CreateAppRequest request, CancellationToken cancellationToken = default);

    /// <summary>更新名称/说明/启用状态。</summary>
    Task<AppDetailDto> UpdateAsync(Guid id, UpdateAppRequest request, CancellationToken cancellationToken = default);
}

/// <summary>应用注册服务实现。</summary>
public sealed partial class AppRegistryService : IAppRegistryService
{
    private readonly PlatformDbContext _db;

    /// <summary>构造。</summary>
    public AppRegistryService(PlatformDbContext db)
    {
        _db = db;
    }

    [GeneratedRegex(AppConventions.AppKeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex AppKeyRegex();

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppSummaryDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await _db.Set<App>()
            .AsNoTracking()
            .OrderBy(static x => x.Key)
            .Select(static x => new AppSummaryDto(
                x.Id, x.Key, x.Name, x.Description, x.IsEnabled, x.CreatedAt, x.UpdatedAt))
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<AppDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var app = await _db.Set<App>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                  ?? throw BusinessException.NotFound(ApiErrorCode.AppsNotFound, $"应用 {id} 不存在。");
        return ToDetail(app);
    }

    /// <inheritdoc />
    public async Task<AppDetailDto> CreateAsync(
        CreateAppRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = request.Key?.Trim();
        if (string.IsNullOrEmpty(key) || !AppKeyRegex().IsMatch(key))
        {
            throw BusinessException.Invalid(
                $"应用键必须匹配 {AppConventions.AppKeyPattern}（小写字母开头，2–64 位小写字母/数字/连字符）。");
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw BusinessException.Invalid("应用名称不能为空。");
        }

        if (await _db.Set<App>().AnyAsync(x => x.Key == key, cancellationToken))
        {
            throw new BusinessException(ApiErrorCode.AppsKeyExists, $"应用键 {key} 已存在。");
        }

        var app = new App
        {
            Key = key,
            Name = name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
        };

        _db.Set<App>().Add(app);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // 并发创建同名键时唯一索引兜底；上下文已不可再用，异常直接映射成业务冲突。
            _db.ChangeTracker.Clear();
            throw new BusinessException(ApiErrorCode.AppsKeyExists, $"应用键 {key} 已存在。", exception);
        }

        return ToDetail(app);
    }

    /// <inheritdoc />
    public async Task<AppDetailDto> UpdateAsync(
        Guid id,
        UpdateAppRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var app = await _db.Set<App>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                  ?? throw BusinessException.NotFound(ApiErrorCode.AppsNotFound, $"应用 {id} 不存在。");

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length == 0)
            {
                throw BusinessException.Invalid("应用名称不能为空。");
            }
            app.Name = name;
        }

        if (request.Description is not null)
        {
            app.Description = request.Description.Trim().Length == 0 ? null : request.Description.Trim();
        }

        if (request.IsEnabled is { } enabled)
        {
            app.IsEnabled = enabled;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return ToDetail(app);
    }

    private static AppDetailDto ToDetail(App app) =>
        new(app.Id, app.Key, app.Name, app.Description, app.IsEnabled, app.SettingsJson, app.CreatedAt, app.UpdatedAt);
}
