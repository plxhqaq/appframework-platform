using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Modules.Licensing.Domain;

namespace Platform.Modules.Licensing.Persistence;

/// <summary>向共享 <c>PlatformDbContext</c> 贡献授权模块的表映射。</summary>
public sealed class LicensingModelContributor : IPlatformModelContributor
{
    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<LicenseProduct>(builder =>
        {
            builder.ToTable("license_products");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Key).HasColumnName("key").HasMaxLength(32).IsRequired();
            builder.Property(static x => x.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.Description).HasColumnName("description").HasMaxLength(1024);
            builder.Property(static x => x.IsEnabled).HasColumnName("is_enabled");
            builder.Property(static x => x.SeatLimit).HasColumnName("seat_limit");
            builder.Property(static x => x.DefaultGraceDays).HasColumnName("default_grace_days");
            builder.Property(static x => x.TrialDays).HasColumnName("trial_days");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.AppKey, x.Key })
                .IsUnique()
                .HasDatabaseName("ux_license_products_app_key");
        });

        modelBuilder.Entity<LicenseEntitlement>(builder =>
        {
            builder.ToTable("license_entitlements");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.ProductId).HasColumnName("product_id");
            builder.Property(static x => x.MachineCode).HasColumnName("machine_code").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.NotBefore).HasColumnName("not_before");
            builder.Property(static x => x.NotAfter).HasColumnName("not_after");
            builder.Property(static x => x.ExpiresAt).HasColumnName("expires_at");
            builder.Property(static x => x.GraceDays).HasColumnName("grace_days");
            builder.Property(static x => x.LicenseNo).HasColumnName("license_no").HasMaxLength(32);
            builder.Property(static x => x.Notes).HasColumnName("notes").HasMaxLength(1024);
            builder.Property(static x => x.IssuedBy).HasColumnName("issued_by").HasMaxLength(256);
            builder.Property(static x => x.RevokedAt).HasColumnName("revoked_at");
            builder.Property(static x => x.RevokedReason).HasColumnName("revoked_reason").HasMaxLength(512);
            builder.Property(static x => x.RevokeScope).HasColumnName("revoke_scope").HasMaxLength(16);
            builder.Property(static x => x.LastSeenAt).HasColumnName("last_seen_at");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.ProductId, x.MachineCode })
                .IsUnique()
                .HasDatabaseName("ux_license_entitlements_product_machine");
            builder.HasIndex(static x => new { x.AppKey, x.ExpiresAt })
                .HasDatabaseName("ix_license_entitlements_app_expires");
            builder.HasIndex(static x => new { x.AppKey, x.RevokedAt })
                .HasDatabaseName("ix_license_entitlements_app_revoked");
            builder.HasIndex(static x => x.MachineCode)
                .HasDatabaseName("ix_license_entitlements_machine_code");

            builder.HasOne(static x => x.Product)
                .WithMany(static x => x.Entitlements)
                .HasForeignKey(static x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OfflineLicenseCode>(builder =>
        {
            builder.ToTable("offline_license_codes");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.ProductId).HasColumnName("product_id");
            builder.Property(static x => x.MachineCode).HasColumnName("machine_code").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.LicenseNo).HasColumnName("license_no").HasMaxLength(32).IsRequired();
            builder.Property(static x => x.Code).HasColumnName("code").HasMaxLength(1024).IsRequired();
            builder.Property(static x => x.Kind).HasColumnName("kind").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.IssuedAt).HasColumnName("issued_at");
            builder.Property(static x => x.NotAfter).HasColumnName("not_after");
            builder.Property(static x => x.GraceDays).HasColumnName("grace_days");
            builder.Property(static x => x.Remark).HasColumnName("remark").HasMaxLength(512);
            builder.Property(static x => x.IssuedBy).HasColumnName("issued_by").HasMaxLength(256);
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.AppKey, x.LicenseNo })
                .IsUnique()
                .HasDatabaseName("ux_offline_license_codes_app_license_no");
            builder.HasIndex(static x => new { x.AppKey, x.MachineCode })
                .HasDatabaseName("ix_offline_license_codes_app_machine");

            builder.HasOne(static x => x.Product)
                .WithMany(static x => x.OfflineCodes)
                .HasForeignKey(static x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LicenseSigningKey>(builder =>
        {
            builder.ToTable("license_signing_keys");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.KeyId).HasColumnName("key_id").HasMaxLength(32).IsRequired();
            builder.Property(static x => x.Algorithm).HasColumnName("algorithm").HasMaxLength(32).IsRequired();
            builder.Property(static x => x.PublicKey).HasColumnName("public_key").HasMaxLength(2048).IsRequired();
            builder.Property(static x => x.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.ActivatedAt).HasColumnName("activated_at");
            builder.Property(static x => x.RetiredAt).HasColumnName("retired_at");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => x.KeyId)
                .IsUnique()
                .HasDatabaseName("ux_license_signing_keys_key_id");
            builder.HasIndex(static x => x.Status)
                .HasDatabaseName("ix_license_signing_keys_status");
        });
    }
}
