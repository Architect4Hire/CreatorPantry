using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddPublishingProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublishingProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Platform = table.Column<int>(type: "int", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishingProfiles", x => x.Id);
                    table.UniqueConstraint("AK_PublishingProfiles_Workspace_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_PublishingProfiles_DisplayName_NotBlank", "trim(DisplayName) <> ''");
                    table.CheckConstraint("CK_PublishingProfiles_Platform_Range", "Platform >= 1 AND Platform <= 12");
                    table.CheckConstraint("CK_PublishingProfiles_ProviderKey_NotBlank", "trim(ProviderKey) <> ''");
                    table.CheckConstraint("CK_PublishingProfiles_Status_Range", "Status >= 1 AND Status <= 4");
                    table.ForeignKey(
                        name: "FK_PublishingProfiles_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublishingProviderSyncs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSucceededAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastOutcome = table.Column<int>(type: "int", nullable: false),
                    RetryNotBefore = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishingProviderSyncs", x => x.Id);
                    table.CheckConstraint("CK_PublishingProviderSyncs_LastOutcome_Attempt", "LastOutcome IN (1, 3, 4, 5)");
                    table.CheckConstraint("CK_PublishingProviderSyncs_ProviderKey_NotBlank", "trim(ProviderKey) <> ''");
                    table.ForeignKey(
                        name: "FK_PublishingProviderSyncs_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalPublishingProfileReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublishingProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExternalAccountId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalPublishingProfileReferences", x => x.Id);
                    table.CheckConstraint("CK_ExternalPublishingProfileReferences_ExternalAccountId_NotBlank", "trim(ExternalAccountId) <> ''");
                    table.CheckConstraint("CK_ExternalPublishingProfileReferences_ExternalId_NotBlank", "trim(ExternalId) <> ''");
                    table.ForeignKey(
                        name: "FK_ExternalPublishingProfileReferences_PublishingProfiles_WorkspaceId_PublishingProfileId",
                        columns: x => new { x.WorkspaceId, x.PublishingProfileId },
                        principalTable: "PublishingProfiles",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_ExternalPublishingProfileReferences_Workspace_Profile",
                table: "ExternalPublishingProfileReferences",
                columns: new[] { "WorkspaceId", "PublishingProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ExternalPublishingProfileReferences_Workspace_Provider_External",
                table: "ExternalPublishingProfileReferences",
                columns: new[] { "WorkspaceId", "ProviderKey", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishingProfiles_Workspace_Status",
                table: "PublishingProfiles",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_PublishingProviderSyncs_Workspace_Provider",
                table: "PublishingProviderSyncs",
                columns: new[] { "WorkspaceId", "ProviderKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalPublishingProfileReferences");

            migrationBuilder.DropTable(
                name: "PublishingProviderSyncs");

            migrationBuilder.DropTable(
                name: "PublishingProfiles");
        }
    }
}
