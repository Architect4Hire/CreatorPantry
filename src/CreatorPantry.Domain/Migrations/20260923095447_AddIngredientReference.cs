using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddIngredientReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_MeasurementUnits_Id_Dimension",
                table: "MeasurementUnits",
                columns: new[] { "Id", "Dimension" });

            migrationBuilder.CreateTable(
                name: "FoodCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FoodCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Ingredients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SearchText = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    FoodCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultCountUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultCountUnitDimension = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ingredients", x => x.Id);
                    table.CheckConstraint("CK_Ingredients_DefaultCountUnit_Dimension", "(DefaultCountUnitId IS NULL AND DefaultCountUnitDimension IS NULL) OR (DefaultCountUnitId IS NOT NULL AND DefaultCountUnitDimension = 2)");
                    table.ForeignKey(
                        name: "FK_Ingredients_FoodCategories_FoodCategoryId",
                        column: x => x.FoodCategoryId,
                        principalTable: "FoodCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Ingredients_MeasurementUnits_DefaultCountUnitId_DefaultCountUnitDimension",
                        columns: x => new { x.DefaultCountUnitId, x.DefaultCountUnitDimension },
                        principalTable: "MeasurementUnits",
                        principalColumns: new[] { "Id", "Dimension" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IngredientAliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IngredientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    NormalizedAlias = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngredientAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngredientAliases_Ingredients_IngredientId",
                        column: x => x.IngredientId,
                        principalTable: "Ingredients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_FoodCategories_Code",
                table: "FoodCategories",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngredientAliases_IngredientId",
                table: "IngredientAliases",
                column: "IngredientId");

            migrationBuilder.CreateIndex(
                name: "UX_IngredientAliases_NormalizedAlias",
                table: "IngredientAliases",
                column: "NormalizedAlias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ingredients_Category_IsActive",
                table: "Ingredients",
                columns: new[] { "FoodCategoryId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Ingredients_DefaultCountUnitId_DefaultCountUnitDimension",
                table: "Ingredients",
                columns: new[] { "DefaultCountUnitId", "DefaultCountUnitDimension" });

            migrationBuilder.CreateIndex(
                name: "UX_Ingredients_NormalizedName",
                table: "Ingredients",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IngredientAliases");

            migrationBuilder.DropTable(
                name: "Ingredients");

            migrationBuilder.DropTable(
                name: "FoodCategories");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_MeasurementUnits_Id_Dimension",
                table: "MeasurementUnits");
        }
    }
}
