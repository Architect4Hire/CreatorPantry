using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeTestRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_RecipeVersions_Workspace_Recipe_Id",
                table: "RecipeVersions",
                columns: new[] { "WorkspaceId", "RecipeId", "Id" });

            migrationBuilder.CreateTable(
                name: "RecipeTestRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TestedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    Rating = table.Column<int>(type: "int", nullable: true),
                    EnvironmentNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EquipmentNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ActualYieldText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ActualYieldQuantity = table.Column<decimal>(type: "decimal(28,12)", nullable: true),
                    ActualYieldUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActualYieldUnitDimension = table.Column<int>(type: "int", nullable: true),
                    ActualPrepTimeMinutes = table.Column<int>(type: "int", nullable: true),
                    ActualCookTimeMinutes = table.Column<int>(type: "int", nullable: true),
                    ActualRestTimeMinutes = table.Column<int>(type: "int", nullable: true),
                    ActualTotalTimeMinutes = table.Column<int>(type: "int", nullable: true),
                    SummaryNotes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeTestRuns", x => x.Id);
                    table.UniqueConstraint("AK_RecipeTestRuns_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.UniqueConstraint("AK_RecipeTestRuns_Workspace_Recipe_Id", x => new { x.WorkspaceId, x.RecipeId, x.Id });
                    table.CheckConstraint("CK_RecipeTestRuns_ActualYield_Positive", "ActualYieldQuantity IS NULL OR ActualYieldQuantity > 0");
                    table.CheckConstraint("CK_RecipeTestRuns_ActualYieldUnit_Dimension", "(ActualYieldUnitId IS NULL AND ActualYieldUnitDimension IS NULL) OR (ActualYieldUnitId IS NOT NULL AND ActualYieldUnitDimension IS NOT NULL AND ActualYieldUnitDimension <> 3)");
                    table.CheckConstraint("CK_RecipeTestRuns_ActualYieldUnit_RequiresQuantity", "ActualYieldUnitId IS NULL OR ActualYieldQuantity IS NOT NULL");
                    table.CheckConstraint("CK_RecipeTestRuns_Rating_Range", "Rating IS NULL OR (Rating >= 1 AND Rating <= 5)");
                    table.CheckConstraint("CK_RecipeTestRuns_Times_Range", "(ActualPrepTimeMinutes IS NULL OR (ActualPrepTimeMinutes >= 0 AND ActualPrepTimeMinutes <= 525600)) AND (ActualCookTimeMinutes IS NULL OR (ActualCookTimeMinutes >= 0 AND ActualCookTimeMinutes <= 525600)) AND (ActualRestTimeMinutes IS NULL OR (ActualRestTimeMinutes >= 0 AND ActualRestTimeMinutes <= 525600)) AND (ActualTotalTimeMinutes IS NULL OR (ActualTotalTimeMinutes >= 0 AND ActualTotalTimeMinutes <= 525600))");
                    table.ForeignKey(
                        name: "FK_RecipeTestRuns_MeasurementUnits_ActualYieldUnitId_ActualYieldUnitDimension",
                        columns: x => new { x.ActualYieldUnitId, x.ActualYieldUnitDimension },
                        principalTable: "MeasurementUnits",
                        principalColumns: new[] { "Id", "Dimension" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeTestRuns_RecipeVersions_WorkspaceId_RecipeId_RecipeVersionId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.RecipeVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeTestRuns_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TestObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeTestRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestObservations", x => x.Id);
                    table.UniqueConstraint("AK_TestObservations_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_TestObservations_SortOrder_NonNegative", "SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_TestObservations_RecipeTestRuns_WorkspaceId_RecipeTestRunId",
                        columns: x => new { x.WorkspaceId, x.RecipeTestRunId },
                        principalTable: "RecipeTestRuns",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TestIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeTestRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TestObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestIssues", x => x.Id);
                    table.UniqueConstraint("AK_TestIssues_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.UniqueConstraint("AK_TestIssues_Workspace_Recipe_Id", x => new { x.WorkspaceId, x.RecipeId, x.Id });
                    table.CheckConstraint("CK_TestIssues_Severity_Specified", "Severity <> 0");
                    table.CheckConstraint("CK_TestIssues_SortOrder_NonNegative", "SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_TestIssues_RecipeTestRuns_WorkspaceId_RecipeId_RecipeTestRunId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.RecipeTestRunId },
                        principalTable: "RecipeTestRuns",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TestIssues_TestObservations_WorkspaceId_TestObservationId",
                        columns: x => new { x.WorkspaceId, x.TestObservationId },
                        principalTable: "TestObservations",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TestAttachmentLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeTestRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestIssueId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Caption = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestAttachmentLinks", x => x.Id);
                    table.CheckConstraint("CK_TestAttachmentLinks_SortOrder_NonNegative", "SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_TestAttachmentLinks_RecipeTestRuns_WorkspaceId_RecipeTestRunId",
                        columns: x => new { x.WorkspaceId, x.RecipeTestRunId },
                        principalTable: "RecipeTestRuns",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TestAttachmentLinks_TestIssues_WorkspaceId_TestIssueId",
                        columns: x => new { x.WorkspaceId, x.TestIssueId },
                        principalTable: "TestIssues",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TestIssueResolutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestIssueId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ResolvedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolutionRecipeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PredatingVersionOverrideReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestIssueResolutions", x => x.Id);
                    table.CheckConstraint("CK_TestIssueResolutions_Kind_Specified", "Kind <> 0");
                    table.CheckConstraint("CK_TestIssueResolutions_Override_RequiresVersion", "PredatingVersionOverrideReason IS NULL OR ResolutionRecipeVersionId IS NOT NULL");
                    table.CheckConstraint("CK_TestIssueResolutions_Version_Kind", "ResolutionRecipeVersionId IS NULL OR Kind = 1");
                    table.ForeignKey(
                        name: "FK_TestIssueResolutions_RecipeVersions_WorkspaceId_RecipeId_ResolutionRecipeVersionId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.ResolutionRecipeVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TestIssueResolutions_TestIssues_WorkspaceId_RecipeId_TestIssueId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.TestIssueId },
                        principalTable: "TestIssues",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeTestRuns_ActualYieldUnitId_ActualYieldUnitDimension",
                table: "RecipeTestRuns",
                columns: new[] { "ActualYieldUnitId", "ActualYieldUnitDimension" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeTestRuns_Workspace_Recipe_TestedAt",
                table: "RecipeTestRuns",
                columns: new[] { "WorkspaceId", "RecipeId", "TestedAt", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeTestRuns_WorkspaceId_RecipeId_RecipeVersionId",
                table: "RecipeTestRuns",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_TestAttachmentLinks_Workspace_MediaAsset",
                table: "TestAttachmentLinks",
                columns: new[] { "WorkspaceId", "MediaAssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_TestAttachmentLinks_WorkspaceId_TestIssueId",
                table: "TestAttachmentLinks",
                columns: new[] { "WorkspaceId", "TestIssueId" });

            migrationBuilder.CreateIndex(
                name: "UX_TestAttachmentLinks_Workspace_Run_Order",
                table: "TestAttachmentLinks",
                columns: new[] { "WorkspaceId", "RecipeTestRunId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestIssueResolutions_WorkspaceId_RecipeId_ResolutionRecipeVersionId",
                table: "TestIssueResolutions",
                columns: new[] { "WorkspaceId", "RecipeId", "ResolutionRecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "UX_TestIssueResolutions_Workspace_Recipe_Issue",
                table: "TestIssueResolutions",
                columns: new[] { "WorkspaceId", "RecipeId", "TestIssueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestIssues_WorkspaceId_RecipeId_RecipeTestRunId",
                table: "TestIssues",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeTestRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_TestIssues_WorkspaceId_TestObservationId",
                table: "TestIssues",
                columns: new[] { "WorkspaceId", "TestObservationId" });

            migrationBuilder.CreateIndex(
                name: "UX_TestIssues_Workspace_Run_Order",
                table: "TestIssues",
                columns: new[] { "WorkspaceId", "RecipeTestRunId", "SortOrder" },
                unique: true)
                .Annotation("SqlServer:Include", new[] { "Severity" });

            migrationBuilder.CreateIndex(
                name: "UX_TestObservations_Workspace_Run_Order",
                table: "TestObservations",
                columns: new[] { "WorkspaceId", "RecipeTestRunId", "SortOrder" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TestAttachmentLinks");

            migrationBuilder.DropTable(
                name: "TestIssueResolutions");

            migrationBuilder.DropTable(
                name: "TestIssues");

            migrationBuilder.DropTable(
                name: "TestObservations");

            migrationBuilder.DropTable(
                name: "RecipeTestRuns");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_RecipeVersions_Workspace_Recipe_Id",
                table: "RecipeVersions");
        }
    }
}
