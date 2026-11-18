using Platform.Kernel.Abstractions;

namespace Platform.Modules.Devices.Domain;

/// <summary>
/// 物理节点（一台机器）。机器码全局唯一，授权席位以它为准；同一节点可承载多个应用安装实例。
/// 节点自身不落在线/工位状态，管理端按安装实例实时聚合，避免最后一个上报者覆盖真实状态。
/// </summary>
public sealed class DeviceNode : IAuditableEntity
{
    /// <summary>主键（服务端分配）。</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>机器码（16 字符 Crockford Base32），全局唯一。</summary>
    public required string MachineCode { get; set; }

    /// <summary>自动名（客户端上报的机器名），每次注册刷新。</summary>
    public string? AutoName { get; set; }

    /// <summary>自定义名（管理端改名后客户端不再覆盖）。</summary>
    public string? CustomName { get; set; }

    /// <summary>自定义名来源：<c>admin</c>/<c>client</c>；为空表示没有自定义名。</summary>
    public string? CustomNameSource { get; set; }

    /// <summary>展示名：自定义名 &gt; 自动名 &gt; 机器码。</summary>
    public required string DisplayName { get; set; }

    /// <summary>任一安装实例最近上报的 IP。</summary>
    public string? IpAddress { get; set; }

    /// <summary>节点总开关；停用后该机器所有应用拒绝注册/心跳。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>首次被任一安装实例注册的时间。</summary>
    public DateTimeOffset? FirstSeenAt { get; set; }

    /// <summary>最近一次注册或心跳时间。</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>该节点上的安装实例。</summary>
    public List<DeviceInstallation> Installations { get; set; } = [];
}

/// <summary>节点自定义名来源：管理端改名后客户端注册不得覆盖。</summary>
internal static class DeviceCustomNameSources
{
    /// <summary>管理端设置（包含管理端清空，客户端不再回填）。</summary>
    public const string Admin = "admin";

    /// <summary>客户端连接设置上报。</summary>
    public const string Client = "client";
}
