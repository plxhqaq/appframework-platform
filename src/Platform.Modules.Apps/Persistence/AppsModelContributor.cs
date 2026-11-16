using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Modules.Apps.Domain;

namespace Platform.Modules.Apps.Persistence;

/// <summary>向共享 <c>PlatformDbContext</c> 贡献本模块的表映射。</summary>
public sealed class AppsModelContributor : IPlatformModelContributor
{
    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<App>(builder =>
        {
            builder.ToTable("apps");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.Key).HasColumnName("key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.Description).HasColumnName("description").HasMaxLength(1024);
            builder.Property(static x => x.IsEnabled).HasColumnName("is_enabled");
            builder.Property(static x => x.PermissionsLocked).HasColumnName("permissions_locked");
            builder.Property(static x => x.SettingsJson).HasColumnName("settings_json").HasColumnType("jsonb");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => x.Key).IsUnique().HasDatabaseName("ux_apps_key");
        });

        modelBuilder.Entity<AppApiKey>(builder =>
        {
            builder.ToTable("app_api_keys");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppId).HasColumnName("app_id");
            builder.Property(static x => x.Prefix).HasColumnName("prefix").HasMaxLength(32).IsRequired();
            builder.Property(static x => x.SecretHash).HasColumnName("secret_hash").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.Scopes).HasColumnName("scopes").HasColumnType("text[]");
            builder.Property(static x => x.ExpiresAt).HasColumnName("expires_at");
            builder.Property(static x => x.RevokedAt).HasColumnName("revoked_at");
            builder.Property(static x => x.LastUsedAt).HasColumnName("last_used_at");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => x.Prefix).IsUnique().HasDatabaseName("ux_app_api_keys_prefix");
            builder.HasIndex(static x => x.AppId).HasDatabaseName("ix_app_api_keys_app");

            builder.HasOne(static x => x.App)
                .WithMany(static x => x.ApiKeys)
                .HasForeignKey(static x => x.AppId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppPermissionDefinition>(builder =>
        {
            builder.ToTable("app_permissions");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppId).HasColumnName("app_id");
            builder.Property(static x => x.Code).HasColumnName("code").HasMaxLength(128).IsRequired();
            builder.Property(static x => x.DisplayName).HasColumnName("display_name").HasMaxLength(128).IsRequired();
            builder.Property(static x => x.Description).HasColumnName("description").HasMaxLength(512);
            builder.Property(static x => x.GroupKey).HasColumnName("group_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.GroupLabel).HasColumnName("group_label").HasMaxLength(64);
            builder.Property(static x => x.Sort).HasColumnName("sort");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            // 权限码跨应用全局唯一：两个应用不得用同一个码，否则权限归属会含糊。
            builder.HasIndex(static x => x.Code).IsUnique().HasDatabaseName("ux_app_permissions_code");
            builder.HasIndex(static x => x.AppId).HasDatabaseName("ix_app_permissions_app");

            builder.HasOne(static x => x.App)
                .WithMany(static x => x.Permissions)
                .HasForeignKey(static x => x.AppId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppPermissionPreset>(builder =>
        {
            builder.ToTable("app_permission_presets");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppId).HasColumnName("app_id");
            builder.Property(static x => x.Key).HasColumnName("key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Label).HasColumnName("label").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Description).HasColumnName("description").HasMaxLength(256);
            builder.Property(static x => x.Permissions).HasColumnName("permissions").HasColumnType("text[]");
            builder.Property(static x => x.Sort).HasColumnName("sort");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.AppId, x.Key })
                .IsUnique()
                .HasDatabaseName("ux_app_permission_presets_app_key");
            builder.HasIndex(static x => x.AppId).HasDatabaseName("ix_app_permission_presets_app");

            builder.HasOne(static x => x.App)
                .WithMany(static x => x.Presets)
                .HasForeignKey(static x => x.AppId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
