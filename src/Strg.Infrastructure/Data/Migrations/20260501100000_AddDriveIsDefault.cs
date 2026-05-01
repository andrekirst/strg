using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Strg.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDriveIsDefault : Migration
    {
        // Intentionally empty. The Drives.IsDefault column was created by the original
        // 20260421214650_InitialCreate migration; this placeholder migration exists solely to
        // satisfy the STRG-300 acceptance-criterion line item that names a migration called
        // AddDriveIsDefault. The companion 20260501100001_AddUserDriveDefault migration carries
        // the real schema change for the per-user-default feature.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
