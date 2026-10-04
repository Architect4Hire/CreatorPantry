using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PromptRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ImageKind = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    GeneratedText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Source = table.Column<int>(type: "int", nullable: false),
                    AiProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecipeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GeneratedImageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DamAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PromptTemplateId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PromptTemplateVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PromptTemplateBodyChecksum = table.Column<string>(type: "nvarchar(71)", maxLength: 71, nullable: true),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptRecords", x => x.Id);
                    table.CheckConstraint("CK_PromptRecords_AiProposal_Source", "(AiProposalId IS NOT NULL AND Source <> 0) OR (AiProposalId IS NULL AND Source = 0)");
                    table.CheckConstraint("CK_PromptRecords_ChannelKey_NotBlank", "trim(ChannelKey) <> ''");
                    table.CheckConstraint("CK_PromptRecords_GeneratedText_Source", "(GeneratedText IS NULL AND Source = 0) OR (GeneratedText IS NOT NULL AND Source <> 0)");
                    table.CheckConstraint("CK_PromptRecords_ImageKind_Range", "ImageKind >= 0 AND ImageKind <= 7");
                    table.CheckConstraint("CK_PromptRecords_RecipeVersion_RequiresRecipe", "RecipeVersionId IS NULL OR RecipeId IS NOT NULL");
                    table.CheckConstraint("CK_PromptRecords_Source_Range", "Source >= 0 AND Source <= 3");
                    table.CheckConstraint("CK_PromptRecords_Template_Generated", "(Source = 0 AND PromptTemplateId IS NULL AND PromptTemplateVersion IS NULL AND PromptTemplateBodyChecksum IS NULL) OR (Source <> 0 AND PromptTemplateId IS NOT NULL AND PromptTemplateVersion IS NOT NULL AND PromptTemplateBodyChecksum IS NOT NULL)");
                    table.CheckConstraint("CK_PromptRecords_Text_NotBlank", "trim(Text) <> ''");
                    table.ForeignKey(
                        name: "FK_PromptRecords_AiProposals_WorkspaceId_AiProposalId",
                        columns: x => new { x.WorkspaceId, x.AiProposalId },
                        principalTable: "AiProposals",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PromptRecords_RecipeVersions_WorkspaceId_RecipeId_RecipeVersionId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.RecipeVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PromptRecords_Recipes_WorkspaceId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.RecipeId },
                        principalTable: "Recipes",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PromptRecords_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PromptRecords_Workspace_Channel_Created",
                table: "PromptRecords",
                columns: new[] { "WorkspaceId", "ChannelKey", "CreatedAt", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_PromptRecords_Workspace_Created",
                table: "PromptRecords",
                columns: new[] { "WorkspaceId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_PromptRecords_WorkspaceId_AiProposalId",
                table: "PromptRecords",
                columns: new[] { "WorkspaceId", "AiProposalId" });

            migrationBuilder.CreateIndex(
                name: "IX_PromptRecords_WorkspaceId_RecipeId_RecipeVersionId",
                table: "PromptRecords",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "UX_PromptRecords_Workspace_GeneratedImage",
                table: "PromptRecords",
                columns: new[] { "WorkspaceId", "GeneratedImageId" },
                unique: true,
                filter: "GeneratedImageId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PromptRecords");
        }
    }
}
