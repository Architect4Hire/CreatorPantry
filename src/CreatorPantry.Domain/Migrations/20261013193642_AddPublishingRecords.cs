using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddPublishingRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublishingRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentCardId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Platform = table.Column<int>(type: "int", nullable: false),
                    ContentText = table.Column<string>(type: "nvarchar(max)", maxLength: 20000, nullable: false),
                    MediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MediaAssetVersionNumber = table.Column<int>(type: "int", nullable: true),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ScheduleTimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ScheduledLocal = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FailureCategory = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FailureSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SubmitAttempts = table.Column<int>(type: "int", nullable: false),
                    CancelAttempts = table.Column<int>(type: "int", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UncertainOperation = table.Column<int>(type: "int", nullable: true),
                    UncertainSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AvailableAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeasedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConfirmedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishingRecords", x => x.Id);
                    table.UniqueConstraint("AK_PublishingRecords_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_PublishingRecords_AssetVersion_WithAsset", "(MediaAssetId IS NULL AND MediaAssetVersionNumber IS NULL) OR (MediaAssetId IS NOT NULL AND MediaAssetVersionNumber IS NOT NULL AND MediaAssetVersionNumber >= 1)");
                    table.CheckConstraint("CK_PublishingRecords_Attempts_NotNegative", "SubmitAttempts >= 0 AND CancelAttempts >= 0");
                    table.CheckConstraint("CK_PublishingRecords_ContentText_NotBlank", "trim(ContentText) <> ''");
                    table.CheckConstraint("CK_PublishingRecords_IdempotencyKey_NotBlank", "trim(IdempotencyKey) <> ''");
                    table.CheckConstraint("CK_PublishingRecords_Lease_Paired", "(LeasedBy IS NULL AND LeaseExpiresAt IS NULL) OR (LeasedBy IS NOT NULL AND LeaseExpiresAt IS NOT NULL)");
                    table.CheckConstraint("CK_PublishingRecords_NeedsAttention_HasReason", "Status <> 8 OR UncertainOperation IS NOT NULL OR FailureCategory IS NOT NULL");
                    table.CheckConstraint("CK_PublishingRecords_Platform_Range", "Platform >= 1 AND Platform <= 12");
                    table.CheckConstraint("CK_PublishingRecords_ScheduleTimeZoneId_NotBlank", "trim(ScheduleTimeZoneId) <> ''");
                    table.CheckConstraint("CK_PublishingRecords_Status_Range", "Status >= 1 AND Status <= 8");
                    table.CheckConstraint("CK_PublishingRecords_Uncertain_Operation_Writes", "UncertainOperation IS NULL OR UncertainOperation IN (1, 2)");
                    table.CheckConstraint("CK_PublishingRecords_Uncertainty_Paired", "(UncertainOperation IS NULL AND UncertainSince IS NULL) OR (UncertainOperation IS NOT NULL AND UncertainSince IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PublishingRecords_ContentCards_WorkspaceId_ContentCardId",
                        columns: x => new { x.WorkspaceId, x.ContentCardId },
                        principalTable: "ContentCards",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PublishingRecords_MediaAssetVersions_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                        columns: x => new { x.WorkspaceId, x.MediaAssetId, x.MediaAssetVersionNumber },
                        principalTable: "MediaAssetVersions",
                        principalColumns: new[] { "WorkspaceId", "MediaAssetId", "VersionNumber" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PublishingRecords_PublishingProfiles_WorkspaceId_PublishingProfileId",
                        columns: x => new { x.WorkspaceId, x.PublishingProfileId },
                        principalTable: "PublishingProfiles",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PublishingRecords_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalPublicationReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublishingRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExternalUpdateId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalPublicationReferences", x => x.Id);
                    table.CheckConstraint("CK_ExternalPublicationReferences_ExternalUpdateId_NotBlank", "trim(ExternalUpdateId) <> ''");
                    table.CheckConstraint("CK_ExternalPublicationReferences_ProviderKey_NotBlank", "trim(ProviderKey) <> ''");
                    table.ForeignKey(
                        name: "FK_ExternalPublicationReferences_PublishingRecords_WorkspaceId_PublishingRecordId",
                        columns: x => new { x.WorkspaceId, x.PublishingRecordId },
                        principalTable: "PublishingRecords",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublishingDeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublishingRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProviderStatusCode = table.Column<int>(type: "int", nullable: true),
                    FailureCategory = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FailureSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishingDeliveryAttempts", x => x.Id);
                    table.CheckConstraint("CK_PublishingDeliveryAttempts_AttemptNumber_Positive", "AttemptNumber >= 1");
                    table.CheckConstraint("CK_PublishingDeliveryAttempts_Operation_Range", "Operation >= 1 AND Operation <= 3");
                    table.CheckConstraint("CK_PublishingDeliveryAttempts_Outcome_Range", "Outcome >= 1 AND Outcome <= 5");
                    table.CheckConstraint("CK_PublishingDeliveryAttempts_ProviderStatusCode_Range", "ProviderStatusCode IS NULL OR (ProviderStatusCode >= 100 AND ProviderStatusCode <= 599)");
                    table.ForeignKey(
                        name: "FK_PublishingDeliveryAttempts_PublishingRecords_WorkspaceId_PublishingRecordId",
                        columns: x => new { x.WorkspaceId, x.PublishingRecordId },
                        principalTable: "PublishingRecords",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_ExternalPublicationReferences_Workspace_Provider_External",
                table: "ExternalPublicationReferences",
                columns: new[] { "WorkspaceId", "ProviderKey", "ExternalUpdateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ExternalPublicationReferences_Workspace_Record",
                table: "ExternalPublicationReferences",
                columns: new[] { "WorkspaceId", "PublishingRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PublishingDeliveryAttempts_Record_AttemptNumber",
                table: "PublishingDeliveryAttempts",
                columns: new[] { "WorkspaceId", "PublishingRecordId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishingRecords_Status_AvailableAt",
                table: "PublishingRecords",
                columns: new[] { "Status", "AvailableAt" },
                filter: "Status IN (1, 2, 3, 5)");

            migrationBuilder.CreateIndex(
                name: "IX_PublishingRecords_Workspace_Card_ScheduledAt",
                table: "PublishingRecords",
                columns: new[] { "WorkspaceId", "ContentCardId", "ScheduledAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_PublishingRecords_Workspace_Status_ScheduledAt",
                table: "PublishingRecords",
                columns: new[] { "WorkspaceId", "Status", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PublishingRecords_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                table: "PublishingRecords",
                columns: new[] { "WorkspaceId", "MediaAssetId", "MediaAssetVersionNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_PublishingRecords_WorkspaceId_PublishingProfileId",
                table: "PublishingRecords",
                columns: new[] { "WorkspaceId", "PublishingProfileId" });

            migrationBuilder.CreateIndex(
                name: "UX_PublishingRecords_Workspace_IdempotencyKey",
                table: "PublishingRecords",
                columns: new[] { "WorkspaceId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalPublicationReferences");

            migrationBuilder.DropTable(
                name: "PublishingDeliveryAttempts");

            migrationBuilder.DropTable(
                name: "PublishingRecords");
        }
    }
}
