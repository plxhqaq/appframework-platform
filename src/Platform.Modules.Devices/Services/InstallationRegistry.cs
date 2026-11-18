using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Contracts;
using Platform.Contracts.Devices;
using Platform.Infrastructure.Persistence;
using Platform.Kernel.Abstractions;
using Platform.Kernel.Errors;
using Platform.Modules.Devices.Domain;
using Platform.Modules.Devices.Options;

namespace Platform.Modules.Devices.Services;

/// <summary>安装实例注册：先按机器码 upsert 全局节点，再按 <c>(appKey, installGuid)</c> upsert 安装实例。</summary>
public interface IInstallationRegistry
{
    /// <summary>注册或复用节点与安装实例；任一开关停用都拒绝注册。</summary>
    Task<DeviceRegistrationResponse> RegisterAsync(
        string appKey,
        DeviceRegistrationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>安装实例注册实现。</summary>
public sealed class InstallationRegistry : IInstallationRegistry
{
    private static readonly Action<ILogger, string, string, bool, Exception?> RegisteredLog =
        LoggerMessage.Define<string, string, bool>(
            LogLevel.Information,
            new EventId(1, "InstallationRegistered"),
            "安装实例注册 [{ShortId}]（应用 {AppKey}，{NewOrExisting}）。");

    private readonly PlatformDbContext _db;
    private readonly IClock _clock;
    private readonly DevicesOptions _options;
    private readonly ILogger<InstallationRegistry> _logger;

    /// <summary>构造。</summary>
    public InstallationRegistry(
        PlatformDbContext db,
        IClock clock,
        IOptions<DevicesOptions> options,
        ILogger<InstallationRegistry> logger)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DeviceRegistrationResponse> RegisterAsync(
        string appKey,
        DeviceRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentNullException.ThrowIfNull(request);

        var identity = request.Identity
                       ?? throw new BusinessException(
                           ApiErrorCode.DevicesIdentityInvalid,
                           "设备身份缺失。");
        if (identity.InstallGuid == Guid.Empty)
        {
            throw new BusinessException(
                ApiErrorCode.DevicesIdentityInvalid,
                "设备身份缺少安装 Guid，请检查客户端身份文件是否可写。");
        }

        var machineName = DeviceValidation.RequireName(request.DisplayName, "机器名");
        var clientName = DeviceValidation.Optional(
            request.CustomName, DeviceConventions.MaxNameLength, "安装实例名称");
        var ipAddress = DeviceValidation.Optional(
            request.IpAddress, DeviceConventions.MaxIpAddressLength, "IP 地址");
        var branch = DeviceValidation.Optional(
            request.Branch, DeviceConventions.MaxBranchLength, "版本分支");
        var frameworkVersion = DeviceValidation.Optional(
            identity.FrameworkVersion, DeviceConventions.MaxFrameworkVersionLength, "客户端版本");
        var heartbeatSeconds = request.HeartbeatSeconds is { } reported
            ? DeviceValidation.RequireHeartbeatSeconds(reported)
            : (int?)null;

        string machineCode;
        try
        {
            machineCode = DeviceIdentityHasher.MachineCode(identity);
        }
        catch (InvalidOperationException exception)
        {
            throw new BusinessException(ApiErrorCode.DevicesPrimaryIdentifierMissing, exception.Message);
        }

        var now = _clock.UtcNow;
        DeviceNode node;
        DeviceInstallation installation;
        for (int attempt = 0; ; attempt++)
        {
            node = await _db.Set<DeviceNode>()
                .FirstOrDefaultAsync(x => x.MachineCode == machineCode, cancellationToken)
                ?? CreateNode(machineCode, machineName, ipAddress, now);
            EnsureNodeEnabled(node);
            ApplyNodeConnection(node, machineName, ipAddress, now);

            installation = await _db.Set<DeviceInstallation>()
                .FirstOrDefaultAsync(
                    x => x.AppKey == appKey && x.InstallGuid == identity.InstallGuid,
                    cancellationToken)
                ?? CreateInstallation(appKey, identity.InstallGuid, node, clientName, now);
            EnsureInstallationEnabled(installation);
            ApplyInstallationConnection(
                installation, clientName, branch, frameworkVersion, heartbeatSeconds, now);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // 并发首次注册：唯一索引兜底，清掉跟踪后按两条唯一键重新走 upsert（最多重试两次）。
                _db.ChangeTracker.Clear();
            }
        }

        RegisteredLog(_logger, DeviceIdentityHasher.ShortId(installation.Id), appKey, false, null);
        return new DeviceRegistrationResponse(
            installation.Id,
            node.Id,
            machineCode,
            DeviceIdentityHasher.ShortId(node.Id),
            DeviceIdentityHasher.ShortId(installation.Id),
            now);
    }

    private DeviceNode CreateNode(
        string machineCode,
        string machineName,
        string? ipAddress,
        DateTimeOffset now)
    {
        var node = new DeviceNode
        {
            MachineCode = machineCode,
            AutoName = machineName,
            DisplayName = DeviceNaming.ResolveDisplayName(machineName, null, machineCode),
            IpAddress = ipAddress,
            FirstSeenAt = now,
            LastSeenAt = now,
        };
        _db.Set<DeviceNode>().Add(node);
        return node;
    }

    private DeviceInstallation CreateInstallation(
        string appKey,
        Guid installGuid,
        DeviceNode node,
        string? clientName,
        DateTimeOffset now)
    {
        var installation = new DeviceInstallation
        {
            NodeId = node.Id,
            Node = node,
            AppKey = appKey,
            InstallGuid = installGuid,
            ClientName = clientName,
            DisplayName = DeviceNaming.ResolveInstallationDisplayName(clientName, null),
            OnlineStatus = DeviceOnlineStatuses.Online,
            StationStatus = DeviceStationStatuses.Idle,
            AlarmLevel = DeviceAlarmLevels.None,
            FirstConnectedAt = now,
            LastConnectedAt = now,
            LastHeartbeatAt = now,
            HeartbeatSeconds = _options.DefaultHeartbeatSeconds,
        };
        _db.Set<DeviceInstallation>().Add(installation);
        return installation;
    }

    private static void EnsureNodeEnabled(DeviceNode node)
    {
        if (!node.IsEnabled)
        {
            throw new BusinessException(
                ApiErrorCode.DevicesNodeDisabled,
                "该物理节点已被管理端停用，请联系管理员启用后再连接。");
        }
    }

    private static void EnsureInstallationEnabled(DeviceInstallation installation)
    {
        if (!installation.IsEnabled)
        {
            throw new BusinessException(
                ApiErrorCode.DevicesInstallationDisabled,
                "该应用安装实例已被管理端停用，请联系管理员启用后再连接。");
        }
    }

    private static void ApplyNodeConnection(
        DeviceNode node,
        string machineName,
        string? ipAddress,
        DateTimeOffset now)
    {
        // 节点对应硬件：客户端只能刷新机器名；节点自定义名只由管理端写入。
        node.AutoName = machineName;
        node.DisplayName = DeviceNaming.ResolveDisplayName(node.AutoName, node.CustomName, node.MachineCode);
        node.FirstSeenAt ??= now;
        node.LastSeenAt = now;
        if (ipAddress is not null)
        {
            node.IpAddress = ipAddress;
        }
    }

    private static void ApplyInstallationConnection(
        DeviceInstallation installation,
        string? clientName,
        string? branch,
        string? frameworkVersion,
        int? heartbeatSeconds,
        DateTimeOffset now)
    {
        installation.OnlineStatus = DeviceOnlineStatuses.Online;
        installation.FirstConnectedAt ??= now;
        installation.LastConnectedAt = now;
        installation.LastHeartbeatAt = now;

        // 安装实例对应软件：应用内录入的名每次注册刷新；管理端改名走 CustomName，优先级更高。
        installation.ClientName = clientName;
        installation.DisplayName = DeviceNaming.ResolveInstallationDisplayName(
            installation.ClientName, installation.CustomName);

        if (branch is not null)
        {
            installation.Branch = branch;
        }

        if (frameworkVersion is not null)
        {
            installation.FrameworkVersion = frameworkVersion;
        }

        if (heartbeatSeconds is { } seconds)
        {
            installation.HeartbeatSeconds = seconds;
        }
    }
}
