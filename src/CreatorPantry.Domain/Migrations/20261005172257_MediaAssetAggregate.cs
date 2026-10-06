using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class MediaAssetAggregate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    AltText = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ChannelKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PlatformKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Day = table.Column<int>(type: "int", nullable: true),
                    StyleKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CuisineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CourseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RightsHolder = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AttributionText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CurrentVersionNumber = table.Column<int>(type: "int", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAssets", x => x.Id);
                    table.UniqueConstraint("AK_MediaAssets_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_MediaAssets_CurrentVersionNumber_Positive", "CurrentVersionNumber >= 1");
                    table.CheckConstraint("CK_MediaAssets_Deleted_Complete", "(DeletedAt IS NULL AND DeletedByMembershipId IS NULL) OR (DeletedAt IS NOT NULL AND DeletedByMembershipId IS NOT NULL)");
                    table.CheckConstraint("CK_MediaAssets_Kind_Declared", "Kind <> 0");
                    table.CheckConstraint("CK_MediaAssets_Title_NotBlank", "trim(Title) <> ''");
                    table.ForeignKey(
                        name: "FK_MediaAssets_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaAssets_Cuisines_CuisineId",
                        column: x => x.CuisineId,
                        principalTable: "Cuisines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaAssets_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaAssetTags",
                columns: table => new
                {
                    MediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceTagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAssetTags", x => new { x.MediaAssetId, x.WorkspaceTagId });
                    table.ForeignKey(
                        name: "FK_MediaAssetTags_MediaAssets_WorkspaceId_MediaAssetId",
                        columns: x => new { x.WorkspaceId, x.MediaAssetId },
                        principalTable: "MediaAssets",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaAssetTags_WorkspaceTags_WorkspaceId_WorkspaceTagId",
                        columns: x => new { x.WorkspaceId, x.WorkspaceTagId },
                        principalTable: "WorkspaceTags",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaAssetTags_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaAssetUtilizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlatformKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UtilizedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    UtilizedDay = table.Column<int>(type: "int", nullable: false),
                    CampaignName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LoggedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAssetUtilizations", x => x.Id);
                    table.CheckConstraint("CK_MediaAssetUtilizations_PlatformKey_NotBlank", "trim(PlatformKey) <> ''");
                    table.ForeignKey(
                        name: "FK_MediaAssetUtilizations_MediaAssets_WorkspaceId_MediaAssetId",
                        columns: x => new { x.WorkspaceId, x.MediaAssetId },
                        principalTable: "MediaAssets",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaAssetUtilizations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaAssetVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    MediaType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    ContentChecksum = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: true),
                    ObjectKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    SourceGeneratedImageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAssetVersions", x => x.Id);
                    table.CheckConstraint("CK_MediaAssetVersions_Checksum_NotBlank", "trim(ContentChecksum) <> ''");
                    table.CheckConstraint("CK_MediaAssetVersions_Dimensions_Positive", "Width > 0 AND Height > 0");
                    table.CheckConstraint("CK_MediaAssetVersions_ObjectKey_NotBlank", "trim(ObjectKey) <> ''");
                    table.CheckConstraint("CK_MediaAssetVersions_Pixels_Range", "CAST(Width AS bigint) * CAST(Height AS bigint) <= 50000000");
                    table.CheckConstraint("CK_MediaAssetVersions_SizeBytes_Positive", "SizeBytes > 0");
                    table.CheckConstraint("CK_MediaAssetVersions_Source_Agrees", "(Source = 1 AND SourceGeneratedImageId IS NULL) OR (Source = 2 AND SourceGeneratedImageId IS NOT NULL)");
                    table.CheckConstraint("CK_MediaAssetVersions_Source_Declared", "Source <> 0");
                    table.CheckConstraint("CK_MediaAssetVersions_VersionNumber_Positive", "VersionNumber >= 1");
                    table.ForeignKey(
                        name: "FK_MediaAssetVersions_GeneratedImages_WorkspaceId_SourceGeneratedImageId",
                        columns: x => new { x.WorkspaceId, x.SourceGeneratedImageId },
                        principalTable: "GeneratedImages",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaAssetVersions_MediaAssets_WorkspaceId_MediaAssetId",
                        columns: x => new { x.WorkspaceId, x.MediaAssetId },
                        principalTable: "MediaAssets",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MediaAssetVersions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_CourseId",
                table: "MediaAssets",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_CuisineId",
                table: "MediaAssets",
                column: "CuisineId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_Workspace_CreatedAt",
                table: "MediaAssets",
                columns: new[] { "WorkspaceId", "CreatedAt", "Id" },
                descending: new[] { false, true, true },
                filter: "DeletedAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_Workspace_Title",
                table: "MediaAssets",
                columns: new[] { "WorkspaceId", "Title", "Id" },
                filter: "DeletedAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssetTags_Workspace_Tag_Asset",
                table: "MediaAssetTags",
                columns: new[] { "WorkspaceId", "WorkspaceTagId", "MediaAssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssetTags_WorkspaceId_MediaAssetId",
                table: "MediaAssetTags",
                columns: new[] { "WorkspaceId", "MediaAssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssetUtilizations_Workspace_Asset_UtilizedOn",
                table: "MediaAssetUtilizations",
                columns: new[] { "WorkspaceId", "MediaAssetId", "UtilizedOn" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssetVersions_Workspace_Asset_VersionNumber",
                table: "MediaAssetVersions",
                columns: new[] { "WorkspaceId", "MediaAssetId", "VersionNumber" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssetVersions_WorkspaceId_SourceGeneratedImageId",
                table: "MediaAssetVersions",
                columns: new[] { "WorkspaceId", "SourceGeneratedImageId" });

            migrationBuilder.CreateIndex(
                name: "UX_MediaAssetVersions_Asset_VersionNumber",
                table: "MediaAssetVersions",
                columns: new[] { "MediaAssetId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_MediaAssetVersions_ObjectKey",
                table: "MediaAssetVersions",
                column: "ObjectKey",
                unique: true);

            // Hand-added, and the one part of this migration that is not generated.
            //
            // RecipeAssetLinks, BrandAssetLinks and TestAttachmentLinks have carried a MediaAssetId with no
            // foreign key since Phase 2, so every value in them was accepted unchecked — and BrandAssetLink
            // is writable today through the brand profile endpoint, straight from a client-supplied
            // BrandAssetInput. Any database that has saved a brand profile with a logo therefore holds rows
            // naming assets that have never existed, and AddForeignKey below would fail on it. That is not
            // hypothetical: it is what a developer's own machine looks like.
            //
            // MediaAssets is empty at this point, so in practice this clears every such row. It is written
            // as "the ones that do not resolve" rather than "all of them" so it stays correct if the order
            // of this migration ever changes, and so the intent is legible: a link to an asset that does not
            // exist could never be read or rendered. Nothing creator-visible is lost that was not already
            // broken, and nothing is deleted that resolves.
            migrationBuilder.Sql(
                """
                DELETE FROM [RecipeAssetLinks]
                WHERE NOT EXISTS (
                    SELECT 1 FROM [MediaAssets] [a]
                    WHERE [a].[WorkspaceId] = [RecipeAssetLinks].[WorkspaceId]
                      AND [a].[Id] = [RecipeAssetLinks].[MediaAssetId]);
                """);

            migrationBuilder.Sql(
                """
                DELETE FROM [BrandAssetLinks]
                WHERE NOT EXISTS (
                    SELECT 1 FROM [MediaAssets] [a]
                    WHERE [a].[WorkspaceId] = [BrandAssetLinks].[WorkspaceId]
                      AND [a].[Id] = [BrandAssetLinks].[MediaAssetId]);
                """);

            migrationBuilder.Sql(
                """
                DELETE FROM [TestAttachmentLinks]
                WHERE NOT EXISTS (
                    SELECT 1 FROM [MediaAssets] [a]
                    WHERE [a].[WorkspaceId] = [TestAttachmentLinks].[WorkspaceId]
                      AND [a].[Id] = [TestAttachmentLinks].[MediaAssetId]);
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_BrandAssetLinks_MediaAssets_WorkspaceId_MediaAssetId",
                table: "BrandAssetLinks",
                columns: new[] { "WorkspaceId", "MediaAssetId" },
                principalTable: "MediaAssets",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RecipeAssetLinks_MediaAssets_WorkspaceId_MediaAssetId",
                table: "RecipeAssetLinks",
                columns: new[] { "WorkspaceId", "MediaAssetId" },
                principalTable: "MediaAssets",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TestAttachmentLinks_MediaAssets_WorkspaceId_MediaAssetId",
                table: "TestAttachmentLinks",
                columns: new[] { "WorkspaceId", "MediaAssetId" },
                principalTable: "MediaAssets",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BrandAssetLinks_MediaAssets_WorkspaceId_MediaAssetId",
                table: "BrandAssetLinks");

            migrationBuilder.DropForeignKey(
                name: "FK_RecipeAssetLinks_MediaAssets_WorkspaceId_MediaAssetId",
                table: "RecipeAssetLinks");

            migrationBuilder.DropForeignKey(
                name: "FK_TestAttachmentLinks_MediaAssets_WorkspaceId_MediaAssetId",
                table: "TestAttachmentLinks");

            migrationBuilder.DropTable(
                name: "MediaAssetTags");

            migrationBuilder.DropTable(
                name: "MediaAssetUtilizations");

            migrationBuilder.DropTable(
                name: "MediaAssetVersions");

            migrationBuilder.DropTable(
                name: "MediaAssets");
        }
    }
}
