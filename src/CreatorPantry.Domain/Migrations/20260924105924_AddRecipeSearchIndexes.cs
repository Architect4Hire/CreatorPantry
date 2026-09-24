using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_RecipeVersions_Workspace_Recipe_VersionNumber",
                table: "RecipeVersions");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_Workspace_Status_UpdatedAt",
                table: "Recipes");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_Workspace_Title",
                table: "Recipes");

            migrationBuilder.DropIndex(
                name: "IX_RecipeIngredients_WorkspaceId_RecipeId_RecipeIngredientGroupId",
                table: "RecipeIngredients");

            migrationBuilder.CreateIndex(
                name: "UX_RecipeVersions_Workspace_Recipe_VersionNumber",
                table: "RecipeVersions",
                columns: new[] { "WorkspaceId", "RecipeId", "VersionNumber" },
                unique: true)
                .Annotation("SqlServer:Include", new[] { "Readiness" });

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_Workspace_Status_UpdatedAt",
                table: "Recipes",
                columns: new[] { "WorkspaceId", "Status", "UpdatedAt", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_Workspace_Title",
                table: "Recipes",
                columns: new[] { "WorkspaceId", "Title", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_Workspace_UpdatedAt",
                table: "Recipes",
                columns: new[] { "WorkspaceId", "UpdatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredients_WorkspaceId_RecipeId_RecipeIngredientGroupId",
                table: "RecipeIngredients",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeIngredientGroupId" })
                .Annotation("SqlServer:Include", new[] { "MatchStatus" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_RecipeVersions_Workspace_Recipe_VersionNumber",
                table: "RecipeVersions");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_Workspace_Status_UpdatedAt",
                table: "Recipes");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_Workspace_Title",
                table: "Recipes");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_Workspace_UpdatedAt",
                table: "Recipes");

            migrationBuilder.DropIndex(
                name: "IX_RecipeIngredients_WorkspaceId_RecipeId_RecipeIngredientGroupId",
                table: "RecipeIngredients");

            migrationBuilder.CreateIndex(
                name: "UX_RecipeVersions_Workspace_Recipe_VersionNumber",
                table: "RecipeVersions",
                columns: new[] { "WorkspaceId", "RecipeId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_Workspace_Status_UpdatedAt",
                table: "Recipes",
                columns: new[] { "WorkspaceId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_Workspace_Title",
                table: "Recipes",
                columns: new[] { "WorkspaceId", "Title" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredients_WorkspaceId_RecipeId_RecipeIngredientGroupId",
                table: "RecipeIngredients",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeIngredientGroupId" });
        }
    }
}
