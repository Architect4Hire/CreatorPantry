using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowRunReplacedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReplacedByRunId",
                table: "WorkflowRuns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_Workspace_ReplacedBy",
                table: "WorkflowRuns",
                columns: new[] { "WorkspaceId", "ReplacedByRunId" },
                filter: "ReplacedByRunId IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkflowRuns_ReplacedBy_Abandoned",
                table: "WorkflowRuns",
                sql: "ReplacedByRunId IS NULL OR Status = 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_Workspace_ReplacedBy",
                table: "WorkflowRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkflowRuns_ReplacedBy_Abandoned",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "ReplacedByRunId",
                table: "WorkflowRuns");
        }
    }
}
