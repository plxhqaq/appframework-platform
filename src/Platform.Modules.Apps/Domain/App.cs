using Platform.Kernel.Abstractions;

namespace Platform.Modules.Apps.Domain;

/// <summary>
/// 接入应用。应用键是全部业务数据的隔离轴：任何业务表都按 <see cref="Key"/> 归属，
/// 令牌里的 <c>app</c> 声明由这里产生。
/// </summary>
public sealed class App : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>应用键（唯一，路由与令牌声明都用它）。</summary>
    public required string Key { get; set; }

    /// <summary>显示名。</summary>
    public required string Name { get; set; }

    /// <summary>说明。</summary>
    public string? Description { get; set; }

    /// <summary>是否启用；禁用后已签发的 API Key 不能再换令牌。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>应用级设置（JSON），由各业务模块自行解释。</summary>
    public string? SettingsJson { get; set; }

    /// <summary>API Key 集合。</summary>
    public ICollection<AppApiKey> ApiKeys { get; set; } = [];

    /// <summary>应用自定义权限清单。</summary>
    public ICollection<AppPermissionDefinition> Permissions { get; set; } = [];

    /// <summary>应用自定义的常用权限组合。</summary>
    public ICollection<AppPermissionPreset> Presets { get; set; } = [];

    /// <summary>
    /// 权限清单是否已锁定。首次上传成功后自动锁定，之后必须由管理端人工解锁才能再次替换，
    /// 防止另一个权限完全不同的应用误连后覆盖本应用清单。
    /// </summary>
    public bool PermissionsLocked { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 应用声明的自定义权限。平台只保存与校验，不理解其业务含义；
/// 权限码全局唯一，避免两个应用用同一个码互相放大权限。
/// </summary>
public sealed class AppPermissionDefinition : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用。</summary>
    public Guid AppId { get; set; }

    /// <summary>所属应用导航。</summary>
    public App? App { get; set; }

    /// <summary>权限码（全局唯一）。</summary>
    public required string Code { get; set; }

    /// <summary>显示名。</summary>
    public required string DisplayName { get; set; }

    /// <summary>权限说明。</summary>
    public string? Description { get; set; }

    /// <summary>管理台权限树的分组键。</summary>
    public required string GroupKey { get; set; }

    /// <summary>分组显示名；为空时用应用名。</summary>
    public string? GroupLabel { get; set; }

    /// <summary>组内排序。</summary>
    public int Sort { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>应用声明的常用权限组合（如作业员、工程师），只用于管理台快捷勾选。</summary>
public sealed class AppPermissionPreset : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用。</summary>
    public Guid AppId { get; set; }

    /// <summary>所属应用导航。</summary>
    public App? App { get; set; }

    /// <summary>应用内唯一的组合键。</summary>
    public required string Key { get; set; }

    /// <summary>显示名。</summary>
    public required string Label { get; set; }

    /// <summary>组合说明。</summary>
    public string? Description { get; set; }

    /// <summary>组合包含的权限码。</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>管理台排序。</summary>
    public int Sort { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 应用 API Key。明文只在签发响应中出现一次，库里只有 SHA-256 哈希；
/// <see cref="Prefix"/> 单独存储并建唯一索引，用于按明文前缀快速定位后再做常量时间比对。
/// </summary>
public sealed class AppApiKey : IAuditableEntity
{
    /// <summary>主键。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>所属应用。</summary>
    public Guid AppId { get; set; }

    /// <summary>所属应用导航。</summary>
    public App? App { get; set; }

    /// <summary>明文中的公开段（<c>afk_{prefix}_{secret}</c> 的 <c>prefix</c>）。</summary>
    public required string Prefix { get; set; }

    /// <summary>secret 段的 SHA-256（小写十六进制）。</summary>
    public required string SecretHash { get; set; }

    /// <summary>用途说明。</summary>
    public required string Name { get; set; }

    /// <summary>授予的权限码。</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>过期时间；为空表示不过期。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>吊销时间；非空即失效。</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>最后使用时间（换令牌成功时更新）。</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }
}
