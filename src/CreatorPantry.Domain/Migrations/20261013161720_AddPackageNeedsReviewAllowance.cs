using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddPackageNeedsReviewAllowance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NeedsReviewAllowedAt",
                table: "RecipeContentPackageLinks",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "NeedsReviewAllowedByMembershipId",
                table: "RecipeContentPackageLinks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecipeContentPackageLinks_NeedsReviewAllowed_Paired",
                table: "RecipeContentPackageLinks",
                sql: "(NeedsReviewAllowedAt IS NULL AND NeedsReviewAllowedByMembershipId IS NULL) OR (NeedsReviewAllowedAt IS NOT NULL AND NeedsReviewAllowedByMembershipId IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RecipeContentPackageLinks_NeedsReviewAllowed_Paired",
                table: "RecipeContentPackageLinks");

            migrationBuilder.DropColumn(
                name: "NeedsReviewAllowedAt",
                table: "RecipeContentPackageLinks");

            migrationBuilder.DropColumn(
                name: "NeedsReviewAllowedByMembershipId",
                table: "RecipeContentPackageLinks");
        }
    }
}
