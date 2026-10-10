using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceDefaultMeasurementSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultMeasurementSystem",
                table: "Workspaces",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Workspaces_DefaultMeasurementSystem",
                table: "Workspaces",
                sql: "DefaultMeasurementSystem IN (1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Workspaces_DefaultMeasurementSystem",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "DefaultMeasurementSystem",
                table: "Workspaces");
        }
    }
}
