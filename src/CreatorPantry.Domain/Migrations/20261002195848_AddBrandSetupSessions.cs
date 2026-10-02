using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandSetupSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BrandSetupSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CurrentStep = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FurthestStep = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CompletedSteps = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SkippedSteps = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DraftJson = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandSetupSessions", x => x.Id);
                    table.CheckConstraint("CK_BrandSetupSessions_Completed_Consistent", "(Status = 2 AND CompletedUtc IS NOT NULL) OR (Status = 1 AND CompletedUtc IS NULL)");
                    table.CheckConstraint("CK_BrandSetupSessions_Status_Specified", "Status IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_BrandSetupSessions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_BrandSetupSessions_Workspace_User",
                table: "BrandSetupSessions",
                columns: new[] { "WorkspaceId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrandSetupSessions");
        }
    }
}
