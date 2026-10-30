namespace Platform.Contracts.Hubs;

/// <summary>SignalR Hub 路径（两端共用，避免字符串各写一遍）。</summary>
public static class PlatformHubPaths
{
    /// <summary>唯一 Hub 根路径；具体业务事件靠事件名区分。</summary>
    public const string Root = "/hubs/platform";
}

/// <summary>Hub 事件名。</summary>
/// <remarks>
/// **约定**：所有推送都只是"提示"，客户端收到后必须回拉接口重新校验
/// （授权、版本、文档均如此）。推送不可作为事实来源，避免伪造/乱序导致状态错乱。
/// </remarks>
public static class PlatformHubEvents
{
    /// <summary>授权状态变化（吊销/续期）。负载 <see cref="LicenseChangedNotification"/>。</summary>
    public const string LicenseChanged = "LicenseChanged";

    /// <summary>配置下发。负载 <see cref="ConfigPushNotification"/>，客户端处理后回 <see cref="ConfigPushAck"/>。</summary>
    public const string ConfigPush = "ConfigPush";

    /// <summary>配置下发回执。负载 <see cref="ConfigPushAck"/>。</summary>
    public const string ConfigPushAck = "ConfigPushAck";

    /// <summary>扫码登录已被审批。负载 <see cref="QrLoginApprovedNotification"/>。</summary>
    public const string QrLoginApproved = "QrLoginApproved";

    /// <summary>版本已发布。负载 <see cref="ReleasePublishedNotification"/>。</summary>
    public const string ReleasePublished = "ReleasePublished";

    /// <summary>文档集合有变更。负载 <see cref="DocumentChangedNotification"/>。</summary>
    public const string DocumentChanged = "DocumentChanged";
}

/// <summary>授权变化通知。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="MachineCode">目标机器码；为空表示该应用下全部主体。</param>
/// <param name="Reason">变化原因：<c>revoked</c> / <c>renewed</c> / <c>issued</c>。</param>
public sealed record LicenseChangedNotification(string AppKey, string? MachineCode, string Reason)
{
    /// <summary>吊销。</summary>
    public const string ReasonRevoked = "revoked";

    /// <summary>续期。</summary>
    public const string ReasonRenewed = "renewed";

    /// <summary>新签发。</summary>
    public const string ReasonIssued = "issued";
}

/// <summary>配置下发通知。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="BackupId">引用的备份行 id（客户端据此下载）。</param>
/// <param name="Namespace">备份命名空间。</param>
/// <param name="SchemaVersion">备份 schema 版本。</param>
/// <param name="Required">是否必须应用。</param>
public sealed record ConfigPushNotification(
    string AppKey,
    Guid BackupId,
    string Namespace,
    int SchemaVersion,
    bool Required);

/// <summary>配置下发回执。</summary>
/// <param name="BackupId">备份行 id。</param>
/// <param name="Succeeded">是否应用成功。</param>
/// <param name="Message">失败原因。</param>
public sealed record ConfigPushAck(Guid BackupId, bool Succeeded, string? Message);

/// <summary>扫码登录审批通过通知。</summary>
/// <param name="RequestId">扫码会话 id（客户端从二维码载荷中解析）。</param>
/// <param name="Approved">是否批准；拒绝时为 <c>false</c>。</param>
public sealed record QrLoginApprovedNotification(Guid RequestId, bool Approved);

/// <summary>版本发布通知。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="ChannelKey">渠道键。</param>
/// <param name="Version">版本号。</param>
public sealed record ReleasePublishedNotification(string AppKey, string ChannelKey, string Version);

/// <summary>文档变更通知。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="CollectionKey">集合键。</param>
public sealed record DocumentChangedNotification(string AppKey, string CollectionKey);
