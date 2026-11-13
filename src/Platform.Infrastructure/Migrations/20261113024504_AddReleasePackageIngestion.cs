using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReleasePackageIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "file_count",
                table: "release_packages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "publish_requested",
                table: "release_packages",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "file_count",
                table: "release_packages");

            migrationBuilder.DropColumn(
                name: "publish_requested",
                table: "release_packages");
        }
    }
}
