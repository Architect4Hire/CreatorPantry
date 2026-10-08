using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RecipeAssetLinkRolesAndPins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MediaAssetVersionNumber",
                table: "TestAttachmentLinks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InstructionStepId",
                table: "RecipeAssetLinks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MediaAssetVersionNumber",
                table: "RecipeAssetLinks",
                type: "int",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_RecipeInstructionSteps_WorkspaceId_Id",
                table: "RecipeInstructionSteps",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_MediaAssetVersions_WorkspaceId_MediaAssetId_VersionNumber",
                table: "MediaAssetVersions",
                columns: new[] { "WorkspaceId", "MediaAssetId", "VersionNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_TestAttachmentLinks_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                table: "TestAttachmentLinks",
                columns: new[] { "WorkspaceId", "MediaAssetId", "MediaAssetVersionNumber" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_TestAttachmentLinks_VersionPin_Positive",
                table: "TestAttachmentLinks",
                sql: "MediaAssetVersionNumber IS NULL OR MediaAssetVersionNumber >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeAssetLinks_Workspace_InstructionStep",
                table: "RecipeAssetLinks",
                columns: new[] { "WorkspaceId", "InstructionStepId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeAssetLinks_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                table: "RecipeAssetLinks",
                columns: new[] { "WorkspaceId", "MediaAssetId", "MediaAssetVersionNumber" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecipeAssetLinks_Step_Paired",
                table: "RecipeAssetLinks",
                sql: "(Role = 5 AND InstructionStepId IS NOT NULL) OR (Role <> 5 AND InstructionStepId IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecipeAssetLinks_VersionPin_Positive",
                table: "RecipeAssetLinks",
                sql: "MediaAssetVersionNumber IS NULL OR MediaAssetVersionNumber >= 1");

            migrationBuilder.AddForeignKey(
                name: "FK_RecipeAssetLinks_MediaAssetVersions_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                table: "RecipeAssetLinks",
                columns: new[] { "WorkspaceId", "MediaAssetId", "MediaAssetVersionNumber" },
                principalTable: "MediaAssetVersions",
                principalColumns: new[] { "WorkspaceId", "MediaAssetId", "VersionNumber" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RecipeAssetLinks_RecipeInstructionSteps_WorkspaceId_InstructionStepId",
                table: "RecipeAssetLinks",
                columns: new[] { "WorkspaceId", "InstructionStepId" },
                principalTable: "RecipeInstructionSteps",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TestAttachmentLinks_MediaAssetVersions_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                table: "TestAttachmentLinks",
                columns: new[] { "WorkspaceId", "MediaAssetId", "MediaAssetVersionNumber" },
                principalTable: "MediaAssetVersions",
                principalColumns: new[] { "WorkspaceId", "MediaAssetId", "VersionNumber" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RecipeAssetLinks_MediaAssetVersions_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                table: "RecipeAssetLinks");

            migrationBuilder.DropForeignKey(
                name: "FK_RecipeAssetLinks_RecipeInstructionSteps_WorkspaceId_InstructionStepId",
                table: "RecipeAssetLinks");

            migrationBuilder.DropForeignKey(
                name: "FK_TestAttachmentLinks_MediaAssetVersions_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                table: "TestAttachmentLinks");

            migrationBuilder.DropIndex(
                name: "IX_TestAttachmentLinks_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                table: "TestAttachmentLinks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TestAttachmentLinks_VersionPin_Positive",
                table: "TestAttachmentLinks");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_RecipeInstructionSteps_WorkspaceId_Id",
                table: "RecipeInstructionSteps");

            migrationBuilder.DropIndex(
                name: "IX_RecipeAssetLinks_Workspace_InstructionStep",
                table: "RecipeAssetLinks");

            migrationBuilder.DropIndex(
                name: "IX_RecipeAssetLinks_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                table: "RecipeAssetLinks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecipeAssetLinks_Step_Paired",
                table: "RecipeAssetLinks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecipeAssetLinks_VersionPin_Positive",
                table: "RecipeAssetLinks");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_MediaAssetVersions_WorkspaceId_MediaAssetId_VersionNumber",
                table: "MediaAssetVersions");

            migrationBuilder.DropColumn(
                name: "MediaAssetVersionNumber",
                table: "TestAttachmentLinks");

            migrationBuilder.DropColumn(
                name: "InstructionStepId",
                table: "RecipeAssetLinks");

            migrationBuilder.DropColumn(
                name: "MediaAssetVersionNumber",
                table: "RecipeAssetLinks");
        }
    }
}
