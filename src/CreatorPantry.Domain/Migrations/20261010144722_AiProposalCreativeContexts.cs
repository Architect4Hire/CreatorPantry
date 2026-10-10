using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AiProposalCreativeContexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiProposalCreativeContexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreativeContextId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContextVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Checksum = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecipeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EstimatedTokens = table.Column<int>(type: "int", nullable: false),
                    AssembledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProposalCreativeContexts", x => x.Id);
                    table.CheckConstraint("CK_AiProposalCreativeContexts_EstimatedTokens_NonNegative", "EstimatedTokens >= 0");
                    table.CheckConstraint("CK_AiProposalCreativeContexts_RecipePin_Whole", "(RecipeId IS NULL AND RecipeVersionId IS NULL) OR (RecipeId IS NOT NULL AND RecipeVersionId IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_AiProposalCreativeContexts_AiProposals_WorkspaceId_AiProposalId",
                        columns: x => new { x.WorkspaceId, x.AiProposalId },
                        principalTable: "AiProposals",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiProposalCreativeContexts_Workspace_Context",
                table: "AiProposalCreativeContexts",
                columns: new[] { "WorkspaceId", "CreativeContextId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiProposalCreativeContexts_WorkspaceId_AiProposalId",
                table: "AiProposalCreativeContexts",
                columns: new[] { "WorkspaceId", "AiProposalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiProposalCreativeContexts");
        }
    }
}
