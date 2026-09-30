using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandProfileRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BrandProfileRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    Document = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ChangedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandProfileRevisions", x => x.Id);
                    table.CheckConstraint("CK_BrandProfileRevisions_Revision_Positive", "Revision >= 1");
                    table.CheckConstraint("CK_BrandProfileRevisions_SchemaVersion_Positive", "SchemaVersion >= 1");
                    table.ForeignKey(
                        name: "FK_BrandProfileRevisions_BrandProfiles_WorkspaceId_BrandProfileId",
                        columns: x => new { x.WorkspaceId, x.BrandProfileId },
                        principalTable: "BrandProfiles",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_BrandProfileRevisions_Workspace_Profile_Revision",
                table: "BrandProfileRevisions",
                columns: new[] { "WorkspaceId", "BrandProfileId", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrandProfileRevisions");
        }
    }
}
