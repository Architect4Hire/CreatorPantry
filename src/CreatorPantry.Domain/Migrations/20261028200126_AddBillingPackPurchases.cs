using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CreatorPantry.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingPackPurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "BillingProviderEvents",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "BillingPackPurchases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ExternalSessionId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ExternalPaymentReference = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    IsLiveMode = table.Column<bool>(type: "bit", nullable: false),
                    PackKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreditsGranted = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    GrantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reversal = table.Column<int>(type: "int", nullable: false),
                    ExternalReversalReference = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    CreditsClawedBack = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    ReversedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingPackPurchases", x => x.Id);
                    table.CheckConstraint("CK_BillingPackPurchases_AccountId_NotBlank", "trim(AccountId) <> ''");
                    table.CheckConstraint("CK_BillingPackPurchases_ClawedBack_Within_Granted", "CAST(CreditsClawedBack AS FLOAT) >= 0 AND CAST(CreditsClawedBack AS FLOAT) <= CAST(CreditsGranted AS FLOAT)");
                    table.CheckConstraint("CK_BillingPackPurchases_Credits_Positive", "CAST(CreditsGranted AS FLOAT) > 0");
                    table.CheckConstraint("CK_BillingPackPurchases_PackKey_NotBlank", "trim(PackKey) <> ''");
                    table.CheckConstraint("CK_BillingPackPurchases_Payment_NotBlank", "ExternalPaymentReference IS NULL OR trim(ExternalPaymentReference) <> ''");
                    table.CheckConstraint("CK_BillingPackPurchases_Reversal_Recorded", "(Reversal = 0 AND ReversedAt IS NULL AND ExternalReversalReference IS NULL AND CAST(CreditsClawedBack AS FLOAT) = 0) OR (Reversal <> 0 AND ReversedAt IS NOT NULL AND ReversedAt >= PurchasedAt)");
                    table.CheckConstraint("CK_BillingPackPurchases_SessionId_NotBlank", "trim(ExternalSessionId) <> ''");
                    table.ForeignKey(
                        name: "FK_BillingPackPurchases_AccountCreditGrants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "AccountCreditGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingPackPurchases_Account_PurchasedAt",
                table: "BillingPackPurchases",
                columns: new[] { "AccountId", "PurchasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingPackPurchases_GrantId",
                table: "BillingPackPurchases",
                column: "GrantId");

            migrationBuilder.CreateIndex(
                name: "UX_BillingPackPurchases_Payment",
                table: "BillingPackPurchases",
                column: "ExternalPaymentReference",
                unique: true,
                filter: "ExternalPaymentReference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_BillingPackPurchases_Session",
                table: "BillingPackPurchases",
                column: "ExternalSessionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingPackPurchases");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "BillingProviderEvents");
        }
    }
}
