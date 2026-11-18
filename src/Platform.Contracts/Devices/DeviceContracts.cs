namespace Platform.Contracts.Devices;

/// <summary>
/// 设备域通用约定。状态一律用字符串词表，平台源码不出现旧客户端的数字枚举。
/// </summary>
public static class DeviceConventions
{
    /// <summary>客户端未上报心跳周期时的默认值（秒）。</summary>
    public const int DefaultHeartbeatSeconds = 10;

    /// <summary>允许的最小心跳周期（秒）。</summary>
    public const int MinHeartbeatSeconds = 1;

    /// <summary>允许的最大心跳周期（秒）。</summary>
    public const int MaxHeartbeatSeconds = 3600;

    /// <summary>设备名（自动名/自定义名）最大长度。</summary>
    public const int MaxNameLength = 128;

    /// <summary>节点展示名最大长度（自定义名与自动名可能都达到 128）。</summary>
    public const int MaxDisplayNameLength = 256;

    /// <summary>版本分支最大长度。</summary>
    public const int MaxBranchLength = 64;

    /// <summary>客户端版本号最大长度。</summary>
    public const int MaxFrameworkVersionLength = 64;

    /// <summary>IP 地址最大长度（含 IPv6/带端口场景留余量）。</summary>
    public const int MaxIpAddressLength = 64;

    /// <summary>报警摘要最大长度。</summary>
    public const int MaxAlarmMessageLength = 512;

    /// <summary>重启原因最大长度。</summary>
    public const int MaxRestartReasonLength = 256;
}

/// <summary>安装实例在线状态词表（<c>device_installations.online_status</c>）。</summary>
public static class DeviceOnlineStatuses
{
    /// <summary>离线（心跳超时或管理端未连接）。</summary>
    public const string Offline = "offline";

    /// <summary>在线。</summary>
    public const string Online = "online";

    /// <summary>是否为本词表内的值。</summary>
    public static bool IsKnown(string? value) =>
        string.Equals(value, Offline, StringComparison.Ordinal)
        || string.Equals(value, Online, StringComparison.Ordinal);
}

/// <summary>安装实例工位状态词表（<c>device_installations.station_status</c>），下发门禁只看这里。</summary>
public static class DeviceStationStatuses
{
    /// <summary>在线且待机（非运行），允许配置/档案下发。</summary>
    public const string Idle = "idle";

    /// <summary>在线且运行中。</summary>
    public const string Online = "online";

    /// <summary>是否为本词表内的值。</summary>
    public static bool IsKnown(string? value) =>
        string.Equals(value, Idle, StringComparison.Ordinal)
        || string.Equals(value, Online, StringComparison.Ordinal);
}

/// <summary>心跳里的综合状态词表：<c>offline</c> / <c>online</c> / <c>idle</c>。</summary>
/// <remarks>
/// 平台落库时拆成两列：<c>offline</c> 写 <see cref="DeviceOnlineStatuses.Offline"/>；
/// <c>online</c> 同时写在线 + 工位运行；<c>idle</c> 同时写在线 + 工位待机。
/// </remarks>
public static class DeviceHeartbeatStatuses
{
    /// <summary>离线。</summary>
    public const string Offline = "offline";

    /// <summary>在线且运行。</summary>
    public const string Online = "online";

    /// <summary>在线且待机。</summary>
    public const string Idle = "idle";

    /// <summary>是否为本词表内的值。</summary>
    public static bool IsKnown(string? value) =>
        string.Equals(value, Offline, StringComparison.Ordinal)
        || string.Equals(value, Online, StringComparison.Ordinal)
        || string.Equals(value, Idle, StringComparison.Ordinal);
}

/// <summary>安装实例报警级别词表。</summary>
public static class DeviceAlarmLevels
{
    /// <summary>无报警。</summary>
    public const string None = "none";

    /// <summary>提示。</summary>
    public const string Info = "info";

