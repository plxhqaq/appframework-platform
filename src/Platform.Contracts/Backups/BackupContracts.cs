namespace Platform.Contracts.Backups;

/// <summary>备份模块常量与校验口径（服务端与客户端共用）。</summary>
public static class BackupConventions
{
    /// <summary>内联上限：不超过该字节数的负载直接存库，超过则转存 Blob。</summary>
    public const int InlineMaxBytes = 64 * 1024;

    /// <summary>命名空间键：小写字母开头，2–32 位小写字母/数字/连字符。</summary>
    public const string KeyPattern = "^[a-z][a-z0-9-]{1,31}$";

    /// <summary>默认渠道：空值按该渠道处理（历史数据无渠道信息时的等价语义）。</summary>
    public const string DefaultChannel = "default";

    /// <summary>客户端按设备上传的默认命名空间键。</summary>
    public const string DeviceBackupsNamespaceKey = "device-backups";

    /// <summary>客户端按设备上传的默认命名空间名称。</summary>
    public const string DeviceBackupsNamespaceName = "设备备份";

    /// <summary>命名空间默认保留版本数。</summary>
    public const int DefaultMaxVersions = 50;

    /// <summary>保留版本数上限。</summary>
    public const int MaxVersionsLimit = 500;

    /// <summary>配置备份。</summary>
    public const string KindConfig = "config";

    /// <summary>档案/参数集备份。</summary>
    public const string KindProfile = "profile";

    /// <summary>是否合法的备份类型。</summary>
    public static bool IsValidKind(string? kind) => kind is KindConfig or KindProfile;

    /// <summary>渠道归一：空/空白 = <see cref="DefaultChannel"/>，统一小写。</summary>
    public static string NormalizeChannel(string? channel) =>
        string.IsNullOrWhiteSpace(channel) ? DefaultChannel : channel.Trim().ToLowerInvariant();
}

/// <summary>备份命名空间：同一应用下的配置域（例如 <c>line-a</c>、<c>global</c>）。</summary>
public sealed record BackupNamespaceDto(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    string Channel,
    int MaxVersions,
    bool IsEnabled,
    int BackupCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>创建命名空间；键创建后不可修改。</summary>
public sealed record CreateBackupNamespaceRequest(
    string? Key,
    string? Name,
    string? Description,
    string? Channel,
    int? MaxVersions);

/// <summary>更新命名空间；缺省字段表示不修改。</summary>
public sealed record UpdateBackupNamespaceRequest(
    string? Name,
    string? Description,
    string? Channel,
    int? MaxVersions,
    bool? IsEnabled);

/// <summary>创建备份：负载为 JSON 文本，服务端按 64KB 阈值决定内联或转存。</summary>
public sealed record CreateBackupRequest(
    string? NamespaceKey,
    string? Kind,
    string? Name,
    Guid? InstallationId,
    string? ModuleId,
    int SchemaVersion,
    string? FrameworkVersion,
    string? Channel,
    string? PayloadJson,
    string? Sha256);

/// <summary>备份摘要（列表与详情共用）。节点字段是创建时快照。</summary>
public sealed record BackupDto(
    Guid Id,
    string NamespaceKey,
    string Kind,
    string Name,
    Guid? InstallationId,
    Guid? NodeId,
    string? NodeName,
    string? ModuleId,
    int SchemaVersion,
    string FrameworkVersion,
    string Channel,
    long SizeBytes,
    string? Sha256,
    bool HasInline,
    bool HasFile,
    string? FileName,
    string? DownloadUrl,
    DateTimeOffset CreatedAt,
    string? CreatedBy);

/// <summary>回收站中的备份（软删除，宽限期内可恢复）。</summary>
public sealed record DeletedBackupDto(
    Guid Id,
    string NamespaceKey,
    string Kind,
    string Name,
    long SizeBytes,
    bool HasFile,
    bool FileIntact,
    DateTimeOffset DeletedAt,
    int RemainingGraceMinutes);

/// <summary>下发备份：目标安装实例/渠道由调用方声明，服务端复核兼容矩阵。</summary>
/// <param name="TargetInstallationId">单个目标安装实例（与 <paramref name="TargetInstallationIds"/> 合并去重）。</param>
/// <param name="TargetChannel">目标渠道；为空按默认渠道处理。</param>
/// <param name="TargetFrameworkVersion">目标框架版本；用于主版本兼容判断。</param>
/// <param name="RestartAfterApply">应用后是否要求重启。</param>
/// <param name="Force">放宽主版本校验；渠道不一致仍然拒绝。</param>
/// <param name="CurrentHash">目标当前配置哈希；仅单安装实例下发时允许，提供时先落一条 pre-push 快照。</param>
/// <param name="ConfirmCrossInstallation">备份来源安装实例与任一目标不一致时，必须显式确认。</param>
/// <param name="TargetInstallationIds">目标安装实例集合（多选）。留空且未显式广播时拒绝。</param>
/// <param name="BroadcastToApp">显式请求应用组广播（兼容能力，管理台不提供）；与目标安装实例互斥。</param>
public sealed record PushBackupRequest(
    Guid? TargetInstallationId,
    string? TargetChannel,
    string? TargetFrameworkVersion,
    bool RestartAfterApply,
    bool Force,
    string? CurrentHash,
    bool ConfirmCrossInstallation,
    IReadOnlyList<Guid>? TargetInstallationIds = null,
    bool BroadcastToApp = false);

/// <summary>单个安装实例的下发结果。</summary>
/// <param name="InstallationId">目标安装实例。</param>
/// <param name="NodeId">目标所在物理节点。</param>
/// <param name="Delivered">命令是否已送达安装实例连接。</param>
/// <param name="Acked">安装实例是否回执。</param>
/// <param name="Success">回执内容：安装实例是否应用成功。</param>
/// <param name="Message">安装实例回执说明。</param>
public sealed record PushTargetResult(
    Guid InstallationId,
    Guid? NodeId,
    bool Delivered,
    bool Acked,
    bool Success,
    string? Message);

/// <summary>下发结果：多安装实例下发时 <paramref name="Success"/> 表示全部目标成功。</summary>
/// <param name="Acked">全部目标是否都回执（应用组广播时为唯一回执）。</param>
/// <param name="Success">全部目标是否都回执并应用成功。</param>
/// <param name="Message">汇总说明（单目标时为回执原文）。</param>
/// <param name="SnapshotBackupId">下发前落下的快照 id（仅单目标且提供当前哈希时）。</param>
/// <param name="Targets">逐安装实例结果；应用组广播时为空。</param>
public sealed record PushBackupResult(
    bool Acked,
    bool Success,
    string? Message,
    Guid? SnapshotBackupId,
    IReadOnlyList<PushTargetResult>? Targets = null);

/// <summary>兼容矩阵判定结果。</summary>
/// <param name="Compatible">是否可下发。</param>
/// <param name="Reason">不兼容原因（中文，可直接展示）。</param>
public readonly record struct BackupCompatibility(bool Compatible, string? Reason);
