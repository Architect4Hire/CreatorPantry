using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class SocialPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SocialPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreativeContextId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPackages", x => x.Id);
                    table.UniqueConstraint("AK_SocialPackages_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.ForeignKey(
                        name: "FK_SocialPackages_CreativeContexts_WorkspaceId_CreativeContextId",
                        columns: x => new { x.WorkspaceId, x.CreativeContextId },
                        principalTable: "CreativeContexts",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SocialPackages_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialPackageChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocialPackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AcceptedRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StaleSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StaleReasons = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPackageChannels", x => x.Id);
                    table.UniqueConstraint("AK_SocialPackageChannels_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_SocialPackageChannels_Accepted_HasRevision", "Status NOT IN (1, 3) OR AcceptedRevisionId IS NOT NULL");
                    table.CheckConstraint("CK_SocialPackageChannels_ChannelKey_NotBlank", "trim(ChannelKey) <> ''");
                    table.CheckConstraint("CK_SocialPackageChannels_Staleness_Status", "(Status = 3 AND StaleSince IS NOT NULL AND StaleReasons <> 0) OR (Status <> 3 AND StaleSince IS NULL AND StaleReasons = 0)");
                    table.CheckConstraint("CK_SocialPackageChannels_Status_Range", "Status >= 0 AND Status <= 3");
                    table.ForeignKey(
                        name: "FK_SocialPackageChannels_SocialPackages_WorkspaceId_SocialPackageId",
                        columns: x => new { x.WorkspaceId, x.SocialPackageId },
                        principalTable: "SocialPackages",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocialPackageChannelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNumber = table.Column<int>(type: "int", nullable: false),
                    ParentRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<int>(type: "int", nullable: false),
                    AiProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CharacterCount = table.Column<int>(type: "int", nullable: true),
                    CharacterLimit = table.Column<int>(type: "int", nullable: true),
                    LimitStatus = table.Column<int>(type: "int", nullable: false),
                    ChannelProfileVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecipeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BrandProfileRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BrandStyleGuideVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PromptTemplateId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PromptTemplateVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PromptTemplateBodyChecksum = table.Column<string>(type: "nvarchar(71)", maxLength: 71, nullable: true),
                    CreativeContextVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ContextPackageChecksum = table.Column<string>(type: "nvarchar(71)", maxLength: 71, nullable: true),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialRevisions", x => x.Id);
                    table.UniqueConstraint("AK_SocialRevisions_Workspace_Channel_Id", x => new { x.WorkspaceId, x.SocialPackageChannelId, x.Id });
                    table.CheckConstraint("CK_SocialRevisions_AiProposal_Source", "(AiProposalId IS NOT NULL AND Source = 0) OR (AiProposalId IS NULL AND Source <> 0)");
                    table.CheckConstraint("CK_SocialRevisions_Body_NotBlank", "trim(Body) <> ''");
                    table.CheckConstraint("CK_SocialRevisions_CharacterCount_NonNegative", "CharacterCount IS NULL OR CharacterCount >= 0");
                    table.CheckConstraint("CK_SocialRevisions_CharacterLimit_Positive", "CharacterLimit IS NULL OR CharacterLimit >= 1");
                    table.CheckConstraint("CK_SocialRevisions_Limit_Status", "(LimitStatus = 0 AND CharacterCount IS NULL AND CharacterLimit IS NULL AND ChannelProfileVersion IS NULL) OR (LimitStatus = 1 AND CharacterCount IS NOT NULL AND ChannelProfileVersion IS NOT NULL AND (CharacterLimit IS NULL OR CharacterCount <= CharacterLimit)) OR (LimitStatus = 2 AND CharacterCount IS NOT NULL AND ChannelProfileVersion IS NOT NULL AND CharacterLimit IS NOT NULL AND CharacterCount > CharacterLimit)");
                    table.CheckConstraint("CK_SocialRevisions_Parent_NotSelf", "ParentRevisionId IS NULL OR ParentRevisionId <> Id");
                    table.CheckConstraint("CK_SocialRevisions_Parent_RevisionNumber", "(RevisionNumber = 1 AND ParentRevisionId IS NULL) OR (RevisionNumber > 1 AND ParentRevisionId IS NOT NULL)");
                    table.CheckConstraint("CK_SocialRevisions_Reaffirmed_HasParent", "Source <> 2 OR ParentRevisionId IS NOT NULL");
                    table.CheckConstraint("CK_SocialRevisions_RecipePin_Whole", "(RecipeId IS NULL AND RecipeVersionId IS NULL) OR (RecipeId IS NOT NULL AND RecipeVersionId IS NOT NULL)");
                    table.CheckConstraint("CK_SocialRevisions_RevisionNumber_Positive", "RevisionNumber >= 1");
                    table.CheckConstraint("CK_SocialRevisions_Template_Generated", "Source <> 0 OR (PromptTemplateId IS NOT NULL AND PromptTemplateVersion IS NOT NULL AND PromptTemplateBodyChecksum IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_SocialRevisions_AiProposals_WorkspaceId_AiProposalId",
                        columns: x => new { x.WorkspaceId, x.AiProposalId },
                        principalTable: "AiProposals",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SocialRevisions_BrandProfileRevisions_WorkspaceId_BrandProfileRevisionId",
                        columns: x => new { x.WorkspaceId, x.BrandProfileRevisionId },
                        principalTable: "BrandProfileRevisions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SocialRevisions_BrandStyleGuideVersions_WorkspaceId_BrandStyleGuideVersionId",
                        columns: x => new { x.WorkspaceId, x.BrandStyleGuideVersionId },
                        principalTable: "BrandStyleGuideVersions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SocialRevisions_RecipeVersions_WorkspaceId_RecipeId_RecipeVersionId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.RecipeVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SocialRevisions_SocialPackageChannels_WorkspaceId_SocialPackageChannelId",
                        columns: x => new { x.WorkspaceId, x.SocialPackageChannelId },
                        principalTable: "SocialPackageChannels",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SocialRevisions_SocialRevisions_WorkspaceId_SocialPackageChannelId_ParentRevisionId",
                        columns: x => new { x.WorkspaceId, x.SocialPackageChannelId, x.ParentRevisionId },
                        principalTable: "SocialRevisions",
                        principalColumns: new[] { "WorkspaceId", "SocialPackageChannelId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SocialRevisions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPackageChannels_Workspace_Status",
                table: "SocialPackageChannels",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPackageChannels_WorkspaceId_Id_AcceptedRevisionId",
                table: "SocialPackageChannels",
                columns: new[] { "WorkspaceId", "Id", "AcceptedRevisionId" });

            migrationBuilder.CreateIndex(
                name: "UX_SocialPackageChannels_Workspace_Package_Channel",
                table: "SocialPackageChannels",
                columns: new[] { "WorkspaceId", "SocialPackageId", "ChannelKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_SocialPackages_Workspace_Context",
                table: "SocialPackages",
                columns: new[] { "WorkspaceId", "CreativeContextId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialRevisions_Workspace_RecipeVersion",
                table: "SocialRevisions",
                columns: new[] { "WorkspaceId", "RecipeVersionId" },
                filter: "RecipeVersionId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SocialRevisions_WorkspaceId_AiProposalId",
                table: "SocialRevisions",
                columns: new[] { "WorkspaceId", "AiProposalId" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialRevisions_WorkspaceId_BrandProfileRevisionId",
                table: "SocialRevisions",
                columns: new[] { "WorkspaceId", "BrandProfileRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialRevisions_WorkspaceId_BrandStyleGuideVersionId",
                table: "SocialRevisions",
                columns: new[] { "WorkspaceId", "BrandStyleGuideVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialRevisions_WorkspaceId_RecipeId_RecipeVersionId",
                table: "SocialRevisions",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialRevisions_WorkspaceId_SocialPackageChannelId_ParentRevisionId",
                table: "SocialRevisions",
                columns: new[] { "WorkspaceId", "SocialPackageChannelId", "ParentRevisionId" });

            migrationBuilder.CreateIndex(
                name: "UX_SocialRevisions_Workspace_Channel_RevisionNumber",
                table: "SocialRevisions",
                columns: new[] { "WorkspaceId", "SocialPackageChannelId", "RevisionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SocialPackageChannels_SocialRevisions_WorkspaceId_Id_AcceptedRevisionId",
                table: "SocialPackageChannels",
                columns: new[] { "WorkspaceId", "Id", "AcceptedRevisionId" },
                principalTable: "SocialRevisions",
                principalColumns: new[] { "WorkspaceId", "SocialPackageChannelId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SocialPackageChannels_SocialPackages_WorkspaceId_SocialPackageId",
                table: "SocialPackageChannels");

            migrationBuilder.DropForeignKey(
                name: "FK_SocialPackageChannels_SocialRevisions_WorkspaceId_Id_AcceptedRevisionId",
                table: "SocialPackageChannels");

            migrationBuilder.DropTable(
                name: "SocialPackages");

            migrationBuilder.DropTable(
                name: "SocialRevisions");

            migrationBuilder.DropTable(
                name: "SocialPackageChannels");
        }
    }
}
