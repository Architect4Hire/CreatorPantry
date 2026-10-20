using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddReferenceSourceLicense : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LicenseCode",
                table: "ReferenceSources",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LicenseUrl",
                table: "ReferenceSources",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReferenceSources_LicenseUrl_RequiresLicenseCode",
                table: "ReferenceSources",
                sql: "LicenseUrl IS NULL OR LicenseCode IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReferenceSources_LicenseUrl_RequiresLicenseCode",
                table: "ReferenceSources");

            migrationBuilder.DropColumn(
                name: "LicenseCode",
                table: "ReferenceSources");

            migrationBuilder.DropColumn(
                name: "LicenseUrl",
                table: "ReferenceSources");
        }
    }
}
