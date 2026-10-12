using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddLongFormDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LongFormDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreativeContextId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AcceptedRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StaleSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StaleReasons = table.Column<int>(type: "int", nullable: false),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LongFormDrafts", x => x.Id);
                    table.UniqueConstraint("AK_LongFormDrafts_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_LongFormDrafts_Accepted_HasRevision", "Status NOT IN (4, 5) OR AcceptedRevisionId IS NOT NULL");
                    table.CheckConstraint("CK_LongFormDrafts_AcceptedAt_Revision", "(AcceptedRevisionId IS NULL AND AcceptedAt IS NULL) OR (AcceptedRevisionId IS NOT NULL AND AcceptedAt IS NOT NULL)");
                    table.CheckConstraint("CK_LongFormDrafts_Kind_Range", "Kind >= 0 AND Kind <= 1");
                    table.CheckConstraint("CK_LongFormDrafts_Staleness_Status", "(Status = 5 AND StaleSince IS NOT NULL AND StaleReasons <> 0) OR (Status <> 5 AND StaleSince IS NULL AND StaleReasons = 0)");
                    table.CheckConstraint("CK_LongFormDrafts_Status_Range", "Status >= 0 AND Status <= 5");
                    table.ForeignKey(
                        name: "FK_LongFormDrafts_CreativeContexts_WorkspaceId_CreativeContextId",
                        columns: x => new { x.WorkspaceId, x.CreativeContextId },
                        principalTable: "CreativeContexts",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LongFormDrafts_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LongFormRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LongFormDraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNumber = table.Column<int>(type: "int", nullable: false),
                    ParentRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<int>(type: "int", nullable: false),
                    AiProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BrandProfileRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BrandStyleGuideVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PromptTemplateId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PromptTemplateVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PromptTemplateBodyChecksum = table.Column<string>(type: "nvarchar(71)", maxLength: 71, nullable: true),
                    CreativeContextVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LongFormRevisions", x => x.Id);
                    table.UniqueConstraint("AK_LongFormRevisions_Workspace_Draft_Id", x => new { x.WorkspaceId, x.LongFormDraftId, x.Id });
                    table.UniqueConstraint("AK_LongFormRevisions_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_LongFormRevisions_AiProposal_Source", "(AiProposalId IS NOT NULL AND Source = 0) OR (AiProposalId IS NULL AND Source <> 0)");
                    table.CheckConstraint("CK_LongFormRevisions_Content_NotBlank", "trim(Content) <> ''");
                    table.CheckConstraint("CK_LongFormRevisions_Parent_NotSelf", "ParentRevisionId IS NULL OR ParentRevisionId <> Id");
                    table.CheckConstraint("CK_LongFormRevisions_Parent_RevisionNumber", "(RevisionNumber = 1 AND ParentRevisionId IS NULL) OR (RevisionNumber > 1 AND ParentRevisionId IS NOT NULL)");
                    table.CheckConstraint("CK_LongFormRevisions_Reaffirmed_HasParent", "Source <> 2 OR ParentRevisionId IS NOT NULL");
                    table.CheckConstraint("CK_LongFormRevisions_RevisionNumber_Positive", "RevisionNumber >= 1");
                    table.CheckConstraint("CK_LongFormRevisions_SchemaVersion_Positive", "SchemaVersion >= 1");
                    table.CheckConstraint("CK_LongFormRevisions_Template_Generated", "Source <> 0 OR (PromptTemplateId IS NOT NULL AND PromptTemplateVersion IS NOT NULL AND PromptTemplateBodyChecksum IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_LongFormRevisions_AiProposals_WorkspaceId_AiProposalId",
                        columns: x => new { x.WorkspaceId, x.AiProposalId },
                        principalTable: "AiProposals",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LongFormRevisions_BrandProfileRevisions_WorkspaceId_BrandProfileRevisionId",
                        columns: x => new { x.WorkspaceId, x.BrandProfileRevisionId },
                        principalTable: "BrandProfileRevisions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LongFormRevisions_BrandStyleGuideVersions_WorkspaceId_BrandStyleGuideVersionId",
                        columns: x => new { x.WorkspaceId, x.BrandStyleGuideVersionId },
                        principalTable: "BrandStyleGuideVersions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LongFormRevisions_LongFormDrafts_WorkspaceId_LongFormDraftId",
                        columns: x => new { x.WorkspaceId, x.LongFormDraftId },
                        principalTable: "LongFormDrafts",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LongFormRevisions_LongFormRevisions_WorkspaceId_LongFormDraftId_ParentRevisionId",
                        columns: x => new { x.WorkspaceId, x.LongFormDraftId, x.ParentRevisionId },
                        principalTable: "LongFormRevisions",
                        principalColumns: new[] { "WorkspaceId", "LongFormDraftId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LongFormRevisions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LongFormRevisionRecipePins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LongFormRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreativeContextReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LongFormRevisionRecipePins", x => x.Id);
                    table.CheckConstraint("CK_LongFormRevisionRecipePins_SortOrder_NonNegative", "SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_LongFormRevisionRecipePins_LongFormRevisions_WorkspaceId_LongFormRevisionId",
                        columns: x => new { x.WorkspaceId, x.LongFormRevisionId },
                        principalTable: "LongFormRevisions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LongFormRevisionRecipePins_RecipeVersions_WorkspaceId_RecipeId_RecipeVersionId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.RecipeVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LongFormRevisionRecipePins_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LongFormDrafts_Workspace_Context_Kind",
                table: "LongFormDrafts",
                columns: new[] { "WorkspaceId", "CreativeContextId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_LongFormDrafts_Workspace_Status",
                table: "LongFormDrafts",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LongFormDrafts_WorkspaceId_Id_AcceptedRevisionId",
                table: "LongFormDrafts",
                columns: new[] { "WorkspaceId", "Id", "AcceptedRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_LongFormRevisionRecipePins_Workspace_RecipeVersion",
                table: "LongFormRevisionRecipePins",
                columns: new[] { "WorkspaceId", "RecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_LongFormRevisionRecipePins_WorkspaceId_RecipeId_RecipeVersionId",
                table: "LongFormRevisionRecipePins",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "UX_LongFormRevisionRecipePins_Workspace_Revision_Recipe",
                table: "LongFormRevisionRecipePins",
                columns: new[] { "WorkspaceId", "LongFormRevisionId", "RecipeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_LongFormRevisionRecipePins_Workspace_Revision_SortOrder",
                table: "LongFormRevisionRecipePins",
                columns: new[] { "WorkspaceId", "LongFormRevisionId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LongFormRevisions_Workspace_BrandProfileRevision",
                table: "LongFormRevisions",
                columns: new[] { "WorkspaceId", "BrandProfileRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_LongFormRevisions_WorkspaceId_AiProposalId",
                table: "LongFormRevisions",
                columns: new[] { "WorkspaceId", "AiProposalId" });

            migrationBuilder.CreateIndex(
                name: "IX_LongFormRevisions_WorkspaceId_BrandStyleGuideVersionId",
                table: "LongFormRevisions",
                columns: new[] { "WorkspaceId", "BrandStyleGuideVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_LongFormRevisions_WorkspaceId_LongFormDraftId_ParentRevisionId",
                table: "LongFormRevisions",
                columns: new[] { "WorkspaceId", "LongFormDraftId", "ParentRevisionId" });

            migrationBuilder.CreateIndex(
                name: "UX_LongFormRevisions_Workspace_Draft_RevisionNumber",
                table: "LongFormRevisions",
                columns: new[] { "WorkspaceId", "LongFormDraftId", "RevisionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_LongFormDrafts_LongFormRevisions_WorkspaceId_Id_AcceptedRevisionId",
                table: "LongFormDrafts",
                columns: new[] { "WorkspaceId", "Id", "AcceptedRevisionId" },
                principalTable: "LongFormRevisions",
                principalColumns: new[] { "WorkspaceId", "LongFormDraftId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LongFormDrafts_LongFormRevisions_WorkspaceId_Id_AcceptedRevisionId",
                table: "LongFormDrafts");

            migrationBuilder.DropTable(
                name: "LongFormRevisionRecipePins");

            migrationBuilder.DropTable(
                name: "LongFormRevisions");

            migrationBuilder.DropTable(
                name: "LongFormDrafts");
        }
    }
}
