using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLicensing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "ix_license_entitlements_app_expires",
                table: "license_entitlements",
                columns: new[] { "app_key", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_license_entitlements_app_revoked",
                table: "license_entitlements",
                columns: new[] { "app_key", "revoked_at" });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "license_entitlements");

            migrationBuilder.DropTable(
                name: "license_signing_keys");

            migrationBuilder.DropTable(
                name: "offline_license_codes");

            migrationBuilder.DropTable(
                name: "license_products");
        }
    }
}
