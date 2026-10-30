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
    Guid? DeviceId,
    string? DeviceName,
    string? ModuleId,
    int SchemaVersion,
    string? FrameworkVersion,
    string? Channel,
    string? PayloadJson,
    string? Sha256);

/// <summary>备份摘要（列表与详情共用）。</summary>
public sealed record BackupDto(
    Guid Id,
    string NamespaceKey,
    string Kind,
    string Name,
    Guid? DeviceId,
    string? DeviceName,
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

/// <summary>下发备份：目标设备/渠道由调用方声明，服务端复核兼容矩阵。</summary>
/// <param name="TargetDeviceId">目标设备；为空表示应用内全部在线连接。</param>
/// <param name="TargetChannel">目标渠道；为空按默认渠道处理。</param>
/// <param name="TargetFrameworkVersion">目标框架版本；用于主版本兼容判断。</param>
/// <param name="RestartAfterApply">应用后是否要求重启。</param>
/// <param name="Force">放宽主版本校验；渠道不一致仍然拒绝。</param>
/// <param name="CurrentHash">目标当前配置哈希；提供时先落一条 pre-push 快照。</param>
/// <param name="ConfirmCrossDevice">备份设备与目标设备不一致时，必须显式确认。</param>
public sealed record PushBackupRequest(
    Guid? TargetDeviceId,
    string? TargetChannel,
    string? TargetFrameworkVersion,
    bool RestartAfterApply,
    bool Force,
    string? CurrentHash,
    bool ConfirmCrossDevice);

/// <summary>下发结果：<paramref name="Acked"/> 表示目标是否回执，<paramref name="Success"/> 表示回执内容。</summary>
public sealed record PushBackupResult(
    bool Acked,
    bool Success,
    string? Message,
    Guid? SnapshotBackupId);

/// <summary>兼容矩阵判定结果。</summary>
/// <param name="Compatible">是否可下发。</param>
/// <param name="Reason">不兼容原因（中文，可直接展示）。</param>
public readonly record struct BackupCompatibility(bool Compatible, string? Reason);
