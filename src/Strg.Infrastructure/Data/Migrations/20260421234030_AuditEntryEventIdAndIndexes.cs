using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Strg.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditEntryEventIdAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                table: "AuditEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_EventId",
                table: "AuditEntries",
                column: "EventId",
                unique: true,
                filter: "\"EventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_ResourceId_ResourceType",
                table: "AuditEntries",
                columns: new[] { "ResourceId", "ResourceType" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_TenantId_PerformedAt",
                table: "AuditEntries",
                columns: new[] { "TenantId", "PerformedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_UserId_PerformedAt",
                table: "AuditEntries",
                columns: new[] { "UserId", "PerformedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_EventId",
                table: "AuditEntries");

            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_ResourceId_ResourceType",
                table: "AuditEntries");

            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_TenantId_PerformedAt",
                table: "AuditEntries");

            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_UserId_PerformedAt",
                table: "AuditEntries");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "AuditEntries");
        }
    }
}
