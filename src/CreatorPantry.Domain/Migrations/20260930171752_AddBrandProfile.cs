using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBrandProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BrandProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ShortDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DefaultAudience = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Locale = table.Column<string>(type: "nvarchar(35)", maxLength: 35, nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedByMembershipId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandProfiles", x => x.Id);
                    table.UniqueConstraint("AK_BrandProfiles_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_BrandProfiles_BrandName_NotBlank", "trim(BrandName) <> ''");
                    table.CheckConstraint("CK_BrandProfiles_Revision_Positive", "Revision >= 1");
                    table.ForeignKey(
                        name: "FK_BrandProfiles_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandAssetLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MediaAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandAssetLinks", x => x.Id);
                    table.CheckConstraint("CK_BrandAssetLinks_Role_Specified", "Role <> 0");
                    table.CheckConstraint("CK_BrandAssetLinks_SortOrder_NonNegative", "SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_BrandAssetLinks_BrandProfiles_WorkspaceId_BrandProfileId",
                        columns: x => new { x.WorkspaceId, x.BrandProfileId },
                        principalTable: "BrandProfiles",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandChannelDefaults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandChannelDefaults", x => x.Id);
                    table.CheckConstraint("CK_BrandChannelDefaults_SortOrder_NonNegative", "SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_BrandChannelDefaults_BrandProfiles_WorkspaceId_BrandProfileId",
                        columns: x => new { x.WorkspaceId, x.BrandProfileId },
                        principalTable: "BrandProfiles",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BrandLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrandProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandLinks", x => x.Id);
                    table.CheckConstraint("CK_BrandLinks_Kind_Specified", "Kind <> 0");
                    table.CheckConstraint("CK_BrandLinks_SortOrder_NonNegative", "SortOrder >= 0");
                    table.ForeignKey(
                        name: "FK_BrandLinks_BrandProfiles_WorkspaceId_BrandProfileId",
                        columns: x => new { x.WorkspaceId, x.BrandProfileId },
                        principalTable: "BrandProfiles",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrandAssetLinks_Workspace_MediaAsset",
                table: "BrandAssetLinks",
                columns: new[] { "WorkspaceId", "MediaAssetId" });

            migrationBuilder.CreateIndex(
                name: "UX_BrandAssetLinks_Workspace_Profile_Order",
                table: "BrandAssetLinks",
                columns: new[] { "WorkspaceId", "BrandProfileId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BrandAssetLinks_Workspace_Profile_PrimaryLogo",
                table: "BrandAssetLinks",
                columns: new[] { "WorkspaceId", "BrandProfileId" },
                unique: true,
                filter: "Role = 1");

            migrationBuilder.CreateIndex(
                name: "UX_BrandChannelDefaults_Workspace_Profile_Channel",
                table: "BrandChannelDefaults",
                columns: new[] { "WorkspaceId", "BrandProfileId", "ChannelKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BrandChannelDefaults_Workspace_Profile_Order",
                table: "BrandChannelDefaults",
                columns: new[] { "WorkspaceId", "BrandProfileId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BrandLinks_Workspace_Profile_Order",
                table: "BrandLinks",
                columns: new[] { "WorkspaceId", "BrandProfileId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BrandProfiles_Workspace",
                table: "BrandProfiles",
                column: "WorkspaceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrandAssetLinks");

            migrationBuilder.DropTable(
                name: "BrandChannelDefaults");

            migrationBuilder.DropTable(
                name: "BrandLinks");

            migrationBuilder.DropTable(
                name: "BrandProfiles");
        }
    }
}
