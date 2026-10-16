using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkTaskOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "WorkTasks",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "UX_WorkTasks_Workspace_Owner_WorkflowStep",
                table: "WorkTasks",
                columns: new[] { "WorkspaceId", "OwnerMembershipId", "WorkflowRunId", "WorkflowStepKey" },
                unique: true,
                filter: "Origin = 2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkTasks_Origin_Range",
                table: "WorkTasks",
                sql: "Origin >= 1 AND Origin <= 2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkTasks_WorkflowOrigin_WithStep",
                table: "WorkTasks",
                sql: "Origin <> 2 OR (WorkflowRunId IS NOT NULL AND WorkflowStepKey IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_WorkTasks_Workspace_Owner_WorkflowStep",
                table: "WorkTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkTasks_Origin_Range",
                table: "WorkTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkTasks_WorkflowOrigin_WithStep",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "WorkTasks");
        }
    }
}
