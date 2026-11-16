using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Modules.Documents.Domain;

namespace Platform.Modules.Documents.Persistence;

/// <summary>向共享 <c>PlatformDbContext</c> 贡献文档模块的表映射。</summary>
public sealed class DocumentsModelContributor : IPlatformModelContributor
{
    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<DocumentCollection>(builder =>
        {
            builder.ToTable("document_collections");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Key).HasColumnName("key").HasMaxLength(32).IsRequired();
            builder.Property(static x => x.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.Description).HasColumnName("description").HasMaxLength(1024);
            builder.Property(static x => x.IsEnabled).HasColumnName("is_enabled");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.AppKey, x.Key })
                .IsUnique()
                .HasDatabaseName("ux_document_collections_app_key");
        });

        modelBuilder.Entity<Document>(builder =>
        {
            builder.ToTable("documents");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.CollectionId).HasColumnName("collection_id");
            builder.Property(static x => x.Title).HasColumnName("title").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.Visibility).HasColumnName("visibility").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.ApplicableVersion).HasColumnName("applicable_version").HasMaxLength(64);
            builder.Property(static x => x.Revision).HasColumnName("revision");
            builder.Property(static x => x.FileName).HasColumnName("file_name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.ContentType).HasColumnName("content_type").HasMaxLength(128);
            builder.Property(static x => x.BlobPath).HasColumnName("blob_path").HasMaxLength(512).IsRequired();
            builder.Property(static x => x.SizeBytes).HasColumnName("size_bytes").HasColumnType("bigint");
            builder.Property(static x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");
            builder.Property(static x => x.DeletedAt).HasColumnName("deleted_at");

            builder.HasIndex(static x => new { x.CollectionId, x.UpdatedAt })
                .HasDatabaseName("ix_documents_collection_updated");
            builder.HasIndex(static x => new { x.AppKey, x.DeletedAt })
                .HasDatabaseName("ix_documents_app_deleted");
            builder.HasIndex(static x => new { x.DeletedAt, x.CollectionId })
                .HasDatabaseName("ix_documents_deleted_collection");

            builder.HasOne(static x => x.Collection)
                .WithMany(static x => x.Documents)
                .HasForeignKey(static x => x.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentRevision>(builder =>
        {
            builder.ToTable("document_revisions");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.DocumentId).HasColumnName("document_id");
            builder.Property(static x => x.Revision).HasColumnName("revision");
            builder.Property(static x => x.FileName).HasColumnName("file_name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.ContentType).HasColumnName("content_type").HasMaxLength(128);
            builder.Property(static x => x.BlobPath).HasColumnName("blob_path").HasMaxLength(512).IsRequired();
            builder.Property(static x => x.SizeBytes).HasColumnName("size_bytes").HasColumnType("bigint");
            builder.Property(static x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");
            builder.Property(static x => x.DeletedAt).HasColumnName("deleted_at");

            builder.HasIndex(static x => new { x.DocumentId, x.Revision })
                .IsUnique()
                .HasDatabaseName("ux_document_revisions_document_revision");
            builder.HasIndex(static x => new { x.DeletedAt, x.DocumentId })
                .HasDatabaseName("ix_document_revisions_deleted_document");

            builder.HasOne(static x => x.Document)
                .WithMany(static x => x.Revisions)
                .HasForeignKey(static x => x.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
