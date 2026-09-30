using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeStatusTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecipeStatusTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromStatus = table.Column<int>(type: "int", nullable: false),
                    ToStatus = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ActorMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    MachineVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReadinessRuleSetVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ReadinessEvaluatedVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeStatusTransitions", x => x.Id);
                    table.CheckConstraint("CK_RecipeStatusTransitions_Approval_Columns", "(ToStatus = 1 AND ReadinessRuleSetVersion IS NOT NULL AND ReadinessEvaluatedVersionId IS NOT NULL AND CreatedVersionId IS NOT NULL) OR (ToStatus <> 1 AND ReadinessRuleSetVersion IS NULL AND ReadinessEvaluatedVersionId IS NULL AND CreatedVersionId IS NULL)");
                    table.CheckConstraint("CK_RecipeStatusTransitions_States_Differ", "FromStatus <> ToStatus");
                    table.ForeignKey(
                        name: "FK_RecipeStatusTransitions_RecipeVersions_WorkspaceId_RecipeId_CreatedVersionId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.CreatedVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeStatusTransitions_RecipeVersions_WorkspaceId_RecipeId_ReadinessEvaluatedVersionId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.ReadinessEvaluatedVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeStatusTransitions_Recipes_WorkspaceId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.RecipeId },
                        principalTable: "Recipes",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeStatusTransitions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeStatusTransitions_Workspace_Recipe_OccurredAt",
                table: "RecipeStatusTransitions",
                columns: new[] { "WorkspaceId", "RecipeId", "OccurredAt", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeStatusTransitions_WorkspaceId_RecipeId_CreatedVersionId",
                table: "RecipeStatusTransitions",
                columns: new[] { "WorkspaceId", "RecipeId", "CreatedVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeStatusTransitions_WorkspaceId_RecipeId_ReadinessEvaluatedVersionId",
                table: "RecipeStatusTransitions",
                columns: new[] { "WorkspaceId", "RecipeId", "ReadinessEvaluatedVersionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecipeStatusTransitions");
        }
    }
}
