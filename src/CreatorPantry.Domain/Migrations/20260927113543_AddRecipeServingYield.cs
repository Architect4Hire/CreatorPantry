using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeServingYield : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ServingCount",
                table: "Recipes",
                type: "decimal(28,12)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ServingSize",
                table: "Recipes",
                type: "decimal(28,12)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Recipes_ServingCount_Positive",
                table: "Recipes",
                sql: "ServingCount IS NULL OR ServingCount > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Recipes_ServingSize_Positive",
                table: "Recipes",
                sql: "ServingSize IS NULL OR ServingSize > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Recipes_ServingSize_RequiresYieldUnit",
                table: "Recipes",
                sql: "ServingSize IS NULL OR YieldUnitId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Recipes_ServingCount_Positive",
                table: "Recipes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Recipes_ServingSize_Positive",
                table: "Recipes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Recipes_ServingSize_RequiresYieldUnit",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "ServingCount",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "ServingSize",
                table: "Recipes");
        }
    }
}
