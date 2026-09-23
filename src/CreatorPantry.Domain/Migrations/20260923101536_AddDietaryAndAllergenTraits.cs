using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddDietaryAndAllergenTraits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Allergens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Allergens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DietaryProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DietaryProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IngredientAllergenTraits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IngredientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllergenId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceSourceKind = table.Column<int>(type: "int", nullable: false),
                    Presence = table.Column<int>(type: "int", nullable: false),
                    EvidenceNote = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ReviewStatus = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngredientAllergenTraits", x => x.Id);
                    table.CheckConstraint("CK_IngredientAllergenTraits_Absence_RequiresVettedSource", "NOT (Presence = 30 AND ReferenceSourceKind IN (4, 5))");
                    table.CheckConstraint("CK_IngredientAllergenTraits_AiEstimate_NotApproved", "NOT (ReferenceSourceKind = 5 AND ReviewStatus = 10)");
                    table.CheckConstraint("CK_IngredientAllergenTraits_EvidenceNote_Required", "Presence NOT IN (20, 30) OR EvidenceNote <> ''");
                    table.ForeignKey(
                        name: "FK_IngredientAllergenTraits_Allergens_AllergenId",
                        column: x => x.AllergenId,
                        principalTable: "Allergens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IngredientAllergenTraits_Ingredients_IngredientId",
                        column: x => x.IngredientId,
                        principalTable: "Ingredients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IngredientAllergenTraits_ReferenceSources_ReferenceSourceId_ReferenceSourceKind",
                        columns: x => new { x.ReferenceSourceId, x.ReferenceSourceKind },
                        principalTable: "ReferenceSources",
                        principalColumns: new[] { "Id", "Kind" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IngredientDietaryTraits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IngredientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DietaryProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceSourceKind = table.Column<int>(type: "int", nullable: false),
                    Compatibility = table.Column<int>(type: "int", nullable: false),
                    EvidenceNote = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ReviewStatus = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngredientDietaryTraits", x => x.Id);
                    table.CheckConstraint("CK_IngredientDietaryTraits_AiEstimate_NotApproved", "NOT (ReferenceSourceKind = 5 AND ReviewStatus = 10)");
                    table.CheckConstraint("CK_IngredientDietaryTraits_EvidenceNote_Required", "Compatibility NOT IN (30) OR EvidenceNote <> ''");
                    table.ForeignKey(
                        name: "FK_IngredientDietaryTraits_DietaryProfiles_DietaryProfileId",
                        column: x => x.DietaryProfileId,
                        principalTable: "DietaryProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IngredientDietaryTraits_Ingredients_IngredientId",
                        column: x => x.IngredientId,
                        principalTable: "Ingredients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IngredientDietaryTraits_ReferenceSources_ReferenceSourceId_ReferenceSourceKind",
                        columns: x => new { x.ReferenceSourceId, x.ReferenceSourceKind },
                        principalTable: "ReferenceSources",
                        principalColumns: new[] { "Id", "Kind" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Allergens_Code",
                table: "Allergens",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DietaryProfiles_Code",
                table: "DietaryProfiles",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngredientAllergenTraits_AllergenId",
                table: "IngredientAllergenTraits",
                column: "AllergenId");

            migrationBuilder.CreateIndex(
                name: "IX_IngredientAllergenTraits_Ingredient_Status_Effective",
                table: "IngredientAllergenTraits",
                columns: new[] { "IngredientId", "ReviewStatus", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_IngredientAllergenTraits_ReferenceSourceId_ReferenceSourceKind",
                table: "IngredientAllergenTraits",
                columns: new[] { "ReferenceSourceId", "ReferenceSourceKind" });

            migrationBuilder.CreateIndex(
                name: "UX_IngredientAllergenTraits_Ingredient_Allergen_Source_Effective",
                table: "IngredientAllergenTraits",
                columns: new[] { "IngredientId", "AllergenId", "ReferenceSourceId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngredientDietaryTraits_DietaryProfileId",
                table: "IngredientDietaryTraits",
                column: "DietaryProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_IngredientDietaryTraits_Ingredient_Status_Effective",
                table: "IngredientDietaryTraits",
                columns: new[] { "IngredientId", "ReviewStatus", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_IngredientDietaryTraits_ReferenceSourceId_ReferenceSourceKind",
                table: "IngredientDietaryTraits",
                columns: new[] { "ReferenceSourceId", "ReferenceSourceKind" });

            migrationBuilder.CreateIndex(
                name: "UX_IngredientDietaryTraits_Ingredient_Profile_Source_Effective",
                table: "IngredientDietaryTraits",
                columns: new[] { "IngredientId", "DietaryProfileId", "ReferenceSourceId", "EffectiveFrom" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IngredientAllergenTraits");

            migrationBuilder.DropTable(
                name: "IngredientDietaryTraits");

            migrationBuilder.DropTable(
                name: "Allergens");

            migrationBuilder.DropTable(
                name: "DietaryProfiles");
        }
    }
}
