using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class GeneratedImageWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GeneratedImageOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PromptText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    AvoidText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    AiProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VariantCount = table.Column<int>(type: "int", nullable: false),
                    ProviderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ModelDeployment = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FailureCategory = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FailureSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeasedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedImageOperations", x => x.Id);
                    table.UniqueConstraint("AK_GeneratedImageOperations_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_GeneratedImageOperations_IdempotencyKey_NotBlank", "trim(IdempotencyKey) <> ''");
                    table.CheckConstraint("CK_GeneratedImageOperations_Status_Declared", "Status <> 0");
                    table.CheckConstraint("CK_GeneratedImageOperations_VariantCount_Range", "VariantCount >= 1 AND VariantCount <= 4");
                    table.ForeignKey(
                        name: "FK_GeneratedImageOperations_AiProposals_WorkspaceId_AiProposalId",
                        columns: x => new { x.WorkspaceId, x.AiProposalId },
                        principalTable: "AiProposals",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GeneratedImageOperations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GeneratedImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GeneratedImageOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantIndex = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    MediaType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentChecksum = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ProviderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModelDeployment = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RetentionExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeneratedImages", x => x.Id);
                    table.UniqueConstraint("AK_GeneratedImages_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_GeneratedImages_Checksum_NotBlank", "trim(ContentChecksum) <> ''");
                    table.CheckConstraint("CK_GeneratedImages_Dimensions_Positive", "Width > 0 AND Height > 0");
                    table.CheckConstraint("CK_GeneratedImages_ObjectKey_NotBlank", "trim(ObjectKey) <> ''");
                    table.CheckConstraint("CK_GeneratedImages_Pixels_Range", "CAST(Width AS bigint) * CAST(Height AS bigint) <= 50000000");
                    table.CheckConstraint("CK_GeneratedImages_SizeBytes_Positive", "SizeBytes > 0");
                    table.CheckConstraint("CK_GeneratedImages_Status_Declared", "Status <> 0");
                    table.CheckConstraint("CK_GeneratedImages_VariantIndex_Range", "VariantIndex >= 0 AND VariantIndex < 4");
                    table.ForeignKey(
                        name: "FK_GeneratedImages_GeneratedImageOperations_WorkspaceId_GeneratedImageOperationId",
                        columns: x => new { x.WorkspaceId, x.GeneratedImageOperationId },
                        principalTable: "GeneratedImageOperations",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GeneratedImages_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedImageOperations_Status_AvailableAt",
                table: "GeneratedImageOperations",
                columns: new[] { "Status", "AvailableAt" },
                filter: "Status IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedImageOperations_Workspace_RequestedAt",
                table: "GeneratedImageOperations",
                columns: new[] { "WorkspaceId", "RequestedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedImageOperations_WorkspaceId_AiProposalId",
                table: "GeneratedImageOperations",
                columns: new[] { "WorkspaceId", "AiProposalId" });

            migrationBuilder.CreateIndex(
                name: "UX_GeneratedImageOperations_Workspace_IdempotencyKey",
                table: "GeneratedImageOperations",
                columns: new[] { "WorkspaceId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedImages_Status_RetentionExpiresAt",
                table: "GeneratedImages",
                columns: new[] { "Status", "RetentionExpiresAt" },
                filter: "Status = 1");

            migrationBuilder.CreateIndex(
                name: "IX_GeneratedImages_Workspace_Operation_Variant",
                table: "GeneratedImages",
                columns: new[] { "WorkspaceId", "GeneratedImageOperationId", "VariantIndex" });

            migrationBuilder.CreateIndex(
                name: "UX_GeneratedImages_ObjectKey",
                table: "GeneratedImages",
                column: "ObjectKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_GeneratedImages_Operation_Variant",
                table: "GeneratedImages",
                columns: new[] { "GeneratedImageOperationId", "VariantIndex" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PromptRecords_GeneratedImages_WorkspaceId_GeneratedImageId",
                table: "PromptRecords",
                columns: new[] { "WorkspaceId", "GeneratedImageId" },
                principalTable: "GeneratedImages",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PromptRecords_GeneratedImages_WorkspaceId_GeneratedImageId",
                table: "PromptRecords");

            migrationBuilder.DropTable(
                name: "GeneratedImages");

            migrationBuilder.DropTable(
                name: "GeneratedImageOperations");
        }
    }
}
