using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingWebhookRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BillingProviderEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalEventId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsLiveMode = table.Column<bool>(type: "bit", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObjectReference = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    SubscriptionReference = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Redrives = table.Column<int>(type: "int", nullable: false),
                    RedrivenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingProviderEvents", x => x.Id);
                    table.CheckConstraint("CK_BillingProviderEvents_EventType_NotBlank", "trim(EventType) <> ''");
                    table.CheckConstraint("CK_BillingProviderEvents_ExternalEventId_NotBlank", "trim(ExternalEventId) <> ''");
                    table.CheckConstraint("CK_BillingProviderEvents_Status_Declared", "Status <> 0");
                });

            migrationBuilder.CreateTable(
                name: "BillingSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ExternalSubscriptionId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ExternalCustomerId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    IsLiveMode = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ItemId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PlanKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Cycle = table.Column<int>(type: "int", nullable: false),
                    CurrentPeriodEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ScheduleId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    NextPlanKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    NextCycle = table.Column<int>(type: "int", nullable: false),
                    NextPhaseStartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LatestInvoicePaid = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingSubscriptions", x => x.Id);
                    table.CheckConstraint("CK_BillingSubscriptions_AccountId_NotBlank", "trim(AccountId) <> ''");
                    table.CheckConstraint("CK_BillingSubscriptions_ExternalCustomerId_NotBlank", "trim(ExternalCustomerId) <> ''");
                    table.CheckConstraint("CK_BillingSubscriptions_ExternalSubscriptionId_NotBlank", "trim(ExternalSubscriptionId) <> ''");
                    table.CheckConstraint("CK_BillingSubscriptions_NextPhase_Whole", "(NextPlanKey IS NULL AND NextPhaseStartsAt IS NULL) OR NextPhaseStartsAt IS NOT NULL");
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingProviderEvents_Status_ReceivedAt",
                table: "BillingProviderEvents",
                columns: new[] { "Status", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_BillingProviderEvents_ExternalEventId",
                table: "BillingProviderEvents",
                column: "ExternalEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingSubscriptions_Account_Mode",
                table: "BillingSubscriptions",
                columns: new[] { "AccountId", "IsLiveMode" });

            migrationBuilder.CreateIndex(
                name: "UX_BillingSubscriptions_ExternalSubscriptionId",
                table: "BillingSubscriptions",
                column: "ExternalSubscriptionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingProviderEvents");

            migrationBuilder.DropTable(
                name: "BillingSubscriptions");
        }
    }
}