    /// <summary>警告。</summary>
    public const string Warning = "warning";

    /// <summary>错误。</summary>
    public const string Error = "error";

    /// <summary>是否为本词表内的值。</summary>
    public static bool IsKnown(string? value) =>
        string.Equals(value, None, StringComparison.Ordinal)
        || string.Equals(value, Info, StringComparison.Ordinal)
        || string.Equals(value, Warning, StringComparison.Ordinal)
        || string.Equals(value, Error, StringComparison.Ordinal);

    /// <summary>报警级别严重度：<c>error &gt; warning &gt; info &gt; none</c>。</summary>
    public static int Severity(string? value) => value switch
    {
        Error => 3,
        Warning => 2,
        Info => 1,
        _ => 0,
    };
}

/// <summary>配置下发内容种类词表。</summary>
public static class DeviceConfigKinds
{
    /// <summary>配置。</summary>
    public const string Config = "config";

    /// <summary>档案/工艺参数。</summary>
    public const string Profile = "profile";

    /// <summary>是否为本词表内的值。</summary>
    public static bool IsKnown(string? value) =>
        string.Equals(value, Config, StringComparison.Ordinal)
        || string.Equals(value, Profile, StringComparison.Ordinal);
}

/// <summary>展示名解析：管理端自定义名优先，其次自动/上报名，最后回退标识。</summary>
public static class DeviceNaming
{
    /// <summary>按节点（硬件）口径解析展示名：管理端改名 &gt; 机器名 &gt; 机器码。</summary>
    public static string ResolveDisplayName(string? autoName, string? customName, string machineCode)
    {
        var custom = customName?.Trim();
        if (!string.IsNullOrEmpty(custom))
        {
            return custom;
        }

        var auto = autoName?.Trim();
        if (!string.IsNullOrEmpty(auto))
        {
            return auto;
        }

        return machineCode;
    }

    /// <summary>
    /// 按安装实例（软件）口径解析展示名：管理端改名 &gt; 应用内录入名。
    /// 两者都没有时返回空，由管理端回退到安装实例短键展示（安装实例没有硬件级天然名）。
    /// </summary>
    public static string? ResolveInstallationDisplayName(string? clientName, string? customName)
    {
        var custom = customName?.Trim();
        if (!string.IsNullOrEmpty(custom))
        {
            return custom;
        }

        var client = clientName?.Trim();
        return string.IsNullOrEmpty(client) ? null : client;
    }
}

/// <summary>设备身份要素。平台据此计算全局机器码，不信任客户端上传的任意算好值。</summary>
/// <param name="InstallGuid">客户端首次安装生成并持久化的 Guid；同一应用重装会变化。</param>
/// <param name="SmbiosUuid">SMBIOS UUID（主板级主标识，老客户端口径优先）。</param>
/// <param name="SystemDiskSerial">系统盘序列号（SMBIOS 缺失时的次选主标识）。</param>
/// <param name="MacAddress">主网卡 MAC（辅助信息；不参与机器码计算）。</param>
/// <param name="FrameworkVersion">客户端框架版本（只记录，不参与机器码计算）。</param>
public sealed record DeviceIdentity(
    Guid InstallGuid,
    string? SmbiosUuid,
    string? SystemDiskSerial,
    string? MacAddress = null,
    string? FrameworkVersion = null);

/// <summary>安装实例注册请求（连接后首帧）。</summary>
/// <param name="Identity">身份要素。</param>
/// <param name="DisplayName">节点自动名：客户端机器名（硬件侧标识，与安装实例名无关）。</param>
/// <param name="IpAddress">客户端上报的本机 IP。</param>
/// <param name="Branch">版本分支；为空表示不改既有值。</param>
/// <param name="HeartbeatSeconds">客户端心跳周期，决定离线阈值。</param>
/// <param name="CustomName">应用内录入的安装实例名称；只写安装实例，节点自定义名仅管理端可改。</param>
public sealed record DeviceRegistrationRequest(
    DeviceIdentity Identity,
    string DisplayName,
    string? IpAddress = null,
    string? Branch = null,
    int? HeartbeatSeconds = null,
    string? CustomName = null);

