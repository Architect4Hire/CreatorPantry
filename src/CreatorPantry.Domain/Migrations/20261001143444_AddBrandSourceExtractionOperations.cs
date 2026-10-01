using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandSourceExtractionOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BrandSourceExtractionOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceDocumentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    BrandSourceExtractionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExtractorId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeasedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailureCategory = table.Column<int>(type: "int", nullable: true),
                    FailureSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    QueuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandSourceExtractionOperations", x => x.Id);
                    table.CheckConstraint("CK_BrandSourceExtractionOperations_Attempts_NotNegative", "Attempts >= 0");
                    table.CheckConstraint("CK_BrandSourceExtractionOperations_Completed_Terminal", "(CompletedAt IS NULL AND Status NOT IN (3, 4, 5)) OR (CompletedAt IS NOT NULL AND Status IN (3, 4, 5))");
                    table.CheckConstraint("CK_BrandSourceExtractionOperations_Extraction_Requires_Completed", "BrandSourceExtractionId IS NULL OR Status = 3");
                    table.CheckConstraint("CK_BrandSourceExtractionOperations_ExtractorId_NotBlank", "ExtractorId IS NULL OR trim(ExtractorId) <> ''");
                    table.CheckConstraint("CK_BrandSourceExtractionOperations_Failure_Status", "(FailureCategory IS NULL AND Status <> 4) OR (FailureCategory IS NOT NULL AND Status = 4)");
                    table.CheckConstraint("CK_BrandSourceExtractionOperations_FailureSummary_Requires_Failure", "FailureSummary IS NULL OR FailureCategory IS NOT NULL");
                    table.CheckConstraint("CK_BrandSourceExtractionOperations_Lease_Complete", "(LeasedBy IS NULL AND LeaseExpiresAt IS NULL) OR (LeasedBy IS NOT NULL AND LeaseExpiresAt IS NOT NULL)");
                    table.CheckConstraint("CK_BrandSourceExtractionOperations_Lease_Requires_Running", "LeasedBy IS NULL OR Status = 2");
                    table.CheckConstraint("CK_BrandSourceExtractionOperations_Running_HasStarted", "Status <> 2 OR StartedAt IS NOT NULL");
                    table.CheckConstraint("CK_BrandSourceExtractionOperations_Status_Specified", "Status <> 0");
                    table.ForeignKey(
                        name: "FK_BrandSourceExtractionOperations_BrandSourceDocumentVersions_WorkspaceId_BrandSourceDocumentVersionId",
                        columns: x => new { x.WorkspaceId, x.BrandSourceDocumentVersionId },
                        principalTable: "BrandSourceDocumentVersions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BrandSourceExtractionOperations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrandSourceExtractionOperations_Status_AvailableAt",
                table: "BrandSourceExtractionOperations",
                columns: new[] { "Status", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandSourceExtractionOperations_Status_LeaseExpiresAt",
                table: "BrandSourceExtractionOperations",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "UX_BrandSourceExtractionOperations_Workspace_Version",
                table: "BrandSourceExtractionOperations",
                columns: new[] { "WorkspaceId", "BrandSourceDocumentVersionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrandSourceExtractionOperations");
        }
    }
}
