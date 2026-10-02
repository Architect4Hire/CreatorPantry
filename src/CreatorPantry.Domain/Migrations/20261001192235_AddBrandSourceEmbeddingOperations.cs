using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandSourceEmbeddingOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupersededAt",
                table: "BrandSourceChunkSets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BrandSourceEmbeddingOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceExtractionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceDocumentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceStatus = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    EmbeddingModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BrandSourceChunkSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
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
                    table.PrimaryKey("PK_BrandSourceEmbeddingOperations", x => x.Id);
                    table.CheckConstraint("CK_BrandSourceEmbeddingOperations_Attempts_NotNegative", "Attempts >= 0");
                    table.CheckConstraint("CK_BrandSourceEmbeddingOperations_Completed_Terminal", "(CompletedAt IS NULL AND Status NOT IN (3, 4, 5)) OR (CompletedAt IS NOT NULL AND Status IN (3, 4, 5))");
                    table.CheckConstraint("CK_BrandSourceEmbeddingOperations_Failure_Status", "(FailureCategory IS NULL AND Status <> 4) OR (FailureCategory IS NOT NULL AND Status = 4)");
                    table.CheckConstraint("CK_BrandSourceEmbeddingOperations_FailureSummary_Requires_Failure", "FailureSummary IS NULL OR FailureCategory IS NOT NULL");
                    table.CheckConstraint("CK_BrandSourceEmbeddingOperations_Lease_Complete", "(LeasedBy IS NULL AND LeaseExpiresAt IS NULL) OR (LeasedBy IS NOT NULL AND LeaseExpiresAt IS NOT NULL)");
                    table.CheckConstraint("CK_BrandSourceEmbeddingOperations_Lease_Requires_Running", "LeasedBy IS NULL OR Status = 2");
                    table.CheckConstraint("CK_BrandSourceEmbeddingOperations_Model_NotBlank", "EmbeddingModel IS NULL OR trim(EmbeddingModel) <> ''");
                    table.CheckConstraint("CK_BrandSourceEmbeddingOperations_Running_HasStarted", "Status <> 2 OR StartedAt IS NOT NULL");
                    table.CheckConstraint("CK_BrandSourceEmbeddingOperations_Set_Requires_Completed", "BrandSourceChunkSetId IS NULL OR Status = 3");
                    table.CheckConstraint("CK_BrandSourceEmbeddingOperations_SourceStatus_Succeeded", "SourceStatus = 1");
                    table.CheckConstraint("CK_BrandSourceEmbeddingOperations_Status_Specified", "Status <> 0");
                    table.ForeignKey(
                        name: "FK_BrandSourceEmbeddingOperations_BrandSourceExtractions_WorkspaceId_BrandSourceExtractionId_BrandSourceDocumentVersionId_Sourc~",
                        columns: x => new { x.WorkspaceId, x.BrandSourceExtractionId, x.BrandSourceDocumentVersionId, x.SourceStatus },
                        principalTable: "BrandSourceExtractions",
                        principalColumns: new[] { "WorkspaceId", "Id", "BrandSourceDocumentVersionId", "Status" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BrandSourceEmbeddingOperations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_BrandSourceChunkSets_SupersededAt_Matches_Status",
                table: "BrandSourceChunkSets",
                sql: "SupersededAt IS NULL OR Status = 3");

            migrationBuilder.CreateIndex(
                name: "IX_BrandSourceEmbeddingOperations_Status_AvailableAt",
                table: "BrandSourceEmbeddingOperations",
                columns: new[] { "Status", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandSourceEmbeddingOperations_Status_LeaseExpiresAt",
                table: "BrandSourceEmbeddingOperations",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandSourceEmbeddingOperations_WorkspaceId_BrandSourceExtractionId_BrandSourceDocumentVersionId_SourceStatus",
                table: "BrandSourceEmbeddingOperations",
                columns: new[] { "WorkspaceId", "BrandSourceExtractionId", "BrandSourceDocumentVersionId", "SourceStatus" });

            migrationBuilder.CreateIndex(
                name: "UX_BrandSourceEmbeddingOperations_Workspace_Extraction_Live",
                table: "BrandSourceEmbeddingOperations",
                columns: new[] { "WorkspaceId", "BrandSourceExtractionId" },
                unique: true,
                filter: "Status IN (1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrandSourceEmbeddingOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BrandSourceChunkSets_SupersededAt_Matches_Status",
                table: "BrandSourceChunkSets");

            migrationBuilder.DropColumn(
                name: "SupersededAt",
                table: "BrandSourceChunkSets");
        }
    }
}
