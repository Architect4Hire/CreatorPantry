using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddContentCardOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "ContentCards",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "UX_ContentCards_Workspace_PipelineContext",
                table: "ContentCards",
                columns: new[] { "WorkspaceId", "CreativeContextId" },
                unique: true,
                filter: "Origin = 2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContentCards_Origin_Range",
                table: "ContentCards",
                sql: "Origin >= 1 AND Origin <= 2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContentCards_PipelineOrigin_WithContext",
                table: "ContentCards",
                sql: "Origin <> 2 OR CreativeContextId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_ContentCards_Workspace_PipelineContext",
                table: "ContentCards");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContentCards_Origin_Range",
                table: "ContentCards");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContentCards_PipelineOrigin_WithContext",
                table: "ContentCards");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "ContentCards");
        }
    }
}
