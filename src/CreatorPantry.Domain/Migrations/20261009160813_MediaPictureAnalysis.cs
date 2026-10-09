using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class MediaPictureAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaPictureAnalyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MediaAssetVersionNumber = table.Column<int>(type: "int", nullable: true),
                    GeneratedImageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContentChecksum = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ObservationsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AiOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PromptTemplateId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PromptTemplateVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AnalyzedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaPictureAnalyses", x => x.Id);
                    table.CheckConstraint("CK_MediaPictureAnalyses_ContentChecksum_NotBlank", "trim(ContentChecksum) <> ''");
                    table.CheckConstraint("CK_MediaPictureAnalyses_OnePicture", "(MediaAssetId IS NOT NULL AND MediaAssetVersionNumber IS NOT NULL AND GeneratedImageId IS NULL) OR (MediaAssetId IS NULL AND MediaAssetVersionNumber IS NULL AND GeneratedImageId IS NOT NULL)");
                    table.CheckConstraint("CK_MediaPictureAnalyses_VersionNumber_Range", "MediaAssetVersionNumber IS NULL OR MediaAssetVersionNumber >= 1");
                    table.ForeignKey(
                        name: "FK_MediaPictureAnalyses_GeneratedImages_WorkspaceId_GeneratedImageId",
                        columns: x => new { x.WorkspaceId, x.GeneratedImageId },
                        principalTable: "GeneratedImages",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaPictureAnalyses_MediaAssets_WorkspaceId_MediaAssetId",
                        columns: x => new { x.WorkspaceId, x.MediaAssetId },
                        principalTable: "MediaAssets",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaPictureAnalyses_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_MediaPictureAnalyses_Workspace_Asset_Version",
                table: "MediaPictureAnalyses",
                columns: new[] { "WorkspaceId", "MediaAssetId", "MediaAssetVersionNumber" },
                unique: true,
                filter: "MediaAssetId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_MediaPictureAnalyses_Workspace_GeneratedImage",
                table: "MediaPictureAnalyses",
                columns: new[] { "WorkspaceId", "GeneratedImageId" },
                unique: true,
                filter: "GeneratedImageId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaPictureAnalyses");
        }
    }
}
