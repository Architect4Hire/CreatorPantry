using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandSourceDocumentsAndStyleGuides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BrandSourceDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DocumentType = table.Column<int>(type: "int", nullable: false),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    ChannelKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Audience = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CurrentVersionNumber = table.Column<int>(type: "int", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RemovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RemovedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandSourceDocuments", x => x.Id);
                    table.UniqueConstraint("AK_BrandSourceDocuments_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_BrandSourceDocuments_Archived_HasTimestamp", "Status <> 2 OR ArchivedAt IS NOT NULL");
                    table.CheckConstraint("CK_BrandSourceDocuments_CurrentVersionNumber_Positive", "CurrentVersionNumber >= 1");
                    table.CheckConstraint("CK_BrandSourceDocuments_DocumentType_Specified", "DocumentType <> 0");
                    table.CheckConstraint("CK_BrandSourceDocuments_Purpose_Specified", "Purpose <> 0");
                    table.CheckConstraint("CK_BrandSourceDocuments_Removed_Consistent", "(Status = 3 AND RemovedAt IS NOT NULL AND RemovedByMembershipId IS NOT NULL) OR (Status <> 3 AND RemovedAt IS NULL AND RemovedByMembershipId IS NULL)");
                    table.CheckConstraint("CK_BrandSourceDocuments_Status_Specified", "Status <> 0");
                    table.CheckConstraint("CK_BrandSourceDocuments_Title_NotBlank", "trim(Title) <> ''");
                    table.ForeignKey(
                        name: "FK_BrandSourceDocuments_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandSourceTags",
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
                    table.PrimaryKey("PK_BrandSourceTags", x => x.Id);
                    table.UniqueConstraint("AK_BrandSourceTags_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_BrandSourceTags_Name_NotBlank", "trim(Name) <> ''");
                    table.ForeignKey(
                        name: "FK_BrandSourceTags_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandStyleGuides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandStyleGuides", x => x.Id);
                    table.UniqueConstraint("AK_BrandStyleGuides_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_BrandStyleGuides_Archived_HasTimestamp", "Status <> 2 OR ArchivedAt IS NOT NULL");
                    table.CheckConstraint("CK_BrandStyleGuides_DisplayName_NotBlank", "trim(DisplayName) <> ''");
                    table.CheckConstraint("CK_BrandStyleGuides_Status_Specified", "Status <> 0");
                    table.ForeignKey(
                        name: "FK_BrandStyleGuides_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandSourceDocumentVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    MediaType = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentChecksum = table.Column<string>(type: "nvarchar(71)", maxLength: 71, nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandSourceDocumentVersions", x => x.Id);
                    table.UniqueConstraint("AK_BrandSourceDocumentVersions_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_BrandSourceDocumentVersions_ContentChecksum_Sha256", "ContentChecksum LIKE 'sha256:%'");
                    table.CheckConstraint("CK_BrandSourceDocumentVersions_MediaType_NotBlank", "trim(MediaType) <> ''");
                    table.CheckConstraint("CK_BrandSourceDocumentVersions_ObjectKey_NotUrl", "trim(ObjectKey) <> '' AND ObjectKey NOT LIKE '%://%'");
                    table.CheckConstraint("CK_BrandSourceDocumentVersions_OriginalFileName_NotBlank", "trim(OriginalFileName) <> ''");
                    table.CheckConstraint("CK_BrandSourceDocumentVersions_SizeBytes_Positive", "SizeBytes > 0");
                    table.CheckConstraint("CK_BrandSourceDocumentVersions_VersionNumber_Positive", "VersionNumber >= 1");
                    table.ForeignKey(
                        name: "FK_BrandSourceDocumentVersions_BrandSourceDocuments_WorkspaceId_BrandSourceDocumentId",
                        columns: x => new { x.WorkspaceId, x.BrandSourceDocumentId },
                        principalTable: "BrandSourceDocuments",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BrandSourceDocumentVersions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandSourceDocumentTags",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceTagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandSourceDocumentTags", x => new { x.WorkspaceId, x.BrandSourceDocumentId, x.BrandSourceTagId });
                    table.ForeignKey(
                        name: "FK_BrandSourceDocumentTags_BrandSourceDocuments_WorkspaceId_BrandSourceDocumentId",
                        columns: x => new { x.WorkspaceId, x.BrandSourceDocumentId },
                        principalTable: "BrandSourceDocuments",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BrandSourceDocumentTags_BrandSourceTags_WorkspaceId_BrandSourceTagId",
                        columns: x => new { x.WorkspaceId, x.BrandSourceTagId },
                        principalTable: "BrandSourceTags",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BrandStyleGuideVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandStyleGuideId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    ParentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandStyleGuideVersions", x => x.Id);
                    table.UniqueConstraint("AK_BrandStyleGuideVersions_Workspace_Guide_Id", x => new { x.WorkspaceId, x.BrandStyleGuideId, x.Id });
                    table.UniqueConstraint("AK_BrandStyleGuideVersions_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_BrandStyleGuideVersions_Parent_NotSelf", "ParentVersionId IS NULL OR ParentVersionId <> Id");
                    table.CheckConstraint("CK_BrandStyleGuideVersions_Parent_VersionNumber", "VersionNumber > 1 OR ParentVersionId IS NULL");
                    table.CheckConstraint("CK_BrandStyleGuideVersions_VersionNumber_Positive", "VersionNumber >= 1");
                    table.ForeignKey(
                        name: "FK_BrandStyleGuideVersions_BrandStyleGuideVersions_WorkspaceId_BrandStyleGuideId_ParentVersionId",
                        columns: x => new { x.WorkspaceId, x.BrandStyleGuideId, x.ParentVersionId },
                        principalTable: "BrandStyleGuideVersions",
                        principalColumns: new[] { "WorkspaceId", "BrandStyleGuideId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BrandStyleGuideVersions_BrandStyleGuides_WorkspaceId_BrandStyleGuideId",
                        columns: x => new { x.WorkspaceId, x.BrandStyleGuideId },
                        principalTable: "BrandStyleGuides",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BrandStyleGuideVersions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandSourceExtractions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceDocumentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Origin = table.Column<int>(type: "int", nullable: false),
                    ExtractedTextObjectKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ContentChecksum = table.Column<string>(type: "nvarchar(71)", maxLength: 71, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandSourceExtractions", x => x.Id);
                    table.UniqueConstraint("AK_BrandSourceExtractions_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_BrandSourceExtractions_ContentChecksum_Sha256", "ContentChecksum IS NULL OR ContentChecksum LIKE 'sha256:%'");
                    table.CheckConstraint("CK_BrandSourceExtractions_Corrected_Succeeded_Attributed", "Origin <> 2 OR (Status = 1 AND CreatedByMembershipId IS NOT NULL)");
                    table.CheckConstraint("CK_BrandSourceExtractions_ObjectKey_NotUrl", "ExtractedTextObjectKey IS NULL OR (trim(ExtractedTextObjectKey) <> '' AND ExtractedTextObjectKey NOT LIKE '%://%')");
                    table.CheckConstraint("CK_BrandSourceExtractions_Ordinal_Positive", "Ordinal >= 1");
                    table.CheckConstraint("CK_BrandSourceExtractions_Origin_Specified", "Origin <> 0");
                    table.CheckConstraint("CK_BrandSourceExtractions_Status_Specified", "Status <> 0");
                    table.CheckConstraint("CK_BrandSourceExtractions_Succeeded_HasArtifact", "(Status = 1 AND ExtractedTextObjectKey IS NOT NULL AND ContentChecksum IS NOT NULL) OR (Status <> 1 AND ExtractedTextObjectKey IS NULL AND ContentChecksum IS NULL)");
                    table.ForeignKey(
                        name: "FK_BrandSourceExtractions_BrandSourceDocumentVersions_WorkspaceId_BrandSourceDocumentVersionId",
                        columns: x => new { x.WorkspaceId, x.BrandSourceDocumentVersionId },
                        principalTable: "BrandSourceDocumentVersions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BrandSourceExtractions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandStyleGuideApprovals",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandStyleGuideVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ApprovedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandStyleGuideApprovals", x => new { x.WorkspaceId, x.BrandStyleGuideVersionId });
                    table.ForeignKey(
                        name: "FK_BrandStyleGuideApprovals_BrandStyleGuideVersions_WorkspaceId_BrandStyleGuideVersionId",
                        columns: x => new { x.WorkspaceId, x.BrandStyleGuideVersionId },
                        principalTable: "BrandStyleGuideVersions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandStyleGuideRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandStyleGuideVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandStyleGuideRules", x => x.Id);
                    table.CheckConstraint("CK_BrandStyleGuideRules_Kind_Specified", "Kind <> 0");
                    table.CheckConstraint("CK_BrandStyleGuideRules_SortOrder_NonNegative", "SortOrder >= 0");
                    table.CheckConstraint("CK_BrandStyleGuideRules_Text_NotBlank", "trim(Text) <> ''");
                    table.ForeignKey(
                        name: "FK_BrandStyleGuideRules_BrandStyleGuideVersions_WorkspaceId_BrandStyleGuideVersionId",
                        columns: x => new { x.WorkspaceId, x.BrandStyleGuideVersionId },
                        principalTable: "BrandStyleGuideVersions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandStyleGuideSections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandStyleGuideVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SectionKey = table.Column<int>(type: "int", nullable: false),
                    ChannelKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandStyleGuideSections", x => x.Id);
                    table.CheckConstraint("CK_BrandStyleGuideSections_Body_NotBlank", "trim(Body) <> ''");
                    table.CheckConstraint("CK_BrandStyleGuideSections_ChannelKey_Variant", "(SectionKey = 12 AND trim(ChannelKey) <> '') OR (SectionKey <> 12 AND ChannelKey = '')");
                    table.CheckConstraint("CK_BrandStyleGuideSections_SectionKey_Specified", "SectionKey <> 0");
                    table.ForeignKey(
                        name: "FK_BrandStyleGuideSections_BrandStyleGuideVersions_WorkspaceId_BrandStyleGuideVersionId",
                        columns: x => new { x.WorkspaceId, x.BrandStyleGuideVersionId },
                        principalTable: "BrandStyleGuideVersions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandStyleGuideSourceLinks",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandStyleGuideVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandSourceDocumentVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandStyleGuideSourceLinks", x => new { x.WorkspaceId, x.BrandStyleGuideVersionId, x.BrandSourceDocumentVersionId });
                    table.ForeignKey(
                        name: "FK_BrandStyleGuideSourceLinks_BrandSourceDocumentVersions_WorkspaceId_BrandSourceDocumentVersionId",
                        columns: x => new { x.WorkspaceId, x.BrandSourceDocumentVersionId },
                        principalTable: "BrandSourceDocumentVersions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BrandStyleGuideSourceLinks_BrandStyleGuideVersions_WorkspaceId_BrandStyleGuideVersionId",
                        columns: x => new { x.WorkspaceId, x.BrandStyleGuideVersionId },
                        principalTable: "BrandStyleGuideVersions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandStyleGuideDefaults",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandStyleGuideVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ActivatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandStyleGuideDefaults", x => x.WorkspaceId);
                    table.ForeignKey(
                        name: "FK_BrandStyleGuideDefaults_BrandStyleGuideApprovals_WorkspaceId_BrandStyleGuideVersionId",
                        columns: x => new { x.WorkspaceId, x.BrandStyleGuideVersionId },
                        principalTable: "BrandStyleGuideApprovals",
                        principalColumns: new[] { "WorkspaceId", "BrandStyleGuideVersionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BrandStyleGuideDefaults_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrandSourceDocuments_Workspace_ChannelKey",
                table: "BrandSourceDocuments",
                columns: new[] { "WorkspaceId", "ChannelKey" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandSourceDocuments_Workspace_DocumentType",
                table: "BrandSourceDocuments",
                columns: new[] { "WorkspaceId", "DocumentType" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandSourceDocuments_Workspace_Status_UpdatedAt",
                table: "BrandSourceDocuments",
                columns: new[] { "WorkspaceId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandSourceDocumentTags_Workspace_Tag",
                table: "BrandSourceDocumentTags",
                columns: new[] { "WorkspaceId", "BrandSourceTagId" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandSourceDocumentVersions_Workspace_ContentChecksum",
                table: "BrandSourceDocumentVersions",
                columns: new[] { "WorkspaceId", "ContentChecksum" });

            migrationBuilder.CreateIndex(
                name: "UX_BrandSourceDocumentVersions_Workspace_Document_VersionNumber",
                table: "BrandSourceDocumentVersions",
                columns: new[] { "WorkspaceId", "BrandSourceDocumentId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BrandSourceDocumentVersions_Workspace_ObjectKey",
                table: "BrandSourceDocumentVersions",
                columns: new[] { "WorkspaceId", "ObjectKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BrandSourceExtractions_Workspace_ObjectKey",
                table: "BrandSourceExtractions",
                columns: new[] { "WorkspaceId", "ExtractedTextObjectKey" },
                unique: true,
                filter: "[ExtractedTextObjectKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_BrandSourceExtractions_Workspace_Version_Ordinal",
                table: "BrandSourceExtractions",
                columns: new[] { "WorkspaceId", "BrandSourceDocumentVersionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BrandSourceTags_Workspace_IsActive_Name",
                table: "BrandSourceTags",
                columns: new[] { "WorkspaceId", "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "UX_BrandSourceTags_Workspace_NormalizedName",
                table: "BrandSourceTags",
                columns: new[] { "WorkspaceId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BrandStyleGuideDefaults_Workspace_Version",
                table: "BrandStyleGuideDefaults",
                columns: new[] { "WorkspaceId", "BrandStyleGuideVersionId" });

            migrationBuilder.CreateIndex(
                name: "UX_BrandStyleGuideRules_Workspace_Version_Order",
                table: "BrandStyleGuideRules",
                columns: new[] { "WorkspaceId", "BrandStyleGuideVersionId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BrandStyleGuides_Workspace_Status_DisplayName",
                table: "BrandStyleGuides",
                columns: new[] { "WorkspaceId", "Status", "DisplayName" });

            migrationBuilder.CreateIndex(
                name: "UX_BrandStyleGuideSections_Workspace_Version_Key_Channel",
                table: "BrandStyleGuideSections",
                columns: new[] { "WorkspaceId", "BrandStyleGuideVersionId", "SectionKey", "ChannelKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BrandStyleGuideSourceLinks_Workspace_SourceDocumentVersion",
                table: "BrandStyleGuideSourceLinks",
                columns: new[] { "WorkspaceId", "BrandSourceDocumentVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_BrandStyleGuideVersions_WorkspaceId_BrandStyleGuideId_ParentVersionId",
                table: "BrandStyleGuideVersions",
                columns: new[] { "WorkspaceId", "BrandStyleGuideId", "ParentVersionId" });

            migrationBuilder.CreateIndex(
                name: "UX_BrandStyleGuideVersions_Workspace_Guide_VersionNumber",
                table: "BrandStyleGuideVersions",
                columns: new[] { "WorkspaceId", "BrandStyleGuideId", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrandSourceDocumentTags");

            migrationBuilder.DropTable(
                name: "BrandSourceExtractions");

            migrationBuilder.DropTable(
                name: "BrandStyleGuideDefaults");

            migrationBuilder.DropTable(
                name: "BrandStyleGuideRules");

            migrationBuilder.DropTable(
                name: "BrandStyleGuideSections");

            migrationBuilder.DropTable(
                name: "BrandStyleGuideSourceLinks");

            migrationBuilder.DropTable(
                name: "BrandSourceTags");

            migrationBuilder.DropTable(
                name: "BrandStyleGuideApprovals");

            migrationBuilder.DropTable(
                name: "BrandSourceDocumentVersions");

            migrationBuilder.DropTable(
                name: "BrandStyleGuideVersions");

            migrationBuilder.DropTable(
                name: "BrandSourceDocuments");

            migrationBuilder.DropTable(
                name: "BrandStyleGuides");
        }
    }
}