/// <summary>安装实例注册结果。</summary>
/// <param name="InstallationId">安装实例主键（同应用同安装重连保持稳定）。</param>
/// <param name="NodeId">物理节点主键（同一机器上的多个应用共享）。</param>
/// <param name="MachineCode">全局机器码（授权席位锚点）。</param>
/// <param name="NodeShortId">节点 id 前 16 位，便于日志与人工核对。</param>
/// <param name="InstallationShortId">安装实例 id 前 16 位。</param>
/// <param name="ConnectedAt">本次连接登记时间（服务端时间）。</param>
public sealed record DeviceRegistrationResponse(
    Guid InstallationId,
    Guid NodeId,
    string MachineCode,
    string NodeShortId,
    string InstallationShortId,
    DateTimeOffset ConnectedAt);

/// <summary>周期心跳与状态上报。</summary>
/// <param name="Status">综合状态，取值见 <see cref="DeviceHeartbeatStatuses"/>。</param>
/// <param name="AlarmLevel">报警级别，取值见 <see cref="DeviceAlarmLevels"/>。</param>
/// <param name="AlarmMessage">报警摘要。</param>
/// <param name="Timestamp">客户端时间；只作诊断参考，平台以服务端时间为准。</param>
public sealed record DeviceHeartbeatRequest(
    string Status,
    string AlarmLevel,
    string? AlarmMessage = null,
    DateTimeOffset Timestamp = default);

/// <summary>工位状态上报。</summary>
/// <param name="State">取值见 <see cref="DeviceStationStatuses"/>；不接受 <c>offline</c>。</param>
public sealed record DeviceStationStateRequest(string State);

/// <summary>配置下发命令（服务端 → 安装实例）。大载荷只走 <paramref name="DownloadUrl"/>。</summary>
public sealed record DeviceConfigPush(
    Guid BackupId,
    string Kind,
    string? PayloadJson = null,
    string? DownloadUrl = null,
    string? Sha256 = null,
    bool RestartAfterApply = false,
    string? Branch = null);

/// <summary>配置下发回执。</summary>
public sealed record DeviceConfigPushAck(
    Guid BackupId,
    bool Succeeded,
    string? Message = null);

/// <summary>重启命令（服务端 → 安装实例/节点）。</summary>
public sealed record DeviceRestart(string? Reason = null);

/// <summary>同一安装实例新连接顶替旧连接时通知旧连接。</summary>
public sealed record InstallationSessionReplaced(string Reason);

/// <summary>物理节点概要（平台级列表）。</summary>
public sealed record NodeSummaryDto(
    Guid Id,
    string ShortId,
    string MachineCode,
    string DisplayName,
    string? AutoName,
    string? CustomName,
    string? IpAddress,
    string OnlineStatus,
    string StationStatus,
    string AlarmLevel,
    string? AlarmMessage,
    int InstallationCount,
    int OnlineInstallationCount,
    IReadOnlyList<string> AppKeys,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastHeartbeatAt,
    bool IsEnabled);

/// <summary>物理节点详情：内嵌该节点全部安装实例（跨应用）。</summary>
public sealed record NodeDetailDto(
    Guid Id,
    string ShortId,
    string MachineCode,
    string DisplayName,
    string? AutoName,
    string? CustomName,
    string? IpAddress,
    string OnlineStatus,
    string StationStatus,
    string AlarmLevel,
    string? AlarmMessage,
    bool IsEnabled,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<InstallationSummaryDto> Installations);

