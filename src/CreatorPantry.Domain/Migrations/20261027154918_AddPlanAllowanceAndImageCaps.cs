using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanAllowanceAndImageCaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestedByAccountId",
                table: "GeneratedImageOperations",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RunningImagesCap",
                table: "GeneratedImageOperations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImagesBilled",
                table: "AccountAiQuotaReservations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImagesRequested",
                table: "AccountAiQuotaReservations",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanKey",
                table: "AccountAiQuotaPeriods",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UpgradeGranted",
                table: "AccountAiQuotaPeriods",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddCheckConstraint(
                name: "CK_GeneratedImageOperations_RunningImagesCap_Account",
                table: "GeneratedImageOperations",
                sql: "(RequestedByAccountId IS NULL AND RunningImagesCap IS NULL) OR (RequestedByAccountId IS NOT NULL AND RunningImagesCap >= 1)");

            migrationBuilder.CreateIndex(
                name: "IX_AccountAiQuotaReservations_Account_Images",
                table: "AccountAiQuotaReservations",
                columns: new[] { "AccountId", "HeldAt" },
                filter: "ImagesRequested IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccountAiQuotaReservations_Images_Counts",
                table: "AccountAiQuotaReservations",
                sql: "(ImagesRequested IS NULL OR ImagesRequested >= 1) AND (ImagesBilled IS NULL OR (ImagesRequested IS NOT NULL AND ImagesBilled >= 0))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccountAiQuotaPeriods_UpgradeGranted_FromPlan",
                table: "AccountAiQuotaPeriods",
                sql: "UpgradeGranted >= 0 AND (PlanKey IS NOT NULL OR CAST(UpgradeGranted AS REAL) <= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_GeneratedImageOperations_RunningImagesCap_Account",
                table: "GeneratedImageOperations");

            migrationBuilder.DropIndex(
                name: "IX_AccountAiQuotaReservations_Account_Images",
                table: "AccountAiQuotaReservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AccountAiQuotaReservations_Images_Counts",
                table: "AccountAiQuotaReservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AccountAiQuotaPeriods_UpgradeGranted_FromPlan",
                table: "AccountAiQuotaPeriods");

            migrationBuilder.DropColumn(
                name: "RequestedByAccountId",
                table: "GeneratedImageOperations");

            migrationBuilder.DropColumn(
                name: "RunningImagesCap",
                table: "GeneratedImageOperations");

            migrationBuilder.DropColumn(
                name: "ImagesBilled",
                table: "AccountAiQuotaReservations");

            migrationBuilder.DropColumn(
                name: "ImagesRequested",
                table: "AccountAiQuotaReservations");

            migrationBuilder.DropColumn(
                name: "PlanKey",
                table: "AccountAiQuotaPeriods");

            migrationBuilder.DropColumn(
                name: "UpgradeGranted",
                table: "AccountAiQuotaPeriods");
        }
    }
}
