using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;
using Platform.Modules.Webhooks.Domain;

namespace Platform.Modules.Webhooks.Persistence;

/// <summary>向共享 <c>PlatformDbContext</c> 贡献 Webhook 表映射。</summary>
public sealed class WebhooksModelContributor : IPlatformModelContributor
{
    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<WebhookSubscription>(builder =>
        {
            builder.ToTable("webhook_subscriptions");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.Url).HasColumnName("url").HasMaxLength(2048).IsRequired();
            builder.Property(static x => x.EventTypesJson).HasColumnName("event_types_json").HasColumnType("text").IsRequired();
            builder.Property(static x => x.Description).HasColumnName("description").HasMaxLength(1024);
            builder.Property(static x => x.IsEnabled).HasColumnName("is_enabled");
            builder.Property(static x => x.Secret).HasColumnName("secret").HasMaxLength(128).IsRequired();
            builder.Property(static x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            builder.Property(static x => x.LastDeliveryAt).HasColumnName("last_delivery_at");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.AppKey, x.IsEnabled })
                .HasDatabaseName("ix_webhook_subscriptions_app_enabled");
        });

        modelBuilder.Entity<WebhookDelivery>(builder =>
        {
            builder.ToTable("webhook_deliveries");
            builder.HasKey(static x => x.Id);

            builder.Property(static x => x.Id).HasColumnName("id");
            builder.Property(static x => x.AppKey).HasColumnName("app_key").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.SubscriptionId).HasColumnName("subscription_id");
            builder.Property(static x => x.EventType).HasColumnName("event_type").HasMaxLength(64).IsRequired();
            builder.Property(static x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("text").IsRequired();
            builder.Property(static x => x.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
            builder.Property(static x => x.Attempts).HasColumnName("attempts");
            builder.Property(static x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            builder.Property(static x => x.LastAttemptAt).HasColumnName("last_attempt_at");
            builder.Property(static x => x.LastStatusCode).HasColumnName("last_status_code");
            builder.Property(static x => x.LastError).HasColumnName("last_error").HasMaxLength(1024);
            builder.Property(static x => x.DeliveredAt).HasColumnName("delivered_at");
            builder.Property(static x => x.CreatedAt).HasColumnName("created_at");
            builder.Property(static x => x.UpdatedAt).HasColumnName("updated_at");

            builder.HasIndex(static x => new { x.Status, x.NextAttemptAt })
                .HasDatabaseName("ix_webhook_deliveries_status_next");
            builder.HasIndex(static x => new { x.AppKey, x.CreatedAt })
                .HasDatabaseName("ix_webhook_deliveries_app_created");

            builder.HasOne(static x => x.Subscription)
                .WithMany(static x => x.Deliveries)
                .HasForeignKey(static x => x.SubscriptionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
