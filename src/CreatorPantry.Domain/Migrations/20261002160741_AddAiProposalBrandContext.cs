using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAiProposalBrandContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiProposalBrandContexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Audience = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AudienceOrigin = table.Column<int>(type: "int", nullable: true),
                    BrandProfileRevision = table.Column<int>(type: "int", nullable: true),
                    BrandGuideId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BrandGuideVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BrandGuideVersionNumber = table.Column<int>(type: "int", nullable: true),
                    GuideWasActiveVersion = table.Column<bool>(type: "bit", nullable: false),
                    Checksum = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    EstimatedTokens = table.Column<int>(type: "int", nullable: false),
                    GuidanceSectionCount = table.Column<int>(type: "int", nullable: false),
                    RuleCount = table.Column<int>(type: "int", nullable: false),
                    AssembledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProposalBrandContexts", x => x.Id);
                    table.UniqueConstraint("AK_AiProposalBrandContexts_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_AiProposalBrandContexts_Active_Requires_Guide", "BrandGuideId IS NOT NULL OR GuideWasActiveVersion = CAST(0 AS bit)");
                    table.CheckConstraint("CK_AiProposalBrandContexts_Audience_HasOrigin", "(Audience IS NULL AND AudienceOrigin IS NULL) OR (Audience IS NOT NULL AND AudienceOrigin IS NOT NULL)");
                    table.CheckConstraint("CK_AiProposalBrandContexts_AudienceOrigin_Declared", "AudienceOrigin IS NULL OR AudienceOrigin <> 0");
                    table.CheckConstraint("CK_AiProposalBrandContexts_Counts_NonNegative", "(BrandProfileRevision IS NULL OR BrandProfileRevision > 0) AND EstimatedTokens >= 0 AND GuidanceSectionCount >= 0 AND RuleCount >= 0");
                    table.CheckConstraint("CK_AiProposalBrandContexts_Guide_AllOrNone", "(BrandGuideId IS NULL AND BrandGuideVersionId IS NULL AND BrandGuideVersionNumber IS NULL) OR (BrandGuideId IS NOT NULL AND BrandGuideVersionId IS NOT NULL AND BrandGuideVersionNumber IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AiProposalBrandContexts_AiProposals_WorkspaceId_AiProposalId",
                        columns: x => new { x.WorkspaceId, x.AiProposalId },
                        principalTable: "AiProposals",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiProposalBrandSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiProposalBrandContextId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentVersionNumber = table.Column<int>(type: "int", nullable: false),
                    BrandSourcePassageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProposalBrandSources", x => x.Id);
                    table.CheckConstraint("CK_AiProposalBrandSources_Positions", "DocumentVersionNumber > 0 AND Ordinal >= 0 AND SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_AiProposalBrandSources_AiProposalBrandContexts_WorkspaceId_AiProposalBrandContextId",
                        columns: x => new { x.WorkspaceId, x.AiProposalBrandContextId },
                        principalTable: "AiProposalBrandContexts",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiProposalBrandContexts_Workspace_Checksum",
                table: "AiProposalBrandContexts",
                columns: new[] { "WorkspaceId", "Checksum" });

            migrationBuilder.CreateIndex(
                name: "IX_AiProposalBrandContexts_Workspace_GuideVersion",
                table: "AiProposalBrandContexts",
                columns: new[] { "WorkspaceId", "BrandGuideVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiProposalBrandContexts_WorkspaceId_AiProposalId",
                table: "AiProposalBrandContexts",
                columns: new[] { "WorkspaceId", "AiProposalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiProposalBrandSources_Workspace_Document",
                table: "AiProposalBrandSources",
                columns: new[] { "WorkspaceId", "BrandSourceDocumentId" });

            migrationBuilder.CreateIndex(
                name: "UX_AiProposalBrandSources_Context_SortOrder",
                table: "AiProposalBrandSources",
                columns: new[] { "WorkspaceId", "AiProposalBrandContextId", "SortOrder" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiProposalBrandSources");

            migrationBuilder.DropTable(
                name: "AiProposalBrandContexts");
        }
    }
}
