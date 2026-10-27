using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneratedImageQualityClass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QualityClass",
                table: "GeneratedImageOperations",
                type: "int",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_GeneratedImageOperations_QualityClass_Declared",
                table: "GeneratedImageOperations",
                sql: "QualityClass IS NULL OR (QualityClass >= 1 AND QualityClass <= 5)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_GeneratedImageOperations_QualityClass_Declared",
                table: "GeneratedImageOperations");

            migrationBuilder.DropColumn(
                name: "QualityClass",
                table: "GeneratedImageOperations");
        }
    }
}
