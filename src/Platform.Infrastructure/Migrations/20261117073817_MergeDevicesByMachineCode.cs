using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MergeDevicesByMachineCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "merged_into_device_id",
                table: "devices",
                type: "uuid",
                nullable: true);

            // 同一应用同一机器码只保留一条主设备：优先保留有自定义名的行，其次保留最近心跳的行。
            // 其余历史行标记为已合并并置离线，避免管理台继续出现同机多设备。
            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT id,
                           first_value(id) OVER (
                               PARTITION BY app_key, machine_code
                               ORDER BY (custom_name IS NOT NULL) DESC,
                                        last_heartbeat_at DESC NULLS LAST,
                                        created_at ASC,
                                        id
                           ) AS canonical_id
                    FROM devices
                )
                UPDATE devices AS d
                SET merged_into_device_id = ranked.canonical_id,
                    online_status = 'offline',
                    updated_at = now()
                FROM ranked
                WHERE d.id = ranked.id
                  AND ranked.id <> ranked.canonical_id;
                """);

            // 历史备份归属改到主设备，按设备筛选备份时不会丢记录。
            migrationBuilder.Sql(
                """
                UPDATE backups AS b
                SET device_id = d.merged_into_device_id
                FROM devices AS d
                WHERE b.device_id = d.id
                  AND d.merged_into_device_id IS NOT NULL;
                """);

            // 主设备的设备键统一为机器码，唯一索引从数据库层阻止再产生同机重复行。
            migrationBuilder.Sql(
                """
                UPDATE devices
                SET device_key = machine_code,
                    updated_at = now()
                WHERE merged_into_device_id IS NULL
                  AND device_key <> machine_code;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "merged_into_device_id",
                table: "devices");
        }
    }
}
