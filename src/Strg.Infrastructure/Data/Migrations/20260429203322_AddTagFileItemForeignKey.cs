using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Strg.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTagFileItemForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_Tags_Files_FileId",
                table: "Tags",
                column: "FileId",
                principalTable: "Files",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tags_Files_FileId",
                table: "Tags");
        }
    }
}
