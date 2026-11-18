using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DeviceNodeCustomNameSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "custom_name_source",
                table: "device_nodes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // 旧数据没有来源：非空自定义名都来自管理端，标成 admin 防止客户端注册顶掉。
            migrationBuilder.Sql(
                "UPDATE device_nodes SET custom_name_source = 'admin' WHERE custom_name IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "custom_name_source",
                table: "device_nodes");
        }
    }
}
