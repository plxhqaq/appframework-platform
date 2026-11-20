namespace Platform.Contracts.Retention;

/// <summary>单个域的清理宽限期；<see cref="IsDatabaseValue"/> 为 false 表示回落出厂默认。</summary>
public sealed record RetentionDomainSettingsDto(
    string ModuleKey,
    int GraceMinutes,
    bool IsDatabaseValue);

/// <summary>备份/文档两域的清理宽限期（平台全部应用生效）。</summary>
public sealed record RetentionSettingsDto(
    RetentionDomainSettingsDto Backups,
    RetentionDomainSettingsDto Documents);

/// <summary>整体提交两域宽限期（分钟，最小 1）。</summary>
public sealed record UpdateRetentionSettingsRequest(
    int BackupsGraceMinutes,
    int DocumentsGraceMinutes);

/// <summary>立即清理结果：删除的数据库行数与回收的物理文件数。</summary>
public sealed record PurgeResultDto(int RowsDeleted, int FilesDeleted);
