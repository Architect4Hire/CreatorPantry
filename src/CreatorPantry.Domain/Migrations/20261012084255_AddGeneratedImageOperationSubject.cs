using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneratedImageOperationSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreativeContextId",
                table: "GeneratedImageOperations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecipeId",
                table: "GeneratedImageOperations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecipeVersionId",
                table: "GeneratedImageOperations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedImageOperations_WorkspaceId_CreativeContextId",
                table: "GeneratedImageOperations",
                columns: new[] { "WorkspaceId", "CreativeContextId" });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedImageOperations_WorkspaceId_RecipeId_RecipeVersionId",
                table: "GeneratedImageOperations",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeVersionId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_GeneratedImageOperations_RecipeVersion_NeedsRecipe",
                table: "GeneratedImageOperations",
                sql: "RecipeVersionId IS NULL OR RecipeId IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_GeneratedImageOperations_CreativeContexts_WorkspaceId_CreativeContextId",
                table: "GeneratedImageOperations",
                columns: new[] { "WorkspaceId", "CreativeContextId" },
                principalTable: "CreativeContexts",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GeneratedImageOperations_RecipeVersions_WorkspaceId_RecipeId_RecipeVersionId",
                table: "GeneratedImageOperations",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeVersionId" },
                principalTable: "RecipeVersions",
                principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GeneratedImageOperations_Recipes_WorkspaceId_RecipeId",
                table: "GeneratedImageOperations",
                columns: new[] { "WorkspaceId", "RecipeId" },
                principalTable: "Recipes",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GeneratedImageOperations_CreativeContexts_WorkspaceId_CreativeContextId",
                table: "GeneratedImageOperations");

            migrationBuilder.DropForeignKey(
                name: "FK_GeneratedImageOperations_RecipeVersions_WorkspaceId_RecipeId_RecipeVersionId",
                table: "GeneratedImageOperations");

            migrationBuilder.DropForeignKey(
                name: "FK_GeneratedImageOperations_Recipes_WorkspaceId_RecipeId",
                table: "GeneratedImageOperations");

            migrationBuilder.DropIndex(
                name: "IX_GeneratedImageOperations_WorkspaceId_CreativeContextId",
                table: "GeneratedImageOperations");

            migrationBuilder.DropIndex(
                name: "IX_GeneratedImageOperations_WorkspaceId_RecipeId_RecipeVersionId",
                table: "GeneratedImageOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GeneratedImageOperations_RecipeVersion_NeedsRecipe",
                table: "GeneratedImageOperations");

            migrationBuilder.DropColumn(
                name: "CreativeContextId",
                table: "GeneratedImageOperations");

            migrationBuilder.DropColumn(
                name: "RecipeId",
                table: "GeneratedImageOperations");

            migrationBuilder.DropColumn(
                name: "RecipeVersionId",
                table: "GeneratedImageOperations");
        }
    }
}
