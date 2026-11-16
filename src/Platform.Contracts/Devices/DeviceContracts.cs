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

    /// <summary>设备名（自动名/自定义名/展示名）最大长度。</summary>
    public const int MaxNameLength = 128;

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

/// <summary>设备在线状态词表（<c>devices.online_status</c>）。</summary>
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

/// <summary>设备工位状态词表（<c>devices.station_status</c>），下发门禁只看这里。</summary>
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

/// <summary>设备报警级别词表。</summary>
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

/// <summary>设备名拼接：自定义名在前、机器名在后；已带前缀时不重复拼（幂等）。</summary>
public static class DeviceNaming
{
    /// <summary>拼出展示名。</summary>
    public static string Compose(string? autoName, string? customName)
    {
        var auto = autoName?.Trim() ?? string.Empty;
        var custom = customName?.Trim();
        if (string.IsNullOrEmpty(custom))
        {
            return auto;
        }

        if (auto.Length == 0)
        {
            return custom;
        }

        return auto.StartsWith(custom + "-", StringComparison.OrdinalIgnoreCase)
            ? auto
            : $"{custom}-{auto}";
    }
}

/// <summary>设备身份要素。平台据此计算稳定 <c>DeviceKey</c> 与机器码，不信任客户端上传的任意算好值。</summary>
/// <param name="MacAddress">主网卡 MAC（辅助信息；当前不参与平台身份计算）。</param>
/// <param name="InstallGuid">客户端首次安装生成并持久化的 Guid；为空表示身份不可用。</param>
/// <param name="FrameworkVersion">客户端框架版本（只记录，不参与身份计算）。</param>
/// <param name="PrimaryIdentifier">SMBIOS UUID 或系统盘序列号；为空时回退 MAC。</param>
public sealed record DeviceIdentity(
    string? MacAddress,
    Guid InstallGuid,
    string? FrameworkVersion = null,
    string? PrimaryIdentifier = null);

/// <summary>设备注册请求（连接后首帧）。</summary>
/// <param name="Identity">身份要素。</param>
/// <param name="DisplayName">自动设备名（当前为客户端机器名）。</param>
/// <param name="IpAddress">客户端上报的本机 IP。</param>
/// <param name="Branch">版本分支；为空表示不改既有值。</param>
/// <param name="HeartbeatSeconds">客户端心跳周期，决定离线阈值。</param>
/// <param name="CustomName">站点自定义名；非空时置于自动名前。</param>
public sealed record DeviceRegistrationRequest(
    DeviceIdentity Identity,
    string DisplayName,
    string? IpAddress = null,
    string? Branch = null,
    int? HeartbeatSeconds = null,
    string? CustomName = null);

/// <summary>设备注册结果。</summary>
/// <param name="DeviceId">平台分配的设备主键（重连保持稳定）。</param>
/// <param name="DeviceShortId">稳定键前 16 位，便于日志与人工核对。</param>
/// <param name="ConnectedAt">本次连接登记时间（服务端时间）。</param>
public sealed record DeviceRegistrationResponse(
    Guid DeviceId,
    string DeviceShortId,
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

/// <summary>配置下发命令（服务端 → 设备）。大载荷只走 <paramref name="DownloadUrl"/>。</summary>
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

/// <summary>重启命令（服务端 → 设备）。</summary>
public sealed record DeviceRestart(string? Reason = null);

/// <summary>同一设备新连接顶替旧连接时通知旧连接。</summary>
public sealed record DeviceSessionReplaced(string Reason);

/// <summary>设备概要（管理端列表）。</summary>
public sealed record DeviceSummaryDto(
    Guid Id,
    string ShortId,
    string MachineCode,
    string DisplayName,
    string? AutoName,
    string? CustomName,
    string? IpAddress,
    string? Branch,
    string? FrameworkVersion,
    string OnlineStatus,
    string StationStatus,
    string AlarmLevel,
    string? AlarmMessage,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastHeartbeatAt,
    int HeartbeatSeconds,
    bool IsEnabled);

/// <summary>设备详情（管理端详情）。</summary>
public sealed record DeviceDetailDto(
    Guid Id,
    string DeviceKey,
    string ShortId,
    string MachineCode,
    Guid? InstallGuid,
    string DisplayName,
    string? AutoName,
    string? CustomName,
    string? IpAddress,
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
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>管理端更新设备。字段为空表示不修改；自定义名传空串表示清除。</summary>
public sealed record UpdateDeviceRequest(
    string? CustomName = null,
    bool? IsEnabled = null);

/// <summary>管理端重启设备。</summary>
/// <param name="Reason">重启原因（可选，透传给设备显示）。</param>
public sealed record DeviceRestartRequest(string? Reason = null);

/// <summary>重启命令受理结果：命令已送达在线设备。</summary>
public sealed record DeviceRestartAccepted(Guid DeviceId);

/// <summary>设备 Hub 路径与分组（服务端与任意设备客户端共用）。</summary>
public static class DeviceHubPaths
{
    /// <summary>通用设备 Hub 根路径。</summary>
    public const string Root = "/hubs/devices";

    /// <summary>单设备分组：定向下发只发到这里，不广播到应用分组。</summary>
    public static string DeviceGroup(Guid deviceId) => $"device:{deviceId:D}";
}

/// <summary>设备 Hub 客户端 → 服务端方法名。</summary>
public static class DeviceHubMethods
{
    /// <summary>注册设备。</summary>
    public const string RegisterDevice = "RegisterDevice";

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

    /// <summary>设备状态变更提示（可选订阅）。</summary>
    public const string StateChanged = "StateChanged";
}
