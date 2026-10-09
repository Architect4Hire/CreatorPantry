using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class CreativeContextReferencePurpose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_CreativeContextReferences_Context_GeneratedImage",
                table: "CreativeContextReferences");

            migrationBuilder.DropIndex(
                name: "UX_CreativeContextReferences_Context_MediaAsset",
                table: "CreativeContextReferences");

            migrationBuilder.AddColumn<int>(
                name: "Purpose",
                table: "CreativeContextReferences",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "UX_CreativeContextReferences_Context_GeneratedImage",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "CreativeContextId", "GeneratedImageId", "Purpose" },
                unique: true,
                filter: "GeneratedImageId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CreativeContextReferences_Context_MediaAsset",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "CreativeContextId", "MediaAssetId", "Purpose" },
                unique: true,
                filter: "MediaAssetId IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CreativeContextReferences_Purpose_Range",
                table: "CreativeContextReferences",
                sql: "Purpose >= 1 AND Purpose <= 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_CreativeContextReferences_Context_GeneratedImage",
                table: "CreativeContextReferences");

            migrationBuilder.DropIndex(
                name: "UX_CreativeContextReferences_Context_MediaAsset",
                table: "CreativeContextReferences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CreativeContextReferences_Purpose_Range",
                table: "CreativeContextReferences");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "CreativeContextReferences");

            migrationBuilder.CreateIndex(
                name: "UX_CreativeContextReferences_Context_GeneratedImage",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "CreativeContextId", "GeneratedImageId" },
                unique: true,
                filter: "GeneratedImageId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CreativeContextReferences_Context_MediaAsset",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "CreativeContextId", "MediaAssetId" },
                unique: true,
                filter: "MediaAssetId IS NOT NULL");
        }
    }
}
