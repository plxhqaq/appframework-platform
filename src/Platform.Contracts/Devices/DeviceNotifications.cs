namespace Platform.Contracts.Devices;

/// <summary>
/// 设备 Hub 下行通知负载。**所有推送都只是提示**，客户端收到后必须回拉接口重新校验，
/// 不得把推送内容当作事实来源。新增通知一律带 <c>OccurredAt</c>（服务端 UTC），供去重/去抖与诊断。
/// </summary>
public static class DeviceNotificationConventions
{
    /// <summary>授权变化原因：吊销。</summary>
    public const string LicenseReasonRevoked = "revoked";

    /// <summary>授权变化原因：续期。</summary>
    public const string LicenseReasonRenewed = "renewed";

    /// <summary>授权变化原因：新签发。</summary>
    public const string LicenseReasonIssued = "issued";
}

/// <summary>授权变化通知（定向到目标机器上该应用的活跃安装实例）。提示回拉 licensing/status。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="MachineCode">目标机器码；为空表示该应用下全部主体。</param>
/// <param name="Reason">变化原因：<c>revoked</c> / <c>renewed</c> / <c>issued</c>。</param>
public sealed record LicenseChangedNotification(string AppKey, string? MachineCode, string Reason)
{
    /// <summary>吊销。</summary>
    public const string ReasonRevoked = DeviceNotificationConventions.LicenseReasonRevoked;

    /// <summary>续期。</summary>
    public const string ReasonRenewed = DeviceNotificationConventions.LicenseReasonRenewed;

    /// <summary>新签发。</summary>
    public const string ReasonIssued = DeviceNotificationConventions.LicenseReasonIssued;
}

/// <summary>版本发布通知（应用组广播）。客户端按自身渠道/分支回拉 versions/check。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="ChannelKey">渠道键。</param>
/// <param name="Version">版本号。</param>
public sealed record ReleasePublishedNotification(string AppKey, string ChannelKey, string Version);

/// <summary>文档变更通知（应用组广播）。客户端按需回拉文档 manifest。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="CollectionKey">集合键。</param>
public sealed record DocumentChangedNotification(string AppKey, string CollectionKey);

/// <summary>应用权限清单变更通知（应用组广播）。提示回拉权限清单。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="Locked">清单当前是否锁定。</param>
/// <param name="OccurredAt">服务端事件时间。</param>
public sealed record PermissionManifestChangedNotification(string AppKey, bool Locked, DateTimeOffset OccurredAt);

/// <summary>安装实例被停用通知：服务端随后强制断线，客户端应置禁用态并拉长退避。</summary>
/// <param name="InstallationId">安装实例主键。</param>
/// <param name="Reason">停用原因（管理端备注，可空）。</param>
/// <param name="OccurredAt">服务端事件时间。</param>
public sealed record InstallationDisabledNotification(Guid InstallationId, string? Reason, DateTimeOffset OccurredAt);

/// <summary>物理节点被停用通知（节点组广播，同机全部应用受影响）。</summary>
/// <param name="NodeId">节点主键。</param>
/// <param name="Reason">停用原因（管理端备注，可空）。</param>
/// <param name="OccurredAt">服务端事件时间。</param>
public sealed record NodeDisabledNotification(Guid NodeId, string? Reason, DateTimeOffset OccurredAt);

/// <summary>用户会话被吊销通知：客户端比对本地会话归属，命中则登出；epoch 守卫是强制兜底。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="UserId">被吊销会话的用户主键。</param>
/// <param name="InstallGuid">目标安装标识；为空表示该用户全部会话。</param>
/// <param name="SessionId">目标会话（刷新令牌家族）id；为空表示范围内全部。</param>
/// <param name="Reason">吊销原因词表（logout/revoked/password_changed/password_reset 等）。</param>
/// <param name="OccurredAt">服务端事件时间。</param>
public sealed record UserSessionRevokedNotification(
    string AppKey,
    Guid UserId,
    Guid? InstallGuid,
    Guid? SessionId,
    string Reason,
    DateTimeOffset OccurredAt);