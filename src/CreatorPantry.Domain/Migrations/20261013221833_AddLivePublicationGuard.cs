using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddLivePublicationGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_PublishingRecords_Live_Card_Profile_Time",
                table: "PublishingRecords",
                columns: new[] { "WorkspaceId", "ContentCardId", "PublishingProfileId", "ScheduledAt" },
                unique: true,
                filter: "ContentCardId IS NOT NULL AND Status IN (1, 2, 3, 5, 8)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_PublishingRecords_Live_Card_Profile_Time",
                table: "PublishingRecords");
        }
    }
}
