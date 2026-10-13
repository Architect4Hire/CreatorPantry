using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddContentBoard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContentCards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Column = table.Column<int>(type: "int", nullable: false),
                    SortRank = table.Column<long>(type: "bigint", nullable: false),
                    ChannelKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PlannedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PlannedDayOfWeek = table.Column<int>(type: "int", nullable: true),
                    CuisineCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DishTypeCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MethodCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreativeContextId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecipeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PromptRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MediaAssetVersionNumber = table.Column<int>(type: "int", nullable: true),
                    SocialPackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentCards", x => x.Id);
                    table.UniqueConstraint("AK_ContentCards_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_ContentCards_AssetVersion_WithAsset", "MediaAssetVersionNumber IS NULL OR (MediaAssetId IS NOT NULL AND MediaAssetVersionNumber >= 1)");
                    table.CheckConstraint("CK_ContentCards_Column_Range", "\"Column\" >= 1 AND \"Column\" <= 4");
                    table.CheckConstraint("CK_ContentCards_PlannedDay_Range", "PlannedDayOfWeek IS NULL OR (PlannedDayOfWeek >= 0 AND PlannedDayOfWeek <= 6)");
                    table.CheckConstraint("CK_ContentCards_PlannedDay_WithDate", "(PlannedDate IS NULL AND PlannedDayOfWeek IS NULL) OR (PlannedDate IS NOT NULL AND PlannedDayOfWeek IS NOT NULL)");
                    table.CheckConstraint("CK_ContentCards_RecipeVersion_WithRecipe", "RecipeVersionId IS NULL OR RecipeId IS NOT NULL");
                    table.CheckConstraint("CK_ContentCards_Title_NotBlank", "trim(Title) <> ''");
                    table.ForeignKey(
                        name: "FK_ContentCards_CreativeContexts_WorkspaceId_CreativeContextId",
                        columns: x => new { x.WorkspaceId, x.CreativeContextId },
                        principalTable: "CreativeContexts",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentCards_MediaAssetVersions_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                        columns: x => new { x.WorkspaceId, x.MediaAssetId, x.MediaAssetVersionNumber },
                        principalTable: "MediaAssetVersions",
                        principalColumns: new[] { "WorkspaceId", "MediaAssetId", "VersionNumber" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentCards_MediaAssets_WorkspaceId_MediaAssetId",
                        columns: x => new { x.WorkspaceId, x.MediaAssetId },
                        principalTable: "MediaAssets",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentCards_PromptRecords_WorkspaceId_PromptRecordId",
                        columns: x => new { x.WorkspaceId, x.PromptRecordId },
                        principalTable: "PromptRecords",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentCards_RecipeVersions_WorkspaceId_RecipeId_RecipeVersionId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.RecipeVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentCards_Recipes_WorkspaceId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.RecipeId },
                        principalTable: "Recipes",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentCards_SocialPackages_WorkspaceId_SocialPackageId",
                        columns: x => new { x.WorkspaceId, x.SocialPackageId },
                        principalTable: "SocialPackages",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentCards_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CardMoveHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentCardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    FromColumn = table.Column<int>(type: "int", nullable: true),
                    ToColumn = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ActorMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardMoveHistory", x => x.Id);
                    table.CheckConstraint("CK_CardMoveHistory_Columns_Differ", "FromColumn IS NULL OR FromColumn <> ToColumn");
                    table.CheckConstraint("CK_CardMoveHistory_FromColumn_Range", "FromColumn IS NULL OR (FromColumn >= 1 AND FromColumn <= 4)");
                    table.CheckConstraint("CK_CardMoveHistory_Sequence_Creation", "(Sequence = 1 AND FromColumn IS NULL) OR (Sequence > 1 AND FromColumn IS NOT NULL)");
                    table.CheckConstraint("CK_CardMoveHistory_ToColumn_Range", "ToColumn >= 1 AND ToColumn <= 4");
                    table.ForeignKey(
                        name: "FK_CardMoveHistory_ContentCards_WorkspaceId_ContentCardId",
                        columns: x => new { x.WorkspaceId, x.ContentCardId },
                        principalTable: "ContentCards",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeContentPackageLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentCardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EditorialProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EditorialRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SocialPackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WindowStartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WindowEndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WindowTimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    WindowLocalStartsAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WindowLocalEndsAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeContentPackageLinks", x => x.Id);
                    table.UniqueConstraint("AK_RecipeContentPackageLinks_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_RecipeContentPackageLinks_Editorial_Paired", "(EditorialProposalId IS NULL AND EditorialRevisionId IS NULL) OR (EditorialProposalId IS NOT NULL AND EditorialRevisionId IS NOT NULL)");
                    table.CheckConstraint("CK_RecipeContentPackageLinks_Window_Ordered", "WindowLocalStartsAt IS NULL OR WindowLocalEndsAt >= WindowLocalStartsAt");
                    table.CheckConstraint("CK_RecipeContentPackageLinks_Window_Whole", "(WindowStartsAt IS NULL AND WindowEndsAt IS NULL AND WindowTimeZoneId IS NULL AND WindowLocalStartsAt IS NULL AND WindowLocalEndsAt IS NULL) OR (WindowStartsAt IS NOT NULL AND WindowEndsAt IS NOT NULL AND WindowTimeZoneId IS NOT NULL AND WindowLocalStartsAt IS NOT NULL AND WindowLocalEndsAt IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_RecipeContentPackageLinks_ContentCards_WorkspaceId_ContentCardId",
                        columns: x => new { x.WorkspaceId, x.ContentCardId },
                        principalTable: "ContentCards",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecipeContentPackageLinks_ContentProposals_WorkspaceId_EditorialProposalId_RecipeId",
                        columns: x => new { x.WorkspaceId, x.EditorialProposalId, x.RecipeId },
                        principalTable: "ContentProposals",
                        principalColumns: new[] { "WorkspaceId", "Id", "RecipeId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeContentPackageLinks_ContentRevisions_WorkspaceId_EditorialProposalId_EditorialRevisionId",
                        columns: x => new { x.WorkspaceId, x.EditorialProposalId, x.EditorialRevisionId },
                        principalTable: "ContentRevisions",
                        principalColumns: new[] { "WorkspaceId", "ContentProposalId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeContentPackageLinks_RecipeVersions_WorkspaceId_RecipeId_RecipeVersionId",
                        columns: x => new { x.WorkspaceId, x.RecipeId, x.RecipeVersionId },
                        principalTable: "RecipeVersions",
                        principalColumns: new[] { "WorkspaceId", "RecipeId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeContentPackageLinks_SocialPackages_WorkspaceId_SocialPackageId",
                        columns: x => new { x.WorkspaceId, x.SocialPackageId },
                        principalTable: "SocialPackages",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecipeContentPackageLinkAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeContentPackageLinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MediaAssetVersionNumber = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeContentPackageLinkAssets", x => x.Id);
                    table.CheckConstraint("CK_RecipeContentPackageLinkAssets_SortOrder_NonNegative", "SortOrder >= 0");
                    table.CheckConstraint("CK_RecipeContentPackageLinkAssets_Version_Positive", "MediaAssetVersionNumber >= 1");
                    table.ForeignKey(
                        name: "FK_RecipeContentPackageLinkAssets_MediaAssetVersions_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                        columns: x => new { x.WorkspaceId, x.MediaAssetId, x.MediaAssetVersionNumber },
                        principalTable: "MediaAssetVersions",
                        principalColumns: new[] { "WorkspaceId", "MediaAssetId", "VersionNumber" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeContentPackageLinkAssets_RecipeContentPackageLinks_WorkspaceId_RecipeContentPackageLinkId",
                        columns: x => new { x.WorkspaceId, x.RecipeContentPackageLinkId },
                        principalTable: "RecipeContentPackageLinks",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeContentPackageLinkChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeContentPackageLinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SocialPackageChannelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SocialRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeContentPackageLinkChannels", x => x.Id);
                    table.CheckConstraint("CK_RecipeContentPackageLinkChannels_ChannelKey_NotBlank", "trim(ChannelKey) <> ''");
                    table.CheckConstraint("CK_RecipeContentPackageLinkChannels_Post_Paired", "(SocialPackageChannelId IS NULL AND SocialRevisionId IS NULL) OR (SocialPackageChannelId IS NOT NULL AND SocialRevisionId IS NOT NULL)");
                    table.CheckConstraint("CK_RecipeContentPackageLinkChannels_SortOrder_NonNegative", "SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_RecipeContentPackageLinkChannels_RecipeContentPackageLinks_WorkspaceId_RecipeContentPackageLinkId",
                        columns: x => new { x.WorkspaceId, x.RecipeContentPackageLinkId },
                        principalTable: "RecipeContentPackageLinks",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecipeContentPackageLinkChannels_SocialRevisions_WorkspaceId_SocialPackageChannelId_SocialRevisionId",
                        columns: x => new { x.WorkspaceId, x.SocialPackageChannelId, x.SocialRevisionId },
                        principalTable: "SocialRevisions",
                        principalColumns: new[] { "WorkspaceId", "SocialPackageChannelId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_CardMoveHistory_Workspace_Card_Sequence",
                table: "CardMoveHistory",
                columns: new[] { "WorkspaceId", "ContentCardId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentCards_Workspace_Channel",
                table: "ContentCards",
                columns: new[] { "WorkspaceId", "ChannelKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentCards_Workspace_Column_Rank",
                table: "ContentCards",
                columns: new[] { "WorkspaceId", "Column", "SortRank", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentCards_Workspace_PlannedDate",
                table: "ContentCards",
                columns: new[] { "WorkspaceId", "PlannedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentCards_Workspace_PlannedDay",
                table: "ContentCards",
                columns: new[] { "WorkspaceId", "PlannedDayOfWeek" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentCards_WorkspaceId_CreativeContextId",
                table: "ContentCards",
                columns: new[] { "WorkspaceId", "CreativeContextId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentCards_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                table: "ContentCards",
                columns: new[] { "WorkspaceId", "MediaAssetId", "MediaAssetVersionNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentCards_WorkspaceId_PromptRecordId",
                table: "ContentCards",
                columns: new[] { "WorkspaceId", "PromptRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentCards_WorkspaceId_RecipeId_RecipeVersionId",
                table: "ContentCards",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentCards_WorkspaceId_SocialPackageId",
                table: "ContentCards",
                columns: new[] { "WorkspaceId", "SocialPackageId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeContentPackageLinkAssets_WorkspaceId_MediaAssetId_MediaAssetVersionNumber",
                table: "RecipeContentPackageLinkAssets",
                columns: new[] { "WorkspaceId", "MediaAssetId", "MediaAssetVersionNumber" });

            migrationBuilder.CreateIndex(
                name: "UX_RecipeContentPackageLinkAssets_Workspace_Link_Asset",
                table: "RecipeContentPackageLinkAssets",
                columns: new[] { "WorkspaceId", "RecipeContentPackageLinkId", "MediaAssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RecipeContentPackageLinkAssets_Workspace_Link_SortOrder",
                table: "RecipeContentPackageLinkAssets",
                columns: new[] { "WorkspaceId", "RecipeContentPackageLinkId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeContentPackageLinkChannels_WorkspaceId_SocialPackageChannelId_SocialRevisionId",
                table: "RecipeContentPackageLinkChannels",
                columns: new[] { "WorkspaceId", "SocialPackageChannelId", "SocialRevisionId" });

            migrationBuilder.CreateIndex(
                name: "UX_RecipeContentPackageLinkChannels_Workspace_Link_Channel",
                table: "RecipeContentPackageLinkChannels",
                columns: new[] { "WorkspaceId", "RecipeContentPackageLinkId", "ChannelKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RecipeContentPackageLinkChannels_Workspace_Link_SortOrder",
                table: "RecipeContentPackageLinkChannels",
                columns: new[] { "WorkspaceId", "RecipeContentPackageLinkId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeContentPackageLinks_Workspace_RecipeVersion",
                table: "RecipeContentPackageLinks",
                columns: new[] { "WorkspaceId", "RecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeContentPackageLinks_WorkspaceId_EditorialProposalId_EditorialRevisionId",
                table: "RecipeContentPackageLinks",
                columns: new[] { "WorkspaceId", "EditorialProposalId", "EditorialRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeContentPackageLinks_WorkspaceId_EditorialProposalId_RecipeId",
                table: "RecipeContentPackageLinks",
                columns: new[] { "WorkspaceId", "EditorialProposalId", "RecipeId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeContentPackageLinks_WorkspaceId_RecipeId_RecipeVersionId",
                table: "RecipeContentPackageLinks",
                columns: new[] { "WorkspaceId", "RecipeId", "RecipeVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeContentPackageLinks_WorkspaceId_SocialPackageId",
                table: "RecipeContentPackageLinks",
                columns: new[] { "WorkspaceId", "SocialPackageId" });

            migrationBuilder.CreateIndex(
                name: "UX_RecipeContentPackageLinks_Workspace_Card",
                table: "RecipeContentPackageLinks",
                columns: new[] { "WorkspaceId", "ContentCardId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardMoveHistory");

            migrationBuilder.DropTable(
                name: "RecipeContentPackageLinkAssets");

            migrationBuilder.DropTable(
                name: "RecipeContentPackageLinkChannels");

            migrationBuilder.DropTable(
                name: "RecipeContentPackageLinks");

            migrationBuilder.DropTable(
                name: "ContentCards");
        }
    }
}
