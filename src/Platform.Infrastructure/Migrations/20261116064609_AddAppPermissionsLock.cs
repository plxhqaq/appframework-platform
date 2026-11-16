using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppPermissionsLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "permissions_locked",
                table: "apps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // 已经上报名单的应用视同上锁，避免升级后下一次上传直接覆盖既有权限。
            migrationBuilder.Sql(
                "UPDATE apps SET permissions_locked = true "
                + "WHERE EXISTS (SELECT 1 FROM app_permissions WHERE app_permissions.app_id = apps.id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "permissions_locked",
                table: "apps");
        }
    }
}