/// <summary>安装实例概要（应用级列表）。</summary>
public sealed record InstallationSummaryDto(
    Guid Id,
    Guid NodeId,
    string NodeDisplayName,
    string? DisplayName,
    string? ClientName,
    string? CustomName,
    string AppKey,
    string MachineCode,
    string ShortId,
    Guid InstallGuid,
    string? Branch,
    string? FrameworkVersion,
    string OnlineStatus,
    string StationStatus,
    string AlarmLevel,
    string? AlarmMessage,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastHeartbeatAt,
    int HeartbeatSeconds,
    bool IsEnabled,
    bool NodeEnabled);

/// <summary>安装实例详情。</summary>
public sealed record InstallationDetailDto(
    Guid Id,
    Guid NodeId,
    string NodeDisplayName,
    string? DisplayName,
    string? ClientName,
    string? CustomName,
    string AppKey,
    string MachineCode,
    string ShortId,
    Guid InstallGuid,
    string? Branch,
    string? FrameworkVersion,
    string OnlineStatus,
    string StationStatus,
    string AlarmLevel,
    string? AlarmMessage,
    DateTimeOffset? FirstConnectedAt,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastHeartbeatAt,
    int HeartbeatSeconds,
    bool IsEnabled,
    bool NodeEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>管理端更新节点。字段为空表示不修改；自定义名传空串表示清除（清除后回退机器名）。</summary>
public sealed record UpdateNodeRequest(
    string? CustomName = null,
    bool? IsEnabled = null);

/// <summary>管理端更新安装实例。字段为空表示不修改；自定义名传空串表示清除（清除后回退应用内录入名）。</summary>
public sealed record UpdateInstallationRequest(
    string? CustomName = null,
    bool? IsEnabled = null);

/// <summary>管理端重启节点（该节点全部安装实例收到）。</summary>
public sealed record NodeRestartRequest(string? Reason = null);

/// <summary>管理端重启单个安装实例。</summary>
public sealed record InstallationRestartRequest(string? Reason = null);

/// <summary>节点重启受理结果：命令已送达该节点至少一个在线安装实例。</summary>
public sealed record NodeRestartAccepted(Guid NodeId, int InstallationCount);

/// <summary>安装实例重启受理结果：命令已送达在线连接。</summary>
public sealed record InstallationRestartAccepted(Guid InstallationId);

/// <summary>设备 Hub 路径与分组（服务端与任意设备客户端共用）。</summary>
public static class DeviceHubPaths
{
    /// <summary>通用设备 Hub 根路径：稳定的连接入口，不随节点/安装实例语义改名。</summary>
    public const string Root = "/hubs/devices";

    /// <summary>安装实例分组：定向下发只发到这里。</summary>
    public static string InstallationGroup(Guid installationId) => $"installation:{installationId:D}";

    /// <summary>节点分组：节点级重启等命令发给该节点全部安装实例。</summary>
    public static string NodeGroup(Guid nodeId) => $"node:{nodeId:D}";
}

/// <summary>设备 Hub 客户端 → 服务端方法名。</summary>
public static class DeviceHubMethods
{
    /// <summary>注册安装实例（旧的 <c>RegisterDevice</c> 已删除）。</summary>
    public const string RegisterInstallation = "RegisterInstallation";

    /// <summary>周期心跳。</summary>
    public const string Heartbeat = "Heartbeat";

    /// <summary>工位状态上报。</summary>
    public const string ReportStationState = "ReportStationState";

    /// <summary>配置下发回执。</summary>
    public const string AcknowledgeConfigPush = "AcknowledgeConfigPush";
}

/// <summary>设备 Hub 服务端 → 客户端事件名。</summary>
public static class DeviceHubEvents
{
    /// <summary>配置下发。</summary>
    public const string ConfigPush = "ConfigPush";

    /// <summary>重启。</summary>
    public const string Restart = "Restart";

    /// <summary>连接被顶替。</summary>
    public const string SessionReplaced = "SessionReplaced";

    /// <summary>安装实例状态变更提示（可选订阅）。</summary>
    public const string StateChanged = "StateChanged";
}
