using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddPackageNeedsReviewAllowedReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RecipeContentPackageLinks_NeedsReviewAllowed_Paired",
                table: "RecipeContentPackageLinks");

            migrationBuilder.AddColumn<string>(
                name: "NeedsReviewAllowedReasons",
                table: "RecipeContentPackageLinks",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecipeContentPackageLinks_NeedsReviewAllowed_Paired",
                table: "RecipeContentPackageLinks",
                sql: "(NeedsReviewAllowedAt IS NULL AND NeedsReviewAllowedByMembershipId IS NULL AND NeedsReviewAllowedReasons IS NULL) OR (NeedsReviewAllowedAt IS NOT NULL AND NeedsReviewAllowedByMembershipId IS NOT NULL AND NeedsReviewAllowedReasons IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RecipeContentPackageLinks_NeedsReviewAllowed_Paired",
                table: "RecipeContentPackageLinks");

            migrationBuilder.DropColumn(
                name: "NeedsReviewAllowedReasons",
                table: "RecipeContentPackageLinks");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecipeContentPackageLinks_NeedsReviewAllowed_Paired",
                table: "RecipeContentPackageLinks",
                sql: "(NeedsReviewAllowedAt IS NULL AND NeedsReviewAllowedByMembershipId IS NULL) OR (NeedsReviewAllowedAt IS NOT NULL AND NeedsReviewAllowedByMembershipId IS NOT NULL)");
        }
    }
}
