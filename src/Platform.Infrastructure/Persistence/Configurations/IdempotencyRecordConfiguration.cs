using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Infrastructure.Persistence.Entities;

namespace Platform.Infrastructure.Persistence.Configurations;

/// <summary><c>idempotency_records</c> 映射。<c>(scope, key)</c> 唯一索引是幂等的物理保证。</summary>
public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");
        builder.HasKey(static x => x.Id);

        builder.Property(static x => x.Id).HasColumnName("id");
        builder.Property(static x => x.Scope).HasColumnName("scope").HasMaxLength(128).IsRequired();
        builder.Property(static x => x.Key).HasColumnName("key").HasMaxLength(256).IsRequired();
        builder.Property(static x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        builder.Property(static x => x.ResponseJson).HasColumnName("response_json").HasColumnType("jsonb");
        builder.Property(static x => x.ExpiresAt).HasColumnName("expires_at");
        builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(static x => new { x.Scope, x.Key }).IsUnique().HasDatabaseName("ux_idempotency_scope_key");
        builder.HasIndex(static x => x.ExpiresAt).HasDatabaseName("ix_idempotency_expires");
    }
}
