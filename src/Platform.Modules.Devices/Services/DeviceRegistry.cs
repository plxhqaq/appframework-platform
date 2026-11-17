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

/// <summary>设备注册：按 <c>(appKey, machineCode)</c> 去重，同机多个客户端安装复用同一条设备记录。</summary>
public interface IDeviceRegistry
{
    /// <summary>注册或复用设备；停用设备拒绝注册。</summary>
    Task<DeviceRegistrationResponse> RegisterAsync(
        string appKey,
        DeviceRegistrationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>设备注册实现。</summary>
public sealed class DeviceRegistry : IDeviceRegistry
{
    private static readonly Action<ILogger, string, string, bool, Exception?> RegisteredLog =
        LoggerMessage.Define<string, string, bool>(
            LogLevel.Information,
            new EventId(1, "DeviceRegistered"),
            "设备注册 [{ShortId}]（应用 {AppKey}，{NewOrExisting}）。");

    private readonly PlatformDbContext _db;
    private readonly IClock _clock;
    private readonly DevicesOptions _options;
    private readonly ILogger<DeviceRegistry> _logger;

    /// <summary>构造。</summary>
    public DeviceRegistry(
        PlatformDbContext db,
        IClock clock,
        IOptions<DevicesOptions> options,
        ILogger<DeviceRegistry> logger)
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

        var autoName = DeviceValidation.RequireName(request.DisplayName, "设备名");
        var customName = DeviceValidation.Optional(
            request.CustomName, DeviceConventions.MaxNameLength, "自定义设备名");
        var ipAddress = DeviceValidation.Optional(
            request.IpAddress, DeviceConventions.MaxIpAddressLength, "IP 地址");
        var branch = DeviceValidation.Optional(
            request.Branch, DeviceConventions.MaxBranchLength, "版本分支");
        var frameworkVersion = DeviceValidation.Optional(
            identity.FrameworkVersion, DeviceConventions.MaxFrameworkVersionLength, "客户端版本");
        var heartbeatSeconds = request.HeartbeatSeconds is { } reported
            ? DeviceValidation.RequireHeartbeatSeconds(reported)
            : (int?)null;

        var (_, machineCode) = DeviceIdentityHasher.Compute(identity);
        // 设备键与机器码同值：同一台机器的不同客户端安装落到同一条记录，唯一索引直接兜底并发。
        string deviceKey = machineCode;
        var now = _clock.UtcNow;

        Device? device = await _db.Set<Device>()
            .FirstOrDefaultAsync(
                x => x.AppKey == appKey
                     && x.MachineCode == machineCode
                     && x.MergedIntoDeviceId == null,
                cancellationToken);
        bool isNew = device is null;

        if (device is null)
        {
            device = new Device
            {
                AppKey = appKey,
                DeviceKey = deviceKey,
                MachineCode = machineCode,
                InstallGuid = identity.InstallGuid,
                AutoName = autoName,
                CustomName = customName,
                DisplayName = DeviceNaming.Compose(autoName, customName),
                IpAddress = ipAddress,
                Branch = branch,
                FrameworkVersion = frameworkVersion,
                OnlineStatus = DeviceOnlineStatuses.Online,
                StationStatus = DeviceStationStatuses.Idle,
                AlarmLevel = DeviceAlarmLevels.None,
                FirstConnectedAt = now,
                LastConnectedAt = now,
                LastHeartbeatAt = now,
                HeartbeatSeconds = heartbeatSeconds ?? _options.DefaultHeartbeatSeconds,
            };
            _db.Set<Device>().Add(device);
        }
        else
        {
            EnsureEnabled(device);
            ApplyConnection(
                device, autoName, customName, ipAddress, branch, frameworkVersion,
                machineCode, identity.InstallGuid, heartbeatSeconds, now);
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 并发首次注册：唯一索引兜底，清除后按机器码走更新路径。
            _db.ChangeTracker.Clear();
            device = await _db.Set<Device>()
                .FirstAsync(
                    x => x.AppKey == appKey
                         && x.MachineCode == machineCode
                         && x.MergedIntoDeviceId == null,
                    cancellationToken);
            EnsureEnabled(device);
            ApplyConnection(
                device, autoName, customName, ipAddress, branch, frameworkVersion,
                machineCode, identity.InstallGuid, heartbeatSeconds, now);
            await _db.SaveChangesAsync(cancellationToken);
            isNew = false;
        }

        string shortId = DeviceIdentityHasher.ShortId(device.DeviceKey);
        RegisteredLog(_logger, shortId, appKey, isNew, null);
        return new DeviceRegistrationResponse(device.Id, shortId, now);
    }

    private static void EnsureEnabled(Device device)
    {
        if (!device.IsEnabled)
        {
            throw new BusinessException(
                ApiErrorCode.DevicesDisabled,
                "设备已被管理端停用，请联系管理员启用后再连接。");
        }
    }

    private static void ApplyConnection(
        Device device,
        string autoName,
        string? customName,
        string? ipAddress,
        string? branch,
        string? frameworkVersion,
        string machineCode,
        Guid installGuid,
        int? heartbeatSeconds,
        DateTimeOffset now)
    {
        device.AutoName = autoName;
        if (!string.IsNullOrWhiteSpace(customName))
        {
            device.CustomName = customName;
        }

        device.DisplayName = DeviceNaming.Compose(device.AutoName, device.CustomName);
        device.DeviceKey = machineCode;
        device.MachineCode = machineCode;
        device.InstallGuid = installGuid;
        device.OnlineStatus = DeviceOnlineStatuses.Online;
        device.LastConnectedAt = now;
        device.LastHeartbeatAt = now;

        if (ipAddress is not null)
        {
            device.IpAddress = ipAddress;
        }

        if (branch is not null)
        {
            device.Branch = branch;
        }

        if (frameworkVersion is not null)
        {
            device.FrameworkVersion = frameworkVersion;
        }

        if (heartbeatSeconds is { } seconds)
        {
            device.HeartbeatSeconds = seconds;
        }
    }
}
