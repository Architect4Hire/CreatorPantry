using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialChannelAcceptedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcceptedAt",
                table: "SocialPackageChannels",
                type: "datetimeoffset",
                nullable: true);

            // Hand-written, and the one thing here EF could not generate: channels accepted before this column
            // existed have a pointer and no instant, which the constraint below refuses. No acceptance time was
            // recorded for them, so each takes the best fact there is: when its accepted revision was written.
            migrationBuilder.Sql(
                """
                UPDATE channel
                SET AcceptedAt = revision.CreatedAt
                FROM SocialPackageChannels AS channel
                INNER JOIN SocialRevisions AS revision
                    ON revision.Id = channel.AcceptedRevisionId
                    AND revision.WorkspaceId = channel.WorkspaceId
                WHERE channel.AcceptedRevisionId IS NOT NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SocialPackageChannels_AcceptedAt_Revision",
                table: "SocialPackageChannels",
                sql: "(AcceptedRevisionId IS NULL AND AcceptedAt IS NULL) OR (AcceptedRevisionId IS NOT NULL AND AcceptedAt IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SocialPackageChannels_AcceptedAt_Revision",
                table: "SocialPackageChannels");

            migrationBuilder.DropColumn(
                name: "AcceptedAt",
                table: "SocialPackageChannels");
        }
    }
}
