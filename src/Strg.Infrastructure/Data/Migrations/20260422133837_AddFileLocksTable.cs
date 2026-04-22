using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Strg.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFileLocksTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileLocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerInfo = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileLocks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileLocks_TenantId_ResourceUri_Active",
                table: "FileLocks",
                columns: new[] { "TenantId", "ResourceUri" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileLocks_Token",
                table: "FileLocks",
                column: "Token");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileLocks");
        }
    }
}
