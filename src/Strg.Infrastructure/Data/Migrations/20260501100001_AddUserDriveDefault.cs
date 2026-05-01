using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Strg.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserDriveDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserDriveDefaults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriveId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserDriveDefaults", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserDriveDefaults_DriveId",
                table: "UserDriveDefaults",
                column: "DriveId");

            migrationBuilder.CreateIndex(
                name: "IX_UserDriveDefaults_TenantId_UserId",
                table: "UserDriveDefaults",
                columns: new[] { "TenantId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserDriveDefaults");
        }
    }
}
