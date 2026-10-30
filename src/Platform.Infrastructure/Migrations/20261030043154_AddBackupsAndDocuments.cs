using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupsAndDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "backups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    namespace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
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
                name: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
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
                name: "ix_backups_namespace_created",
                table: "backups",
                columns: new[] { "namespace_id", "created_at" });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backups");

            migrationBuilder.DropTable(
                name: "document_revisions");

            migrationBuilder.DropTable(
                name: "backup_namespaces");

            migrationBuilder.DropTable(
                name: "documents");

            migrationBuilder.DropTable(
                name: "document_collections");
        }
    }
}
