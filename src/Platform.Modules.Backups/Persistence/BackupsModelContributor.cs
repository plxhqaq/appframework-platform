using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Modules.Backups.Domain;

namespace Platform.Modules.Backups.Persistence;

/// <summary>向共享 <c>PlatformDbContext</c> 贡献备份模块的表映射。</summary>
public sealed class BackupsModelContributor : IPlatformModelContributor
{
    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<BackupNamespace>(builder =>
        {
            builder.ToTable("backup_namespaces");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Key).HasColumnName("key").HasMaxLength(32).IsRequired();
            builder.Property(static x => x.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.Description).HasColumnName("description").HasMaxLength(1024);
            builder.Property(static x => x.Channel).HasColumnName("channel").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.MaxVersions).HasColumnName("max_versions");
            builder.Property(static x => x.IsEnabled).HasColumnName("is_enabled");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.AppKey, x.Key })
                .IsUnique()
                .HasDatabaseName("ux_backup_namespaces_app_key");
        });

        modelBuilder.Entity<Backup>(builder =>
        {
            builder.ToTable("backups");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.NamespaceId).HasColumnName("namespace_id");
            builder.Property(static x => x.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.InstallationId).HasColumnName("installation_id");
            builder.Property(static x => x.NodeId).HasColumnName("node_id");
            builder.Property(static x => x.NodeName).HasColumnName("node_name").HasMaxLength(256);
            builder.Property(static x => x.ModuleId).HasColumnName("module_id").HasMaxLength(64);
            builder.Property(static x => x.SchemaVersion).HasColumnName("schema_version");
            builder.Property(static x => x.FrameworkVersion)
                .HasColumnName("framework_version").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Channel).HasColumnName("channel").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("text");
            builder.Property(static x => x.FileName).HasColumnName("file_name").HasMaxLength(256);
            builder.Property(static x => x.BlobPath).HasColumnName("blob_path").HasMaxLength(512);
            builder.Property(static x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
            builder.Property(static x => x.SizeBytes).HasColumnName("size_bytes").HasColumnType("bigint");
            builder.Property(static x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");
            builder.Property(static x => x.DeletedAt).HasColumnName("deleted_at");

            builder.HasIndex(static x => new { x.NamespaceId, x.CreatedAt })
                .HasDatabaseName("ix_backups_namespace_created");
            builder.HasIndex(static x => new { x.InstallationId, x.CreatedAt })
                .HasDatabaseName("ix_backups_installation_created");
            builder.HasIndex(static x => new { x.NodeId, x.CreatedAt })
                .HasDatabaseName("ix_backups_node_created");
            builder.HasIndex(static x => new { x.AppKey, x.DeletedAt })
                .HasDatabaseName("ix_backups_app_deleted");
            builder.HasIndex(static x => new { x.DeletedAt, x.NamespaceId })
                .HasDatabaseName("ix_backups_deleted_namespace");

            builder.HasOne(static x => x.Namespace)
                .WithMany(static x => x.Backups)
                .HasForeignKey(static x => x.NamespaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
