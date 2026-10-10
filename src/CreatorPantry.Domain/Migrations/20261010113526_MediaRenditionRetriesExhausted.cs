using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class MediaRenditionRetriesExhausted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaRenditions_Reason_Declared",
                table: "MediaRenditions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaRenditions_Reason_Declared",
                table: "MediaRenditions",
                sql: "NotCompressedReason IS NULL OR NotCompressedReason BETWEEN 1 AND 7");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaRenditions_Reason_Declared",
                table: "MediaRenditions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaRenditions_Reason_Declared",
                table: "MediaRenditions",
                sql: "NotCompressedReason IS NULL OR NotCompressedReason BETWEEN 1 AND 6");
        }
    }
}
