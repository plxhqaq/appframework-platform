using System;
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
                name: "ix_idempotency_expires",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ux_idempotency_scope_key",
                table: "idempotency_records",
                columns: new[] { "scope", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "idempotency_records");
        }
    }
}
