using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeVersionRestoreLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RestoredFromVersionId",
                table: "RecipeVersions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeVersions_WorkspaceId_RestoredFromVersionId",
                table: "RecipeVersions",
                columns: new[] { "WorkspaceId", "RestoredFromVersionId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecipeVersions_RestoredFrom_NotSelf",
                table: "RecipeVersions",
                sql: "RestoredFromVersionId IS NULL OR RestoredFromVersionId <> Id");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecipeVersions_RestoredFrom_Source",
                table: "RecipeVersions",
                sql: "(RestoredFromVersionId IS NULL AND Source <> 3) OR (RestoredFromVersionId IS NOT NULL AND Source = 3)");

            migrationBuilder.AddForeignKey(
                name: "FK_RecipeVersions_RecipeVersions_WorkspaceId_RestoredFromVersionId",
                table: "RecipeVersions",
                columns: new[] { "WorkspaceId", "RestoredFromVersionId" },
                principalTable: "RecipeVersions",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RecipeVersions_RecipeVersions_WorkspaceId_RestoredFromVersionId",
                table: "RecipeVersions");

            migrationBuilder.DropIndex(
                name: "IX_RecipeVersions_WorkspaceId_RestoredFromVersionId",
                table: "RecipeVersions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecipeVersions_RestoredFrom_NotSelf",
                table: "RecipeVersions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecipeVersions_RestoredFrom_Source",
                table: "RecipeVersions");

            migrationBuilder.DropColumn(
                name: "RestoredFromVersionId",
                table: "RecipeVersions");
        }
    }
}
