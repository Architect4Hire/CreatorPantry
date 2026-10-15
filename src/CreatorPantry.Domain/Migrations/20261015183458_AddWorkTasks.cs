using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssigneeMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    ScheduledDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PlanningWeekStart = table.Column<DateOnly>(type: "date", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EstimateMinutes = table.Column<int>(type: "int", nullable: true),
                    SortRank = table.Column<long>(type: "bigint", nullable: false),
                    WorkTaskTemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecurrenceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkflowStepKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContentCardId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompletedFromStatus = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTasks", x => x.Id);
                    table.UniqueConstraint("AK_WorkTasks_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_WorkTasks_Completed_Recorded", "(Status = 4 AND CompletedAt IS NOT NULL AND CompletedByMembershipId IS NOT NULL AND CompletedFromStatus IS NOT NULL) OR (Status <> 4 AND CompletedAt IS NULL AND CompletedByMembershipId IS NULL AND CompletedFromStatus IS NULL)");
                    table.CheckConstraint("CK_WorkTasks_CompletedFrom_Range", "CompletedFromStatus IS NULL OR (CompletedFromStatus >= 1 AND CompletedFromStatus <= 3)");
                    table.CheckConstraint("CK_WorkTasks_Description_NotBlank", "Description IS NULL OR trim(Description) <> ''");
                    table.CheckConstraint("CK_WorkTasks_Estimate_Range", "EstimateMinutes IS NULL OR (EstimateMinutes >= 1 AND EstimateMinutes <= 1440)");
                    table.CheckConstraint("CK_WorkTasks_Priority_Range", "Priority >= 1 AND Priority <= 3");
                    table.CheckConstraint("CK_WorkTasks_Recurrence_Whole", "(WorkTaskTemplateId IS NULL AND RecurrenceDate IS NULL) OR (WorkTaskTemplateId IS NOT NULL AND RecurrenceDate IS NOT NULL)");
                    table.CheckConstraint("CK_WorkTasks_ScheduledDate_InWeek", "ScheduledDate IS NULL OR (PlanningWeekStart IS NOT NULL AND ScheduledDate >= PlanningWeekStart)");
                    table.CheckConstraint("CK_WorkTasks_Status_Range", "Status >= 1 AND Status <= 4");
                    table.CheckConstraint("CK_WorkTasks_Title_NotBlank", "trim(Title) <> ''");
                    table.CheckConstraint("CK_WorkTasks_WorkflowStep_WithRun", "WorkflowStepKey IS NULL OR (WorkflowRunId IS NOT NULL AND trim(WorkflowStepKey) <> '')");
                    table.ForeignKey(
                        name: "FK_WorkTasks_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkTaskLabels",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkTaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceTagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTaskLabels", x => new { x.WorkspaceId, x.WorkTaskId, x.WorkspaceTagId });
                    table.ForeignKey(
                        name: "FK_WorkTaskLabels_WorkTasks_WorkspaceId_WorkTaskId",
                        columns: x => new { x.WorkspaceId, x.WorkTaskId },
                        principalTable: "WorkTasks",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkTaskLabels_WorkspaceTags_WorkspaceId_WorkspaceTagId",
                        columns: x => new { x.WorkspaceId, x.WorkspaceTagId },
                        principalTable: "WorkspaceTags",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskLabels_Workspace_Tag_Task",
                table: "WorkTaskLabels",
                columns: new[] { "WorkspaceId", "WorkspaceTagId", "WorkTaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_Workspace_Assignee_Completed",
                table: "WorkTasks",
                columns: new[] { "WorkspaceId", "AssigneeMembershipId", "CompletedAt" },
                filter: "CompletedAt IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_Workspace_Assignee_Due",
                table: "WorkTasks",
                columns: new[] { "WorkspaceId", "AssigneeMembershipId", "DueDate" },
                filter: "DueDate IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_Workspace_Assignee_Status_Day_Rank",
                table: "WorkTasks",
                columns: new[] { "WorkspaceId", "AssigneeMembershipId", "Status", "ScheduledDate", "SortRank", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_Workspace_Assignee_Week_Status",
                table: "WorkTasks",
                columns: new[] { "WorkspaceId", "AssigneeMembershipId", "PlanningWeekStart", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_Workspace_Owner_Status",
                table: "WorkTasks",
                columns: new[] { "WorkspaceId", "OwnerMembershipId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_Workspace_Run_Step",
                table: "WorkTasks",
                columns: new[] { "WorkspaceId", "WorkflowRunId", "WorkflowStepKey" },
                filter: "WorkflowRunId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_WorkTasks_Workspace_Template_Occurrence",
                table: "WorkTasks",
                columns: new[] { "WorkspaceId", "WorkTaskTemplateId", "RecurrenceDate" },
                unique: true,
                filter: "WorkTaskTemplateId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkTaskLabels");

            migrationBuilder.DropTable(
                name: "WorkTasks");
        }
    }
}
