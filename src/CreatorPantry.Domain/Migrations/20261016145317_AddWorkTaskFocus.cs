using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkTaskFocus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkTaskFocuses",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssigneeMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FocusDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    WorkTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTaskFocuses", x => new { x.WorkspaceId, x.AssigneeMembershipId, x.FocusDate, x.Position });
                    table.CheckConstraint("CK_WorkTaskFocuses_Position_Range", "Position >= 1 AND Position <= 3");
                    table.ForeignKey(
                        name: "FK_WorkTaskFocuses_WorkTasks_WorkspaceId_WorkTaskId",
                        columns: x => new { x.WorkspaceId, x.WorkTaskId },
                        principalTable: "WorkTasks",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskFocuses_WorkspaceId_WorkTaskId",
                table: "WorkTaskFocuses",
                columns: new[] { "WorkspaceId", "WorkTaskId" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkTaskFocuses_Workspace_Assignee_Date_Task",
                table: "WorkTaskFocuses",
                columns: new[] { "WorkspaceId", "AssigneeMembershipId", "FocusDate", "WorkTaskId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkTaskFocuses");
        }
    }
}
