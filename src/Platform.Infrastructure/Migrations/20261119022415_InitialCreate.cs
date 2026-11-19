using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_updater_releases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    manifest_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    manifest_json = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_updater_releases", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "apps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    settings_json = table.Column<string>(type: "jsonb", nullable: true),
                    permissions_locked = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_apps", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    actor_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    target_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    trace_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    detail_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "backup_namespaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    channel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    max_versions = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_namespaces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device_nodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    machine_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    auto_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    custom_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_nodes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_collections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_collections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    response_json = table.Column<string>(type: "jsonb", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "license_products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    seat_limit = table.Column<int>(type: "integer", nullable: true),
                    default_grace_days = table.Column<int>(type: "integer", nullable: false),
                    trial_days = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_products", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "license_signing_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    algorithm = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    public_key = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_signing_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "login_throttles",
                columns: table => new
                {
                    dimension = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    failure_count = table.Column<int>(type: "integer", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_throttles", x => new { x.dimension, x.key });
                });

            migrationBuilder.CreateTable(
                name: "qr_login_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    client_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    device_fingerprint = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    machine_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    install_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    connection_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_by_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    access_token = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    temporary_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_qr_login_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "release_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    allow_downgrade = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    audience_tags = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "ARRAY[]::text[]"),
                    auto_created = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_channels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    permissions = table.Column<List<string>>(type: "text[]", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "single_login_keys",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    device_fingerprint = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    bound_machine_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    bound_install_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_single_login_keys", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    password_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false),
                    token_epoch = table.Column<int>(type: "integer", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    login_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "version_upload_tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_version_upload_tickets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    event_types_json = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    secret = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    last_delivery_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_subscriptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    scopes = table.Column<List<string>>(type: "text[]", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "FK_app_api_keys_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "app_permission_presets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    permissions = table.Column<List<string>>(type: "text[]", nullable: false),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_permission_presets", x => x.id);
                    table.ForeignKey(
                        name: "FK_app_permission_presets_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "app_permissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    group_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    group_label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_permissions", x => x.id);
                    table.ForeignKey(
                        name: "FK_app_permissions_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "backups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    namespace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    node_id = table.Column<Guid>(type: "uuid", nullable: true),
                    node_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    module_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    framework_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    channel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: true),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    blob_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backups", x => x.id);
                    table.ForeignKey(
                        name: "FK_backups_backup_namespaces_namespace_id",
                        column: x => x.namespace_id,
                        principalTable: "backup_namespaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_installations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    install_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    client_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    custom_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    branch = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    framework_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    online_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    station_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    alarm_level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    alarm_message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    first_connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    heartbeat_seconds = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_installations", x => x.id);
                    table.ForeignKey(
                        name: "FK_device_installations_device_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "device_nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    applicable_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    content_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    blob_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.id);
                    table.ForeignKey(
                        name: "FK_documents_document_collections_collection_id",
                        column: x => x.collection_id,
                        principalTable: "document_collections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "license_entitlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    machine_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    not_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    grace_days = table.Column<int>(type: "integer", nullable: false),
                    license_no = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    issued_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    revoke_scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_license_entitlements", x => x.id);
                    table.ForeignKey(
                        name: "FK_license_entitlements_license_products_product_id",
                        column: x => x.product_id,
                        principalTable: "license_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "offline_license_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    machine_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    license_no = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    code = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    not_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    grace_days = table.Column<int>(type: "integer", nullable: false),
                    remark = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    issued_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offline_license_codes", x => x.id);
                    table.ForeignKey(
                        name: "FK_offline_license_codes_license_products_product_id",
                        column: x => x.product_id,
                        principalTable: "license_products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "releases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_releases", x => x.id);
                    table.ForeignKey(
                        name: "FK_releases_release_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "release_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    device_fingerprint = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    machine_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    install_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    replaced_by_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "FK_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_status_code = table.Column<int>(type: "integer", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "FK_webhook_deliveries_webhook_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "webhook_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    content_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    blob_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_revisions_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "release_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    runtime_identifier = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    manifest_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_count = table.Column<int>(type: "integer", nullable: false),
                    total_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "FK_release_artifacts_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "release_packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    format = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    manifest_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    manifest_json = table.Column<string>(type: "jsonb", nullable: true),
                    manifest_source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    publish_requested = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    file_count = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_packages", x => x.id);
                    table.ForeignKey(
                        name: "FK_release_packages_releases_release_id",
                        column: x => x.release_id,
                        principalTable: "releases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "release_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_files", x => x.id);
                    table.ForeignKey(
                        name: "FK_release_files_release_artifacts_artifact_id",
                        column: x => x.artifact_id,
                        principalTable: "release_artifacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_app_api_keys_app",
                table: "app_api_keys",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "ux_app_api_keys_prefix",
                table: "app_api_keys",
                column: "prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_app_permission_presets_app",
                table: "app_permission_presets",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "ux_app_permission_presets_app_key",
                table: "app_permission_presets",
                columns: new[] { "app_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_app_permissions_app",
                table: "app_permissions",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "ux_app_permissions_code",
                table: "app_permissions",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_app_updater_releases_app_status",
                table: "app_updater_releases",
                columns: new[] { "app_key", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_app_updater_releases_app_target_version",
                table: "app_updater_releases",
                columns: new[] { "app_key", "target", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_apps_key",
                table: "apps",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_action_created",
                table: "audit_logs",
                columns: new[] { "action", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_actor_created",
                table: "audit_logs",
                columns: new[] { "actor_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_app_created",
                table: "audit_logs",
                columns: new[] { "app_key", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_backup_namespaces_app_key",
                table: "backup_namespaces",
                columns: new[] { "app_key", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_backups_app_deleted",
                table: "backups",
                columns: new[] { "app_key", "deleted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_backups_deleted_namespace",
                table: "backups",
                columns: new[] { "deleted_at", "namespace_id" });

            migrationBuilder.CreateIndex(
                name: "ix_backups_installation_created",
                table: "backups",
                columns: new[] { "installation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_backups_namespace_created",
                table: "backups",
                columns: new[] { "namespace_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_backups_node_created",
                table: "backups",
                columns: new[] { "node_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_device_installations_app_heartbeat",
                table: "device_installations",
                columns: new[] { "app_key", "last_heartbeat_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_device_installations_app_online",
                table: "device_installations",
                columns: new[] { "app_key", "online_status" });

            migrationBuilder.CreateIndex(
                name: "ix_device_installations_node",
                table: "device_installations",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "ux_device_installations_app_install",
                table: "device_installations",
                columns: new[] { "app_key", "install_guid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_nodes_last_seen",
                table: "device_nodes",
                column: "last_seen_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ux_device_nodes_machine_code",
                table: "device_nodes",
                column: "machine_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_document_collections_app_key",
                table: "document_collections",
                columns: new[] { "app_key", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_revisions_deleted_document",
                table: "document_revisions",
                columns: new[] { "deleted_at", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ux_document_revisions_document_revision",
                table: "document_revisions",
                columns: new[] { "document_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_documents_app_deleted",
                table: "documents",
                columns: new[] { "app_key", "deleted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_documents_collection_updated",
                table: "documents",
                columns: new[] { "collection_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_documents_deleted_collection",
                table: "documents",
                columns: new[] { "deleted_at", "collection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_expires",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ux_idempotency_scope_key",
                table: "idempotency_records",
                columns: new[] { "scope", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_license_entitlements_app_expires",
                table: "license_entitlements",
                columns: new[] { "app_key", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_license_entitlements_app_revoked",
                table: "license_entitlements",
                columns: new[] { "app_key", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_license_entitlements_machine_code",
                table: "license_entitlements",
                column: "machine_code");

            migrationBuilder.CreateIndex(
                name: "ux_license_entitlements_product_machine",
                table: "license_entitlements",
                columns: new[] { "product_id", "machine_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_license_products_app_key",
                table: "license_products",
                columns: new[] { "app_key", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_license_signing_keys_status",
                table: "license_signing_keys",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_license_signing_keys_key_id",
                table: "license_signing_keys",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_login_throttles_locked_until",
                table: "login_throttles",
                column: "locked_until");

            migrationBuilder.CreateIndex(
                name: "ix_offline_license_codes_app_machine",
                table: "offline_license_codes",
                columns: new[] { "app_key", "machine_code" });

            migrationBuilder.CreateIndex(
                name: "IX_offline_license_codes_product_id",
                table: "offline_license_codes",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ux_offline_license_codes_app_license_no",
                table: "offline_license_codes",
                columns: new[] { "app_key", "license_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_qr_login_sessions_expires_at",
                table: "qr_login_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_qr_login_sessions_status",
                table: "qr_login_sessions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_expires_at",
                table: "refresh_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_family",
                table: "refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user",
                table: "refresh_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_release_artifacts_release_runtime",
                table: "release_artifacts",
                columns: new[] { "release_id", "runtime_identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_release_channels_app_key",
                table: "release_channels",
                columns: new[] { "app_key", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_release_files_sha256",
                table: "release_files",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "ux_release_files_artifact_path",
                table: "release_files",
                columns: new[] { "artifact_id", "path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_release_packages_app_status",
                table: "release_packages",
                columns: new[] { "app_key", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_release_packages_release_kind_target_active",
                table: "release_packages",
                columns: new[] { "release_id", "kind", "target", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_release_packages_sha256",
                table: "release_packages",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "ix_releases_app_status",
                table: "releases",
                columns: new[] { "app_key", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_releases_channel_status_published",
                table: "releases",
                columns: new[] { "channel_id", "status", "published_at" });

            migrationBuilder.CreateIndex(
                name: "ux_releases_channel_version",
                table: "releases",
                columns: new[] { "channel_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_roles_app_key",
                table: "roles",
                column: "app_key");

            migrationBuilder.CreateIndex(
                name: "ux_roles_name",
                table: "roles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_single_login_keys_expires_at",
                table: "single_login_keys",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_role",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ux_users_user_name",
                table: "users",
                column: "user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_version_upload_tickets_app_expires",
                table: "version_upload_tickets",
                columns: new[] { "app_key", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_app_created",
                table: "webhook_deliveries",
                columns: new[] { "app_key", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_status_next",
                table: "webhook_deliveries",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_subscription_id",
                table: "webhook_deliveries",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_subscriptions_app_enabled",
                table: "webhook_subscriptions",
                columns: new[] { "app_key", "is_enabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_api_keys");

            migrationBuilder.DropTable(
                name: "app_permission_presets");

            migrationBuilder.DropTable(
                name: "app_permissions");

            migrationBuilder.DropTable(
                name: "app_updater_releases");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "backups");

            migrationBuilder.DropTable(
                name: "device_installations");

            migrationBuilder.DropTable(
                name: "document_revisions");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "license_entitlements");

            migrationBuilder.DropTable(
                name: "license_signing_keys");

            migrationBuilder.DropTable(
                name: "login_throttles");

            migrationBuilder.DropTable(
                name: "offline_license_codes");

            migrationBuilder.DropTable(
                name: "qr_login_sessions");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "release_files");

            migrationBuilder.DropTable(
                name: "release_packages");

            migrationBuilder.DropTable(
                name: "single_login_keys");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "version_upload_tickets");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "apps");

            migrationBuilder.DropTable(
                name: "backup_namespaces");

            migrationBuilder.DropTable(
                name: "device_nodes");

            migrationBuilder.DropTable(
                name: "documents");

            migrationBuilder.DropTable(
                name: "license_products");

            migrationBuilder.DropTable(
                name: "release_artifacts");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "webhook_subscriptions");

            migrationBuilder.DropTable(
                name: "document_collections");

            migrationBuilder.DropTable(
                name: "releases");

            migrationBuilder.DropTable(
                name: "release_channels");
        }
    }
}
