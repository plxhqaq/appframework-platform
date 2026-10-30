using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Modules.Identity.Domain;

namespace Platform.Modules.Identity.Persistence;

/// <summary>向共享 <c>PlatformDbContext</c> 贡献身份模块的表映射。</summary>
public sealed class IdentityModelContributor : IPlatformModelContributor
{
    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<User>(builder =>
        {
            builder.ToTable("users");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.UserName).HasColumnName("user_name").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.DisplayName).HasColumnName("display_name").HasMaxLength(256);
            builder.Property(static x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(512).IsRequired();
            builder.Property(static x => x.IsEnabled).HasColumnName("is_enabled");
            builder.Property(static x => x.MustChangePassword).HasColumnName("must_change_password");
            builder.Property(static x => x.LastLoginAt).HasColumnName("last_login_at");
            builder.Property(static x => x.LoginCount).HasColumnName("login_count");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => x.UserName).IsUnique().HasDatabaseName("ux_users_user_name");
        });

        modelBuilder.Entity<Role>(builder =>
        {
            builder.ToTable("roles");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.DisplayName).HasColumnName("display_name").HasMaxLength(256);
            builder.Property(static x => x.Scope).HasColumnName("scope").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64);
            builder.Property(static x => x.Permissions).HasColumnName("permissions").HasColumnType("text[]");
            builder.Property(static x => x.IsSystem).HasColumnName("is_system");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => x.Name).IsUnique().HasDatabaseName("ux_roles_name");
            builder.HasIndex(static x => x.AppKey).HasDatabaseName("ix_roles_app_key");
        });

        modelBuilder.Entity<UserRole>(builder =>
        {
            builder.ToTable("user_roles");
            builder.HasKey(static x => new { x.UserId, x.RoleId });

            builder.Property(static x => x.UserId).HasColumnName("user_id");
            builder.Property(static x => x.RoleId).HasColumnName("role_id");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");

            builder.HasIndex(static x => x.RoleId).HasDatabaseName("ix_user_roles_role");

            builder.HasOne(static x => x.User)
                .WithMany(static x => x.UserRoles)
                .HasForeignKey(static x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(static x => x.Role)
                .WithMany(static x => x.UserRoles)
                .HasForeignKey(static x => x.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshToken>(builder =>
        {
            builder.ToTable("refresh_tokens");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.UserId).HasColumnName("user_id");
            builder.Property(static x => x.FamilyId).HasColumnName("family_id");
            builder.Property(static x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.DeviceFingerprint).HasColumnName("device_fingerprint").HasMaxLength(256);
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64);
            builder.Property(static x => x.MachineCode).HasColumnName("machine_code").HasMaxLength(128);
            builder.Property(static x => x.CreatedByIp).HasColumnName("created_by_ip").HasMaxLength(64);
            builder.Property(static x => x.ExpiresAt).HasColumnName("expires_at");
            builder.Property(static x => x.RevokedAt).HasColumnName("revoked_at");
            builder.Property(static x => x.RevokedReason).HasColumnName("revoked_reason").HasMaxLength(32);
            builder.Property(static x => x.ReplacedByTokenId).HasColumnName("replaced_by_token_id");
            builder.Property(static x => x.LastUsedAt).HasColumnName("last_used_at");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => x.TokenHash).IsUnique().HasDatabaseName("ux_refresh_tokens_token_hash");
            builder.HasIndex(static x => x.UserId).HasDatabaseName("ix_refresh_tokens_user");
            builder.HasIndex(static x => x.FamilyId).HasDatabaseName("ix_refresh_tokens_family");
            builder.HasIndex(static x => x.ExpiresAt).HasDatabaseName("ix_refresh_tokens_expires_at");

            builder.HasOne(static x => x.User)
                .WithMany(static x => x.RefreshTokens)
                .HasForeignKey(static x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LoginThrottle>(builder =>
        {
            builder.ToTable("login_throttles");
            builder.HasKey(static x => new { x.Dimension, x.Key });

            builder.Property(static x => x.Dimension).HasColumnName("dimension").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.Key).HasColumnName("key").HasMaxLength(256).IsRequired();
            builder.Property(static x => x.FailureCount).HasColumnName("failure_count");
            builder.Property(static x => x.WindowStart).HasColumnName("window_start");
            builder.Property(static x => x.LockedUntil).HasColumnName("locked_until");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => x.LockedUntil).HasDatabaseName("ix_login_throttles_locked_until");
        });

        modelBuilder.Entity<SingleLoginKey>(builder =>
        {
            builder.ToTable("single_login_keys");
            builder.HasKey(static x => x.Key);

            builder.Property(static x => x.Key).HasColumnName("key").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.DeviceFingerprint).HasColumnName("device_fingerprint").HasMaxLength(256);
            builder.Property(static x => x.CreatedBy).HasColumnName("created_by");
            builder.Property(static x => x.ExpiresAt).HasColumnName("expires_at");
            builder.Property(static x => x.UsedAt).HasColumnName("used_at");
            builder.Property(static x => x.AttemptCount).HasColumnName("attempt_count");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => x.ExpiresAt).HasDatabaseName("ix_single_login_keys_expires_at");
        });

        modelBuilder.Entity<QrLoginSession>(builder =>
        {
            builder.ToTable("qr_login_sessions");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.ClientName).HasColumnName("client_name").HasMaxLength(256);
            builder.Property(static x => x.DeviceFingerprint).HasColumnName("device_fingerprint").HasMaxLength(256);
            builder.Property(static x => x.ConnectionId).HasColumnName("connection_id").HasMaxLength(128);
            builder.Property(static x => x.CreatedByIp).HasColumnName("created_by_ip").HasMaxLength(64);
            builder.Property(static x => x.ExpiresAt).HasColumnName("expires_at");
            builder.Property(static x => x.ApprovedAt).HasColumnName("approved_at");
            builder.Property(static x => x.ApprovedBy).HasColumnName("approved_by");
            builder.Property(static x => x.ApprovedByName).HasColumnName("approved_by_name").HasMaxLength(256);
            builder.Property(static x => x.AccessToken).HasColumnName("access_token").HasMaxLength(4096);
            builder.Property(static x => x.TokenExpiresAt).HasColumnName("token_expires_at");
            builder.Property(static x => x.TemporaryUserId).HasColumnName("temporary_user_id");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => x.Status).HasDatabaseName("ix_qr_login_sessions_status");
            builder.HasIndex(static x => x.ExpiresAt).HasDatabaseName("ix_qr_login_sessions_expires_at");
        });
    }
}
