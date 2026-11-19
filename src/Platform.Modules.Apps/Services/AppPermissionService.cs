using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Platform.Contracts;
using Platform.Contracts.Apps;
using Platform.Contracts.Devices;
using Platform.Contracts.Permissions;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Infrastructure.Persistence;
using Platform.Modules.Apps.Domain;

namespace Platform.Modules.Apps.Services;

/// <summary>应用权限清单读写。</summary>
public interface IAppPermissionService : IAppPermissionDirectory
{
    /// <summary>读取应用声明的权限清单与常用组合。</summary>
    Task<AppPermissionManifestDto> GetAsync(string appKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 整表替换应用权限清单；空清单表示清空。成功后清单自动锁定。
    /// </summary>
    Task<AppPermissionManifestDto> ReplaceAsync(
        string appKey,
        ReplaceAppPermissionsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>人工解锁权限清单，允许应用再次上传；解锁后可被下一次上传重新锁定。</summary>
    Task<AppPermissionManifestDto> UnlockAsync(string appKey, CancellationToken cancellationToken = default);

    /// <summary>人工锁定权限清单，立即拒绝后续上传，直到再次解锁。</summary>
    Task<AppPermissionManifestDto> LockAsync(string appKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// 应用权限清单服务。平台只校验权限码格式、全局唯一性与误接保护，语义由应用自身负责。
/// </summary>
public sealed partial class AppPermissionService : IAppPermissionService
{
    private const string DefaultGroup = "app";

    private readonly PlatformDbContext _db;
    private readonly IClock _clock;
    private readonly IInstallationEventRouter? _router;
    private readonly ILogger<AppPermissionService> _logger;

    private static readonly Action<ILogger, string, Exception?> ManifestPushFailedLog =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, "PermissionManifestPushFailed"),
            "权限清单变更提示推送失败（清单结果不受影响）：应用 {AppKey}。");

    /// <summary>构造。</summary>
    public AppPermissionService(
        PlatformDbContext db,
        IClock clock,
        IEnumerable<IInstallationEventRouter> routers,
        ILogger<AppPermissionService> logger)
    {
        ArgumentNullException.ThrowIfNull(routers);
        _db = db;
        _clock = clock;
        _router = routers.FirstOrDefault();
        _logger = logger;
    }

    [GeneratedRegex(AppConventions.AppPermissionCodePattern, RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupRegex();

    [GeneratedRegex(AppConventions.AppPermissionPresetKeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex PresetKeyRegex();

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermissionDescriptor>> ListAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var app = await LoadAppAsync(appKey, cancellationToken);
        return [.. app.Permissions
            .OrderBy(static x => x.Sort)
            .ThenBy(static x => x.Code, StringComparer.Ordinal)
            .Select(permission => ToDescriptor(app, permission))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> ListCodesAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var codes = await _db.Set<AppPermissionDefinition>()
            .AsNoTracking()
            .Where(permission => permission.App!.Key == appKey)
            .Select(static permission => permission.Code)
            .ToListAsync(cancellationToken);
        return codes.ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> ListAllCodesAsync(CancellationToken cancellationToken = default)
    {
        var codes = await _db.Set<AppPermissionDefinition>()
            .AsNoTracking()
            .Select(static permission => permission.Code)
            .ToListAsync(cancellationToken);
        return codes.ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<bool> IsLockedAsync(string appKey, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeAppKey(appKey);
        return await _db.Set<App>()
            .AsNoTracking()
            .Where(x => x.Key == normalizedKey)
            .Select(static x => x.PermissionsLocked)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AppPermissionManifestDto> GetAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var app = await LoadAppAsync(appKey, cancellationToken);
        return ToManifest(app);
    }

    /// <inheritdoc />
    public async Task<AppPermissionManifestDto> ReplaceAsync(
        string appKey,
        ReplaceAppPermissionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedKey = NormalizeAppKey(appKey);
        var app = await _db.Set<App>()
            .Include(static x => x.Permissions)
            .Include(static x => x.Presets)
            .FirstOrDefaultAsync(x => x.Key == normalizedKey, cancellationToken)
            ?? throw BusinessException.NotFound(ApiErrorCode.AppsNotFound, $"应用 {normalizedKey} 不存在。");

        // 首次上传成功后自动锁定，避免另一个权限完全不同的应用误连后整表覆盖。
        if (app.PermissionsLocked)
        {
            throw new BusinessException(
                ApiErrorCode.AppsPermissionManifestLocked,
                $"应用 {normalizedKey} 的权限清单已锁定，请先在管理台解锁后再上传。");
        }

        var permissions = NormalizePermissions(request.Permissions);
        var permissionCodes = permissions.Select(static x => x.Code).ToHashSet(StringComparer.Ordinal);
        var presets = NormalizePresets(request.Presets, permissionCodes);
        await EnsureNoConflictsAsync(app.Id, permissions, cancellationToken);

        // 原地更新同名条目，避免「先删后插」在唯一索引上撞车（InMemory 测试库也不支持事务）。
        SyncPermissions(app, permissions);
        SyncPresets(app, presets);
        app.PermissionsLocked = true;
        await _db.SaveChangesAsync(cancellationToken);
        await NotifyManifestChangedAsync(normalizedKey, locked: true, cancellationToken);

        return ToManifest(app);
    }

    /// <inheritdoc />
    public async Task<AppPermissionManifestDto> UnlockAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeAppKey(appKey);
        var app = await _db.Set<App>()
            .Include(static x => x.Permissions)
            .Include(static x => x.Presets)
            .FirstOrDefaultAsync(x => x.Key == normalizedKey, cancellationToken)
            ?? throw BusinessException.NotFound(ApiErrorCode.AppsNotFound, $"应用 {normalizedKey} 不存在。");

        if (app.PermissionsLocked)
        {
            app.PermissionsLocked = false;
            await _db.SaveChangesAsync(cancellationToken);
            await NotifyManifestChangedAsync(normalizedKey, locked: false, cancellationToken);
        }

        return ToManifest(app);
    }

    /// <inheritdoc />
    public async Task<AppPermissionManifestDto> LockAsync(
        string appKey,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeAppKey(appKey);
        var app = await _db.Set<App>()
            .Include(static x => x.Permissions)
            .Include(static x => x.Presets)
            .FirstOrDefaultAsync(x => x.Key == normalizedKey, cancellationToken)
            ?? throw BusinessException.NotFound(ApiErrorCode.AppsNotFound, $"应用 {normalizedKey} 不存在。");

        if (!app.PermissionsLocked)
        {
            app.PermissionsLocked = true;
            await _db.SaveChangesAsync(cancellationToken);
            await NotifyManifestChangedAsync(normalizedKey, locked: true, cancellationToken);
        }

        return ToManifest(app);
    }

    /// <summary>清单变更提示：app 组广播，客户端回拉权限清单核对；推送失败不影响已落库清单。</summary>
    private async Task NotifyManifestChangedAsync(string appKey, bool locked, CancellationToken cancellationToken)
    {
        if (_router is null)
        {
            return;
        }

        try
        {
            await _router.ToAppAsync(
                appKey,
                DeviceHubEvents.PermissionManifestChanged,
                new PermissionManifestChangedNotification(appKey, locked, _clock.UtcNow),
                cancellationToken);
        }
        catch (Exception exception)
        {
            ManifestPushFailedLog(_logger, appKey, exception);
        }
    }

    private async Task<App> LoadAppAsync(string appKey, CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeAppKey(appKey);
        return await _db.Set<App>()
                   .AsNoTracking()
                   .Include(static x => x.Permissions)
                   .Include(static x => x.Presets)
                   .FirstOrDefaultAsync(x => x.Key == normalizedKey, cancellationToken)
               ?? throw BusinessException.NotFound(ApiErrorCode.AppsNotFound, $"应用 {normalizedKey} 不存在。");
    }

    private void SyncPermissions(App app, IReadOnlyList<AppPermissionDefinition> permissions)
    {
        var set = _db.Set<AppPermissionDefinition>();
        var existing = app.Permissions.ToDictionary(static x => x.Code, StringComparer.Ordinal);
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in permissions)
        {
            retained.Add(item.Code);
            if (existing.TryGetValue(item.Code, out var current))
            {
                current.DisplayName = item.DisplayName;
                current.Description = item.Description;
                current.GroupKey = item.GroupKey;
                current.GroupLabel = item.GroupLabel;
                current.Sort = item.Sort;
                continue;
            }

            var created = new AppPermissionDefinition
            {
                AppId = app.Id,
                Code = item.Code,
                DisplayName = item.DisplayName,
                Description = item.Description,
                GroupKey = item.GroupKey,
                GroupLabel = item.GroupLabel,
                Sort = item.Sort,
            };
            app.Permissions.Add(created);
            set.Add(created);
        }

        foreach (var stale in existing.Values.Where(item => !retained.Contains(item.Code)).ToList())
        {
            app.Permissions.Remove(stale);
            set.Remove(stale);
        }
    }

    private void SyncPresets(App app, IReadOnlyList<AppPermissionPreset> presets)
    {
        var set = _db.Set<AppPermissionPreset>();
        var existing = app.Presets.ToDictionary(static x => x.Key, StringComparer.Ordinal);
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in presets)
        {
            retained.Add(item.Key);
            if (existing.TryGetValue(item.Key, out var current))
            {
                current.Label = item.Label;
                current.Description = item.Description;
                current.Permissions = item.Permissions;
                current.Sort = item.Sort;
                continue;
            }

            var created = new AppPermissionPreset
            {
                AppId = app.Id,
                Key = item.Key,
                Label = item.Label,
                Description = item.Description,
                Permissions = item.Permissions,
                Sort = item.Sort,
            };
            app.Presets.Add(created);
            set.Add(created);
        }

        foreach (var stale in existing.Values.Where(item => !retained.Contains(item.Key)).ToList())
        {
            app.Presets.Remove(stale);
            set.Remove(stale);
        }
    }

    private async Task EnsureNoConflictsAsync(
        Guid appId,
        IReadOnlyList<AppPermissionDefinition> permissions,
        CancellationToken cancellationToken)
    {
        foreach (var permission in permissions)
        {
            if (PermissionCatalog.IsKnown(permission.Code))
            {
                throw Invalid($"权限码 {permission.Code} 已被平台占用。");
            }
        }

        var codes = permissions.Select(static x => x.Code).ToArray();
        if (codes.Length == 0)
        {
            return;
        }

        var conflicts = await _db.Set<AppPermissionDefinition>()
            .AsNoTracking()
            .Where(permission => permission.AppId != appId && codes.Contains(permission.Code))
            .Select(permission => new { permission.Code, AppKey = permission.App!.Key })
            .ToListAsync(cancellationToken);

        if (conflicts.Count > 0)
        {
            var first = conflicts[0];
            throw Invalid($"权限码 {first.Code} 已被应用 {first.AppKey} 声明。");
        }
    }

    private static List<AppPermissionDefinition> NormalizePermissions(
        IReadOnlyList<AppPermissionDto>? permissions)
    {
        if (permissions is null || permissions.Count == 0)
        {
            return [];
        }

        if (permissions.Count > AppConventions.MaxAppPermissions)
        {
            throw Invalid($"单个应用最多声明 {AppConventions.MaxAppPermissions} 个权限。");
        }

        var result = new List<AppPermissionDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < permissions.Count; index++)
        {
            var source = permissions[index];
            var code = source.Code?.Trim();
            if (string.IsNullOrEmpty(code) || !CodeRegex().IsMatch(code))
            {
                throw Invalid($"权限码 '{source.Code}' 必须是小写点分格式（至少两段）。");
            }

            if (string.Equals(code, PermissionCatalog.Wildcard, StringComparison.Ordinal))
            {
                throw Invalid("应用权限清单不得声明通配符 *。");
            }

            if (!seen.Add(code))
            {
                throw Invalid($"权限码 {code} 在清单中重复。");
            }

            var displayName = source.DisplayName?.Trim();
            if (string.IsNullOrEmpty(displayName))
            {
                throw Invalid($"权限 {code} 缺少显示名。");
            }

            if (displayName.Length > 128)
            {
                throw Invalid($"权限 {code} 的显示名不得超过 128 个字符。");
            }

            var description = string.IsNullOrWhiteSpace(source.Description) ? null : source.Description.Trim();
            if (description is { Length: > 512 })
            {
                throw Invalid($"权限 {code} 的说明不得超过 512 个字符。");
            }

            var group = string.IsNullOrWhiteSpace(source.Group) ? DefaultGroup : source.Group.Trim();
            if (!GroupRegex().IsMatch(group))
            {
                throw Invalid($"权限 {code} 的分组键必须是小写字母开头的 1–64 位小写字母/数字/连字符。");
            }

            var groupLabel = string.IsNullOrWhiteSpace(source.GroupLabel) ? null : source.GroupLabel.Trim();
            if (groupLabel is { Length: > 64 })
            {
                throw Invalid($"权限 {code} 的分组显示名不得超过 64 个字符。");
            }

            result.Add(new AppPermissionDefinition
            {
                Code = code,
                DisplayName = displayName,
                Description = description,
                GroupKey = group,
                GroupLabel = groupLabel,
                Sort = source.Sort == 0 ? 1000 + (index * 10) : source.Sort,
            });
        }

        return result;
    }

    private static List<AppPermissionPreset> NormalizePresets(
        IReadOnlyList<AppPermissionPresetDto>? presets,
        HashSet<string> permissionCodes)
    {
        if (presets is null || presets.Count == 0)
        {
            return [];
        }

        if (presets.Count > AppConventions.MaxAppPermissionPresets)
        {
            throw Invalid($"单个应用最多声明 {AppConventions.MaxAppPermissionPresets} 个常用权限组合。");
        }

        var result = new List<AppPermissionPreset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < presets.Count; index++)
        {
            var source = presets[index];
            var key = source.Key?.Trim();
            if (string.IsNullOrEmpty(key) || !PresetKeyRegex().IsMatch(key))
            {
                throw Invalid($"组合键 '{source.Key}' 必须是小写字母开头的 1–64 位小写字母/数字/连字符。");
            }

            if (!seen.Add(key))
            {
                throw Invalid($"组合键 {key} 在清单中重复。");
            }

            var label = source.Label?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                throw Invalid($"常用组合 {key} 缺少显示名。");
            }

            if (label.Length > 64)
            {
                throw Invalid($"常用组合 {key} 的显示名不得超过 64 个字符。");
            }

            var description = string.IsNullOrWhiteSpace(source.Description) ? null : source.Description.Trim();
            if (description is { Length: > 256 })
            {
                throw Invalid($"常用组合 {key} 的说明不得超过 256 个字符。");
            }

            var codes = source.Permissions?.Where(static code => !string.IsNullOrWhiteSpace(code))
                .Select(static code => code.Trim())
                .ToList() ?? [];
            if (codes.Count == 0)
            {
                throw Invalid($"常用组合 {key} 至少包含一个权限码。");
            }

            if (codes.Count != codes.Distinct(StringComparer.Ordinal).Count())
            {
                throw Invalid($"常用组合 {key} 内的权限码重复。");
            }

            var undeclared = codes.Where(code => !permissionCodes.Contains(code)).ToList();
            if (undeclared.Count > 0)
            {
                throw Invalid($"常用组合 {key} 引用了未声明的权限码：{string.Join(", ", undeclared)}。");
            }

            result.Add(new AppPermissionPreset
            {
                Key = key,
                Label = label,
                Description = description,
                Permissions = codes,
                Sort = source.Sort == 0 ? 1000 + (index * 10) : source.Sort,
            });
        }

        return result;
    }

    private static AppPermissionManifestDto ToManifest(App app) =>
        new(
            [.. app.Permissions
                .OrderBy(static x => x.Sort)
                .ThenBy(static x => x.Code, StringComparer.Ordinal)
                .Select(ToDto)],
            [.. app.Presets
                .OrderBy(static x => x.Sort)
                .ThenBy(static x => x.Key, StringComparer.Ordinal)
                .Select(ToPresetDto)],
            app.PermissionsLocked);

    private static PermissionDescriptor ToDescriptor(App app, AppPermissionDefinition permission) =>
        new(
            Code: permission.Code,
            ModuleKey: ModuleKey(app.Key, permission.GroupKey),
            DisplayNameKey: string.Empty,
            Sort: permission.Sort,
            PlatformAdminOnly: false,
            Description: permission.Description,
            ModuleLabel: permission.GroupLabel ?? app.Name);

    private static AppPermissionDto ToDto(AppPermissionDefinition permission) =>
        new(
            permission.Code,
            permission.DisplayName,
            permission.Description,
            permission.GroupKey,
            permission.GroupLabel,
            permission.Sort);

    private static AppPermissionPresetDto ToPresetDto(AppPermissionPreset preset) =>
        new(
            preset.Key,
            preset.Label,
            preset.Description,
            preset.Permissions,
            preset.Sort);

    private static string ModuleKey(string appKey, string group) => $"app:{appKey}:{group}";

    private static string NormalizeAppKey(string appKey) =>
        string.IsNullOrWhiteSpace(appKey)
            ? throw BusinessException.Invalid("应用键不能为空。")
            : appKey.Trim().ToLowerInvariant();

    private static BusinessException Invalid(string message) =>
        new(ApiErrorCode.AppsPermissionManifestInvalid, message);
}
