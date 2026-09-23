using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddIngredientDensityReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReferenceSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Citation = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceSources", x => x.Id);
                    table.UniqueConstraint("AK_ReferenceSources_Id_Kind", x => new { x.Id, x.Kind });
                });

            migrationBuilder.CreateTable(
                name: "IngredientDensityReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IngredientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceSourceKind = table.Column<int>(type: "int", nullable: false),
                    MassQuantity = table.Column<decimal>(type: "decimal(28,12)", nullable: false),
                    MassUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MassUnitDimension = table.Column<int>(type: "int", nullable: false),
                    VolumeQuantity = table.Column<decimal>(type: "decimal(28,12)", nullable: false),
                    VolumeUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VolumeUnitDimension = table.Column<int>(type: "int", nullable: false),
                    ConditionNote = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    NormalizedCondition = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayPrecision = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ReviewStatus = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngredientDensityReferences", x => x.Id);
                    table.CheckConstraint("CK_IngredientDensityReferences_AiEstimate_NotApproved", "NOT (ReferenceSourceKind = 5 AND ReviewStatus = 10)");
                    table.CheckConstraint("CK_IngredientDensityReferences_DisplayPrecision", "DisplayPrecision BETWEEN 0 AND 6");
                    table.CheckConstraint("CK_IngredientDensityReferences_MassQuantity_Positive", "MassQuantity > 0");
                    table.CheckConstraint("CK_IngredientDensityReferences_MassUnit_Dimension", "MassUnitDimension = 0");
                    table.CheckConstraint("CK_IngredientDensityReferences_VolumeQuantity_Positive", "VolumeQuantity > 0");
                    table.CheckConstraint("CK_IngredientDensityReferences_VolumeUnit_Dimension", "VolumeUnitDimension = 1");
                    table.ForeignKey(
                        name: "FK_IngredientDensityReferences_Ingredients_IngredientId",
                        column: x => x.IngredientId,
                        principalTable: "Ingredients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IngredientDensityReferences_MeasurementUnits_MassUnitId_MassUnitDimension",
                        columns: x => new { x.MassUnitId, x.MassUnitDimension },
                        principalTable: "MeasurementUnits",
                        principalColumns: new[] { "Id", "Dimension" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IngredientDensityReferences_MeasurementUnits_VolumeUnitId_VolumeUnitDimension",
                        columns: x => new { x.VolumeUnitId, x.VolumeUnitDimension },
                        principalTable: "MeasurementUnits",
                        principalColumns: new[] { "Id", "Dimension" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IngredientDensityReferences_ReferenceSources_ReferenceSourceId_ReferenceSourceKind",
                        columns: x => new { x.ReferenceSourceId, x.ReferenceSourceKind },
                        principalTable: "ReferenceSources",
                        principalColumns: new[] { "Id", "Kind" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IngredientDensityReferences_Ingredient_Status_Effective",
                table: "IngredientDensityReferences",
                columns: new[] { "IngredientId", "ReviewStatus", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_IngredientDensityReferences_MassUnitId_MassUnitDimension",
                table: "IngredientDensityReferences",
                columns: new[] { "MassUnitId", "MassUnitDimension" });

            migrationBuilder.CreateIndex(
                name: "IX_IngredientDensityReferences_ReferenceSourceId_ReferenceSourceKind",
                table: "IngredientDensityReferences",
                columns: new[] { "ReferenceSourceId", "ReferenceSourceKind" });

            migrationBuilder.CreateIndex(
                name: "IX_IngredientDensityReferences_VolumeUnitId_VolumeUnitDimension",
                table: "IngredientDensityReferences",
                columns: new[] { "VolumeUnitId", "VolumeUnitDimension" });

            migrationBuilder.CreateIndex(
                name: "UX_IngredientDensityReferences_Ingredient_Condition_Source_Effective",
                table: "IngredientDensityReferences",
                columns: new[] { "IngredientId", "NormalizedCondition", "ReferenceSourceId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ReferenceSources_Code",
                table: "ReferenceSources",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IngredientDensityReferences");

            migrationBuilder.DropTable(
                name: "ReferenceSources");
        }
    }
}
