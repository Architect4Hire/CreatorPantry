using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeDuplicationLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DuplicatedFromVersionId",
                table: "Recipes",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_WorkspaceId_DuplicatedFromVersionId",
                table: "Recipes",
                columns: new[] { "WorkspaceId", "DuplicatedFromVersionId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Recipes_RecipeVersions_WorkspaceId_DuplicatedFromVersionId",
                table: "Recipes",
                columns: new[] { "WorkspaceId", "DuplicatedFromVersionId" },
                principalTable: "RecipeVersions",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Recipes_RecipeVersions_WorkspaceId_DuplicatedFromVersionId",
                table: "Recipes");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_WorkspaceId_DuplicatedFromVersionId",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "DuplicatedFromVersionId",
                table: "Recipes");
        }
    }
}
