using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountAiUsageAndQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountAiQuotaPeriods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LocalStartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Unit = table.Column<int>(type: "int", nullable: false),
                    Allowance = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    CarriedOver = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Consumed = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Reserved = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAiQuotaPeriods", x => x.Id);
                    table.CheckConstraint("CK_AccountAiQuotaPeriods_Ends_After_Starts", "EndsAt > StartsAt");
                    table.CheckConstraint("CK_AccountAiQuotaPeriods_Settled_After_End", "SettledAt IS NULL OR SettledAt >= EndsAt");
                    table.CheckConstraint("CK_AccountAiQuotaPeriods_Totals_NotNegative", "Allowance >= 0 AND CarriedOver >= 0 AND Consumed >= 0 AND Reserved >= 0");
                    table.CheckConstraint("CK_AccountAiQuotaPeriods_Unit_Declared", "Unit <> 0");
                });

            migrationBuilder.CreateTable(
                name: "AccountAiQuotas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Unit = table.Column<int>(type: "int", nullable: false),
                    Allowance = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PeriodLength = table.Column<int>(type: "int", nullable: false),
                    PeriodAnchor = table.Column<int>(type: "int", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CarryOver = table.Column<int>(type: "int", nullable: false),
                    CarryOverCap = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    IsSuspended = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastChangedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    LastChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAiQuotas", x => x.Id);
                    table.CheckConstraint("CK_AccountAiQuotas_Allowance_NotNegative", "Allowance IS NULL OR Allowance >= 0");
                    table.CheckConstraint("CK_AccountAiQuotas_Anchor_Matches_Length", "(PeriodLength = 1 AND PeriodAnchor = 0) OR (PeriodLength = 2 AND PeriodAnchor BETWEEN 0 AND 6) OR (PeriodLength = 3 AND PeriodAnchor BETWEEN 1 AND 28)");
                    table.CheckConstraint("CK_AccountAiQuotas_CarryOver_Declared", "CarryOver <> 0");
                    table.CheckConstraint("CK_AccountAiQuotas_CarryOverCap_Matches_Rule", "(CarryOver = 2 AND CarryOverCap IS NOT NULL AND CarryOverCap >= 0) OR (CarryOver = 1 AND CarryOverCap IS NULL)");
                    table.CheckConstraint("CK_AccountAiQuotas_Effective_Range", "EffectiveTo IS NULL OR EffectiveTo > EffectiveFrom");
                    table.CheckConstraint("CK_AccountAiQuotas_PeriodLength_Declared", "PeriodLength <> 0");
                    table.CheckConstraint("CK_AccountAiQuotas_Unit_Declared", "Unit <> 0");
                });

            migrationBuilder.CreateTable(
                name: "AccountAiUsageEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AiOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    TaskType = table.Column<int>(type: "int", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModelDeployment = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InputTokens = table.Column<int>(type: "int", nullable: true),
                    OutputTokens = table.Column<int>(type: "int", nullable: true),
                    TotalTokens = table.Column<int>(type: "int", nullable: true),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    IsBillable = table.Column<bool>(type: "bit", nullable: false),
                    UsageReported = table.Column<bool>(type: "bit", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAiUsageEntries", x => x.Id);
                    table.CheckConstraint("CK_AccountAiUsageEntries_Attempt_Positive", "AttemptNumber >= 1");
                    table.CheckConstraint("CK_AccountAiUsageEntries_Cost_NotNegative", "EstimatedCost IS NULL OR EstimatedCost >= 0");
                    table.CheckConstraint("CK_AccountAiUsageEntries_Outcome_Declared", "Outcome <> 0");
                    table.CheckConstraint("CK_AccountAiUsageEntries_TaskType_Declared", "TaskType <> 0");
                    table.CheckConstraint("CK_AccountAiUsageEntries_Tokens_NotNegative", "(InputTokens IS NULL OR InputTokens >= 0) AND (OutputTokens IS NULL OR OutputTokens >= 0) AND (TotalTokens IS NULL OR TotalTokens >= 0)");
                    table.CheckConstraint("CK_AccountAiUsageEntries_UsageReported_Matches_Tokens", "(UsageReported = 0 AND InputTokens IS NULL AND OutputTokens IS NULL AND TotalTokens IS NULL) OR (UsageReported = 1 AND (InputTokens IS NOT NULL OR OutputTokens IS NOT NULL OR TotalTokens IS NOT NULL))");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountAiQuotaPeriods_Unsettled",
                table: "AccountAiQuotaPeriods",
                column: "EndsAt",
                filter: "SettledAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_AccountAiQuotaPeriods_Account_StartsAt",
                table: "AccountAiQuotaPeriods",
                columns: new[] { "AccountId", "StartsAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_AccountAiQuotas_Account_Current",
                table: "AccountAiQuotas",
                column: "AccountId",
                unique: true,
                filter: "EffectiveTo IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_AccountAiQuotas_Account_EffectiveFrom",
                table: "AccountAiQuotas",
                columns: new[] { "AccountId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountAiUsageEntries_Account_OccurredAt",
                table: "AccountAiUsageEntries",
                columns: new[] { "AccountId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountAiUsageEntries_Account_Workspace_OccurredAt",
                table: "AccountAiUsageEntries",
                columns: new[] { "AccountId", "WorkspaceId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "UX_AccountAiUsageEntries_Operation_Attempt",
                table: "AccountAiUsageEntries",
                columns: new[] { "AiOperationId", "AttemptNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountAiQuotaPeriods");

            migrationBuilder.DropTable(
                name: "AccountAiQuotas");

            migrationBuilder.DropTable(
                name: "AccountAiUsageEntries");
        }
    }
}
