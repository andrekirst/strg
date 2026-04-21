using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Strg.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FileVersion_BlobSizeBytes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BlobSizeBytes",
                table: "FileVersions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "FileKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EncryptedDek = table.Column<byte[]>(type: "bytea", nullable: false),
                    Algorithm = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileKeys_FileVersions_FileVersionId",
                        column: x => x.FileVersionId,
                        principalTable: "FileVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileKeys_FileVersionId",
                table: "FileKeys",
                column: "FileVersionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileKeys");

            migrationBuilder.DropColumn(
                name: "BlobSizeBytes",
                table: "FileVersions");
        }
    }
}
