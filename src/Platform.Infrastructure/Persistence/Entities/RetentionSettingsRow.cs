namespace Platform.Infrastructure.Persistence.Entities;

/// <summary>模块清理宽限期配置行（横切表，不属于任何业务模块）。</summary>
public sealed class RetentionSettingsRow
{
    /// <summary>模块键（<c>backups</c> / <c>documents</c>）。</summary>
    public string ModuleKey { get; set; } = string.Empty;

    /// <summary>软删除到物理清理的宽限期（分钟，最小 1）。</summary>
    public int CleanupGraceMinutes { get; set; }

    /// <summary>最近一次修改时间。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>最近一次修改人（登录名）。</summary>
    public string? UpdatedBy { get; set; }
}
