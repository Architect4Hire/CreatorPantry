using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class CreativeContextWorkingBrief : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BriefSource",
                table: "CreativeContexts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkingBrief",
                table: "CreativeContexts",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_CreativeContexts_BriefSource_Range",
                table: "CreativeContexts",
                sql: "BriefSource IS NULL OR (BriefSource >= 1 AND BriefSource <= 3)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CreativeContexts_WorkingBrief_NotBlank",
                table: "CreativeContexts",
                sql: "WorkingBrief IS NULL OR trim(WorkingBrief) <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CreativeContexts_BriefSource_Range",
                table: "CreativeContexts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CreativeContexts_WorkingBrief_NotBlank",
                table: "CreativeContexts");

            migrationBuilder.DropColumn(
                name: "BriefSource",
                table: "CreativeContexts");

            migrationBuilder.DropColumn(
                name: "WorkingBrief",
                table: "CreativeContexts");
        }
    }
}
