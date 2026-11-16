using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Modules.Devices.Domain;

namespace Platform.Modules.Devices.Persistence;

/// <summary>向共享 <c>PlatformDbContext</c> 贡献设备表映射。</summary>
public sealed class DevicesModelContributor : IPlatformModelContributor
{
    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Device>(builder =>
        {
            builder.ToTable("devices");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.DeviceKey).HasColumnName("device_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.MachineCode).HasColumnName("machine_code").HasMaxLength(32).IsRequired();
            builder.Property(static x => x.InstallGuid).HasColumnName("install_guid");
            builder.Property(static x => x.AutoName).HasColumnName("auto_name").HasMaxLength(128);
            builder.Property(static x => x.CustomName).HasColumnName("custom_name").HasMaxLength(128);
            builder.Property(static x => x.DisplayName).HasColumnName("display_name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            builder.Property(static x => x.Branch).HasColumnName("branch").HasMaxLength(64);
            builder.Property(static x => x.FrameworkVersion).HasColumnName("framework_version").HasMaxLength(64);
            builder.Property(static x => x.OnlineStatus).HasColumnName("online_status").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.StationStatus).HasColumnName("station_status").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.AlarmLevel).HasColumnName("alarm_level").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.AlarmMessage).HasColumnName("alarm_message").HasMaxLength(512);
            builder.Property(static x => x.FirstConnectedAt).HasColumnName("first_connected_at");
            builder.Property(static x => x.LastConnectedAt).HasColumnName("last_connected_at");
            builder.Property(static x => x.LastHeartbeatAt).HasColumnName("last_heartbeat_at");
            builder.Property(static x => x.HeartbeatSeconds).HasColumnName("heartbeat_seconds");
            builder.Property(static x => x.IsEnabled).HasColumnName("is_enabled");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.AppKey, x.DeviceKey })
                .IsUnique()
                .HasDatabaseName("ux_devices_app_device_key");
            builder.HasIndex(static x => new { x.AppKey, x.LastHeartbeatAt })
                .HasDatabaseName("ix_devices_app_last_heartbeat");
            builder.HasIndex(static x => new { x.AppKey, x.MachineCode })
                .HasDatabaseName("ix_devices_app_machine_code");
        });
    }
}
