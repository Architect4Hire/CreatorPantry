using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowAiRequestDraftKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkflowStepRuns_DraftKind_Range",
                table: "WorkflowStepRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkflowRunLinks_ResourceKind_Range",
                table: "WorkflowRunLinks");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkflowStepRuns_DraftKind_Range",
                table: "WorkflowStepRuns",
                sql: "DraftResourceKind IS NULL OR (DraftResourceKind >= 1 AND DraftResourceKind <= 9)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkflowRunLinks_ResourceKind_Range",
                table: "WorkflowRunLinks",
                sql: "ResourceKind >= 1 AND ResourceKind <= 9");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkflowStepRuns_DraftKind_Range",
                table: "WorkflowStepRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkflowRunLinks_ResourceKind_Range",
                table: "WorkflowRunLinks");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkflowStepRuns_DraftKind_Range",
                table: "WorkflowStepRuns",
                sql: "DraftResourceKind IS NULL OR (DraftResourceKind >= 1 AND DraftResourceKind <= 8)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkflowRunLinks_ResourceKind_Range",
                table: "WorkflowRunLinks",
                sql: "ResourceKind >= 1 AND ResourceKind <= 8");
        }
    }
}
