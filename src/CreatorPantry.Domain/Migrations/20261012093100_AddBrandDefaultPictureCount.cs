using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandDefaultPictureCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultPictureCount",
                table: "BrandProfiles",
                type: "int",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_BrandProfiles_DefaultPictureCount_Range",
                table: "BrandProfiles",
                sql: "DefaultPictureCount IS NULL OR (DefaultPictureCount >= 1 AND DefaultPictureCount <= 4)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BrandProfiles_DefaultPictureCount_Range",
                table: "BrandProfiles");

            migrationBuilder.DropColumn(
                name: "DefaultPictureCount",
                table: "BrandProfiles");
        }
    }
}
