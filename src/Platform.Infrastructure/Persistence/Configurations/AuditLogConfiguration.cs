using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Infrastructure.Persistence.Entities;

namespace Platform.Infrastructure.Persistence.Configurations;

/// <summary><c>audit_logs</c> 映射。</summary>
public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(static x => x.Id);

        builder.Property(static x => x.Id).HasColumnName("id");
        builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64);
        builder.Property(static x => x.ActorId).HasColumnName("actor_id").HasMaxLength(128);
        builder.Property(static x => x.ActorName).HasColumnName("actor_name").HasMaxLength(256);
        builder.Property(static x => x.Action).HasColumnName("action").HasMaxLength(128).IsRequired();
        builder.Property(static x => x.TargetType).HasColumnName("target_type").HasMaxLength(64);
        builder.Property(static x => x.TargetId).HasColumnName("target_id").HasMaxLength(128);
        builder.Property(static x => x.Result).HasColumnName("result").HasMaxLength(16).IsRequired();
        builder.Property(static x => x.Message).HasColumnName("message").HasMaxLength(1024);
        builder.Property(static x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
        builder.Property(static x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
        builder.Property(static x => x.TraceId).HasColumnName("trace_id").HasMaxLength(128);
        builder.Property(static x => x.DetailJson).HasColumnName("detail_json").HasColumnType("jsonb");
        builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(static x => new { x.AppKey, x.CreatedAt }).HasDatabaseName("ix_audit_logs_app_created");
        builder.HasIndex(static x => new { x.ActorId, x.CreatedAt }).HasDatabaseName("ix_audit_logs_actor_created");
        builder.HasIndex(static x => new { x.Action, x.CreatedAt }).HasDatabaseName("ix_audit_logs_action_created");
    }
}
