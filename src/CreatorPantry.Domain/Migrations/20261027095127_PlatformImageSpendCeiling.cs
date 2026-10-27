using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class PlatformImageSpendCeiling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PlatformImageSpendMicros",
                table: "AccountAiQuotaReservations",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlatformImageSpendDays",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    AdmittedMicros = table.Column<long>(type: "bigint", nullable: false),
                    TrippedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LiftedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LiftedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformImageSpendDays", x => x.Day);
                    table.CheckConstraint("CK_PlatformImageSpendDays_Admitted_NotNegative", "AdmittedMicros >= 0");
                    table.CheckConstraint("CK_PlatformImageSpendDays_Lift_Complete", "(LiftedAt IS NULL AND LiftedBy IS NULL) OR (LiftedAt IS NOT NULL AND LiftedBy IS NOT NULL)");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccountAiQuotaReservations_PlatformImageSpend_Positive",
                table: "AccountAiQuotaReservations",
                sql: "PlatformImageSpendMicros IS NULL OR PlatformImageSpendMicros > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformImageSpendDays");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AccountAiQuotaReservations_PlatformImageSpend_Positive",
                table: "AccountAiQuotaReservations");

            migrationBuilder.DropColumn(
                name: "PlatformImageSpendMicros",
                table: "AccountAiQuotaReservations");
        }
    }
}
