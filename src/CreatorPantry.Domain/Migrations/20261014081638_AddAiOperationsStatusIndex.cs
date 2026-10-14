using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAiOperationsStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AiOperations_Workspace_Status_StatusChanged",
                table: "AiOperations",
                columns: new[] { "WorkspaceId", "Status", "StatusChangedAt", "Id" },
                descending: new[] { false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiOperations_Workspace_Status_StatusChanged",
                table: "AiOperations");
        }
    }
}
