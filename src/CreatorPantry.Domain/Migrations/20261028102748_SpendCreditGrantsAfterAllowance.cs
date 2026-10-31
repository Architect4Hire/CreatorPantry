using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class SpendCreditGrantsAfterAllowance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ChargedToAllowance",
                table: "AccountAiQuotaReservations",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReservedFromAllowance",
                table: "AccountAiQuotaReservations",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "GrantReserved",
                table: "AccountAiQuotaPeriods",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            // Every hold taken before AP.5.2 was paid wholly from the allowance, and every charge posted before it
            // landed wholly on the allowance. Filled before the checks below are added, which a posted row with no
            // allowance share would fail.
            migrationBuilder.Sql(
                "UPDATE AccountAiQuotaReservations SET ReservedFromAllowance = ReservedAmount;");

            migrationBuilder.Sql(
                "UPDATE AccountAiQuotaReservations SET ChargedToAllowance = SettledAmount WHERE PostedAt IS NOT NULL;");

            migrationBuilder.CreateTable(
                name: "AccountCreditGrantDraws",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    HoldId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreditsHeld = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CreditsSpent = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    CreditsClawedBack = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    HeldAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountCreditGrantDraws", x => x.Id);
                    table.CheckConstraint("CK_AccountCreditGrantDraws_ClawedBack_Within_Held", "CAST(CreditsClawedBack AS FLOAT) >= 0 AND CAST(CreditsClawedBack AS FLOAT) <= CAST(CreditsHeld AS FLOAT)");
                    table.CheckConstraint("CK_AccountCreditGrantDraws_Held_Positive", "CAST(CreditsHeld AS FLOAT) > 0");
                    table.CheckConstraint("CK_AccountCreditGrantDraws_Resolution_Recorded", "(ResolvedAt IS NULL AND CreditsSpent IS NULL AND CAST(CreditsClawedBack AS FLOAT) = 0) OR (ResolvedAt IS NOT NULL AND CreditsSpent IS NOT NULL AND ResolvedAt >= HeldAt)");
                    table.CheckConstraint("CK_AccountCreditGrantDraws_Spent_Within_Held", "CreditsSpent IS NULL OR (CAST(CreditsSpent AS FLOAT) >= 0 AND CAST(CreditsSpent AS FLOAT) <= CAST(CreditsHeld AS FLOAT))");
                    table.ForeignKey(
                        name: "FK_AccountCreditGrantDraws_AccountCreditGrants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "AccountCreditGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccountAiQuotaReservations_ChargedToAllowance_Matches_Posting",
                table: "AccountAiQuotaReservations",
                sql: "(PostedAt IS NULL AND ChargedToAllowance IS NULL) OR (PostedAt IS NOT NULL AND ChargedToAllowance IS NOT NULL AND CAST(ChargedToAllowance AS FLOAT) >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccountAiQuotaReservations_FromAllowance_Within_Hold",
                table: "AccountAiQuotaReservations",
                sql: "CAST(ReservedFromAllowance AS FLOAT) >= 0 AND CAST(ReservedFromAllowance AS FLOAT) <= CAST(ReservedAmount AS FLOAT)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccountAiQuotaPeriods_GrantReserved_NotNegative",
                table: "AccountAiQuotaPeriods",
                sql: "CAST(GrantReserved AS FLOAT) >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_AccountCreditGrantDraws_Grant",
                table: "AccountCreditGrantDraws",
                column: "GrantId");

            migrationBuilder.CreateIndex(
                name: "UX_AccountCreditGrantDraws_Hold_Grant",
                table: "AccountCreditGrantDraws",
                columns: new[] { "HoldId", "GrantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountCreditGrantDraws");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AccountAiQuotaReservations_ChargedToAllowance_Matches_Posting",
                table: "AccountAiQuotaReservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AccountAiQuotaReservations_FromAllowance_Within_Hold",
                table: "AccountAiQuotaReservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AccountAiQuotaPeriods_GrantReserved_NotNegative",
                table: "AccountAiQuotaPeriods");

            migrationBuilder.DropColumn(
                name: "ChargedToAllowance",
                table: "AccountAiQuotaReservations");

            migrationBuilder.DropColumn(
                name: "ReservedFromAllowance",
                table: "AccountAiQuotaReservations");

            migrationBuilder.DropColumn(
                name: "GrantReserved",
                table: "AccountAiQuotaPeriods");
        }
    }
}
