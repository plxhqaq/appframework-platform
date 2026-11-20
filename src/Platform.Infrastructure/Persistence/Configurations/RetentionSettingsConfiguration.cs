using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Infrastructure.Persistence.Entities;

namespace Platform.Infrastructure.Persistence.Configurations;

/// <summary><c>retention_settings</c> 映射。</summary>
public sealed class RetentionSettingsConfiguration : IEntityTypeConfiguration<RetentionSettingsRow>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RetentionSettingsRow> builder)
    {
        builder.ToTable("retention_settings");
        builder.HasKey(static x => x.ModuleKey);

        builder.Property(static x => x.ModuleKey).HasColumnName("module_key").HasMaxLength(64);
        builder.Property(static x => x.CleanupGraceMinutes).HasColumnName("cleanup_grace_minutes").IsRequired();
        builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(static x => x.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);
    }
}
