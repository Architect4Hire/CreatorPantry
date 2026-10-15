using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowRunAbandonment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AbandonReason",
                table: "WorkflowRuns",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AbandonedByMembershipId",
                table: "WorkflowRuns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkflowRuns_AbandonedBy_Abandoned",
                table: "WorkflowRuns",
                sql: "AbandonedByMembershipId IS NULL OR Status = 3");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkflowRuns_AbandonReason_Abandoned",
                table: "WorkflowRuns",
                sql: "AbandonReason IS NULL OR (Status = 3 AND trim(AbandonReason) <> '')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkflowRuns_AbandonedBy_Abandoned",
                table: "WorkflowRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkflowRuns_AbandonReason_Abandoned",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "AbandonReason",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "AbandonedByMembershipId",
                table: "WorkflowRuns");
        }
    }
}
