using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkspaceTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceTags", x => x.Id);
                    table.UniqueConstraint("AK_WorkspaceTags_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.ForeignKey(
                        name: "FK_WorkspaceTags_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeTags",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceTagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeTags", x => new { x.WorkspaceId, x.RecipeId, x.WorkspaceTagId });
                    table.ForeignKey(
                        name: "FK_RecipeTags_Recipes_WorkspaceId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.RecipeId },
                        principalTable: "Recipes",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecipeTags_WorkspaceTags_WorkspaceId_WorkspaceTagId",
                        columns: x => new { x.WorkspaceId, x.WorkspaceTagId },
                        principalTable: "WorkspaceTags",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeTags_Workspace_Tag",
                table: "RecipeTags",
                columns: new[] { "WorkspaceId", "WorkspaceTagId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceTags_Workspace_IsActive_Name",
                table: "WorkspaceTags",
                columns: new[] { "WorkspaceId", "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkspaceTags_Workspace_NormalizedName",
                table: "WorkspaceTags",
                columns: new[] { "WorkspaceId", "NormalizedName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecipeTags");

            migrationBuilder.DropTable(
                name: "WorkspaceTags");
        }
    }
}
