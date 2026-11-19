using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Modules.Versions.Domain;

namespace Platform.Modules.Versions.Persistence;

/// <summary>向共享 <c>PlatformDbContext</c> 贡献版本模块的表映射。</summary>
public sealed class VersionsModelContributor : IPlatformModelContributor
{
    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ReleaseChannel>(builder =>
        {
            builder.ToTable("release_channels");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Key).HasColumnName("key").HasMaxLength(32).IsRequired();
            builder.Property(static x => x.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.Description).HasColumnName("description").HasMaxLength(1024);
            builder.Property(static x => x.IsEnabled).HasColumnName("is_enabled");
            builder.Property(static x => x.AllowDowngrade).HasColumnName("allow_downgrade").HasDefaultValue(false);
            builder.Property(static x => x.AudienceTags)
                .HasColumnName("audience_tags")
                .HasColumnType("text[]")
                .HasDefaultValueSql("ARRAY[]::text[]");
            builder.Property(static x => x.AutoCreated).HasColumnName("auto_created").HasDefaultValue(false);
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.AppKey, x.Key })
                .IsUnique()
                .HasDatabaseName("ux_release_channels_app_key");
        });

        modelBuilder.Entity<Release>(builder =>
        {
            builder.ToTable("releases");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.ChannelId).HasColumnName("channel_id");
            builder.Property(static x => x.Version).HasColumnName("version").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Notes).HasColumnName("notes").HasMaxLength(4000);
            builder.Property(static x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.PublishedAt).HasColumnName("published_at");
            builder.Property(static x => x.ArchivedAt).HasColumnName("archived_at");
            builder.Property(static x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.ChannelId, x.Version })
                .IsUnique()
                .HasDatabaseName("ux_releases_channel_version");
            builder.HasIndex(static x => new { x.AppKey, x.Status })
                .HasDatabaseName("ix_releases_app_status");
            builder.HasIndex(static x => new { x.ChannelId, x.Status, x.PublishedAt })
                .HasDatabaseName("ix_releases_channel_status_published");

            builder.HasOne(static x => x.Channel)
                .WithMany(static x => x.Releases)
                .HasForeignKey(static x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReleaseArtifact>(builder =>
        {
            builder.ToTable("release_artifacts");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.ReleaseId).HasColumnName("release_id");
            builder.Property(static x => x.RuntimeIdentifier)
                .HasColumnName("runtime_identifier").HasMaxLength(32).IsRequired();
            builder.Property(static x => x.ManifestHash).HasColumnName("manifest_hash").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.FileCount).HasColumnName("file_count");
            builder.Property(static x => x.TotalSizeBytes).HasColumnName("total_size_bytes").HasColumnType("bigint");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.ReleaseId, x.RuntimeIdentifier })
                .IsUnique()
                .HasDatabaseName("ux_release_artifacts_release_runtime");

            builder.HasOne(static x => x.Release)
                .WithMany(static x => x.Artifacts)
                .HasForeignKey(static x => x.ReleaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReleaseFile>(builder =>
        {
            builder.ToTable("release_files");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.ArtifactId).HasColumnName("artifact_id");
            builder.Property(static x => x.Path).HasColumnName("path").HasMaxLength(512).IsRequired();
            builder.Property(static x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.SizeBytes).HasColumnName("size_bytes").HasColumnType("bigint");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.ArtifactId, x.Path })
                .IsUnique()
                .HasDatabaseName("ux_release_files_artifact_path");
            builder.HasIndex(static x => x.Sha256)
                .HasDatabaseName("ix_release_files_sha256");

            builder.HasOne(static x => x.Artifact)
                .WithMany(static x => x.Files)
                .HasForeignKey(static x => x.ArtifactId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReleasePackage>(builder =>
        {
            builder.ToTable("release_packages");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.ReleaseId).HasColumnName("release_id");
            builder.Property(static x => x.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.Target).HasColumnName("target").HasMaxLength(32).IsRequired();
            builder.Property(static x => x.Format).HasColumnName("format").HasMaxLength(8).IsRequired();
            builder.Property(static x => x.FileName).HasColumnName("file_name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.SizeBytes).HasColumnName("size_bytes").HasColumnType("bigint");
            builder.Property(static x => x.ManifestHash).HasColumnName("manifest_hash").HasMaxLength(64);
            builder.Property(static x => x.ManifestJson).HasColumnName("manifest_json").HasColumnType("jsonb");
            builder.Property(static x => x.ManifestSource).HasColumnName("manifest_source").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.PublishRequested).HasColumnName("publish_requested").HasDefaultValue(false);
            builder.Property(static x => x.FileCount).HasColumnName("file_count");
            builder.Property(static x => x.IsActive).HasColumnName("is_active").HasDefaultValue(false);
            builder.Property(static x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
            builder.Property(static x => x.ErrorMessage).HasColumnName("error_message").HasMaxLength(2000);
            builder.Property(static x => x.Attempts).HasColumnName("attempts");
            builder.Property(static x => x.LockedUntil).HasColumnName("locked_until");
            builder.Property(static x => x.LockedBy).HasColumnName("locked_by").HasMaxLength(128);
            builder.Property(static x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            builder.Property(static x => x.CompletedAt).HasColumnName("completed_at");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.ReleaseId, x.Kind, x.Target, x.IsActive })
                .HasDatabaseName("ix_release_packages_release_kind_target_active");
            builder.HasIndex(static x => new { x.AppKey, x.Status })
                .HasDatabaseName("ix_release_packages_app_status");
            builder.HasIndex(static x => x.Sha256)
                .HasDatabaseName("ix_release_packages_sha256");

            builder.HasOne(static x => x.Release)
                .WithMany(static x => x.Packages)
                .HasForeignKey(static x => x.ReleaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppUpdaterRelease>(builder =>
        {
            builder.ToTable("app_updater_releases");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Target).HasColumnName("target").HasMaxLength(32).IsRequired();
            builder.Property(static x => x.Version).HasColumnName("version").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.FileName).HasColumnName("file_name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.SizeBytes).HasColumnName("size_bytes").HasColumnType("bigint");
            builder.Property(static x => x.ManifestHash).HasColumnName("manifest_hash").HasMaxLength(64);
            builder.Property(static x => x.ManifestJson).HasColumnName("manifest_json").HasColumnType("jsonb");
            builder.Property(static x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.Notes).HasColumnName("notes").HasMaxLength(4000);
            builder.Property(static x => x.PublishedAt).HasColumnName("published_at");
            builder.Property(static x => x.ArchivedAt).HasColumnName("archived_at");
            builder.Property(static x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.AppKey, x.Target, x.Version })
                .IsUnique()
                .HasDatabaseName("ux_app_updater_releases_app_target_version");
            builder.HasIndex(static x => new { x.AppKey, x.Status })
                .HasDatabaseName("ix_app_updater_releases_app_status");
        });

        modelBuilder.Entity<VersionUploadTicket>(builder =>
        {
            builder.ToTable("version_upload_tickets");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.SizeBytes).HasColumnName("size_bytes").HasColumnType("bigint");
            builder.Property(static x => x.ExpiresAt).HasColumnName("expires_at");
            builder.Property(static x => x.ConsumedAt).HasColumnName("consumed_at");
            builder.Property(static x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.AppKey, x.ExpiresAt })
                .HasDatabaseName("ix_version_upload_tickets_app_expires");
        });

        modelBuilder.Entity<DriverMedia>(builder =>
        {
            builder.ToTable("driver_media");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.MediaKey).HasColumnName("media_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.Version).HasColumnName("version").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.IsActive).HasColumnName("is_active");
            builder.Property(static x => x.FileName).HasColumnName("file_name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.SizeBytes).HasColumnName("size_bytes").HasColumnType("bigint");
            builder.Property(static x => x.FileCount).HasColumnName("file_count");
            builder.Property(static x => x.ManifestHash).HasColumnName("manifest_hash").HasMaxLength(64);
            builder.Property(static x => x.ManifestJson).HasColumnName("manifest_json").HasColumnType("jsonb");
            builder.Property(static x => x.InstallOrderJson).HasColumnName("install_order_json").HasColumnType("jsonb");
            builder.Property(static x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.AppKey, x.MediaKey, x.Version })
                .IsUnique()
                .HasDatabaseName("ux_driver_media_app_key_media_key_version");
            builder.HasIndex(static x => x.Sha256)
                .HasDatabaseName("ix_driver_media_sha256");
        });
    }
}
