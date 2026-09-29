using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountAiQuotaReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountAiQuotaReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PeriodId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskType = table.Column<int>(type: "int", nullable: false),
                    Unit = table.Column<int>(type: "int", nullable: false),
                    ReservedAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    PerAttemptEstimate = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    SettledAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    UsageReported = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    HeldAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PostedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAiQuotaReservations", x => x.Id);
                    table.CheckConstraint("CK_AccountAiQuotaReservations_Amounts_NotNegative", "ReservedAmount > 0 AND PerAttemptEstimate > 0 AND (SettledAmount IS NULL OR SettledAmount >= 0)");
                    table.CheckConstraint("CK_AccountAiQuotaReservations_Outcome_Matches_Status", "(Status IN (1, 4) AND SettledAt IS NULL AND SettledAmount IS NULL) OR (Status IN (2, 3) AND SettledAt IS NOT NULL AND SettledAmount IS NOT NULL)");
                    table.CheckConstraint("CK_AccountAiQuotaReservations_Posted_Requires_Settlement", "PostedAt IS NULL OR SettledAmount IS NOT NULL");
                    table.CheckConstraint("CK_AccountAiQuotaReservations_Status_Declared", "Status <> 0");
                    table.CheckConstraint("CK_AccountAiQuotaReservations_Timestamps_Match_Status", "(Status <> 1 OR (ReleasedAt IS NULL AND PostedAt IS NULL)) AND (Status <> 4 OR ReleasedAt IS NOT NULL)");
                    table.CheckConstraint("CK_AccountAiQuotaReservations_Unit_Declared", "Unit <> 0");
                    table.ForeignKey(
                        name: "FK_AccountAiQuotaReservations_AccountAiQuotaPeriods_PeriodId",
                        column: x => x.PeriodId,
                        principalTable: "AccountAiQuotaPeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountAiQuotaReservations_Outstanding",
                table: "AccountAiQuotaReservations",
                column: "ExpiresAt",
                filter: "ReleasedAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccountAiQuotaReservations_PeriodId",
                table: "AccountAiQuotaReservations",
                column: "PeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountAiQuotaReservations_Unposted",
                table: "AccountAiQuotaReservations",
                column: "SettledAt",
                filter: "PostedAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_AccountAiQuotaReservations_Operation_Lease",
                table: "AccountAiQuotaReservations",
                columns: new[] { "AiOperationId", "LeaseToken" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountAiQuotaReservations");
        }
    }
}
