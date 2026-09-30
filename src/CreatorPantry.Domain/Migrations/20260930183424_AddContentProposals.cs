using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddContentProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_BrandProfileRevisions_Workspace_Id",
                table: "BrandProfileRevisions",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateTable(
                name: "ContentProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AcceptedRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StaleSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StaleReasons = table.Column<int>(type: "int", nullable: false),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentProposals", x => x.Id);
                    table.UniqueConstraint("AK_ContentProposals_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.UniqueConstraint("AK_ContentProposals_Workspace_Id_Recipe", x => new { x.WorkspaceId, x.Id, x.RecipeId });
                    table.CheckConstraint("CK_ContentProposals_Accepted_HasRevision", "Status NOT IN (1, 3) OR AcceptedRevisionId IS NOT NULL");
                    table.CheckConstraint("CK_ContentProposals_Staleness_Status", "(Status = 3 AND StaleSince IS NOT NULL AND StaleReasons <> 0) OR (Status <> 3 AND StaleSince IS NULL AND StaleReasons = 0)");
                    table.ForeignKey(
                        name: "FK_ContentProposals_Recipes_WorkspaceId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.RecipeId },
                        principalTable: "Recipes",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentProposals_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNumber = table.Column<int>(type: "int", nullable: false),
                    ParentRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<int>(type: "int", nullable: false),
                    AiProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecipeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandProfileRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BrandStyleGuideVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PromptTemplateId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PromptTemplateVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PromptTemplateBodyChecksum = table.Column<string>(type: "nvarchar(71)", maxLength: 71, nullable: true),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentRevisions", x => x.Id);
                    table.UniqueConstraint("AK_ContentRevisions_Workspace_Proposal_Id", x => new { x.WorkspaceId, x.ContentProposalId, x.Id });
                    table.CheckConstraint("CK_ContentRevisions_AiProposal_Source", "(AiProposalId IS NOT NULL AND Source = 0) OR (AiProposalId IS NULL AND Source <> 0)");
                    table.CheckConstraint("CK_ContentRevisions_Parent_NotSelf", "ParentRevisionId IS NULL OR ParentRevisionId <> Id");
                    table.CheckConstraint("CK_ContentRevisions_Parent_RevisionNumber", "(RevisionNumber = 1 AND ParentRevisionId IS NULL) OR (RevisionNumber > 1 AND ParentRevisionId IS NOT NULL)");
                    table.CheckConstraint("CK_ContentRevisions_Reaffirmed_HasParent", "Source <> 2 OR ParentRevisionId IS NOT NULL");
                    table.CheckConstraint("CK_ContentRevisions_RevisionNumber_Positive", "RevisionNumber >= 1");
                    table.CheckConstraint("CK_ContentRevisions_SchemaVersion_Positive", "SchemaVersion >= 1");
                    table.CheckConstraint("CK_ContentRevisions_Template_Generated", "Source <> 0 OR (PromptTemplateId IS NOT NULL AND PromptTemplateVersion IS NOT NULL AND PromptTemplateBodyChecksum IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ContentRevisions_AiProposals_WorkspaceId_AiProposalId",
                        columns: x => new { x.WorkspaceId, x.AiProposalId },
                        principalTable: "AiProposals",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentRevisions_BrandProfileRevisions_WorkspaceId_BrandProfileRevisionId",
                        columns: x => new { x.WorkspaceId, x.BrandProfileRevisionId },
                        principalTable: "BrandProfileRevisions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentRevisions_ContentProposals_WorkspaceId_ContentProposalId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.ContentProposalId, x.RecipeId },
                        principalTable: "ContentProposals",
                        principalColumns: new[] { "WorkspaceId", "Id", "RecipeId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentRevisions_ContentRevisions_WorkspaceId_ContentProposalId_ParentRevisionId",
                        columns: x => new { x.WorkspaceId, x.ContentProposalId, x.ParentRevisionId },
                        principalTable: "ContentRevisions",
                        principalColumns: new[] { "WorkspaceId", "ContentProposalId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentRevisions_RecipeVersions_WorkspaceId_RecipeId_RecipeVersionId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.RecipeVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentRevisions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentProposalTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromStatus = table.Column<int>(type: "int", nullable: true),
                    ToStatus = table.Column<int>(type: "int", nullable: false),
                    ContentRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ActorMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StaleReasons = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    MachineVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentProposalTransitions", x => x.Id);
                    table.CheckConstraint("CK_ContentProposalTransitions_Actor_Status", "(ToStatus = 3 AND ActorMembershipId IS NULL) OR (ToStatus <> 3 AND ActorMembershipId IS NOT NULL)");
                    table.CheckConstraint("CK_ContentProposalTransitions_StaleReasons_Status", "(ToStatus = 3 AND StaleReasons <> 0) OR (ToStatus <> 3 AND StaleReasons = 0)");
                    table.ForeignKey(
                        name: "FK_ContentProposalTransitions_ContentProposals_WorkspaceId_ContentProposalId",
                        columns: x => new { x.WorkspaceId, x.ContentProposalId },
                        principalTable: "ContentProposals",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentProposalTransitions_ContentRevisions_WorkspaceId_ContentProposalId_ContentRevisionId",
                        columns: x => new { x.WorkspaceId, x.ContentProposalId, x.ContentRevisionId },
                        principalTable: "ContentRevisions",
                        principalColumns: new[] { "WorkspaceId", "ContentProposalId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentProposalTransitions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentProposals_Workspace_Status",
                table: "ContentProposals",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentProposals_WorkspaceId_Id_AcceptedRevisionId",
                table: "ContentProposals",
                columns: new[] { "WorkspaceId", "Id", "AcceptedRevisionId" });

            migrationBuilder.CreateIndex(
                name: "UX_ContentProposals_Workspace_Recipe_Kind",
                table: "ContentProposals",
                columns: new[] { "WorkspaceId", "RecipeId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentProposalTransitions_Workspace_Proposal_OccurredAt",
                table: "ContentProposalTransitions",
                columns: new[] { "WorkspaceId", "ContentProposalId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentProposalTransitions_WorkspaceId_ContentProposalId_ContentRevisionId",
                table: "ContentProposalTransitions",
                columns: new[] { "WorkspaceId", "ContentProposalId", "ContentRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentRevisions_Workspace_BrandProfileRevision",
                table: "ContentRevisions",
                columns: new[] { "WorkspaceId", "BrandProfileRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentRevisions_Workspace_RecipeVersion",
                table: "ContentRevisions",
                columns: new[] { "WorkspaceId", "RecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentRevisions_WorkspaceId_AiProposalId",
                table: "ContentRevisions",
                columns: new[] { "WorkspaceId", "AiProposalId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentRevisions_WorkspaceId_ContentProposalId_ParentRevisionId",
                table: "ContentRevisions",
                columns: new[] { "WorkspaceId", "ContentProposalId", "ParentRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentRevisions_WorkspaceId_ContentProposalId_RecipeId",
                table: "ContentRevisions",
                columns: new[] { "WorkspaceId", "ContentProposalId", "RecipeId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentRevisions_WorkspaceId_RecipeId_RecipeVersionId",
                table: "ContentRevisions",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "UX_ContentRevisions_Workspace_Proposal_RevisionNumber",
                table: "ContentRevisions",
                columns: new[] { "WorkspaceId", "ContentProposalId", "RevisionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ContentProposals_ContentRevisions_WorkspaceId_Id_AcceptedRevisionId",
                table: "ContentProposals",
                columns: new[] { "WorkspaceId", "Id", "AcceptedRevisionId" },
                principalTable: "ContentRevisions",
                principalColumns: new[] { "WorkspaceId", "ContentProposalId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ContentProposals_ContentRevisions_WorkspaceId_Id_AcceptedRevisionId",
                table: "ContentProposals");

            migrationBuilder.DropTable(
                name: "ContentProposalTransitions");

            migrationBuilder.DropTable(
                name: "ContentRevisions");

            migrationBuilder.DropTable(
                name: "ContentProposals");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_BrandProfileRevisions_Workspace_Id",
                table: "BrandProfileRevisions");
        }
    }
}
