using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefinitionKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DefinitionVersion = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CurrentStepKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    StepCount = table.Column<int>(type: "int", nullable: false),
                    CompletedStepCount = table.Column<int>(type: "int", nullable: false),
                    SkippedStepCount = table.Column<int>(type: "int", nullable: false),
                    LastRoute = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AbandonedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRuns", x => x.Id);
                    table.UniqueConstraint("AK_WorkflowRuns_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_WorkflowRuns_AbandonedAt_Status", "(Status = 3 AND AbandonedAt IS NOT NULL) OR (Status <> 3 AND AbandonedAt IS NULL)");
                    table.CheckConstraint("CK_WorkflowRuns_CompletedAt_Status", "(Status = 2 AND CompletedAt IS NOT NULL) OR (Status <> 2 AND CompletedAt IS NULL)");
                    table.CheckConstraint("CK_WorkflowRuns_CurrentStep_NotBlank", "CurrentStepKey IS NULL OR trim(CurrentStepKey) <> ''");
                    table.CheckConstraint("CK_WorkflowRuns_CurrentStep_Status", "(Status = 1 AND CurrentStepKey IS NOT NULL) OR (Status = 2 AND CurrentStepKey IS NULL) OR Status = 3");
                    table.CheckConstraint("CK_WorkflowRuns_DefinitionKey_NotBlank", "trim(DefinitionKey) <> ''");
                    table.CheckConstraint("CK_WorkflowRuns_DefinitionVersion_Positive", "DefinitionVersion >= 1");
                    table.CheckConstraint("CK_WorkflowRuns_LastRoute_Relative", "LastRoute IS NULL OR (trim(LastRoute) <> '' AND LastRoute NOT LIKE '/%' AND LastRoute NOT LIKE '%:%' AND LastRoute NOT LIKE '%\\%' AND LastRoute NOT LIKE '%..%')");
                    table.CheckConstraint("CK_WorkflowRuns_Progress_Bounded", "CompletedStepCount >= 0 AND SkippedStepCount >= 0 AND CompletedStepCount + SkippedStepCount <= StepCount");
                    table.CheckConstraint("CK_WorkflowRuns_Status_Range", "Status >= 1 AND Status <= 3");
                    table.CheckConstraint("CK_WorkflowRuns_StepCount_Positive", "StepCount >= 1");
                    table.CheckConstraint("CK_WorkflowRuns_Title_NotBlank", "trim(Title) <> ''");
                    table.ForeignKey(
                        name: "FK_WorkflowRuns_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowRunLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceKind = table.Column<int>(type: "int", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LinkedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LinkedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRunLinks", x => x.Id);
                    table.CheckConstraint("CK_WorkflowRunLinks_ResourceKind_Range", "ResourceKind >= 1 AND ResourceKind <= 1");
                    table.CheckConstraint("CK_WorkflowRunLinks_StepKey_NotBlank", "StepKey IS NULL OR trim(StepKey) <> ''");
                    table.ForeignKey(
                        name: "FK_WorkflowRunLinks_WorkflowRuns_WorkspaceId_WorkflowRunId",
                        columns: x => new { x.WorkspaceId, x.WorkflowRunId },
                        principalTable: "WorkflowRuns",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowStepRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SkippedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SkippedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SkipReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DraftResourceKind = table.Column<int>(type: "int", nullable: true),
                    DraftResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DraftSavedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowStepRuns", x => x.Id);
                    table.CheckConstraint("CK_WorkflowStepRuns_Completed_Recorded", "(Status = 3 AND CompletedAt IS NOT NULL AND CompletedByMembershipId IS NOT NULL) OR (Status <> 3 AND CompletedAt IS NULL AND CompletedByMembershipId IS NULL)");
                    table.CheckConstraint("CK_WorkflowStepRuns_Draft_Whole", "(DraftResourceKind IS NULL AND DraftResourceId IS NULL AND DraftSavedAt IS NULL) OR (DraftResourceKind IS NOT NULL AND DraftResourceId IS NOT NULL AND DraftSavedAt IS NOT NULL)");
                    table.CheckConstraint("CK_WorkflowStepRuns_DraftKind_Range", "DraftResourceKind IS NULL OR (DraftResourceKind >= 1 AND DraftResourceKind <= 1)");
                    table.CheckConstraint("CK_WorkflowStepRuns_NotStarted_NoStart", "Status <> 1 OR StartedAt IS NULL");
                    table.CheckConstraint("CK_WorkflowStepRuns_Skipped_Recorded", "(Status = 4 AND SkippedAt IS NOT NULL AND SkippedByMembershipId IS NOT NULL) OR (Status <> 4 AND SkippedAt IS NULL AND SkippedByMembershipId IS NULL AND SkipReason IS NULL)");
                    table.CheckConstraint("CK_WorkflowStepRuns_SkipReason_NotBlank", "SkipReason IS NULL OR trim(SkipReason) <> ''");
                    table.CheckConstraint("CK_WorkflowStepRuns_Status_Range", "Status >= 1 AND Status <= 4");
                    table.CheckConstraint("CK_WorkflowStepRuns_StepKey_NotBlank", "trim(StepKey) <> ''");
                    table.ForeignKey(
                        name: "FK_WorkflowStepRuns_WorkflowRuns_WorkspaceId_WorkflowRunId",
                        columns: x => new { x.WorkspaceId, x.WorkflowRunId },
                        principalTable: "WorkflowRuns",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRunLinks_Workspace_Resource",
                table: "WorkflowRunLinks",
                columns: new[] { "WorkspaceId", "ResourceKind", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowRunLinks_Workspace_Run_Resource",
                table: "WorkflowRunLinks",
                columns: new[] { "WorkspaceId", "WorkflowRunId", "ResourceKind", "ResourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Workspace_Assignee_Status_Updated",
                table: "WorkflowRuns",
                columns: new[] { "WorkspaceId", "AssignedMembershipId", "Status", "UpdatedAt" },
                filter: "AssignedMembershipId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Workspace_Creator_Status_Updated",
                table: "WorkflowRuns",
                columns: new[] { "WorkspaceId", "CreatedByMembershipId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Workspace_Definition_Version",
                table: "WorkflowRuns",
                columns: new[] { "WorkspaceId", "DefinitionKey", "DefinitionVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Workspace_Status_Updated",
                table: "WorkflowRuns",
                columns: new[] { "WorkspaceId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowStepRuns_Workspace_Run_StepKey",
                table: "WorkflowStepRuns",
                columns: new[] { "WorkspaceId", "WorkflowRunId", "StepKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowRunLinks");

            migrationBuilder.DropTable(
                name: "WorkflowStepRuns");

            migrationBuilder.DropTable(
                name: "WorkflowRuns");
        }
    }
}
