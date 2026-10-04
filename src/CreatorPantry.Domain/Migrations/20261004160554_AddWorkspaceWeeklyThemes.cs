using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceWeeklyThemes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkspaceWeeklyThemes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RetiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceWeeklyThemes", x => x.Id);
                    table.CheckConstraint("CK_WorkspaceWeeklyThemes_Day_Range", "Day >= 0 AND Day <= 6");
                    table.CheckConstraint("CK_WorkspaceWeeklyThemes_DisplayName_NotBlank", "trim(DisplayName) <> ''");
                    table.CheckConstraint("CK_WorkspaceWeeklyThemes_Key_NotBlank", "trim([Key]) <> ''");
                    table.CheckConstraint("CK_WorkspaceWeeklyThemes_Revision_Positive", "Revision >= 1");
                    table.ForeignKey(
                        name: "FK_WorkspaceWeeklyThemes_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_WorkspaceWeeklyThemes_Workspace_Day",
                table: "WorkspaceWeeklyThemes",
                columns: new[] { "WorkspaceId", "Day" },
                unique: true,
                filter: "RetiredAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_WorkspaceWeeklyThemes_Workspace_Key",
                table: "WorkspaceWeeklyThemes",
                columns: new[] { "WorkspaceId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkspaceWeeklyThemes");
        }
    }
}
