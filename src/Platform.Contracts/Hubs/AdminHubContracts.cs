namespace Platform.Contracts.Hubs;

/// <summary>管理台 Hub 路径（浏览器专用，管理员令牌鉴权）。</summary>
public static class AdminHubPaths
{
    /// <summary>管理台实时通道根路径。</summary>
    public const string Root = "/hubs/admin";
}

/// <summary>管理台权限域词表：连接按令牌 perms 声明入对应域组，事件只发相关域。</summary>
public static class AdminHubDomains
{
    /// <summary>设备域（节点/安装实例）。</summary>
    public const string Devices = "devices";

    /// <summary>身份域（用户/会话/扫码）。</summary>
    public const string Identity = "identity";

    /// <summary>版本域。</summary>
    public const string Versions = "versions";

    /// <summary>授权域。</summary>
    public const string Licensing = "licensing";

    /// <summary>应用域。</summary>
    public const string Apps = "apps";

    /// <summary>全部域（平台管理员通配）。</summary>
    public static IReadOnlyList<string> All { get; } = [Devices, Identity, Versions, Licensing, Apps];
}

/// <summary>管理台 Hub 分组名。</summary>
public static class AdminHubGroups
{
    /// <summary>权限域分组。</summary>
    public static string Domain(string domain) => $"domain:{domain}";
}

/// <summary>管理台 Hub 客户端 → 服务端方法名。</summary>
public static class AdminHubMethods
{
    /// <summary>轻量保活上行（浏览器每 30s 调用）：承载令牌过期/epoch 校验与服务端踢线。</summary>
    public const string Touch = "Touch";
}

/// <summary>管理台 Hub 服务端 → 客户端事件名。所有事件只是提示，前端据此 invalidate 回拉，事实以接口为准。</summary>
public static class AdminHubEvents
{
    /// <summary>授权状态变化。负载 <see cref="AdminLicenseChangedNotification"/>。</summary>
    public const string LicenseChanged = "LicenseChanged";

    /// <summary>扫码会话状态变化。负载 <see cref="AdminQrSessionChangedNotification"/>。</summary>
    public const string QrSessionChanged = "QrSessionChanged";

    /// <summary>用户会话被吊销。负载 <see cref="AdminUserSessionsRevokedNotification"/>。</summary>
    public const string UserSessionsRevoked = "UserSessionsRevoked";

    /// <summary>增量包摄取状态迁移。负载 <see cref="AdminReleaseIngestionProgressNotification"/>。</summary>
    public const string ReleaseIngestionProgress = "ReleaseIngestionProgress";

    /// <summary>安装实例状态跃迁（在线/工位/报警值变化）。负载 <see cref="AdminInstallationStateChangedNotification"/>。</summary>
    public const string InstallationStateChanged = "InstallationStateChanged";

    /// <summary>节点聚合状态跃迁。负载 <see cref="AdminNodeStateChangedNotification"/>。</summary>
    public const string NodeStateChanged = "NodeStateChanged";
}

/// <summary>管理台授权变化通知。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="MachineCode">目标机器码。</param>
/// <param name="Reason">变化原因：<c>revoked</c> / <c>renewed</c> / <c>issued</c>。</param>
/// <param name="OccurredAt">服务端事件时间。</param>
public sealed record AdminLicenseChangedNotification(
    string AppKey,
    string MachineCode,
    string Reason,
    DateTimeOffset OccurredAt);

/// <summary>管理台扫码会话变化通知。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="SessionId">扫码会话 id。</param>
/// <param name="Status">会话状态（approved/rejected 等）。</param>
/// <param name="OccurredAt">服务端事件时间。</param>
public sealed record AdminQrSessionChangedNotification(
    string AppKey,
    Guid SessionId,
    string Status,
    DateTimeOffset OccurredAt);

/// <summary>管理台会话吊销通知（提示刷新会话列表；事实以管理端接口为准）。</summary>
/// <param name="AppKey">被吊销会话记录的应用键；平台上下文会话为空。</param>
/// <param name="UserId">被吊销的用户主键。</param>
/// <param name="Count">本次吊销的会话（家族）数。</param>
/// <param name="OccurredAt">服务端事件时间。</param>
public sealed record AdminUserSessionsRevokedNotification(
    string? AppKey,
    Guid UserId,
    int Count,
    DateTimeOffset OccurredAt);

/// <summary>管理台增量包摄取进度通知（提示回拉包详情，事实以管理端接口为准）。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="PackageId">摄取包主键。</param>
/// <param name="Status">迁移后的状态（<c>processing</c> / <c>ready</c> / <c>failed</c>）。</param>
/// <param name="Progress">进度 0–1（认领 0、完成 1；失败保持 0，语义由 Status 承载）。</param>
/// <param name="OccurredAt">服务端事件时间。</param>
public sealed record AdminReleaseIngestionProgressNotification(
    string AppKey,
    Guid PackageId,
    string Status,
    double Progress,
    DateTimeOffset OccurredAt);

/// <summary>安装实例状态跃迁通知（仅值变化时发送；提示刷新列表，事实以管理端接口为准）。</summary>
/// <param name="AppKey">应用键。</param>
/// <param name="InstallationId">安装实例主键。</param>
/// <param name="NodeId">所属节点主键。</param>
/// <param name="OnlineStatus">跃迁后的在线状态，词表同 <c>InstallationSummaryDto.OnlineStatus</c>。</param>
/// <param name="StationStatus">跃迁后的工位状态，词表同 <c>InstallationSummaryDto.StationStatus</c>。</param>
/// <param name="AlarmLevel">跃迁后的报警级别，词表同 <c>InstallationSummaryDto.AlarmLevel</c>。</param>
/// <param name="OccurredAt">服务端事件时间。</param>
public sealed record AdminInstallationStateChangedNotification(
    string AppKey,
    Guid InstallationId,
    Guid NodeId,
    string OnlineStatus,
    string StationStatus,
    string AlarmLevel,
    DateTimeOffset OccurredAt);

/// <summary>节点聚合状态跃迁通知（节点跨应用，故不带 appKey，前端一律放行后回拉）。
/// 词表与 <c>NodeSummaryDto</c> 同名字段一致。</summary>
/// <param name="NodeId">节点主键。</param>
/// <param name="OnlineStatus">聚合后的在线状态。</param>
/// <param name="StationStatus">聚合后的运行状态：任一在线实例工位在线即 <c>online</c>，否则 <c>idle</c>（与 <c>NodeSummaryDto</c> 聚合口径一致）。</param>
/// <param name="AlarmLevel">聚合后的报警级别。</param>
/// <param name="OccurredAt">服务端事件时间。</param>
public sealed record AdminNodeStateChangedNotification(
    Guid NodeId,
    string OnlineStatus,
    string StationStatus,
    string AlarmLevel,
    DateTimeOffset OccurredAt);