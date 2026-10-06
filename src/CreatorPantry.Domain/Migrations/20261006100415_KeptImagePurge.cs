using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class KeptImagePurge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GeneratedImages_Status_ObjectDeletedAt",
                table: "GeneratedImages");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedImages_Status_ObjectDeletedAt",
                table: "GeneratedImages",
                columns: new[] { "Status", "ObjectDeletedAt" },
                filter: "Status IN (2, 3, 4) AND ObjectDeletedAt IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GeneratedImages_Status_ObjectDeletedAt",
                table: "GeneratedImages");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedImages_Status_ObjectDeletedAt",
                table: "GeneratedImages",
                columns: new[] { "Status", "ObjectDeletedAt" },
                filter: "Status IN (3, 4) AND ObjectDeletedAt IS NULL");
        }
    }
}
