using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddMeasurementUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MeasurementUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PluralName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Abbreviation = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Dimension = table.Column<int>(type: "int", nullable: false),
                    System = table.Column<int>(type: "int", nullable: false),
                    BaseUnitFactor = table.Column<decimal>(type: "decimal(28,12)", nullable: true),
                    DisplayPrecision = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasurementUnits", x => x.Id);
                    table.CheckConstraint("CK_MeasurementUnits_DisplayPrecision", "DisplayPrecision BETWEEN 0 AND 6");
                    table.CheckConstraint("CK_MeasurementUnits_Factor_Dimension", "(Dimension IN (0, 1, 2) AND BaseUnitFactor IS NOT NULL) OR (Dimension IN (3, 4) AND BaseUnitFactor IS NULL)");
                    table.CheckConstraint("CK_MeasurementUnits_Factor_Positive", "BaseUnitFactor IS NULL OR BaseUnitFactor > 0");
                });

            migrationBuilder.CreateTable(
                name: "UnitAliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MeasurementUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NormalizedAlias = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnitAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UnitAliases_MeasurementUnits_MeasurementUnitId",
                        column: x => x.MeasurementUnitId,
                        principalTable: "MeasurementUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementUnits_Dimension_IsActive",
                table: "MeasurementUnits",
                columns: new[] { "Dimension", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "UX_MeasurementUnits_Code",
                table: "MeasurementUnits",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UnitAliases_MeasurementUnitId",
                table: "UnitAliases",
                column: "MeasurementUnitId");

            migrationBuilder.CreateIndex(
                name: "UX_UnitAliases_NormalizedAlias",
                table: "UnitAliases",
                column: "NormalizedAlias",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UnitAliases");

            migrationBuilder.DropTable(
                name: "MeasurementUnits");
        }
    }
}
