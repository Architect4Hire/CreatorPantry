using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class MediaRenditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaRenditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GeneratedImageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MediaAssetVersionNumber = table.Column<int>(type: "int", nullable: true),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    NotCompressedReason = table.Column<int>(type: "int", nullable: true),
                    SourceContentChecksum = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MediaType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    ContentChecksum = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaRenditions", x => x.Id);
                    table.CheckConstraint("CK_MediaRenditions_Bytes_Positive", "SizeBytes IS NULL OR (SizeBytes > 0 AND Width > 0 AND Height > 0)");
                    table.CheckConstraint("CK_MediaRenditions_OneSource", "(GeneratedImageId IS NOT NULL AND MediaAssetId IS NULL AND MediaAssetVersionNumber IS NULL) OR (GeneratedImageId IS NULL AND MediaAssetId IS NOT NULL AND MediaAssetVersionNumber IS NOT NULL)");
                    table.CheckConstraint("CK_MediaRenditions_Purpose_Declared", "Purpose IN (1, 2)");
                    table.CheckConstraint("CK_MediaRenditions_Reason_Declared", "NotCompressedReason IS NULL OR NotCompressedReason BETWEEN 1 AND 6");
                    table.CheckConstraint("CK_MediaRenditions_Status_Agrees", "(Status = 1 AND NotCompressedReason IS NULL AND ObjectKey IS NOT NULL AND MediaType IS NOT NULL AND SizeBytes IS NOT NULL AND Width IS NOT NULL AND Height IS NOT NULL AND ContentChecksum IS NOT NULL) OR (Status = 2 AND NotCompressedReason IS NOT NULL AND ObjectKey IS NULL AND MediaType IS NULL AND SizeBytes IS NULL AND Width IS NULL AND Height IS NULL AND ContentChecksum IS NULL)");
                    table.CheckConstraint("CK_MediaRenditions_Text_NotBlank", "trim(SourceContentChecksum) <> '' AND (ObjectKey IS NULL OR (trim(ObjectKey) <> '' AND trim(MediaType) <> '' AND trim(ContentChecksum) <> ''))");
                    table.ForeignKey(
                        name: "FK_MediaRenditions_GeneratedImages_WorkspaceId_GeneratedImageId",
                        columns: x => new { x.WorkspaceId, x.GeneratedImageId },
                        principalTable: "GeneratedImages",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaRenditions_MediaAssetVersions_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                        columns: x => new { x.WorkspaceId, x.MediaAssetId, x.MediaAssetVersionNumber },
                        principalTable: "MediaAssetVersions",
                        principalColumns: new[] { "WorkspaceId", "MediaAssetId", "VersionNumber" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaRenditions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_MediaRenditions_ObjectKey",
                table: "MediaRenditions",
                column: "ObjectKey",
                unique: true,
                filter: "ObjectKey IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_MediaRenditions_Workspace_Asset_Version_Purpose",
                table: "MediaRenditions",
                columns: new[] { "WorkspaceId", "MediaAssetId", "MediaAssetVersionNumber", "Purpose" },
                unique: true,
                filter: "MediaAssetId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_MediaRenditions_Workspace_GeneratedImage_Purpose",
                table: "MediaRenditions",
                columns: new[] { "WorkspaceId", "GeneratedImageId", "Purpose" },
                unique: true,
                filter: "GeneratedImageId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaRenditions");
        }
    }
}
