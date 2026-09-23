using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecipeVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Readiness = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ParentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BasedOnRecipeRowVersion = table.Column<byte[]>(type: "varbinary(8)", maxLength: 8, nullable: true),
                    AiProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SnapshotSchemaVersion = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeVersions", x => x.Id);
                    table.UniqueConstraint("AK_RecipeVersions_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_RecipeVersions_Parent_NotSelf", "ParentVersionId IS NULL OR ParentVersionId <> Id");
                    table.CheckConstraint("CK_RecipeVersions_Proposal_Source", "(AiProposalId IS NULL AND Source <> 1) OR (AiProposalId IS NOT NULL AND Source = 1)");
                    table.CheckConstraint("CK_RecipeVersions_VersionNumber_Positive", "VersionNumber >= 1");
                    table.ForeignKey(
                        name: "FK_RecipeVersions_RecipeVersions_WorkspaceId_ParentVersionId",
                        columns: x => new { x.WorkspaceId, x.ParentVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeVersions_Recipes_WorkspaceId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.RecipeId },
                        principalTable: "Recipes",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeVersions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeVersionSnapshots",
                columns: table => new
                {
                    RecipeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Document = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeVersionSnapshots", x => x.RecipeVersionId);
                    table.ForeignKey(
                        name: "FK_RecipeVersionSnapshots_RecipeVersions_WorkspaceId_RecipeVersionId",
                        columns: x => new { x.WorkspaceId, x.RecipeVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeVersions_Workspace_Recipe_CreatedAt",
                table: "RecipeVersions",
                columns: new[] { "WorkspaceId", "RecipeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeVersions_Workspace_SnapshotSchemaVersion",
                table: "RecipeVersions",
                columns: new[] { "WorkspaceId", "SnapshotSchemaVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeVersions_WorkspaceId_ParentVersionId",
                table: "RecipeVersions",
                columns: new[] { "WorkspaceId", "ParentVersionId" });

            migrationBuilder.CreateIndex(
                name: "UX_RecipeVersions_Workspace_Recipe_VersionNumber",
                table: "RecipeVersions",
                columns: new[] { "WorkspaceId", "RecipeId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeVersionSnapshots_WorkspaceId_RecipeVersionId",
                table: "RecipeVersionSnapshots",
                columns: new[] { "WorkspaceId", "RecipeVersionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecipeVersionSnapshots");

            migrationBuilder.DropTable(
                name: "RecipeVersions");
        }
    }
}
