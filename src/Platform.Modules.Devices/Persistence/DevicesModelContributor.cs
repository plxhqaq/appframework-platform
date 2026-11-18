using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Modules.Devices.Domain;

namespace Platform.Modules.Devices.Persistence;

/// <summary>向共享 <c>PlatformDbContext</c> 贡献节点与安装实例表映射。</summary>
public sealed class DevicesModelContributor : IPlatformModelContributor
{
    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<DeviceNode>(builder =>
        {
            builder.ToTable("device_nodes");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.MachineCode).HasColumnName("machine_code").HasMaxLength(32).IsRequired();
            builder.Property(static x => x.AutoName).HasColumnName("auto_name").HasMaxLength(128);
            builder.Property(static x => x.CustomName).HasColumnName("custom_name").HasMaxLength(128);
            builder.Property(static x => x.DisplayName).HasColumnName("display_name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            builder.Property(static x => x.IsEnabled).HasColumnName("is_enabled");
            builder.Property(static x => x.FirstSeenAt).HasColumnName("first_seen_at");
            builder.Property(static x => x.LastSeenAt).HasColumnName("last_seen_at");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => x.MachineCode)
                .IsUnique()
                .HasDatabaseName("ux_device_nodes_machine_code");
            builder.HasIndex(static x => x.LastSeenAt)
                .IsDescending()
                .HasDatabaseName("ix_device_nodes_last_seen");
        });

        modelBuilder.Entity<DeviceInstallation>(builder =>
        {
            builder.ToTable("device_installations");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.NodeId).HasColumnName("node_id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.InstallGuid).HasColumnName("install_guid");
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

            builder.HasIndex(static x => new { x.AppKey, x.InstallGuid })
                .IsUnique()
                .HasDatabaseName("ux_device_installations_app_install");
            builder.HasIndex(static x => x.NodeId)
                .HasDatabaseName("ix_device_installations_node");
            builder.HasIndex(static x => new { x.AppKey, x.LastHeartbeatAt })
                .IsDescending(false, true)
                .HasDatabaseName("ix_device_installations_app_heartbeat");
            builder.HasIndex(static x => new { x.AppKey, x.OnlineStatus })
                .HasDatabaseName("ix_device_installations_app_online");

            builder.HasOne(static x => x.Node)
                .WithMany(static x => x.Installations)
                .HasForeignKey(static x => x.NodeId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
