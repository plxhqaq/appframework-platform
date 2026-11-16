using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    device_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    machine_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    install_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    auto_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    custom_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
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
                    table.PrimaryKey("PK_devices", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_devices_app_last_heartbeat",
                table: "devices",
                columns: new[] { "app_key", "last_heartbeat_at" });

            migrationBuilder.CreateIndex(
                name: "ix_devices_app_machine_code",
                table: "devices",
                columns: new[] { "app_key", "machine_code" });

            migrationBuilder.CreateIndex(
                name: "ux_devices_app_device_key",
                table: "devices",
                columns: new[] { "app_key", "device_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "devices");
        }
    }
}
