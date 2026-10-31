using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAiLowCreditNotice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LowCreditNoticeDismissedAt",
                table: "AccountAiQuotaPeriods",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LowCreditNoticedAt",
                table: "AccountAiQuotaPeriods",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountAiNoticeSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    LowCreditNoticeEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAiNoticeSettings", x => x.Id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccountAiQuotaPeriods_LowCreditNotice_Dismissed_After_Raised",
                table: "AccountAiQuotaPeriods",
                sql: "LowCreditNoticeDismissedAt IS NULL OR (LowCreditNoticedAt IS NOT NULL AND LowCreditNoticeDismissedAt >= LowCreditNoticedAt)");

            migrationBuilder.CreateIndex(
                name: "UX_AccountAiNoticeSettings_Account",
                table: "AccountAiNoticeSettings",
                column: "AccountId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountAiNoticeSettings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AccountAiQuotaPeriods_LowCreditNotice_Dismissed_After_Raised",
                table: "AccountAiQuotaPeriods");

            migrationBuilder.DropColumn(
                name: "LowCreditNoticeDismissedAt",
                table: "AccountAiQuotaPeriods");

            migrationBuilder.DropColumn(
                name: "LowCreditNoticedAt",
                table: "AccountAiQuotaPeriods");
        }
    }
}
