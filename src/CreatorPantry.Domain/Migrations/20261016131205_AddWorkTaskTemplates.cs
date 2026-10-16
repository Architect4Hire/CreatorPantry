using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkTaskTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkTaskTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    EstimateMinutes = table.Column<int>(type: "int", nullable: true),
                    Frequency = table.Column<int>(type: "int", nullable: false),
                    Weekdays = table.Column<int>(type: "int", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NextOccurrenceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttentionReason = table.Column<int>(type: "int", nullable: true),
                    LeasedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTaskTemplates", x => x.Id);
                    table.UniqueConstraint("AK_WorkTaskTemplates_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_WorkTaskTemplates_Attention_Range", "AttentionReason IS NULL OR (AttentionReason >= 1 AND AttentionReason <= 2)");
                    table.CheckConstraint("CK_WorkTaskTemplates_Attention_Reason", "(Status = 4 AND AttentionReason IS NOT NULL) OR (Status <> 4 AND AttentionReason IS NULL)");
                    table.CheckConstraint("CK_WorkTaskTemplates_Description_NotBlank", "Description IS NULL OR trim(Description) <> ''");
                    table.CheckConstraint("CK_WorkTaskTemplates_Ended_NoNext", "Status <> 3 OR NextOccurrenceDate IS NULL");
                    table.CheckConstraint("CK_WorkTaskTemplates_Ends_AfterStart", "EndsOn IS NULL OR EndsOn >= StartsOn");
                    table.CheckConstraint("CK_WorkTaskTemplates_Estimate_Range", "EstimateMinutes IS NULL OR (EstimateMinutes >= 1 AND EstimateMinutes <= 1440)");
                    table.CheckConstraint("CK_WorkTaskTemplates_Frequency_Range", "Frequency >= 1 AND Frequency <= 2");
                    table.CheckConstraint("CK_WorkTaskTemplates_Lease_Whole", "(LeasedBy IS NULL AND LeaseExpiresAt IS NULL) OR (LeasedBy IS NOT NULL AND LeaseExpiresAt IS NOT NULL)");
                    table.CheckConstraint("CK_WorkTaskTemplates_Next_InRange", "NextOccurrenceDate IS NULL OR (NextOccurrenceDate >= StartsOn AND (EndsOn IS NULL OR NextOccurrenceDate <= EndsOn))");
                    table.CheckConstraint("CK_WorkTaskTemplates_Priority_Range", "Priority >= 1 AND Priority <= 3");
                    table.CheckConstraint("CK_WorkTaskTemplates_Status_Range", "Status >= 1 AND Status <= 4");
                    table.CheckConstraint("CK_WorkTaskTemplates_TimeZone_NotBlank", "trim(TimeZoneId) <> ''");
                    table.CheckConstraint("CK_WorkTaskTemplates_Title_NotBlank", "trim(Title) <> ''");
                    table.CheckConstraint("CK_WorkTaskTemplates_Weekdays_Frequency", "(Frequency = 2 AND Weekdays >= 1 AND Weekdays <= 127) OR (Frequency <> 2 AND Weekdays = 0)");
                    table.ForeignKey(
                        name: "FK_WorkTaskTemplates_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskTemplates_Status_Next",
                table: "WorkTaskTemplates",
                columns: new[] { "Status", "NextOccurrenceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskTemplates_Workspace_Owner_Status",
                table: "WorkTaskTemplates",
                columns: new[] { "WorkspaceId", "OwnerMembershipId", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_WorkTasks_WorkTaskTemplates_WorkspaceId_WorkTaskTemplateId",
                table: "WorkTasks",
                columns: new[] { "WorkspaceId", "WorkTaskTemplateId" },
                principalTable: "WorkTaskTemplates",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkTasks_WorkTaskTemplates_WorkspaceId_WorkTaskTemplateId",
                table: "WorkTasks");

            migrationBuilder.DropTable(
                name: "WorkTaskTemplates");
        }
    }
}
