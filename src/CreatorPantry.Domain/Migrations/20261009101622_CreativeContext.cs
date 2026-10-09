using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class CreativeContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_PromptRecords_WorkspaceId_Id",
                table: "PromptRecords",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateTable(
                name: "CreativeContexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkingTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PictureBrief = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Day = table.Column<int>(type: "int", nullable: true),
                    WeeklyThemeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreativeContexts", x => x.Id);
                    table.UniqueConstraint("AK_CreativeContexts_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_CreativeContexts_Day_Range", "Day IS NULL OR (Day >= 0 AND Day <= 6)");
                    table.CheckConstraint("CK_CreativeContexts_PictureBrief_NotBlank", "PictureBrief IS NULL OR trim(PictureBrief) <> ''");
                    table.CheckConstraint("CK_CreativeContexts_WeeklyThemeKey_NotBlank", "WeeklyThemeKey IS NULL OR trim(WeeklyThemeKey) <> ''");
                    table.CheckConstraint("CK_CreativeContexts_WorkingTitle_NotBlank", "WorkingTitle IS NULL OR trim(WorkingTitle) <> ''");
                    table.ForeignKey(
                        name: "FK_CreativeContexts_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CreativeContextChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreativeContextId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreativeContextChannels", x => x.Id);
                    table.CheckConstraint("CK_CreativeContextChannels_ChannelKey_NotBlank", "trim(ChannelKey) <> ''");
                    table.CheckConstraint("CK_CreativeContextChannels_SortOrder_NonNegative", "SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_CreativeContextChannels_CreativeContexts_WorkspaceId_CreativeContextId",
                        columns: x => new { x.WorkspaceId, x.CreativeContextId },
                        principalTable: "CreativeContexts",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CreativeContextReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreativeContextId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecipeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConceptRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConceptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MediaAssetVersionNumber = table.Column<int>(type: "int", nullable: true),
                    GeneratedImageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PromptRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SocialPackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreativeContextReferences", x => x.Id);
                    table.CheckConstraint("CK_CreativeContextReferences_Kind_Columns", "(Kind = 1 AND RecipeId IS NOT NULL AND ConceptRequestId IS NULL AND ConceptId IS NULL AND MediaAssetId IS NULL AND MediaAssetVersionNumber IS NULL AND GeneratedImageId IS NULL AND PromptRecordId IS NULL AND SocialPackageId IS NULL) OR (Kind = 2 AND RecipeId IS NULL AND RecipeVersionId IS NULL AND ConceptRequestId IS NOT NULL AND ConceptId IS NOT NULL AND MediaAssetId IS NULL AND MediaAssetVersionNumber IS NULL AND GeneratedImageId IS NULL AND PromptRecordId IS NULL AND SocialPackageId IS NULL) OR (Kind = 3 AND RecipeId IS NULL AND RecipeVersionId IS NULL AND ConceptRequestId IS NULL AND ConceptId IS NULL AND MediaAssetId IS NOT NULL AND GeneratedImageId IS NULL AND PromptRecordId IS NULL AND SocialPackageId IS NULL) OR (Kind = 4 AND RecipeId IS NULL AND RecipeVersionId IS NULL AND ConceptRequestId IS NULL AND ConceptId IS NULL AND MediaAssetId IS NULL AND MediaAssetVersionNumber IS NULL AND GeneratedImageId IS NOT NULL AND PromptRecordId IS NULL AND SocialPackageId IS NULL) OR (Kind = 5 AND RecipeId IS NULL AND RecipeVersionId IS NULL AND ConceptRequestId IS NULL AND ConceptId IS NULL AND MediaAssetId IS NULL AND MediaAssetVersionNumber IS NULL AND GeneratedImageId IS NULL AND PromptRecordId IS NOT NULL AND SocialPackageId IS NULL) OR (Kind = 6 AND RecipeId IS NULL AND RecipeVersionId IS NULL AND ConceptRequestId IS NULL AND ConceptId IS NULL AND MediaAssetId IS NULL AND MediaAssetVersionNumber IS NULL AND GeneratedImageId IS NULL AND PromptRecordId IS NULL AND SocialPackageId IS NOT NULL)");
                    table.CheckConstraint("CK_CreativeContextReferences_Kind_Range", "Kind >= 1 AND Kind <= 6");
                    table.CheckConstraint("CK_CreativeContextReferences_SortOrder_NonNegative", "SortOrder >= 0");
                    table.CheckConstraint("CK_CreativeContextReferences_VersionPin_Positive", "MediaAssetVersionNumber IS NULL OR MediaAssetVersionNumber >= 1");
                    table.ForeignKey(
                        name: "FK_CreativeContextReferences_AiOperations_WorkspaceId_ConceptRequestId",
                        columns: x => new { x.WorkspaceId, x.ConceptRequestId },
                        principalTable: "AiOperations",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreativeContextReferences_CreativeContexts_WorkspaceId_CreativeContextId",
                        columns: x => new { x.WorkspaceId, x.CreativeContextId },
                        principalTable: "CreativeContexts",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CreativeContextReferences_GeneratedImages_WorkspaceId_GeneratedImageId",
                        columns: x => new { x.WorkspaceId, x.GeneratedImageId },
                        principalTable: "GeneratedImages",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreativeContextReferences_MediaAssetVersions_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                        columns: x => new { x.WorkspaceId, x.MediaAssetId, x.MediaAssetVersionNumber },
                        principalTable: "MediaAssetVersions",
                        principalColumns: new[] { "WorkspaceId", "MediaAssetId", "VersionNumber" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreativeContextReferences_MediaAssets_WorkspaceId_MediaAssetId",
                        columns: x => new { x.WorkspaceId, x.MediaAssetId },
                        principalTable: "MediaAssets",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreativeContextReferences_PromptRecords_WorkspaceId_PromptRecordId",
                        columns: x => new { x.WorkspaceId, x.PromptRecordId },
                        principalTable: "PromptRecords",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreativeContextReferences_RecipeVersions_WorkspaceId_RecipeId_RecipeVersionId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.RecipeVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreativeContextReferences_Recipes_WorkspaceId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.RecipeId },
                        principalTable: "Recipes",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_CreativeContextChannels_Workspace_Context_Channel",
                table: "CreativeContextChannels",
                columns: new[] { "WorkspaceId", "CreativeContextId", "ChannelKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CreativeContextChannels_Workspace_Context_Order",
                table: "CreativeContextChannels",
                columns: new[] { "WorkspaceId", "CreativeContextId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreativeContextReferences_WorkspaceId_ConceptRequestId",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "ConceptRequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_CreativeContextReferences_WorkspaceId_GeneratedImageId",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "GeneratedImageId" });

            migrationBuilder.CreateIndex(
                name: "IX_CreativeContextReferences_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "MediaAssetId", "MediaAssetVersionNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_CreativeContextReferences_WorkspaceId_PromptRecordId",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "PromptRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_CreativeContextReferences_WorkspaceId_RecipeId_RecipeVersionId",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "UX_CreativeContextReferences_Context_Concept",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "CreativeContextId", "ConceptRequestId", "ConceptId" },
                unique: true,
                filter: "ConceptId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CreativeContextReferences_Context_GeneratedImage",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "CreativeContextId", "GeneratedImageId" },
                unique: true,
                filter: "GeneratedImageId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CreativeContextReferences_Context_MediaAsset",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "CreativeContextId", "MediaAssetId" },
                unique: true,
                filter: "MediaAssetId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CreativeContextReferences_Context_PromptRecord",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "CreativeContextId", "PromptRecordId" },
                unique: true,
                filter: "PromptRecordId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CreativeContextReferences_Context_Recipe",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "CreativeContextId", "RecipeId" },
                unique: true,
                filter: "RecipeId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CreativeContextReferences_Context_SocialPackage",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "CreativeContextId", "SocialPackageId" },
                unique: true,
                filter: "SocialPackageId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CreativeContextReferences_Workspace_Context_Order",
                table: "CreativeContextReferences",
                columns: new[] { "WorkspaceId", "CreativeContextId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreativeContexts_Workspace_Theme_Updated",
                table: "CreativeContexts",
                columns: new[] { "WorkspaceId", "WeeklyThemeKey", "UpdatedAt" },
                descending: new[] { false, false, true },
                filter: "WeeklyThemeKey IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CreativeContexts_Workspace_Updated",
                table: "CreativeContexts",
                columns: new[] { "WorkspaceId", "UpdatedAt", "Id" },
                descending: new[] { false, true, true },
                filter: "ArchivedAt IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreativeContextChannels");

            migrationBuilder.DropTable(
                name: "CreativeContextReferences");

            migrationBuilder.DropTable(
                name: "CreativeContexts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PromptRecords_WorkspaceId_Id",
                table: "PromptRecords");
        }
    }
}
