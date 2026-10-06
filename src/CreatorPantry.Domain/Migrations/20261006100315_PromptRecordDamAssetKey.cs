using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class PromptRecordDamAssetKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No data cleanup here, unlike MediaAssetAggregate's three link keys. PromptRecords.DamAssetId
            // has existed since 12.3 and has never been writable: 12.3a left the field off the request
            // precisely because nothing could resolve an id into an immutable row, and a test pinned its
            // absence until this prompt. Every stored value is therefore null, so there is no wrong one for
            // this key to find.
            migrationBuilder.CreateIndex(
                name: "IX_PromptRecords_WorkspaceId_DamAssetId",
                table: "PromptRecords",
                columns: new[] { "WorkspaceId", "DamAssetId" });

            migrationBuilder.AddForeignKey(
                name: "FK_PromptRecords_MediaAssets_WorkspaceId_DamAssetId",
                table: "PromptRecords",
                columns: new[] { "WorkspaceId", "DamAssetId" },
                principalTable: "MediaAssets",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PromptRecords_MediaAssets_WorkspaceId_DamAssetId",
                table: "PromptRecords");

            migrationBuilder.DropIndex(
                name: "IX_PromptRecords_WorkspaceId_DamAssetId",
                table: "PromptRecords");
        }
    }
}
